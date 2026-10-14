namespace Sample.Core;

/// <summary>索引器、属性初始化器、record：分析器要把这些位置上的调用归到正确的成员。</summary>
public class Catalog
{
    private readonly Repo repo = new Repo();

    public List<Item> Items { get; } = new List<Item>();

    public Item this[int id] => repo.Find(id);
}

public record Tag(string Name);
