using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Indexing.TreeSitter;

/// <param name="RelativePath">相对主根，'/' 分隔；外部根的文件形如 ../other/src/a.ts。</param>
public sealed record ScannedFile(string RelativePath, string FullPath, string Language, long Size);

/// <summary>
/// 按扩展名枚举仓库文件，跳过排除目录与重解析点（符号链接、junction）。
/// 多个根（主根 + 外部根）依次遍历；遍历一个根时遇到另一个根的目录就跳过（那个根自己遍历），同一个文件按全路径只出一次。
/// 嵌套仓库：worktree（.git 文件指向 …/worktrees/…）总是跳过——它是本仓库的副本；独立克隆与子模块按 roots.IncludeNested 纳入。
/// </summary>
public sealed class RepositoryScanner(IReadOnlyDictionary<string, string> extensionToLanguage, IReadOnlyCollection<string> excludedDirectories)
{
    private readonly HashSet<string> _excluded = new(excludedDirectories, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ScannedFile> Scan(string root) => Scan(ProjectRoots.Single(root));

    public IEnumerable<ScannedFile> Scan(ProjectRoots roots)
    {
        var seen = new HashSet<string>(ProjectRoots.PathComparer);
        foreach (var root in roots.All)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                foreach (var entry in entries)
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is DirectoryInfo)
                    {
                        if (_excluded.Contains(entry.Name)) continue;
                        if (!roots.ShouldDescend(entry.FullName, root)) continue;
                        stack.Push(entry.FullName);
                        continue;
                    }
                    if (entry is not FileInfo file) continue;
                    if (!extensionToLanguage.TryGetValue(file.Extension, out var language)) continue;
                    if (!seen.Add(file.FullName)) continue;
                    yield return new ScannedFile(roots.Relative(file.FullName), file.FullName, language, file.Length);
                }
            }
        }
    }
}
