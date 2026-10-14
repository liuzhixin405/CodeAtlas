using System.Text;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Indexing.Roslyn;

// codeatlas-roslyn --sln <solution-or-project> --repo <repo-root>
// stdout：每行一条 JSON 记录（见 CodeAtlas.Core.Roslyn.Records）；stderr：日志。
// 由 codeatlas.exe 通过 RoslynRunner 拉起；也可以手工运行看输出。

string? sln = null, repo = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sln" when i + 1 < args.Length: sln = args[++i]; break;
        case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
        case "-h" or "--help": Usage(); return 0;
        default: Console.Error.WriteLine($"未知参数：{args[i]}"); Usage(); return 2;
    }
}
if (sln is null || repo is null) { Usage(); return 2; }
if (!File.Exists(sln)) { Console.Error.WriteLine($"解决方案不存在：{sln}"); return 2; }
if (!Directory.Exists(repo)) { Console.Error.WriteLine($"仓库目录不存在：{repo}"); return 2; }

// stderr 被父进程重定向时默认是系统 ANSI 代码页，中文会乱码
Console.OutputEncoding = new UTF8Encoding(false);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
var sw = System.Diagnostics.Stopwatch.StartNew();
Console.Error.WriteLine($"codeatlas-roslyn: 打开 {sln}");
try
{
    var sinceFlush = 0;
    await foreach (var record in RoslynAnalyzer.AnalyzeAsync(Path.GetFullPath(sln), Path.GetFullPath(repo), cts.Token))
    {
        stdout.WriteLine(RecordJson.Serialize(record));
        // 进度与结束记录立刻刷出去，父进程才能及时看到；普通记录攒一批
        if (record is ProgressRec or DoneRec or ErrorRec || ++sinceFlush >= 500) { stdout.Flush(); sinceFlush = 0; }
        if (record is DoneRec done) Console.Error.WriteLine($"codeatlas-roslyn: 完成 projects={done.Projects} documents={done.Documents} errors={done.Errors} elapsed={sw.Elapsed.TotalSeconds:F1}s");
    }
    stdout.Flush();
    return 0;
}
catch (OperationCanceledException)
{
    stdout.Flush();
    Console.Error.WriteLine("codeatlas-roslyn: 已取消");
    return 130;
}
catch (Exception ex)
{
    stdout.Flush();
    Console.Error.WriteLine($"codeatlas-roslyn: 失败 {ex}");
    return 1;
}

static void Usage() => Console.Error.WriteLine("用法：codeatlas-roslyn --sln <解决方案或项目文件> --repo <仓库根目录>");
