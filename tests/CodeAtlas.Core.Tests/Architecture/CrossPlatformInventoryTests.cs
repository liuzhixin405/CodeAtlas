using System.Diagnostics;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// 清单在 Windows（NTFS：不分大小写、目录按名字排序、git 不检出符号链接）与 Linux（ext4：分大小写、散列序、真符号链接）上要给出同样的结果。
/// </summary>
public sealed class CrossPlatformInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-xplat-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        // 链接先单独删（只删链接本身）：递归删除碰到指回上级的 junction 会报拒绝访问
        foreach (var link in _links) try { Directory.Delete(link); } catch (IOException) { }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Lowercase_dockerfile_is_found_with_its_real_name()
    {
        Write("Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Api/dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nEXPOSE 8080\n");

        var p = Assert.Single(ProjectInventory.Load(_root).Projects);

        // 以前：Windows 上 File.Exists("Api/Dockerfile") 命中却记成 "Api/Dockerfile"（磁盘上没有这个名字），Linux 上直接找不到
        Assert.Equal("Api/dockerfile", p.Dockerfile);
        Assert.Contains(8080, p.Ports);
        Assert.Contains(p.DeployEvidence, e => e.File == "Api/dockerfile" && e.Line == 2);
    }

    [Fact]
    public void Exact_Dockerfile_name_wins_and_missing_one_is_null()
    {
        Write("Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Api/Dockerfile", "EXPOSE 8080\n");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("Lib/Dockerfile.dev", "EXPOSE 9999\n"); // 只是前缀相同，不算

        var inv = ProjectInventory.Load(_root);

        Assert.Equal("Api/Dockerfile", inv.Projects.Single(p => p.Name == "Api").Dockerfile);
        Assert.Null(inv.Projects.Single(p => p.Name == "Lib").Dockerfile);
    }

    [Fact]
    public void Appsettings_are_matched_case_insensitively_and_read_in_name_order()
    {
        Write("Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        // 没有主文件：Config 取第一个环境文件，顺序必须与平台无关（Development 在 Production 前，同 NTFS 的名字序）
        Write("Api/appsettings.Production.json", """{ "Billing": { "Url": "http://prod/" } }""");
        Write("Api/AppSettings.Development.json", """{ "Billing": { "Url": "http://dev/" } }""");

        var inv = ProjectInventory.Load(_root);

        Assert.Equal(["Development", "Production"], inv.ConfigEntries.Select(c => c.Environment).ToArray());
        Assert.Equal("http://dev/", inv.Config("Api", "Billing:Url"));
        Assert.Equal("Api/AppSettings.Development.json", inv.ConfigEntries[0].File);
    }

    [Fact]
    public void Directory_links_are_not_followed()
    {
        Write("Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Api/Program.cs", "var x = 1;\n");
        // Linux / macOS 上 git 检出的真链接：一个指回仓库根（循环），一个指向兄弟目录（重复）
        if (!TryLinkDirectory(Path.Combine(_root, "Api", "loop"), _root) || !TryLinkDirectory(Path.Combine(_root, "Copy"), Path.Combine(_root, "Api")))
            return; // 这台机器建不了链接（也建不了 junction）：没法测

        var inv = ProjectInventory.Load(_root);

        Assert.Equal("Api", Assert.Single(inv.Projects).Dir);
        Assert.Equal(["Api/Program.cs"], inv.SourceFiles.ToArray());
    }

    /// <summary>符号链接；Windows 没有开发者模式时退回 junction（同样是重解析点，不需要管理员）。</summary>
    private bool TryLinkDirectory(string link, string target)
    {
        _links.Add(link);
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!OperatingSystem.IsWindows()) return false; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void Path_comparison_follows_the_platform()
    {
        var expectIgnoreCase = !OperatingSystem.IsLinux();
        Assert.Equal(expectIgnoreCase, ProjectRoots.PathComparer.Equals("/w/Repo/Program.cs", "/w/repo/program.cs"));
        Assert.Equal(expectIgnoreCase, "/w/Repo/Program.cs".StartsWith("/w/repo/", ProjectRoots.PathComparison));
    }
}
