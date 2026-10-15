using System.Text;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Indexing.Roslyn;

// codeatlas-roslyn --sln <solution-or-project> --repo <repo-root> [--repo <另一个根> …] [--base <路径基准>]
// stdout：每行一条 JSON 记录（见 CodeAtlas.Core.Roslyn.Records）；stderr：日志。
// 由 codeatlas.exe 通过 RoslynRunner 拉起；也可以手工运行看输出。
// 多项目索引：--repo 给出全部根（主根 + 外部根），落在它们之下的源码才输出；--base 是输出路径的基准（主根，缺省为第一个 --repo），
// 外部根的文件输出成 ../other/…，嵌套仓库里的解决方案输出的路径带子目录前缀。

// --probe：只检查本机有没有能用的 .NET SDK（MSBuild），供 codeatlas 在跑 Roslyn 之前探测。
// 能运行到这里就说明 .NET 10 运行时在；退出码 0 = 有 SDK（stdout 打印版本与路径），3 = 没有 SDK。
if (args is ["--probe"])
{
    var msbuild = RoslynAnalyzer.ProbeMsBuild();
    if (msbuild is null) { Console.Error.WriteLine("codeatlas-roslyn: 没有找到 .NET SDK"); return RoslynRunner.ProbeNoSdkExitCode; }
    Console.WriteLine(msbuild);
    return 0;
}

string? sln = null, basePath = null;
var repos = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sln" when i + 1 < args.Length: sln = args[++i]; break;
        case "--repo" when i + 1 < args.Length: repos.Add(args[++i]); break;
        case "--base" when i + 1 < args.Length: basePath = args[++i]; break;
        case "-h" or "--help": Usage(); return 0;
        default: Console.Error.WriteLine($"未知参数：{args[i]}"); Usage(); return 2;
    }
}
if (sln is null || repos.Count == 0) { Usage(); return 2; }
if (!File.Exists(sln)) { Console.Error.WriteLine($"解决方案不存在：{sln}"); return 2; }
foreach (var repo in repos)
    if (!Directory.Exists(repo)) { Console.Error.WriteLine($"仓库目录不存在：{repo}"); return 2; }
basePath ??= repos[0];

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
    await foreach (var record in RoslynAnalyzer.AnalyzeAsync(Path.GetFullPath(sln), repos.Select(Path.GetFullPath).ToList(), Path.GetFullPath(basePath), cts.Token))
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
catch (InvalidOperationException ex) when (ex.Message.Contains("compatible .NET SDK was not found", StringComparison.OrdinalIgnoreCase))
{
    // 仓库的 global.json 锁了 SDK 版本、本机没有兼容的：给一句看得懂的话，不打堆栈
    stdout.Flush();
    var requested = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Requested SDK version:\s*(\S+)").Groups[1].Value;
    Console.Error.WriteLine($"codeatlas-roslyn: 失败 仓库的 global.json 要求 .NET SDK {(requested.Length > 0 ? requested : "（见 global.json）")}，本机没有兼容的版本（rollForward 规则见 global.json）。");
    return RoslynRunner.GlobalJsonSdkMissingExitCode;
}
catch (Exception ex)
{
    stdout.Flush();
    Console.Error.WriteLine($"codeatlas-roslyn: 失败 {ex}");
    return 1;
}

static void Usage() => Console.Error.WriteLine("用法：codeatlas-roslyn --sln <解决方案或项目文件> --repo <仓库根目录> [--repo <另一个根> …] [--base <输出路径基准，缺省为第一个 --repo>]\n       codeatlas-roslyn --probe   检查本机 .NET SDK");
