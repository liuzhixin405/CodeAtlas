using System.Reflection;

namespace CodeAtlas.Indexing.TreeSitter;

public static class QueryResources
{
    public static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"嵌入的查询资源不存在：{name}。可用：{string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
