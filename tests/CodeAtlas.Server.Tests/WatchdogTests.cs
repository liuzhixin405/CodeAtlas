using System.Diagnostics;

namespace CodeAtlas.Server.Tests;

public class WatchdogTests
{
    private static string NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codeatlas-wd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.ts"), "export function a() {}\n");
        return repo;
    }

    [Fact]
    public async Task RunAsync_PrintsPortLine_AndStopsOnCancel()
    {
        var repo = NewRepo();
        try
        {
            var stdout = new StringWriter();
            using var cts = new CancellationTokenSource();
            var run = ServerHost.RunAsync(new ServerOptions(repo, ServerOptions.DefaultDbPath(repo)), stdout, cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!stdout.ToString().Contains("\"port\":") && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Matches("\\{\"port\":\\d+\\}", stdout.ToString().Trim());

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task Watchdog_ExitsWhenParentGone()
    {
        var repo = NewRepo();
        try
        {
            // 用一个一秒后退出的子进程扮演父进程
            using var fakeParent = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 1 /nobreak >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            var options = new ServerOptions(repo, ServerOptions.DefaultDbPath(repo), ParentPid: fakeParent.Id)
            {
                WatchdogPollSeconds = 1,
                WatchdogGraceSeconds = 1,
            };
            var sw = Stopwatch.StartNew();
            await ServerHost.RunAsync(options, new StringWriter(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        }
        finally { Directory.Delete(repo, recursive: true); }
    }
}
