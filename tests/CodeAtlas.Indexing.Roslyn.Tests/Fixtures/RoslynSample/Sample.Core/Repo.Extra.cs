namespace Sample.Core;

public partial class Repo
{
    public List<Item> All() => new[] { new Item(1), new Item(2) }.ToList();
}
