import { describe, expect, it } from "vitest";
import { lastSegment, loadMapViewChoice, MapState, MapViewKey, parseMapViewChoice, saveMapViewChoice, type StorageLike } from "../webview/mapState";

describe("MapState", () => {
  it("starts at the top level and drills down building breadcrumbs", () => {
    const s = new MapState();
    expect(s.root).toBe("");
    s.drill("Acme");
    s.drill("Acme.Infra");
    expect(s.root).toBe("Acme.Infra");
    expect(s.crumbs.map((c) => c.label)).toEqual(["项目", "Acme", "Infra"]);
  });

  it("goTo a crumb truncates deeper levels; goTo top clears", () => {
    const s = new MapState();
    s.drill("Acme"); s.drill("Acme.Infra"); s.drill("Acme.Infra.Repo");
    s.goTo("Acme");
    expect(s.root).toBe("Acme");
    expect(s.crumbs).toHaveLength(2);
    s.goTo("");
    expect(s.root).toBe("");
  });

  it("up pops one level and is a no-op at top", () => {
    const s = new MapState();
    s.up();
    expect(s.root).toBe("");
    s.drill("src"); s.drill("src/app");
    s.up();
    expect(s.root).toBe("src");
  });

  it("goToPath expands an auto-derived root into one crumb per level", () => {
    const s = new MapState();
    s.goToPath("Acme.App.Modules");
    expect(s.root).toBe("Acme.App.Modules");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme", "Acme.App", "Acme.App.Modules"]);
    expect(s.crumbs.map((c) => c.label)).toEqual(["项目", "Acme", "App", "Modules"]);
    s.goTo("Acme.App");
    expect(s.root).toBe("Acme.App");
    s.goToPath("src/app");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "src", "src/app"]);
    s.goToPath("");
    expect(s.root).toBe("");
  });

  it("lastSegment handles namespaces, paths and plain keys", () => {
    expect(lastSegment("Acme.Infra")).toBe("Infra");
    expect(lastSegment("src/app")).toBe("app");
    expect(lastSegment("src")).toBe("src");
  });
});

function memory(init: Record<string, string> = {}): StorageLike & { data: Record<string, string> } {
  const data = { ...init };
  return { data, getItem: (k) => data[k] ?? null, setItem: (k, v) => { data[k] = v; } };
}

describe("map view choice (top-level 容器 | 框架流程 | 命名空间)", () => {
  it("maps the old persisted 'pipeline' view to 'framework'", () => {
    expect(parseMapViewChoice("pipeline")).toBe("framework");
    expect(parseMapViewChoice("framework")).toBe("framework");
    expect(parseMapViewChoice("arch")).toBe("arch");
    expect(parseMapViewChoice("ns")).toBe("ns");
    // 子视图与不认识的值不记，交给第一屏的默认规则
    expect(parseMapViewChoice("dsm")).toBeNull();
    expect(parseMapViewChoice("flow")).toBeNull();
    expect(parseMapViewChoice("")).toBeNull();
    expect(parseMapViewChoice(null)).toBeNull();
  });

  it("load rewrites the old value in storage; save / load round-trip", () => {
    const s = memory({ [MapViewKey]: "pipeline" });
    expect(loadMapViewChoice(() => s)).toBe("framework");
    expect(s.data[MapViewKey]).toBe("framework");
    saveMapViewChoice(() => s, "ns");
    expect(loadMapViewChoice(() => s)).toBe("ns");
  });

  it("storage that throws or is missing reads as no choice", () => {
    const broken: StorageLike = { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } };
    expect(loadMapViewChoice(() => broken)).toBeNull();
    expect(() => saveMapViewChoice(() => broken, "arch")).not.toThrow();
    expect(loadMapViewChoice(() => null)).toBeNull();
  });
});
