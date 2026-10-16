using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// C# 宿主注册：常驻服务 / Hub 按"哪个容器的启动代码走得到注册调用"分配（bool 实参沿调用链判条件）；共享库里的 Minimal API 只算调了那个扩展方法的容器；
/// lambda 处理器落在它调的仓库方法上；健康检查端点；MVC 约定路由；Blazor @page；gRPC 健康检查不算业务入口。
/// </summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> HostFiles = new()
    {
        ["Acme.Boot/Setup.cs"] =
        [
            "public static class Setup",                                                            // 1
            "{",                                                                                    // 2
            "    public static void AddCore(this IServiceCollection services, bool runTimers = true)", // 3
            "    {",                                                                                // 4
            "        services.AddTasks(runTimers);",                                                // 5
            "    }",                                                                                // 6
            "    public static void AddTasks(this IServiceCollection services, bool registerTimer = true)", // 7
            "    {",                                                                                // 8
            "        // services.AddHostedService<Ghost>(); isn't registered anywhere",             // 9：注释里的不算
            "        if (registerTimer)",                                                           // 10
            "            services.AddHostedService<TimerService>();",                               // 11
            "        services.AddHostedService<AlwaysService>();",                                  // 12
            "    }",                                                                                // 13
            "    public static void AddRealtime(this IServiceCollection services)",                // 14
            "    {",                                                                                // 15
            "        services.AddSingleton<IHostedService>(sp => (BusService)sp.GetRequiredService<IBus>());", // 16
            "    }",                                                                                // 17
            "    public static void MapRealtime(this IEndpointRouteBuilder e) => e.MapHub<LiveHub>(\"/hub\");", // 18
            "}",                                                                                    // 19
        ],
        ["Acme.Boot/Docs.cs"] =
        [
            "public static class Docs",                                                   // 1
            "{",                                                                          // 2
            "    public static void UseDocs(this WebApplication app)",                    // 3
            "    {",                                                                      // 4
            "        app.MapGet(\"/\", () => Results.Redirect(\"/docs\"));",              // 5
            "    }",                                                                      // 6
            "}",                                                                          // 7
        ],
        ["Acme.Infra/Services.cs"] =
        [
            "public sealed class TimerService : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }",   // 1
            "public sealed class AlwaysService : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }",  // 2
            "public sealed class BusService : IHostedService { public Task StartAsync(CancellationToken ct) => Task.CompletedTask; }",                      // 3
            "public sealed class Ghost : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }",          // 4
            "public sealed class NightlyService : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }", // 5
            "public abstract class TimerBase : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }",    // 6
            "public sealed class LiveHub : Hub { public Task Ping() => Task.CompletedTask; }",                                                             // 7
        ],
        ["Acme.Api/Program.cs"] =
        [
            "builder.Services.AddCore(runTimers: false);", // 1：Api 关掉定时器
            "builder.Services.AddRealtime();",             // 2
            "app.MapRealtime();",                          // 3
            "app.MapHealthChecks(\"/healthz/live\");",     // 4
            "app.UseDocs();",                              // 5
            "app.Run();",                                  // 6
        ],
        ["Acme.Worker/Program.cs"] =
        [
            "var nightly = config.GetValue<bool>(\"Nightly\");",           // 1
            "builder.Services.AddCore();",                                 // 2：默认 runTimers = true
            "if (nightly) builder.Services.AddHostedService<NightlyService>();", // 3：运行时配置决定 → 条件注册
            "host.Run();",                                                 // 4
        ],
        ["Acme.Tool/Program.cs"] =
        [
            "public static class Program",                                      // 1
            "{",                                                                // 2
            "    public static int Main(string[] args) { return Dispatch(args); }", // 3：跑一次就退出
            "    static int Dispatch(string[] args) => 0;",                     // 4
            "}",                                                                // 5
        ],
    };

    private static readonly List<MapSymbol> HostSymbols =
    [
        Sym(300, SymbolKind.Class, "Setup", "Acme.Boot/Setup.cs", null, 1, 19, "public"),
        Sym(301, SymbolKind.Method, "Setup.AddCore(IServiceCollection, bool)", "Acme.Boot/Setup.cs", 300, 3, 6, "public"),
        Sym(302, SymbolKind.Method, "Setup.AddTasks(IServiceCollection, bool)", "Acme.Boot/Setup.cs", 300, 7, 13, "public"),
        Sym(303, SymbolKind.Method, "Setup.AddRealtime(IServiceCollection)", "Acme.Boot/Setup.cs", 300, 14, 17, "public"),
        Sym(304, SymbolKind.Method, "Setup.MapRealtime(IEndpointRouteBuilder)", "Acme.Boot/Setup.cs", 300, 18, 18, "public"),
        Sym(305, SymbolKind.Class, "Docs", "Acme.Boot/Docs.cs", null, 1, 7, "public"),
        Sym(306, SymbolKind.Method, "Docs.UseDocs(WebApplication)", "Acme.Boot/Docs.cs", 305, 3, 6, "public"),
        Sym(310, SymbolKind.Class, "Acme.Infra.TimerService", "Acme.Infra/Services.cs", null, 1, 1, "public"),
        Sym(311, SymbolKind.Method, "Acme.Infra.TimerService.ExecuteAsync(CancellationToken)", "Acme.Infra/Services.cs", 310, 1, 1, "protected"),
        Sym(312, SymbolKind.Class, "Acme.Infra.AlwaysService", "Acme.Infra/Services.cs", null, 2, 2, "public"),
        Sym(313, SymbolKind.Method, "Acme.Infra.AlwaysService.ExecuteAsync(CancellationToken)", "Acme.Infra/Services.cs", 312, 2, 2, "protected"),
        Sym(314, SymbolKind.Class, "Acme.Infra.BusService", "Acme.Infra/Services.cs", null, 3, 3, "public"),
        Sym(315, SymbolKind.Method, "Acme.Infra.BusService.StartAsync(CancellationToken)", "Acme.Infra/Services.cs", 314, 3, 3, "public"),
        Sym(316, SymbolKind.Class, "Acme.Infra.Ghost", "Acme.Infra/Services.cs", null, 4, 4, "public"),
        Sym(317, SymbolKind.Method, "Acme.Infra.Ghost.ExecuteAsync(CancellationToken)", "Acme.Infra/Services.cs", 316, 4, 4, "protected"),
        Sym(318, SymbolKind.Class, "Acme.Infra.NightlyService", "Acme.Infra/Services.cs", null, 5, 5, "public"),
        Sym(319, SymbolKind.Method, "Acme.Infra.NightlyService.ExecuteAsync(CancellationToken)", "Acme.Infra/Services.cs", 318, 5, 5, "protected"),
        Sym(320, SymbolKind.Class, "Acme.Infra.TimerBase", "Acme.Infra/Services.cs", null, 6, 6, "public"),
        Sym(321, SymbolKind.Method, "Acme.Infra.TimerBase.ExecuteAsync(CancellationToken)", "Acme.Infra/Services.cs", 320, 6, 6, "protected"),
        Sym(322, SymbolKind.Class, "Acme.Infra.LiveHub", "Acme.Infra/Services.cs", null, 7, 7, "public"),
        Sym(323, SymbolKind.Method, "Acme.Infra.LiveHub.Ping()", "Acme.Infra/Services.cs", 322, 7, 7, "public"),
        Sym(330, SymbolKind.Function, "Program.<Main>$(string[])", "Acme.Api/Program.cs", null, 1, 6),
        Sym(331, SymbolKind.Function, "Program.<Main>$(string[])", "Acme.Worker/Program.cs", null, 1, 4),
        Sym(332, SymbolKind.Class, "Program", "Acme.Tool/Program.cs", null, 1, 5, "public"),
        Sym(333, SymbolKind.Method, "Program.Main(string[])", "Acme.Tool/Program.cs", 332, 3, 3, "public"),
        Sym(334, SymbolKind.Method, "Program.Dispatch(string[])", "Acme.Tool/Program.cs", 332, 4, 4, "private"),
        Sym(390, SymbolKind.External, "external:BackgroundService", "<external>", null, 0, 0),
        Sym(391, SymbolKind.External, "external:IHostedService", "<external>", null, 0, 0),
        Sym(392, SymbolKind.External, "external:Hub", "<external>", null, 0, 0),
    ];

    private static readonly List<MapEdge> HostEdges =
    [
        new(310, 390, EdgeKind.Inherits), new(312, 390, EdgeKind.Inherits), new(316, 390, EdgeKind.Inherits),
        new(318, 390, EdgeKind.Inherits), new(320, 390, EdgeKind.Inherits), new(314, 391, EdgeKind.Implements),
        new(322, 392, EdgeKind.Inherits),
        new(330, 301, EdgeKind.Calls, Line: 1), new(330, 303, EdgeKind.Calls, Line: 2), new(330, 304, EdgeKind.Calls, Line: 3),
        new(330, 306, EdgeKind.Calls, Line: 5),
        new(331, 301, EdgeKind.Calls, Line: 2),
        new(301, 302, EdgeKind.Calls, Line: 5),
        new(333, 334, EdgeKind.Calls, Line: 3),
    ];

    private static readonly List<ArchNode> HostContainers =
    [
        Container("Acme.Api", "dotnet-web", "Acme.Api", "Acme.Boot", "Acme.Infra"),
        Container("Acme.Worker", "dotnet-exe", "Acme.Worker", "Acme.Boot", "Acme.Infra"),
        Container("Acme.Tool", "dotnet-exe", "Acme.Tool"),
    ];

    private static List<EntryPoint> HostEntries() =>
        EntryDiscovery.Discover(HostSymbols, HostEdges, HostContainers, p => HostFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void HostedServices_GoToTheContainerWhoseStartupReachesTheRegistration_WithBoolArgumentsFollowed()
    {
        var hosted = HostEntries().Where(e => e.RuleId == "hosted.background")
            .Select(e => (e.ContainerKey, e.Route!, e.SymbolId)).OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("Acme.Api", "AlwaysService", 313L),                         // AddCore → AddTasks 无条件注册
            ("Acme.Api", "BusService", 315L),                            // AddSingleton<IHostedService>(sp => (BusService)…)，只有 Api 调了 AddRealtime
            ("Acme.Worker", "AlwaysService", 313L),
            ("Acme.Worker", "NightlyService（条件注册）", 319L),            // if (nightly)：运行时配置，判不出
            ("Acme.Worker", "TimerService", 311L),                       // 默认 runTimers = true → registerTimer = true
        }, hosted);
        // Api：AddCore(runTimers: false) → AddTasks(registerTimer: false) → if (registerTimer) 为假 → 没有 TimerService
        // Ghost 只在注释里出现、TimerBase 是抽象类：都不是入口
        Assert.DoesNotContain(HostEntries(), e => e.SymbolId is 317 or 321);
    }

    [Fact]
    public void Hub_NeedsAMapHubReachedFromTheContainer()
    {
        var hubs = HostEntries().Where(e => e.Kind == "hub").Select(e => (e.ContainerKey, e.Route!)).ToList();
        Assert.Equal(new[] { ("Acme.Api", "LiveHub.Ping") }, hubs); // Worker 没调 MapRealtime
    }

    [Fact]
    public void SharedLibraryMinimalApi_OnlyInContainersCallingTheExtension_AndHealthChecksAreEntries()
    {
        var http = HostEntries().Where(e => e.Kind == "http").Select(e => (e.ContainerKey, e.RuleId, e.Route!, e.HttpMethod, e.SymbolId, e.Line))
            .OrderBy(e => e.Item3, StringComparer.Ordinal).ToList();
        Assert.Equal(new (string, string, string, string?, long, int)[]
        {
            ("Acme.Api", "aspnet.minimal", "/", "GET", 306, 5),                      // UseDocs 只有 Api 调了；Worker 也引用这个库但没调
            ("Acme.Api", "aspnet.healthchecks", "/healthz/live", "GET", 330, 4),     // 健康检查：入口留在映射所在的顶级语句上
        }, http);
    }

    [Fact]
    public void CSharpMain_ConsoleWithoutHost_IsCommand_HostRunIsHosted()
    {
        var mains = HostEntries().Where(e => e.RuleId == "csharp.main").Select(e => (e.ContainerKey, e.Kind)).OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("Acme.Api", "hosted"), ("Acme.Tool", "command"), ("Acme.Worker", "hosted") }, mains); // Worker 是 dotnet-exe 但 host.Run()
    }

    [Theory]
    [InlineData("registerTimer", true, true)]
    [InlineData("!registerTimer", true, false)]
    [InlineData("registerTimer && (env == null || !env.IsDevelopment())", false, false)]
    [InlineData("registerTimer && (env == null || !env.IsDevelopment())", true, null)]
    [InlineData("registerTimer || options.Enabled", true, true)]
    [InlineData("options.Enabled", true, null)]
    public void Eval_ThreeValued(string expr, bool registerTimer, bool? expected) =>
        Assert.Equal(expected, EntryDiscovery.Eval(expr, new Dictionary<string, bool> { ["registerTimer"] = registerTimer }));

    [Fact]
    public void CsMask_BlanksCommentsAndStrings_KeepsLength()
    {
        var src = "a(\"x)\"); // it's (\n/* } */ b('}', @\"c\"\"d\", \"\"\"e)\"\"\");";
        var masked = EntryDiscovery.CsMask(src);
        Assert.Equal(src.Length, masked.Length);
        // 注释里的撇号、字符串里的括号都不再干扰配括号
        Assert.Equal("a(\"  \");" + new string(' ', 10) + "\n" + new string(' ', 8) + "b(' ', @\"    \", \"\"\"  \"\"\");", masked);
    }
}
