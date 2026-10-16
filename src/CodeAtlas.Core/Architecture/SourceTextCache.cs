using System.Collections.Concurrent;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 一次后处理（容器图扫描 → 入口识别 → 规则还原）共用的源码行缓存：仓库相对路径（'/' 分隔，外部根是 ../other/…）→ 行。
/// 读不到（被删、无权限、路径非法）给空数组，规则按"没有这段代码"处理。
/// 先 <see cref="Prefetch"/> 并行读一遍：Windows 上每次打开文件要过杀毒 / EDR 扫描（ccs-backend 7031 个文件单线程 16–20s，16 路并行 1.7–1.9s），
/// 之后各规则逐个取都是内存命中。只在一次后处理里用：文件改了要新建一个。
/// </summary>
public sealed class SourceTextCache
{
    private readonly ConcurrentDictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    private readonly string _root;

    /// <param name="mainRoot">主根的绝对路径；相对路径都拼在它后面。</param>
    public SourceTextCache(string mainRoot) => _root = mainRoot;

    public int Count => _lines.Count;

    public string[] Lines(string rel) => _lines.GetOrAdd(rel, Read);

    /// <summary>并行读进缓存（已在缓存里的跳过）。</summary>
    public void Prefetch(IEnumerable<string> rels)
    {
        var todo = rels.Where(r => !_lines.ContainsKey(r)).Distinct(StringComparer.Ordinal).ToList();
        if (todo.Count == 0) return;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 16) }, rel => _lines.TryAdd(rel, Read(rel)));
    }

    private string[] Read(string rel)
    {
        try { return File.ReadAllLines(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }
}
