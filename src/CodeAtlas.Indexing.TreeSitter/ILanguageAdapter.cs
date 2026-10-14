using CodeAtlas.Core.Model;

namespace CodeAtlas.Indexing.TreeSitter;

public interface ILanguageAdapter
{
    string LanguageId { get; }
    IReadOnlyList<string> Extensions { get; }
    ParsedFile Parse(string relativePath, string source);
}
