namespace Sample.Core;

public record Item(int Id);

public interface IRepo
{
    Item Find(int id);
    Item Find(string name);
    List<Item> All();
}
