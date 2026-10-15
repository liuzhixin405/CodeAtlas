namespace CodeAtlas.Indexing.Roslyn.Tests;

/// <summary>Roslyn 文档路径是不是落在仓库根下：目录边界 + 按平台的大小写（Windows / macOS 不分，Linux 分）。</summary>
public class SymbolKeysPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xplat", "Repo");

    [Fact]
    public void Directory_boundary_is_respected()
    {
        Assert.True(SymbolKeys.IsUnderAny(Path.Combine(Root, "src", "A.cs"), [Root]));
        Assert.True(SymbolKeys.IsUnderAny(Path.Combine(Root, "A.cs"), [Root + Path.DirectorySeparatorChar]));
        Assert.False(SymbolKeys.IsUnderAny(Path.Combine(Root + "-old", "A.cs"), [Root]));
    }

    [Fact]
    public void Case_sensitivity_follows_the_platform()
    {
        // 以前固定 OrdinalIgnoreCase：Linux 上 /tmp/xplat/repo/A.cs（另一个目录）也被当成 /tmp/xplat/Repo 之下
        var otherCase = Path.Combine(Path.GetTempPath(), "xplat", "repo", "A.cs");
        Assert.Equal(!OperatingSystem.IsLinux(), SymbolKeys.IsUnderAny(otherCase, [Root]));
    }
}
