namespace CodeAtlas.Indexing.TreeSitter;

public sealed record ScannedFile(string RelativePath, string FullPath, string Language, long Size);

/// <summary>按扩展名枚举仓库文件，跳过排除目录与重解析点（符号链接、junction）。</summary>
public sealed class RepositoryScanner(IReadOnlyDictionary<string, string> extensionToLanguage, IReadOnlyCollection<string> excludedDirectories)
{
    private readonly HashSet<string> _excluded = new(excludedDirectories, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ScannedFile> Scan(string root)
    {
        root = Path.GetFullPath(root);
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
                if (entry is DirectoryInfo subdir)
                {
                    if (_excluded.Contains(entry.Name)) continue;
                    // 含有自己 .git 的子目录是嵌套仓库：git worktree（.git 文件）、子模块或独立克隆（.git 目录），不属于本仓库
                    if (IsNestedGitCheckout(subdir)) continue;
                    stack.Push(entry.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                if (!extensionToLanguage.TryGetValue(file.Extension, out var language)) continue;
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                yield return new ScannedFile(relative, file.FullName, language, file.Length);
            }
        }
    }

    private static bool IsNestedGitCheckout(DirectoryInfo dir)
    {
        var marker = Path.Combine(dir.FullName, ".git");
        return File.Exists(marker) || Directory.Exists(marker);
    }
}
