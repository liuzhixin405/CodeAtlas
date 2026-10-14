namespace CodeAtlas.Indexing.TreeSitter;

public sealed record IndexOptions
{
    public IReadOnlyList<string> ExcludedDirectories { get; init; } =
        ["node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea"];
    public long MaxFileBytes { get; init; } = 2_000_000;
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;
    public bool Full { get; init; }
    public int WriteBatchSize { get; init; } = 200;
}

public sealed record IndexProgress(string Phase, int Done, int Total, string? CurrentFile);

public sealed record IndexReport(
    int FilesScanned, int FilesParsed, int FilesFailed, int FilesSkipped, int FilesRemoved, int FilesUnchanged,
    long TotalSymbols, long TotalEdges, TimeSpan Elapsed, IReadOnlyList<(string Path, string Error)> Errors);
