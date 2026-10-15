import { describe, expect, it } from "vitest";
import {
  findSameEntry, hasDsmContent, interfaceCalls, isRepoContainer, isThinFlow, NoServiceHint, pickDefaultEntry, rankDefaultEntries, repoContainerNode, RepoDsmKey,
  shouldAutoOpenFlow, shouldOpenRepoDsm, soleContainer,
} from "../webview/firstScreen";
import type { ArchDto, ArchNodeDto, DsmDto, EntryDto, FlowDto, FlowNodeDto } from "../webview/types";

function archNode(key: string, kind: string): ArchNodeDto {
  return { key, kind, subKind: kind === "container" ? "frontend" : "http-service", name: key, path: null, source: "discovered", evidence: [], members: [], ports: [], namespaceRoots: [], outbound: 0 };
}

function arch(...nodes: ArchNodeDto[]): ArchDto {
  return { nodes, edges: [], conflicts: [], note: null };
}

let nextId = 1;
function entry(kind: string, route: string | null, name = route ?? "x", extra: Partial<EntryDto> = {}): EntryDto {
  return { symbolId: nextId++, kind, ruleId: kind, route, httpMethod: null, name, qualifiedName: `q.${name}`, file: "a.ts", line: 1, ...extra };
}

function flow(entrySymbolId: number, nodes: number): FlowDto {
  return {
    entrySymbolId, entryLabel: "e", route: null, containerKey: "c", actors: [],
    root: { symbolId: entrySymbolId, label: "e", qualifiedName: "e", kind: "entry", actor: "a", via: null, confidence: 1, count: 1, depth: 0, children: [], hidden: 0, note: null },
    behaviors: [], nodes, truncated: false, note: null,
  };
}

describe("soleContainer / shouldAutoOpenFlow", () => {
  it("one container plus external systems and people → auto open", () => {
    const a = arch(archNode("ccs-ui", "container"), archNode("api-v5", "external"), archNode("api-v4", "external"), archNode("user", "person"));
    expect(soleContainer(a)?.key).toBe("ccs-ui");
    expect(shouldAutoOpenFlow(a)).toBe(true);
  });

  it("two or more containers → keep the container diagram", () => {
    expect(shouldAutoOpenFlow(arch(archNode("ui", "container"), archNode("api", "container"), archNode("db", "external")))).toBe(false);
  });

  it("no containers / no arch → no auto open", () => {
    expect(shouldAutoOpenFlow(arch(archNode("ext", "external")))).toBe(false);
    expect(shouldAutoOpenFlow(arch())).toBe(false);
    expect(shouldAutoOpenFlow(null)).toBe(false);
  });
});

describe("shouldOpenRepoDsm / repo DSM (library-only repos)", () => {
  function dsm(components: number): DsmDto {
    return {
      container: RepoDsmKey, root: "", cells: [], cycles: [], coupling: [], gitAvailable: false, note: null, rules: [], violations: [],
      components: Array.from({ length: components }, (_, i) => ({ key: `P${i}`, label: `P${i}`, symbols: 1, files: 1, hasChildren: false, scc: i, hot: null })),
    } as DsmDto;
  }

  it("no container at all (only externals / people, or nothing) → open the whole-repo DSM", () => {
    expect(shouldOpenRepoDsm(arch())).toBe(true);
    expect(shouldOpenRepoDsm(arch(archNode("ext", "external"), archNode("user", "person")))).toBe(true);
  });

  it("any container → keep the normal first screen; old server (null) → old behaviour", () => {
    expect(shouldOpenRepoDsm(arch(archNode("api", "container")))).toBe(false);
    expect(shouldOpenRepoDsm(null)).toBe(false);
  });

  it("repo DSM only when it has components (not indexed yet → fall back)", () => {
    expect(hasDsmContent(dsm(2))).toBe(true);
    expect(hasDsmContent(dsm(0))).toBe(false);
    expect(hasDsmContent(null)).toBe(false);
  });

  it("pseudo container uses the server's repo key and carries the hint text", () => {
    const repo = repoContainerNode("structurizr-lib");
    expect(repo.key).toBe("*");
    expect(repo.name).toBe("structurizr-lib");
    expect(repo.kind).toBe("container");
    expect(isRepoContainer(repo)).toBe(true);
    expect(isRepoContainer(archNode("api", "container"))).toBe(false);
    expect(isRepoContainer(null)).toBe(false);
    expect(repoContainerNode("").name).toBe("仓库");
    expect(NoServiceHint).toBe("未发现可部署的服务，显示模块依赖");
  });
});

describe("rankDefaultEntries", () => {
  it("command-line commands come after http and before hosted / other entries", () => {
    const main = entry("hosted", "tool", "Main");
    const serve = entry("command", "serve");
    const job = entry("job", "Cleanup");
    const api = entry("http", "/x");
    const add = entry("command", "remote add");
    expect(rankDefaultEntries([main, serve, job, api, add])).toEqual([api, serve, add, main, job]);
  });

  it("pages first by shortest route, then http in order, then the rest", () => {
    const job = entry("job", null, "Nightly");
    const h1 = entry("http", "/api/b");
    const pLong = entry("page", "/settings/users");
    const h2 = entry("http", "/api/a");
    const pRoot = entry("page", "/");
    const pMid = entry("page", "/login");
    const pMid2 = entry("page", "/about");
    const ranked = rankDefaultEntries([job, h1, pLong, h2, pRoot, pMid, pMid2]);
    expect(ranked.map((e) => e.route ?? e.name)).toEqual(["/", "/about", "/login", "/settings/users", "/api/b", "/api/a", "Nightly"]);
  });

  it("no pages: first http entry leads; no http either: original order", () => {
    const h = entry("http", "/api/x");
    const hd = entry("handler", null, "H");
    expect(rankDefaultEntries([hd, h])[0]).toBe(h);
    const a = entry("handler", null, "A");
    const b = entry("job", null, "B");
    expect(rankDefaultEntries([a, b])).toEqual([a, b]);
    expect(rankDefaultEntries([])).toEqual([]);
  });
});

function node(kind: FlowNodeDto["kind"], count = 1, children: FlowNodeDto[] = []): FlowNodeDto {
  return { symbolId: null, label: kind, qualifiedName: kind, kind, actor: "a", via: null, confidence: 1, count, depth: 1, children, hidden: 0, note: null };
}

/** 一条流程：nodes 个节点、calls 个接口调用（http 叶子）。 */
function flowWith(entrySymbolId: number, nodes: number, calls: number): FlowDto {
  const f = flow(entrySymbolId, nodes);
  return { ...f, root: { ...f.root, children: Array.from({ length: calls }, () => node("http")) } };
}

describe("pickDefaultEntry", () => {
  it("prefers the page with the most interface calls over a shorter route without any (login vs groups)", async () => {
    // 2026-10-15 ccs-ui 实测：按"路由最短且不太薄"会选到 /{lang}/login（35 节点、0 个接口调用）；用户要看的是组管理这种业务页
    const login = entry("page", "/login");
    const groups = entry("page", "/server/groups");
    const sizes = new Map<number, [number, number]>([[login.symbolId, [35, 0]], [groups.symbolId, [108, 9]]]);
    const pick = await pickDefaultEntry([login, groups], async (e) => { const [n, c] = sizes.get(e.symbolId)!; return flowWith(e.symbolId, n, c); });
    expect(pick?.entry).toBe(groups);
    expect(pick?.flow?.nodes).toBe(108);
  });

  it("no interface calls anywhere → most nodes; a thin flow never beats a non-thin one", async () => {
    const root = entry("page", "/");          // 2 节点，太薄，但有 1 个接口调用
    const a = entry("page", "/a");            // 6 节点
    const b = entry("page", "/bb");           // 9 节点 → 选它
    const sizes = new Map<number, [number, number]>([[root.symbolId, [2, 1]], [a.symbolId, [6, 0]], [b.symbolId, [9, 0]]]);
    const pick = await pickDefaultEntry([root, a, b], async (e) => { const [n, c] = sizes.get(e.symbolId)!; return flowWith(e.symbolId, n, c); });
    expect(pick?.entry).toBe(b);
    expect(isThinFlow(flow(1, 2))).toBe(true);
    expect(isThinFlow(flow(1, 3))).toBe(false);
  });

  it("ties keep the earlier candidate; only the first maxTries are looked at", async () => {
    const es = [entry("page", "/"), entry("page", "/a"), entry("page", "/bb"), entry("page", "/ccc")];
    const sizes: [number, number][] = [[5, 2], [5, 2], [4, 1], [50, 9]];
    let calls = 0;
    const pick = await pickDefaultEntry(es, async (e) => { calls++; const [n, c] = sizes[es.indexOf(e)]!; return flowWith(e.symbolId, n, c); }, 3);
    expect(calls).toBe(3);          // 第 4 个（最好的）不在前 3 个里，看不到
    expect(pick?.entry).toBe(es[0]); // 前两个同分（5 节点 / 2 调用），取靠前的
  });

  it("counts hop and http nodes anywhere in the tree, using their ×N count", () => {
    const f = flow(1, 10);
    const tree = { ...f, root: { ...f.root, children: [node("call", 1, [node("http", 3), node("hop", 1, [node("call")])]), node("external-group")] } };
    expect(interfaceCalls(tree)).toBe(4);
  });

  it("flows unavailable (old server / errors) → first candidate without a flow", async () => {
    const h = entry("http", "/api/x");
    const p = entry("page", "/home");
    const pick = await pickDefaultEntry([h, p], async (e) => { if (e === p) throw new Error("boom"); return null; });
    expect(pick).toEqual({ entry: p, flow: null });
  });

  it("no entries → null; cancelled → null", async () => {
    expect(await pickDefaultEntry([], async () => null)).toBeNull();
    const p = entry("page", "/");
    expect(await pickDefaultEntry([p], async (e) => flow(e.symbolId, 1), 5, () => true)).toBeNull();
  });
});
describe("findSameEntry", () => {
  it("matches by id first, then by kind + qualified name + route after a rebuild", () => {
    const old = entry("page", "/login", "LoginPage");
    expect(findSameEntry([old], old)).toBe(old);
    const rebuilt = { ...old, symbolId: 9999 };
    expect(findSameEntry([entry("page", "/"), rebuilt], old)).toBe(rebuilt);
    expect(findSameEntry([entry("page", "/other", "Other")], old)).toBeNull();
  });
});
