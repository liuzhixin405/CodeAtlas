using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// Minimal API 的 lambda 处理器落点、同一外层方法上多条路由的流程截取（PickEntry / FlowEdges）、MVC 约定路由、Blazor @page、gRPC 健康检查排除。
/// </summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> EndpointFiles = new()
    {
        ["Shop.Api/Endpoints.cs"] =
        [
            "public static class Endpoints",                                                    // 1
            "{",                                                                                // 2
            "    public static void Map(WebApplication app)",                                   // 3
            "    {",                                                                            // 4
            "        app.MapGet(\"/api/orders\", (OrderService s, int page) => s.List(page));", // 5：注入服务上的调用
            "        app.MapPost(\"/api/orders\", async (OrderService s, Order o) =>",          // 6
            "        {",                                                                        // 7
            "            Validate(o);",                                                         // 8：先调的本地校验不是处理器
            "            return await s.Create(o);",                                            // 9
            "        });",                                                                      // 10
            "        app.MapGet(\"/api/ping\", (ClaimsPrincipal user) =>",                      // 11
            "        {",                                                                        // 12
            "            var id = user.GetUserId();",                                           // 13：多语句、没有注入服务上的调用
            "            return id;",                                                           // 14
            "        });",                                                                      // 15
            "        app.MapGet(\"/api/pong\", () => Results.Ok(\"it's (fine\"));",             // 16：字符串里的括号 / 撇号
            "        app.MapPut(\"/api/pay\", (IPayments p, int id) => p.Pay(id));",            // 17：接口方法 → 唯一实现
            "    }",                                                                            // 18
            "    static void Validate(Order o) { }",                                            // 19
            "}",                                                                                // 20
        ],
        ["Shop.App/OrderService.cs"] =
        [
            "public class OrderService",                                       // 1
            "{",                                                               // 2
            "    public Task<int> List(int page) => Task.FromResult(page);",  // 3
            "    public Task<int> Create(Order o) => Task.FromResult(1);",    // 4
            "}",                                                               // 5
            "public static class UserExt { public static string GetUserId(this ClaimsPrincipal u) => \"\"; }", // 6
            "public interface IPayments { Task Pay(int id); }",                // 7
            "public class Payments : IPayments { public Task Pay(int id) => Task.CompletedTask; }", // 8
        ],
        ["Shop.Api/Program.cs"] =
        [
            "app.MapDefaultControllerRoute();", // 1
        ],
        ["Shop.Api/AccountController.cs"] =
        [
            "public class AccountController : Controller",  // 1
            "{",                                            // 2
            "    [HttpGet]",                                // 3
            "    public IActionResult Login() => View();",  // 4：没有特性路由 → 约定路由 /Account/Login
            "    [HttpGet(\"/account/me\")]",               // 5：有特性路由的照旧
            "    public IActionResult Me() => View();",     // 6
            "}",                                            // 7
        ],
        ["Shop.Web/Pages/Cart.razor"] =
        [
            "﻿@page \"/cart\"",       // 1：文件开头带 BOM
            "@page \"/basket/\"",          // 2：一个组件多条路由
            "<h1>Cart</h1>",               // 3
        ],
        ["Shop.Cart/Health.cs"] =
        [
            "public class HealthCheckService : HealthBase { public override Task<HealthCheckResponse> Check(HealthCheckRequest r, ServerCallContext c) => default!; }", // 1
            "public class CartService : CartBase { public override Task<Cart> GetCart(GetCartRequest r, ServerCallContext c) => default!; }", // 2
        ],
    };

    private static readonly List<MapSymbol> EndpointSymbols =
    [
        Sym(400, SymbolKind.Class, "Endpoints", "Shop.Api/Endpoints.cs", null, 1, 20, "public"),
        Sym(401, SymbolKind.Method, "Endpoints.Map(WebApplication)", "Shop.Api/Endpoints.cs", 400, 3, 18, "public"),
        Sym(402, SymbolKind.Method, "Endpoints.Validate(Order)", "Shop.Api/Endpoints.cs", 400, 19, 19, "private"),
        Sym(410, SymbolKind.Class, "Shop.App.OrderService", "Shop.App/OrderService.cs", null, 1, 5, "public"),
        Sym(411, SymbolKind.Method, "Shop.App.OrderService.List(int)", "Shop.App/OrderService.cs", 410, 3, 3, "public"),
        Sym(412, SymbolKind.Method, "Shop.App.OrderService.Create(Order)", "Shop.App/OrderService.cs", 410, 4, 4, "public"),
        Sym(413, SymbolKind.Class, "Shop.App.UserExt", "Shop.App/OrderService.cs", null, 6, 6, "public"),
        Sym(414, SymbolKind.Method, "Shop.App.UserExt.GetUserId(ClaimsPrincipal)", "Shop.App/OrderService.cs", 413, 6, 6, "public"),
        Sym(415, SymbolKind.Interface, "Shop.App.IPayments", "Shop.App/OrderService.cs", null, 7, 7, "public"),
        Sym(416, SymbolKind.Method, "Shop.App.IPayments.Pay(int)", "Shop.App/OrderService.cs", 415, 7, 7, "public"),
        Sym(417, SymbolKind.Class, "Shop.App.Payments", "Shop.App/OrderService.cs", null, 8, 8, "public"),
        Sym(418, SymbolKind.Method, "Shop.App.Payments.Pay(int)", "Shop.App/OrderService.cs", 417, 8, 8, "public"),
        Sym(420, SymbolKind.Function, "Program.<Main>$(string[])", "Shop.Api/Program.cs", null, 1, 1),
        Sym(421, SymbolKind.Class, "Shop.Api.AccountController", "Shop.Api/AccountController.cs", null, 1, 7, "public"),
        Sym(422, SymbolKind.Method, "Shop.Api.AccountController.Login()", "Shop.Api/AccountController.cs", 421, 4, 4, "public"),
        Sym(423, SymbolKind.Method, "Shop.Api.AccountController.Me()", "Shop.Api/AccountController.cs", 421, 6, 6, "public"),
        Sym(430, SymbolKind.Module, "Shop.Web/Pages/Cart.razor", "Shop.Web/Pages/Cart.razor", null, 1, 3),
        Sym(440, SymbolKind.Class, "Shop.Cart.HealthCheckService", "Shop.Cart/Health.cs", null, 1, 1, "public"),
        Sym(441, SymbolKind.Method, "Shop.Cart.HealthCheckService.Check(HealthCheckRequest, ServerCallContext)", "Shop.Cart/Health.cs", 440, 1, 1, "public"),
        Sym(442, SymbolKind.Class, "Shop.Cart.CartService", "Shop.Cart/Health.cs", null, 2, 2, "public"),
        Sym(443, SymbolKind.Method, "Shop.Cart.CartService.GetCart(GetCartRequest, ServerCallContext)", "Shop.Cart/Health.cs", 442, 2, 2, "public"),
        Sym(490, SymbolKind.External, "external:Controller", "<external>", null, 0, 0),
        Sym(491, SymbolKind.External, "external:Grpc.Health.V1.Health.HealthBase", "<external>", null, 0, 0),
        Sym(492, SymbolKind.External, "external:Shop.Grpc.Cart.CartBase", "<external>", null, 0, 0),
        Sym(493, SymbolKind.External, "external:Results.Ok(object)", "<external>", null, 0, 0),
    ];

    private static readonly List<MapEdge> EndpointEdges =
    [
        new(421, 490, EdgeKind.Inherits),
        new(417, 415, EdgeKind.Implements),
        new(440, 491, EdgeKind.Inherits),
        new(442, 492, EdgeKind.Inherits),
        new(401, 411, EdgeKind.Calls, Line: 5), new(401, 402, EdgeKind.Calls, Line: 8), new(401, 412, EdgeKind.Calls, Line: 9),
        new(401, 414, EdgeKind.Calls, Line: 13), new(401, 493, EdgeKind.Calls, Line: 16), new(401, 416, EdgeKind.Calls, Line: 17),
    ];

    private static readonly List<ArchNode> EndpointContainers =
    [
        Container("Shop.Api", "dotnet-web", "Shop.Api", "Shop.App"),
        Container("Shop.Web", "dotnet-web", "Shop.Web"),
        Container("Shop.Cart", "dotnet-web", "Shop.Cart"),
    ];

    private static List<EntryPoint> EndpointEntries() =>
        EntryDiscovery.Discover(EndpointSymbols, EndpointEdges, EndpointContainers, p => EndpointFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void MinimalApi_LambdaHandlers_LandOnTheServiceMethodTheyCall()
    {
        var minimal = EndpointEntries().Where(e => e.RuleId == "aspnet.minimal").OrderBy(e => e.Line)
            .Select(e => (e.HttpMethod!, e.Route!, e.SymbolId, e.Line)).ToList();
        Assert.Equal(new[]
        {
            ("GET", "/api/orders", 411L, 5),   // s.List(page)
            ("POST", "/api/orders", 412L, 6),  // 多语句：注入服务上的 s.Create，不是先调的 Validate
            ("GET", "/api/ping", 401L, 11),    // 多语句、只调了扩展方法：留在外层方法
            ("GET", "/api/pong", 401L, 16),    // 只调框架：留在外层方法
            ("PUT", "/api/pay", 418L, 17),     // 接口 IPayments.Pay → 唯一实现 Payments.Pay
        }, minimal);
    }

    [Fact]
    public void Flow_OnOuterMethod_TakesOnlyThatRoutesLines_AndRouteCanBePicked()
    {
        var entries = EndpointEntries();
        // 同一外层方法 Map 上挂着 /api/ping 与 /api/pong：按 (符号, 路由, 方法) 取，根节点只留它自己那段 lambda 的调用
        Assert.Equal(EntryDiscovery.PickEntry(entries, 401), entries.First(e => e.SymbolId == 401)); // 不给路由取第一条
        var ping = EntryDiscovery.PickEntry(entries, 401, "/api/ping", "GET")!;
        Assert.Equal((401L, 11), (ping.SymbolId, ping.Line));
        var pingEdges = EntryDiscovery.FlowEdges(ping, entries, EndpointSymbols, EndpointEdges).Where(e => e.FromId == 401).Select(e => e.ToId).ToList();
        Assert.Equal(new[] { 414L }, pingEdges); // [11, 16)：只有 GetUserId，不混进 List / Validate / Create / Ok / Pay
        var pong = EntryDiscovery.PickEntry(entries, 401, "/api/pong")!;
        Assert.Equal(new[] { 493L }, EntryDiscovery.FlowEdges(pong, entries, EndpointSymbols, EndpointEdges).Where(e => e.FromId == 401).Select(e => e.ToId));
        Assert.Null(EntryDiscovery.PickEntry(entries, 401, "/api/ping", "POST"));
        // 入口落在处理方法本身上：边原样
        var list = EntryDiscovery.PickEntry(entries, 411)!;
        Assert.Same(EndpointEdges, EntryDiscovery.FlowEdges(list, entries, EndpointSymbols, EndpointEdges));
    }

    [Fact]
    public void Mvc_ConventionalRoute_ForActionsWithoutAttributeRoutes()
    {
        var http = EndpointEntries().Where(e => e.RuleId == "aspnet.controller").ToDictionary(e => e.SymbolId, e => (e.Route!, e.HttpMethod));
        Assert.Equal(("/Account/Login", "GET"), http[422]); // MapDefaultControllerRoute → {controller=Home}/{action=Index}/{id?}
        Assert.Equal(("/account/me", "GET"), http[423]);
    }

    [Theory]
    [InlineData("{controller=Home}/{action=Index}/{id?}", "AccountController", "Login", "/Account/Login")]
    [InlineData("{area:exists}/{controller}/{action}", "OrdersController", "List", "/Orders/List")]
    [InlineData("admin/{controller=Home}/{action=Index}/{id}", "UsersController", "Edit", "/admin/Users/Edit/{id}")]
    public void ConventionalRoute_Template(string pattern, string cls, string method, string expected) =>
        Assert.Equal(expected, EntryDiscovery.ConventionalRoute(pattern, cls, method));

    [Fact]
    public void Blazor_PageDirectives_InIndexedRazorFiles()
    {
        var pages = EndpointEntries().Where(e => e.RuleId == "blazor.page").Select(e => (e.ContainerKey, e.Kind, e.Route!, e.SymbolId, e.Line)).ToList();
        Assert.Equal(new[] { ("Shop.Web", "page", "/basket", 430L, 2), ("Shop.Web", "page", "/cart", 430L, 1) }, pages.OrderBy(p => p.Item3, StringComparer.Ordinal));
    }

    [Fact]
    public void Grpc_HealthCheckService_IsNotABusinessEntry()
    {
        var grpc = EndpointEntries().Where(e => e.RuleId == "grpc.service").Select(e => (e.Route!, e.SymbolId)).ToList();
        Assert.Equal(new[] { ("Cart/GetCart", 443L) }, grpc); // Health/Check（HealthBase）不算
    }
}
