using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter;

/// <summary>支持的语言一览：命令行与服务端都从这里建适配器，加一门语言只改这一处。</summary>
public static class LanguageAdapters
{
    public static List<ILanguageAdapter> Create() =>
    [
        new TypeScriptAdapter(),
        new TsxAdapter(),
        new JavaScriptAdapter(),
        new CSharpAdapter(),
        new GoAdapter(),
        new PythonAdapter(),
        new JavaAdapter(),
    ];
}
