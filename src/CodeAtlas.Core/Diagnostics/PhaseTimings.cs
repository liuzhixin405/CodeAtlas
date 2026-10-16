using System.Diagnostics;

namespace CodeAtlas.Core.Diagnostics;

/// <summary>一个阶段的耗时（秒）。</summary>
public sealed record PhaseTiming(string Phase, double Seconds);

/// <summary>
/// 一次索引里各阶段的耗时，按发生顺序记。Lap 记"从创建或上一次 Lap 到现在"，适合顺序执行的一串步骤；
/// Measure 单独计一段，适合穿插在别的代码里的步骤。线程安全。
/// </summary>
public sealed class PhaseTimings
{
    private readonly List<PhaseTiming> _items = [];
    private readonly Stopwatch _lap = Stopwatch.StartNew();

    public IReadOnlyList<PhaseTiming> Items { get { lock (_items) return _items.ToList(); } }

    public void Lap(string phase)
    {
        lock (_items)
        {
            _items.Add(new PhaseTiming(phase, _lap.Elapsed.TotalSeconds));
            _lap.Restart();
        }
    }

    public void Add(string phase, double seconds) { lock (_items) _items.Add(new PhaseTiming(phase, seconds)); }

    public void AddRange(IEnumerable<PhaseTiming> items) { lock (_items) _items.AddRange(items); }

    public T Measure<T>(string phase, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        try { return action(); }
        finally { Add(phase, sw.Elapsed.TotalSeconds); }
    }

    public void Measure(string phase, Action action) => Measure(phase, () => { action(); return 0; });

    public static string Format(IEnumerable<PhaseTiming> items) => string.Join("  ", items.Select(t => $"{t.Phase} {t.Seconds:F1}s"));
}
