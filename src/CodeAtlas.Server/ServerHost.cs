using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CodeAtlas.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.Server;

public static class ServerHost
{
    /// <summary>构建但不启动。只监听 127.0.0.1。chatProvider 为测试注入假端点用；null 走 OpenAI 兼容 HTTP。</summary>
    public static WebApplication Build(ServerOptions options, Narration.IChatProvider? chatProvider = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddSingleton(_ => new IndexService(options));
        // 叙述服务单独注册（在 IndexService 之后，容器按逆序释放：先关叙述的库连接，再关索引服务）
        builder.Services.AddSingleton(sp => Narration.NarrationService.Create(options, sp.GetRequiredService<IndexService>(), chatProvider));
        // 页面在 VS Code Webview（vscode-webview://）或浏览器（file:// 时 Origin 为 null）里跨源访问本服务；
        // 服务只监听回环地址、无凭据，放开任意 Origin 是安全的。
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        // 索引是旧版本解析规则建的：服务一起来就全量重建。以前只有右键"在 CodeAtlas 中查看"才触发，
        // 直接打开面板看项目地图的人会一直用着旧索引（新规则只体现在每次重算的容器图上，调用链还是旧的）
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var index = app.Services.GetRequiredService<IndexService>();
            if (index.Health() is { Files: > 0, NeedsRebuild: true }) index.TryStartIndex(full: true);
        });
        if (!string.IsNullOrEmpty(options.Token))
        {
            // 只监听回环并不能阻止用户浏览器里的网页替用户发请求，所以用一次性 token 把调用方限定为扩展和它的页面。
            app.Use(async (ctx, next) =>
            {
                if (HttpMethods.IsOptions(ctx.Request.Method) || !ctx.Request.Path.StartsWithSegments("/api"))
                {
                    await next();
                    return;
                }
                var provided = ctx.Request.Headers["X-CodeAtlas-Token"].ToString();
                if (!string.Equals(provided, options.Token, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "缺少或错误的 X-CodeAtlas-Token" });
                    return;
                }
                await next();
            });
        }

        app.MapGet("/api/health", (IndexService s) => s.Health());

        app.MapPost("/api/index", (IndexService s, IndexRequest? body) =>
            s.TryStartIndex(body?.Full ?? false, body?.Roslyn) ? Results.Accepted("/api/index/status") : Results.Conflict(new { error = "索引正在进行中" }));

        app.MapGet("/api/index/status", (IndexService s) => s.Status());

        app.MapGet("/api/search", (IndexService s, string q, int? limit) =>
            s.Search(q, Math.Clamp(limit ?? 30, 1, 500)));

        app.MapGet("/api/map", (IndexService s, string? root) => s.Map(root ?? ""));

        app.MapGet("/api/pipeline", (IndexService s, string? root) => s.Pipeline(root ?? ""));

        app.MapGet("/api/arch/containers", (IndexService s) => s.Architecture());

        app.MapGet("/api/arch/dsm", (IndexService s, string container, string? root) =>
            s.Dsm(container, root ?? "") is { } dsm ? Results.Ok(dsm) : Results.NotFound(new { error = $"没有这个容器：{container}" }));

        app.MapGet("/api/arch/entries", (IndexService s, string container, string? kind, string? q) => s.Entries(container, kind, q));

        app.MapGet("/api/arch/flow", (IndexService s, long entry, int? depth) =>
            s.Flow(entry, depth) is { } flow ? Results.Ok(flow) : Results.NotFound(new { error = $"没有这个入口：{entry}" }));

        // 本地 AI 叙述：读（没有缓存时自动排队）/ 触发 / 队列状态 / 停 / 续
        app.MapGet("/api/arch/narrative", (Narration.NarrationService n, string kind, string key) =>
            Narration.NarrativePresets.IsValidKind(kind) ? Results.Ok(n.Get(kind, key)) : BadKind(kind));

        app.MapPost("/api/arch/narrate", (Narration.NarrationService n, NarrateRequest? body) =>
        {
            if (body is null || string.IsNullOrEmpty(body.Key)) return Results.BadRequest(new { error = "需要 body {kind, key, force?}" });
            if (!Narration.NarrativePresets.IsValidKind(body.Kind)) return BadKind(body.Kind);
            var dto = n.Enqueue(body.Kind, body.Key, body.Force ?? false);
            return Results.Accepted($"/api/arch/narrative?kind={Uri.EscapeDataString(body.Kind)}&key={Uri.EscapeDataString(body.Key)}", dto);
        });

        app.MapGet("/api/arch/narrate/status", (Narration.NarrationService n) => n.Status());
        app.MapPost("/api/arch/narrate/stop", (Narration.NarrationService n) => n.Stop());
        app.MapPost("/api/arch/narrate/resume", (Narration.NarrationService n) => n.Resume());

        app.MapGet("/api/files/lens", (IndexService s, string path) => s.Lens(path));

        app.MapGet("/api/symbols/at", (IndexService s, string path, int line, int col) =>
            s.SymbolAt(path, line, col) is { } dto ? Results.Ok(dto) : Results.NotFound());

        app.MapGet("/api/symbols/{id:long}", (IndexService s, long id) =>
            s.GetSymbol(id) is { } dto ? Results.Ok(dto) : Results.NotFound());

        app.MapGet("/api/symbols/{id:long}/outline", (IndexService s, long id, bool? externals, bool? tests) =>
            s.Outline(id, new OutlineOptions(externals ?? true, tests ?? false)) is { } outline ? Results.Ok(outline) : Results.NotFound());

        app.MapGet("/api/symbols/{id:long}/relations", (IndexService s, long id, int? depth, bool? tests) =>
            s.Relations(id, new RelationsOptions(depth ?? 1, tests ?? false)) is { } relations ? Results.Ok(relations) : Results.NotFound());

        app.MapGet("/api/symbols/{id:long}/impact", (IndexService s, long id, bool? entries, bool? tests) =>
            s.Impact(id, new ImpactOptions(entries ?? true, tests ?? false)) is { } impact ? Results.Ok(impact) : Results.NotFound());

        app.MapGet("/api/symbols/{id:long}/neighbors", (IndexService s, long id, string? direction, string? kinds) =>
        {
            var dir = direction switch { "in" => Direction.Incoming, "out" => Direction.Outgoing, _ => Direction.Both };
            HashSet<EdgeKind>? kindSet = null;
            if (!string.IsNullOrWhiteSpace(kinds))
            {
                kindSet = [];
                foreach (var k in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!DbNames.TryParseEdgeKind(k, out var kind))
                        return Results.BadRequest(new { error = $"未知的边类型：{k}", valid = DbNames.EdgeKindNames });
                    kindSet.Add(kind);
                }
            }
            return s.Neighbors(id, dir, kindSet) is { } list ? Results.Ok(list) : Results.NotFound();
        });

        return app;
    }

    private static IResult BadKind(string? kind) =>
        Results.BadRequest(new { error = $"未知的叙述对象类型：{kind}", valid = Narration.NarrativePresets.Kinds });

    /// <summary>启动、向 stdout 打印 {"port":N}、运行看门狗、等待停止。</summary>
    public static async Task RunAsync(ServerOptions options, TextWriter stdout, CancellationToken ct)
    {
        var app = Build(options);
        await app.StartAsync(ct);
        var port = new Uri(app.Urls.First()).Port;
        stdout.WriteLine(JsonSerializer.Serialize(new { port }));
        stdout.Flush();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.ParentPid is { } pid)
            _ = WatchParentAsync(pid, options, linked);

        try { await app.WaitForShutdownAsync(linked.Token); }
        catch (OperationCanceledException) { /* 正常停止 */ }
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static async Task WatchParentAsync(int pid, ServerOptions options, CancellationTokenSource stop)
    {
        DateTime? goneSince = null;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.WatchdogPollSeconds), stop.Token); }
            catch (OperationCanceledException) { return; }
            if (IsAlive(pid)) { goneSince = null; continue; }
            goneSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - goneSince >= TimeSpan.FromSeconds(options.WatchdogGraceSeconds))
            {
                stop.Cancel();
                return;
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
