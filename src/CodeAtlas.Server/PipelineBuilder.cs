using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

/// <summary>
/// 旧"管道视图"（按模块统计格子）已在 2026-10-16 换成「框架流程」（FrameworkFlowBuilder，/api/arch/framework），统计部分已删。
/// 这里只留下各处共用的两个工具：测试代码判定、按段取公共前缀。类名保留是为了不动 30 多处调用。
/// </summary>
public static partial class PipelineBuilder
{
    // Go：*_test.go；Python：test_*.py / *_test.py / conftest.py
    [GeneratedRegex(@"(^|[/.])(tests?|__tests__|spec|specs)([/.]|$)|\.(test|spec)\.[tj]sx?$|Tests?\.cs$|_test\.go$|(^|/)test_[^/]*\.py$|_test\.py$|(^|/)conftest\.py$", RegexOptions.IgnoreCase)]
    private static partial Regex TestPath();

    [GeneratedRegex(@"\.Tests?\.")]
    private static partial Regex TestQualifiedName();

    // Java：src/test/java/** 已由上面的 test 段规则覆盖；文件名 *Test.java / *Tests.java / *IT.java / *TestCase.java。
    // 单独一条区分大小写的规则、且后缀前须是小写字母或数字：Rabbit.java、Latest.java 不算（并进上面忽略大小写的那条会误伤它们）
    [GeneratedRegex(@"[a-z0-9_](Test|Tests|IT|TestCase)\.java$")]
    private static partial Regex JavaTestFile();

    /// <summary>与页面 isTestSymbol 同一条规则：路径或限定名里带 Tests/Test/Spec 段。</summary>
    public static bool IsTest(string path, string qualifiedName) =>
        TestPath().IsMatch(path.Replace('\\', '/')) || TestQualifiedName().IsMatch(qualifiedName) || JavaTestFile().IsMatch(path);

    /// <summary>键（命名空间或目录）的最长公共前缀（按段）。</summary>
    internal static string CommonPrefix(IEnumerable<string> keys)
    {
        string[]? prefix = null;
        var sep = '.';
        foreach (var k in keys)
        {
            var segs = MapBuilder.KeySegments(k);
            if (prefix is null) { prefix = segs; sep = k.Contains('/') ? '/' : '.'; continue; }
            var n = 0;
            while (n < prefix.Length && n < segs.Length && prefix[n] == segs[n]) n++;
            prefix = prefix[..n];
        }
        return prefix is null ? "" : string.Join(sep, prefix);
    }
}
