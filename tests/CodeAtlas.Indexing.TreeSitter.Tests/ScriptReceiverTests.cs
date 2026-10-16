using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// TS/JS 接收者形态推断与回调边（审计 2026-10-16 #11、#14、#15）：
/// 内建 / 宿主对象（Set、Map、URLSearchParams、Promise、vscode 对象）的方法不按名字连到仓库里的同名方法；
/// const api = useApi()（hook 返回仓库类实例）→ api.post 连到那个类的 post；作为实参 / 属性值传出去的函数是回调边。
/// 夹具是 Acme.Shop 风格的小前端，文件在测试里现写。
/// </summary>
public class ScriptReceiverTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-recv-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter()];

    private const string ServerManagerTs = """
        export class ServerManager {
          has(repo: string): boolean { return true; }
          stop(repo: string): void {}
        }
        """;

    private const string PanelTs = """
        export class PanelHost {
          show(): void {}
        }
        """;

    // 与 ServerManager.has / PanelHost.show 同名的内建 / 宿主对象方法
    private const string ExtensionTs = """
        import * as vscode from "vscode";
        import { ServerManager } from "./serverManager";
        import { PanelHost } from "./panel";

        const Supported = new Set([".ts", ".cs"]);
        let servers: ServerManager;
        let panel: PanelHost | undefined;
        let status: vscode.StatusBarItem;

        export function activate(ext: string, repo: string) {
          const output = vscode.window.createOutputChannel("Shop");
          const seen = new Set<string>();
          const params = new URLSearchParams("a=1");
          if (Supported.has(ext) && seen.has(repo) && servers.has(repo)) panel?.show();
          status.show();
          output.show(true);
          params.get("a");
          return Promise.resolve(1);
        }

        export function guess(x: any) {
          return x.has("r");
        }
        """;

    private const string ApiClientTs = """
        export class ApiClient {
          get(url: string) { return url; }
          post(url: string, body?: unknown) { return url; }
        }
        export const createApiClient = (base: string) => new ApiClient(base);
        export class Registry {
          static instance(): Registry { return new Registry(); }
          lookup(key: string) { return key; }
        }
        """;

    private const string UseApiTs = """
        import { useMemo } from "react";
        import { createApiClient } from "../services/apiClient";

        export const useApi = () => {
          return useMemo(() => createApiClient("/api"), []);
        };
        """;

    private const string UseOrdersTs = """
        import { useApi } from "./useApi";
        import { Registry } from "../services/apiClient";

        type Client = ReturnType<typeof useApi>;

        export function useOrders() {
          const api = useApi();
          return api.post("/orders", {});
        }

        export const listOrders = (api: Client) => api.get("/orders");

        export function lookupOrder() {
          return Registry.instance().lookup("o");
        }
        """;

    private const string UseAuthTs = """
        import { useMutation } from "@tanstack/react-query";

        export function useAuth(stream: { on(e: string, f: unknown): void; off(e: string, f: unknown): void }) {
          const login = async (name: string) => name;
          const onData = (chunk: string) => chunk;
          const limit = 3;
          stream.on("data", onData);
          stream.off("data", onData);
          const loginMutation = useMutation({ mutationFn: login, retry: limit });
          return { loginMutation };
        }
        """;

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteRepo()
    {
        Write("src/serverManager.ts", ServerManagerTs);
        Write("src/panel.ts", PanelTs);
        Write("src/extension.ts", ExtensionTs);
        Write("web/services/apiClient.ts", ApiClientTs);
        Write("web/hooks/useApi.ts", UseApiTs);
        Write("web/hooks/useOrders.ts", UseOrdersTs);
        Write("web/hooks/useAuth.ts", UseAuthTs);
    }

    private async Task<IndexReader> IndexAsync()
    {
        await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        return new IndexReader(_db);
    }

    private static List<(string To, string? Via, double Confidence, int Line)> Calls(IndexReader reader, string qualifiedName) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Select(n => (n.Edge.ToName, n.Edge.Via, n.Edge.Confidence, n.Edge.Line)).OrderBy(x => x.Line).ThenBy(x => x.ToName, StringComparer.Ordinal).ToList();

    private static Dictionary<string, ScriptBinding?> MemberCallBindings(ParsedFile f, int line) =>
        f.References.Where(r => r.Kind == EdgeKind.Calls && r.Line == line && r.Via is null).ToDictionary(r => r.Name + "@" + r.Col, r => r.Script);

    // ---------- 适配器：接收者形态 ----------

    [Fact]
    public void Receivers_BuiltinAndHostObjects_AreMarked_TypedVariablesCarryTheirType()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/extension.ts", ExtensionTs);
        ScriptBinding? At(string name, int line) => f.References.Single(r => r.Kind == EdgeKind.Calls && r.Name == name && r.Line == line && r.Via is null).Script;
        ScriptBinding Builtin(string type) => new(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, type);

        var line14 = f.References.Where(r => r.Line == 14 && r.Name == "has").OrderBy(r => r.Col).Select(r => r.Script).ToList();
        // 同一行三个 .has：引用按（符号, 名字, 行）去重，接收者形态相同的 seen.has 不重复记，形态不同的 servers.has 要单独记
        Assert.Equal(new ScriptBinding?[]
        {
            Builtin("Set"),                                                        // Supported = new Set([...])；seen = new Set<string>() 同形态
            new ScriptBinding(ScriptReceiver.Typed, "./serverManager", "ServerManager"), // let servers: ServerManager
        }, line14);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "./panel", "PanelHost"), At("show", 14));             // let panel: PanelHost | undefined
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "vscode", "StatusBarItem"), At("show", 15));          // let status: vscode.StatusBarItem
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptPackageMarker + "vscode", null), At("show", 16)); // vscode.window.createOutputChannel(...)
        Assert.Equal(Builtin("URLSearchParams"), At("get", 17));
        Assert.Equal(Builtin("Promise"), At("resolve", 18));                                                        // 全局对象
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other), At("has", 22));                                       // 参数 x: any：推不出
    }

    [Fact]
    public void Receivers_FunctionReturnValues_AndReturnTypeAliases_PointAtTheFunction()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("web/hooks/useOrders.ts", UseOrdersTs);
        ScriptBinding? At(string name) => f.References.Single(r => r.Kind == EdgeKind.Calls && r.Name == name && r.Via is null).Script;

        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "./useApi", "useApi()"), At("post"));                  // const api = useApi()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "./useApi", "useApi()"), At("get"));                   // api: Client，type Client = ReturnType<typeof useApi>
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "../services/apiClient", "Registry.instance()"), At("lookup")); // Registry.instance().lookup()
    }

    [Fact]
    public void ReturnHints_RecordWhatEachFunctionReturns()
    {
        using var ts = new TypeScriptAdapter();
        var useApi = ts.Parse("web/hooks/useApi.ts", UseApiTs);
        var hint = Assert.Single(useApi.References, r => r.Via == HeuristicEdgeResolver.ScriptReturnsVia);
        Assert.Equal(("useApi", "createApiClient", EdgeKind.References), (useApi.Symbols[hint.FromIndex].Name, hint.Name, hint.Kind));
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "../services/apiClient", "createApiClient()"), hint.Script); // useMemo(() => createApiClient(...)) 展开

        var client = ts.Parse("web/services/apiClient.ts", ApiClientTs);
        var hints = client.References.Where(r => r.Via == HeuristicEdgeResolver.ScriptReturnsVia)
            .Select(r => (client.Symbols[r.FromIndex].Name, r.Name, r.Script)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
        Assert.Equal(new (string, string, ScriptBinding?)[]
        {
            ("createApiClient", "ApiClient", new ScriptBinding(ScriptReceiver.None, null, "ApiClient")),           // => new ApiClient(base)
            ("get", "string", new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, "string")), // return url（url: string）
            ("instance", "Registry", new ScriptBinding(ScriptReceiver.None, null, "Registry")),                    // static instance(): Registry
            ("lookup", "string", new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, "string")),
            ("post", "string", new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, "string")),
        }, hints);
    }

    [Fact]
    public void Callbacks_PassedFunctions_AreCallbackReferences_ButNotRemovedListenersOrPlainValues()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("web/hooks/useAuth.ts", UseAuthTs);
        var callbacks = f.References.Where(r => r.Via == HeuristicEdgeResolver.ScriptCallbackVia)
            .Select(r => (r.Name, r.Line, r.Context, f.Symbols[r.FromIndex].Name)).ToList();
        Assert.Equal(new (string, int, string?, string)[]
        {
            ("onData", 7, CallContext.Callback, "useAuth"),  // stream.on("data", onData)；第 8 行 stream.off 摘监听不算
            ("login", 9, CallContext.Callback, "useAuth"),   // useMutation({ mutationFn: login })；retry: limit 是普通值不算
        }, callbacks);
    }

    // ---------- 解析器（整仓索引） ----------

    [Fact]
    public async Task BuiltinReceivers_DoNotLinkToSameNamedRepoMethods()
    {
        WriteRepo();
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:createOutputChannel", (string?)null, 0.5, 11),
            ("external:has", null, 0.5, 14),                                         // Supported.has / seen.has：new Set（以前按名字连到 ServerManager.has 0.7）
            ("src/panel.ts:PanelHost.show", null, 0.9, 14),                          // panel: PanelHost
            ("src/serverManager.ts:ServerManager.has", null, 0.9, 14),               // servers: ServerManager（以前 0.7）
            ("external:vscode.StatusBarItem.show", null, 0.5, 15),                   // 不连 PanelHost.show
            ("external:show", null, 0.5, 16),                                        // vscode.window.createOutputChannel() 的返回值
            ("external:get", null, 0.5, 17),                                         // URLSearchParams.get
            ("external:resolve", null, 0.5, 18),                                     // Promise.resolve
        }, Calls(reader, "src/extension.ts:activate").Where(c => c.To != "external:Set" && c.To != "external:URLSearchParams"));
        // 接收者推不出（x: any）：只凭方法名在 import 范围里找，置信度降到 0.5（以前 0.7）
        Assert.Equal(new[] { ("src/serverManager.ts:ServerManager.has", (string?)null, 0.5, 22) },
            Calls(reader, "src/extension.ts:guess"));
    }

    [Fact]
    public async Task HookReturningRepoClass_LinksMemberCallsToThatClass_AlsoAfterIncrementalReindex()
    {
        WriteRepo();
        var reader = await IndexAsync();
        Assert.Contains(("web/services/apiClient.ts:ApiClient.post", (string?)null, 0.9, 8), Calls(reader, "web/hooks/useOrders.ts:useOrders"));
        Assert.Contains(("web/services/apiClient.ts:ApiClient.get", (string?)null, 0.9, 11), Calls(reader, "web/hooks/useOrders.ts:listOrders"));
        Assert.Contains(("web/services/apiClient.ts:Registry.lookup", (string?)null, 0.9, 14), Calls(reader, "web/hooks/useOrders.ts:lookupOrder"));
        // returns 辅助边：useApi → ApiClient（顺着 createApiClient 推到底），置信度压在视图门槛 0.4 以下
        var returns = reader.GetNeighbors(reader.FindByQualifiedName("web/hooks/useApi.ts:useApi").Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.References })
            .Where(n => n.Edge.Via == HeuristicEdgeResolver.ScriptReturnsVia).Select(n => (n.Edge.ToName, n.Edge.Confidence)).ToList();
        Assert.Equal(new[] { ("web/services/apiClient.ts:ApiClient", HeuristicEdgeResolver.ScriptReturnsConfidence) }, returns);

        // 只改调用方文件再增量索引：useApi.ts / apiClient.ts 没重新解析，返回值从库里的 returns 边读
        Write("web/hooks/useOrders.ts", UseOrdersTs.Replace("\"/orders\", {}", "\"/orders/v2\", {}"));
        reader = await IndexAsync();
        Assert.Contains(("web/services/apiClient.ts:ApiClient.post", (string?)null, 0.9, 8), Calls(reader, "web/hooks/useOrders.ts:useOrders"));
    }

    [Fact]
    public async Task PassedFunctions_BecomeCallbackEdges_ToRepoFunctionsOnly()
    {
        WriteRepo();
        var reader = await IndexAsync();
        var callbacks = Calls(reader, "web/hooks/useAuth.ts:useAuth").Where(c => c.Via == HeuristicEdgeResolver.ScriptCallbackVia).ToList();
        Assert.Equal(new[]
        {
            ("web/hooks/useAuth.ts:useAuth.onData", (string?)HeuristicEdgeResolver.ScriptCallbackVia, HeuristicEdgeResolver.ScriptCallbackConfidence, 7),
            ("web/hooks/useAuth.ts:useAuth.login", HeuristicEdgeResolver.ScriptCallbackVia, HeuristicEdgeResolver.ScriptCallbackConfidence, 9),
        }, callbacks);
        var ctx = reader.GetNeighbors(reader.FindByQualifiedName("web/hooks/useAuth.ts:useAuth").Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Where(n => n.Edge.Via == HeuristicEdgeResolver.ScriptCallbackVia).Select(n => n.Edge.Ctx).Distinct().ToList();
        Assert.Equal(new[] { CallContext.Callback }, ctx);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
