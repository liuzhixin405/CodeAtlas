using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// VS Code 扩展 → 它拉起的本仓库进程：扩展代码里 spawn / execFile / fork 的可执行文件名（字符串字面量 codeatlas / codeatlas.exe）
/// 对上本仓库某个容器的可执行文件名（.NET 的 AssemblyName 或项目名、Node 命令行的包名、Go / Python 的目录名）就连过去，协议 http
/// （扩展与 webview 经 fetch 调它的 /api/*）。证据：spawn 那一行 + 每个调 /api/ 的文件的第一处 fetch。按名字对上，置信度同"按配置对上"。
/// </summary>
public sealed class VsCodeExtensionRule : IDiscoveryRule
{
    public const string RuleId = "vscode-extension";
    public string Id => RuleId;
    public string Description => "VS Code 扩展拉起的本仓库进程（spawn 的可执行文件名）与对它的 HTTP 调用";

    private static readonly Regex Spawn = new(@"\b(?:spawn|spawnSync|execFile|execFileSync|fork)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Literal = new(@"[""'`]([\w.\-]+)[""'`]", RegexOptions.Compiled);
    private static readonly Regex ApiCall = new(@"\bfetch\s*\(", RegexOptions.Compiled);

    public void Apply(RuleContext ctx)
    {
        var extensions = ctx.Containers.Where(c => c.SubKind == "vscode-extension").ToList();
        if (extensions.Count == 0) return;
        var byExe = ExecutableNames(ctx);
        foreach (var ext in extensions)
        {
            var files = ctx.Inventory.SourceFiles.Where(f => PolyText.LangOf(f) == SourceLang.JavaScript && !f.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
                                                             && !RuleContext.IsTestFile(f) && ext.Members.Any(m => RuleContext.Under(f, m))).ToList();
            ArchNode? target = null;
            (string File, int Line, string Exe)? spawnSite = null;
            foreach (var f in files)
            {
                var lines = ctx.Lines(f);
                if (!Array.Exists(lines, l => Spawn.IsMatch(l))) continue;
                var code = SourceMask.Code(lines, SourceLang.JavaScript);
                var text = SourceMask.WithoutComments(lines, SourceLang.JavaScript);
                // 文件里出现的可执行文件名字面量（exe 常在别处算好再传给 spawn）
                var exe = text.SelectMany(t => Literal.Matches(t).Select(m => m.Groups[1].Value))
                    .Select(v => v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v)
                    .FirstOrDefault(v => byExe.TryGetValue(v, out var c) && c.Key != ext.Key);
                if (exe is null) continue;
                var line = Array.FindIndex(code, l => Spawn.IsMatch(l));
                if (line < 0) continue;
                target = byExe[exe];
                spawnSite = (f, line + 1, exe);
                break;
            }
            if (target is null || spawnSite is not { } s) continue;
            ctx.Outbound.Add(new Outbound(ext.Key, RuleId, "http", $"拉起 {s.Exe}", null, null, s.File, s.Line, target.Key));
            foreach (var f in files)
            {
                var lines = ctx.Lines(f);
                if (!Array.Exists(lines, l => l.Contains("/api/", StringComparison.Ordinal))) continue;
                var code = SourceMask.Code(lines, SourceLang.JavaScript);
                var text = SourceMask.WithoutComments(lines, SourceLang.JavaScript);
                var call = Enumerable.Range(0, lines.Length).FirstOrDefault(i => ApiCall.IsMatch(code[i]) && text.Skip(Math.Max(0, i - 3)).Take(7).Any(t => t.Contains("/api/", StringComparison.Ordinal)), -1);
                if (call < 0) continue;
                ctx.Outbound.Add(new Outbound(ext.Key, RuleId, "http", $"{s.Exe} 的 /api/*", null, null, f, call + 1, target.Key));
            }
        }
    }

    /// <summary>容器的可执行文件名（不分大小写）→ 容器；同名多个时不猜。</summary>
    private static Dictionary<string, ArchNode> ExecutableNames(RuleContext ctx)
    {
        var names = new List<(string Name, ArchNode Container)>();
        foreach (var c in ctx.Containers)
        {
            var project = ctx.Inventory.Projects.FirstOrDefault(p => p.Dir == c.Path && ContainerDiscovery.SubKindOf(p) is not null);
            if (project is not null) names.Add((project.AssemblyName ?? project.Name, c));
            else if (c.SubKind is "node-cli" or "go-service" or "python-service") names.Add((c.Name, c));
        }
        return names.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Container.Key).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Container, StringComparer.OrdinalIgnoreCase);
    }
}
