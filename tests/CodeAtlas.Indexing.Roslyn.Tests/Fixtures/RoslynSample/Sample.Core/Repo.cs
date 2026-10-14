namespace Sample.Core;

public partial class Repo : IRepo
{
    public Item Find(int id) => new(id);
    public Item Find(string name) => new(name.Length);
}
