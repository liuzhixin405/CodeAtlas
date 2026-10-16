import { readFileSync } from "node:fs";
import ts from "typescript";
import { afterEach, describe, expect, it, vi } from "vitest";

const source = ts.createSourceFile("main.ts", readFileSync(new URL("../webview/main.ts", import.meta.url), "utf8"), ts.ScriptTarget.Latest, true);

function compile(code: string, bindings: Record<string, unknown>, result: string): unknown {
  const compiled = ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  return new Function(...Object.keys(bindings), compiled + "\nreturn " + result)(...Object.values(bindings));
}

function declaration(name: string): string {
  return source.statements.filter(ts.isFunctionDeclaration).find(node => node.name?.text === name)!.getText(source);
}

afterEach(() => vi.useRealTimers());

describe("search response ownership", () => {
  for (const clear of [false, true]) {
    it(clear ? "does not reopen cleared search" : "does not overwrite newer results", async () => {
      vi.useFakeTimers();
      const pending: ((hits: { qualifiedName: string; path: string }[]) => void)[] = [];
      const classes = new Set<string>();
      const dropdown = {
        innerHTML: "", classList: { add: (name: string) => classes.add(name), remove: (name: string) => classes.delete(name) },
        querySelectorAll: () => [],
      };
      const searchEl = { value: "old" };
      const input = source.statements.find(node => node.getText(source).startsWith('searchEl.addEventListener("input"')) as ts.ExpressionStatement;
      const listener = (input.expression as ts.CallExpression).arguments[1]!.getText(source);
      const onInput = compile("const onInput = " + listener, {
        searchEl, dropdown, searchSeq: 0, searchTimer: 0, lastHits: [], activeIndex: -1,
        api: { search: () => new Promise(resolve => pending.push(resolve)) },
        window: { setTimeout, clearTimeout }, esc: (text: string) => text,
      }, "onInput") as () => void;
      onInput();
      await vi.advanceTimersByTimeAsync(300);
      searchEl.value = clear ? "" : "new";
      onInput();
      if (!clear) {
        await vi.advanceTimersByTimeAsync(300);
        pending[1]!([{ qualifiedName: "new result", path: "new.ts" }]);
        await Promise.resolve();
      }
      pending[0]!([{ qualifiedName: "old result", path: "old.ts" }]);
      await Promise.resolve();
      expect(dropdown.innerHTML).not.toContain("old result");
      if (clear) expect(classes.has("open")).toBe(false);
      else expect(dropdown.innerHTML).toContain("new result");
    });
  }
});

describe("DSM response ownership and index refresh", () => {
  it("ignores an older response for the same drill path", async () => {
    const pending: ((dto: unknown) => void)[] = [];
    const render = vi.fn();
    const setMapView = vi.fn();
    const detailsEl = { innerHTML: "" };
    const load = compile(declaration("loadDsm"), {
      api: { dsm: () => new Promise(resolve => pending.push(resolve)) },
      dsmContainer: { key: "api", name: "API" }, dsmPath: ["src"], dsmSeq: 0, mapView: "dsm",
      dsmDto: null, dsmShown: null, dsmView: { render }, detailsEl, presets: { component: "default" },
      reorderDsm: (dto: unknown) => dto, renderContainerCrumbs: () => {}, isRepoContainer: () => false,
      esc: (text: string) => text, toast: vi.fn(), setMapView,
    }, "loadDsm") as () => Promise<void>;
    const old = load();
    const latest = load();
    const fresh = { components: [1, 2], cells: [], cycles: [], coupling: [], gitAvailable: true, note: null };
    pending[1]!(fresh);
    await latest;
    pending[0]!(null);
    await old;
    expect(render).toHaveBeenCalledExactlyOnceWith(fresh);
    expect(detailsEl.innerHTML).toContain("2 个组件");
    expect(setMapView).not.toHaveBeenCalled();
  });

  for (const view of ["dsm", "ns"]) {
    it(`refreshes the active ${view} view after indexing`, async () => {
      const loadDsm = vi.fn(async () => {});
      const loadNamespaceMap = vi.fn(async () => {});
      const refresh = compile(declaration("doRefreshMap"), {
        api: {}, autoFlowPending: false, mapView: view, mode: "map", loadDsm, loadNamespaceMap,
      }, "doRefreshMap") as () => Promise<void>;
      await refresh();
      expect(view === "dsm" ? loadDsm : loadNamespaceMap).toHaveBeenCalledOnce();
    });
  }
});