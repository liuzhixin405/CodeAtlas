using System;
using Microsoft.Extensions.Logging;
namespace Acme.Orders;
public interface IOrderRepo : IRepo<Order> { Order Find(int id); }
public record Order(int Id);
public struct Money { public decimal Amount; }
public enum Status { New, Done }
public delegate void Handler(int x);
public class OrderService : BaseService, IOrderRepo, IDisposable
{
    private readonly IOrderRepo _repo;
    private int _count, _other;
    public int Count { get; set; }
    public OrderService(IOrderRepo repo) { _repo = repo; }
    public Order Find(int id) { Log(id); return _repo.Find(id); }
    public void Run<T>(List<T> items) { var svc = new OrderService(null); var l = new List<Order>(); Helper.Do<int>(1); svc.Find(2); int Local(int a) => a; Local(1); Dispose(); }
    public void Dispose() { }
}
