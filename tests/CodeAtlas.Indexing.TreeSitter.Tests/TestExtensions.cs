namespace CodeAtlas.Indexing.TreeSitter.Tests;

internal static class TestExtensions
{
    /// <summary>IReadOnlyList 没有 IndexOf；测试里用它把符号对象换算回 ParsedFile.Symbols 的下标。</summary>
    public static int IndexOf<T>(this IReadOnlyList<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item) || Equals(list[i], item)) return i;
        return -1;
    }
}
