using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 索引后处理的"增量结果 == 全量结果"：同一个库上反复 增量索引 + PostIndexSteps（输入没变就跳过），每一步都和"新库全量索引 + 旧的整段重算"比
/// arch_* / entry_points / route_matches / arch_rules 的内容（符号 id 换成 限定名@文件:行，两个库的 id 不同）。
/// 改动覆盖：控制器动作的路由、前端加一个 fetch、appsettings 里的服务地址（不是源码，tree-sitter 不会动库）、只有索引内容变了（Roslyn 写回）。
/// </summary>
public sealed class PostIndexStepsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-postincr-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private ProjectRoots Roots => ProjectRoots.Single(_root);

    public PostIndexStepsTests()
    {
        Write("shop-api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("shop-api/Properties/launchSettings.json", """{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5100" } } }""");
        Write("shop-api/OrdersController.cs", Controller("{id}", "Get"));
        Write("billing/Billing.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("billing/Properties/launchSettings.json", """{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5200" } } }""");
        Write("billing/appsettings.json", """{ "Services": { "Shop": "http://localhost:5100/" } }""");
        Write("billing/Setup.cs", """
            namespace Billing.Api;
            public static class Setup
            {
                public static IServiceCollection AddShop(this IServiceCollection services, IConfiguration configuration)
                {
                    services.AddHttpClient("Shop", client => client.BaseAddress = new Uri(configuration["Services:Shop"]!));
                    return services;
                }
            }
            """);
        Write("billing/ShopClient.cs", """
            namespace Billing.Api;
            public sealed class ShopClient(IHttpClientFactory factory)
            {
                public async Task<string> Order(int id)
                {
                    var http = factory.CreateClient("Shop");
                    return await http.GetStringAsync($"/api/orders/{id}");
                }
            }
            """);
        Write("shop-web/package.json", """{ "name": "shop-web", "scripts": { "dev": "vite" }, "dependencies": { "react": "19.0.0", "vite": "6.0.0" } }""");
        Write("shop-web/src/api.ts", "export async function loadOrder(id: string) { return fetch(`/api/orders/${id}`); }\n");
    }

    private static string Controller(string getRoute, string getName) => $$"""
        namespace Shop.Api;

        [ApiController]
        [Route("api/orders")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{{getRoute}}")]
            public string {{getName}}(int id) => "order";

            [HttpPost]
            public string Create() => "created";
        }
        """;

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static async Task Index(IndexDatabase db, ProjectRoots roots, bool full)
    {
        var adapters = LanguageAdapters.Create();
        try { await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver(), new IndexOptions { Full = full }).IndexAsync(roots); }
        finally { foreach (var a in adapters.OfType<IDisposable>()) a.Dispose(); }
    }

    /// <summary>增量：同一个库上增量索引 + PostIndexSteps。返回三步各自是否真的跑了。</summary>
    private async Task<(bool Arch, bool Entries)> Incremental()
    {
        await Index(_db, Roots, full: false);
        var steps = new PostIndexSteps(Roots, _db);
        var arch = steps.Arch();
        var entries = steps.Entries();
        Assert.Equal(arch is null, entries.Skipped);
        return (arch is not null, !entries.Skipped);
    }

    /// <summary>全量：新库全量索引 + 旧的整段重算（ScanAndStore + Run，不看指纹）。</summary>
    private async Task<List<string>> Full()
    {
        using var db = IndexDatabase.OpenInMemory();
        await Index(db, Roots, full: true);
        ArchitectureScanner.ScanAndStore(Roots, db);
        ArchitecturePostProcess.Run(Roots, db);
        return Snapshot(db);
    }

    /// <summary>后处理写的各表，按写入顺序；符号 id 换成稳定的 限定名@文件:起始行。</summary>
    private static List<string> Snapshot(IndexDatabase db)
    {
        const string Sym = "(SELECT s.qualified_name || '@' || f.path || ':' || s.start_line FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id = {0})";
        var queries = new[]
        {
            "SELECT 'node|' || key || '|' || kind || '|' || subkind || '|' || name || '|' || COALESCE(path, '') || '|' || source || '|' || members_json || '|' || ports_json || '|' || evidence_json FROM arch_nodes ORDER BY id",
            "SELECT 'edge|' || from_key || '|' || to_key || '|' || protocol || '|' || confidence || '|' || source || '|' || COALESCE(config_key, '') || '|' || COALESCE(url, '') || '|' || evidence_json FROM arch_edges ORDER BY id",
            "SELECT 'out|' || container_key || '|' || rule_id || '|' || protocol || '|' || COALESCE(client_name, '') || '|' || COALESCE(config_key, '') || '|' || COALESCE(url, '') || '|' || file || '|' || line || '|' || COALESCE(target_key, '') FROM arch_outbound ORDER BY id",
            "SELECT 'conflict|' || kind || '|' || message FROM arch_conflicts ORDER BY id",
            $"SELECT 'entry|' || {string.Format(Sym, "symbol_id")} || '|' || container_key || '|' || rule_id || '|' || kind || '|' || COALESCE(route, '') || '|' || COALESCE(http_method, '') || '|' || file || '|' || line FROM entry_points ORDER BY id",
            $"SELECT 'match|' || from_container || '|' || file || '|' || line || '|' || path || '|' || {string.Format(Sym, "entry_symbol_id")} || '|' || to_container FROM route_matches ORDER BY id",
            "SELECT 'rule|' || kind || '|' || from_pattern || '|' || to_pattern || '|' || file || '|' || line FROM arch_rules ORDER BY id",
        };
        return queries.SelectMany(q => db.Query<string>(q)).ToList();
    }

    [Fact]
    public async Task IncrementalEqualsFull_AcrossControllerFetchAndConfigChanges_AndSkipsWhenNothingChanged()
    {
        // 第一次：没有指纹，整段跑
        Assert.Equal((true, true), await Incremental());
        var initial = Snapshot(_db);
        Assert.Equal(await Full(), initial);
        Assert.Contains(initial, l => l.StartsWith("match|shop-web|", StringComparison.Ordinal)); // 前端 fetch 对上了控制器
        Assert.Contains(initial, l => l.StartsWith("edge|billing|shop-api|http|", StringComparison.Ordinal)); // 配置地址 → launchSettings 端口 → shop-api

        // 什么都没改：两步都跳过，表原样
        Assert.Equal((false, false), await Incremental());
        Assert.Equal(initial, Snapshot(_db));

        // 改控制器动作的路由与方法名
        Write("shop-api/OrdersController.cs", Controller("by-id/{id}", "GetById"));
        Assert.Equal((true, true), await Incremental());
        var renamed = Snapshot(_db);
        Assert.NotEqual(initial, renamed);
        Assert.Equal(await Full(), renamed);

        // 前端加一个 fetch
        Write("shop-web/src/orders.ts", "export async function createOrder() { return fetch('/api/orders', { method: 'POST' }); }\n");
        Assert.Equal((true, true), await Incremental());
        var withFetch = Snapshot(_db);
        Assert.NotEqual(renamed, withFetch);
        Assert.Equal(await Full(), withFetch);

        // appsettings 里的服务地址改成没人监听的端口：不是源码，库不变，但容器图要重算
        Write("billing/appsettings.json", """{ "Services": { "Shop": "http://localhost:5999/" } }""");
        Assert.Equal((true, true), await Incremental());
        var moved = Snapshot(_db);
        Assert.DoesNotContain(moved, l => l.StartsWith("edge|billing|shop-api|", StringComparison.Ordinal));
        Assert.Equal(await Full(), moved);

        Assert.Equal((false, false), await Incremental());
        Assert.Equal(moved, Snapshot(_db));
    }

    [Fact]
    public async Task OnlyIndexContentChanged_RerunsBoth_SoTheContainerGraphIsNotUpgradedTwice()
    {
        await Incremental();
        // 模拟 Roslyn 写回：文件没变，库里的内容变了（入口识别读的是库）
        _db.Execute("UPDATE files SET roslyn_at = '2026-10-16T00:00:00Z' WHERE path = 'shop-api/OrdersController.cs'");
        Assert.Equal((true, true), await Incremental());
        var steps = Snapshot(_db);
        // 同一个库上旧的整段重算：结果必须一样（只重跑 entries 的话，已升级的容器边会再追加一遍证据）
        ArchitectureScanner.ScanAndStore(Roots, _db);
        ArchitecturePostProcess.Run(Roots, _db);
        Assert.Equal(Snapshot(_db), steps);
    }

    [Fact]
    public async Task ResultsWrittenWithoutFingerprint_ForceTheNextRun()
    {
        await Incremental();
        // codeatlas arch 之类的旧入口直接写结果表：指纹作废，下次后处理一定重算
        ArchitectureScanner.ScanAndStore(Roots, _db);
        Assert.Null(ArchFingerprintStorage.Get(_db, ArchFingerprintStorage.Post));
        Assert.Equal((true, true), await Incremental());
        Assert.Equal((false, false), await Incremental());
    }

    [Fact]
    public async Task ArchFailure_DoesNotRecordAFingerprint()
    {
        await Index(_db, Roots, full: false);
        var steps = new PostIndexSteps(Roots, _db);
        // 没调 Arch()（等同于 Arch 抛了异常）：Entries 照跑，但不能记指纹，否则下次会把没扫完的容器图当成最新的
        var report = steps.Entries();
        Assert.False(report.Skipped);
        Assert.Null(ArchFingerprintStorage.Get(_db, ArchFingerprintStorage.Post));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
