using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;
using Microsoft.AspNetCore.Builder;

namespace CodeAtlas.Server.Tests;

/// <summary>写一个小仓库到临时目录，索引进文件库，启动服务在随机端口。</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public string Repo { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-srv-" + Guid.NewGuid().ToString("N"));
    public string DbPath => ServerOptions.DefaultDbPath(Repo);
    public HttpClient Client { get; private set; } = null!;
    public FakeChatProvider Chat { get; } = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Repo, "src", "util"));
        File.WriteAllText(Path.Combine(Repo, "src", "util", "format.ts"), "export function helper(id: number): string { return String(id); }\n");
        File.WriteAllText(Path.Combine(Repo, "src", "svc.ts"), "import { helper } from './util/format';\nexport class Svc {\n  find(id: number): string { return helper(id); }\n}\n");
        // util.helper2()：接收者类型未知、两个 import 进来的文件里都有 helper2 方法 → 多候选（各 0.3）。
        // 裸调用 helper2() 没有 import 绑定，现在按全局函数算 external，不再按全仓同名去猜，所以多候选要靠 import 范围造出来
        File.WriteAllText(Path.Combine(Repo, "src", "app.ts"), "import { Svc } from './svc'; import './dup1'; import './dup2';\nexport function main(): void { new Svc().find(1); fetch('x'); util.helper2(); }\n");
        File.WriteAllText(Path.Combine(Repo, "src", "dup1.ts"), "export class Dup1 { helper2(): void {} }\n");
        File.WriteAllText(Path.Combine(Repo, "src", "dup2.ts"), "export class Dup2 { helper2(): void {} }\n");

        using (var db = IndexDatabase.Open(DbPath))
        {
            List<ILanguageAdapter> adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];
            await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver()).IndexAsync(Repo);
            foreach (var a in adapters.OfType<IDisposable>()) a.Dispose();
        }

        // 叙述走假的模型端点：不碰真机器上的 llama-server
        _app = ServerHost.Build(new ServerOptions(Repo, DbPath, Port: 0) { Narration = new Narration.NarrationOptions("http://127.0.0.1:1/v1", "fake-model") }, Chat);
        await _app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        if (Directory.Exists(Repo)) Directory.Delete(Repo, recursive: true);
    }
}
