using System.Net.Http.Json;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 旧版本解析规则建的索引：服务一起来就自动全量重建。
/// 以前只在右键"在 CodeAtlas 中查看"时才重建——直接打开面板看项目地图的用户一直用着旧索引（2026-10-15 ccs-ui 实测：新 VSIX 装上后流程仍是 2 个节点）。
/// </summary>
public class StartupRebuildTests
{
    [Fact]
    public async Task OldIndexVersion_IsRebuiltFully_WhenTheServerStarts()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codeatlas-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "a.ts"), "export function a(): number { return 1; }\n");
        File.WriteAllText(Path.Combine(repo, "src", "b.ts"), "import { a } from './a';\nexport function b(): number { return a(); }\n");
        var dbPath = ServerOptions.DefaultDbPath(repo);
        try
        {
            using (var db = IndexDatabase.Open(dbPath))
            {
                List<ILanguageAdapter> adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter()];
                await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver()).IndexAsync(repo);
                foreach (var ad in adapters.OfType<IDisposable>()) ad.Dispose();
                db.Execute($"PRAGMA user_version = {IndexDatabase.CurrentIndexVersion - 1}"); // 模拟上一版插件建的库
            }

            var app = ServerHost.Build(new ServerOptions(repo, dbPath, Port: 0) { Narration = new Narration.NarrationOptions("http://127.0.0.1:1/v1", "m", Enabled: false), Roslyn = false });
            await app.StartAsync();
            try
            {
                using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                IndexStatusDto status;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                do
                {
                    await Task.Delay(100);
                    status = (await c.GetFromJsonAsync<IndexStatusDto>("/api/index/status"))!;
                } while ((status.Running || status.LastReport is null) && DateTime.UtcNow < deadline);

                Assert.Null(status.Error);
                Assert.NotNull(status.LastReport);                       // 没人发 /api/index，服务自己跑了一次
                Assert.Equal(2, status.LastReport!.FilesParsed);          // 全量：内容没变的文件也重新解析
                var health = (await c.GetFromJsonAsync<HealthDto>("/api/health"))!;
                Assert.False(health.NeedsRebuild);
            }
            finally { await app.StopAsync(); await app.DisposeAsync(); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(repo, recursive: true); } catch { /* 临时目录 */ }
        }
    }
}
