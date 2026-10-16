using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 单页应用的页面入口（EntryDiscovery.Spa.cs）：TanStack Router 文件路由、React Router 的 JSX 与对象写法。
/// 入口落在路由指向的页面组件（同文件 → 同容器唯一导出），布局路由与 __root 不算页面。
/// </summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> SpaFiles = new()
    {
        ["shop/src/routes/__root.tsx"] = ["export const Route = createRootRoute({ component: Root })"],
        ["shop/src/routes/_layout.tsx"] = ["export const Route = createFileRoute(\"/_layout\")({ component: Layout, beforeLoad: guard })"],
        ["shop/src/routes/_layout/index.tsx"] = ["import { createFileRoute } from \"@tanstack/react-router\"", "export const Route = createFileRoute(\"/_layout/\")({", "  component: Dashboard,", "})"],
        ["shop/src/routes/_layout/items.$id.tsx"] = ["export const Route = createFileRoute(\"/_layout/items/$id\")({ component: ItemDetail })", "function ItemDetail() {}"],
        ["shop/src/routes/login.tsx"] =
        [
            "// export const Route = createFileRoute(\"/commented\")({ component: Login })",
            "export const Route = createFileRoute(\"/login\")({",
            "  component: Login,",
            "  beforeLoad: async () => { if (isLoggedIn()) throw redirect({ to: \"/\" }) },",
            "})",
            "function Login() {}",
        ],
        ["shop/src/components/Dashboard.tsx"] = ["export function Dashboard() {}"],
        // React Router：JSX 嵌套 + 对象写法
        ["admin/src/App.tsx"] =
        [
            "export default function App() {",                                   // 1
            "  return (",                                                         // 2
            "    <Routes>",                                                       // 3
            "      <Route path=\"/login\" element={<Login />} />",                // 4：布局前面的同级页面（布局关闭时不能被一起作废）
            "      <Route path=\"/\" element={<AdminLayout />}>",                 // 5：有子路由 → 布局
            "        <Route index element={<Home />} />",                         // 6
            "        <Route path=\"orders\" element={<Orders />} />",             // 7
            "        <Route path=\"orders/:orderId\" element={<OrderDetail />} />", // 8
            "      </Route>",                                                     // 9
            "      <Route path=\"*\" Component={NotFound} />",                    // 10
            "    </Routes>",                                                      // 11
            "  );",                                                               // 12
            "}",                                                                  // 13
        ],
        ["admin/src/router.tsx"] =
        [
            "export const router = createBrowserRouter([",                                    // 1
            "  { path: \"/reports\", element: <ReportsLayout />, children: [",                 // 2
            "    { index: true, element: <ReportList /> },",                                  // 3
            "    { path: \":reportId\", element: <ReportDetail />, loader: loadReport },",     // 4
            "  ] },",                                                                         // 5
            "  { path: \"/settings\", Component: Settings },",                                 // 6
            "]);",                                                                            // 7
        ],
    };

    private static readonly List<MapSymbol> SpaSymbols =
    [
        Sym(900, SymbolKind.Module, "shop/src/routes/__root.tsx", "shop/src/routes/__root.tsx", null, 1, 1),
        Sym(901, SymbolKind.Module, "shop/src/routes/_layout.tsx", "shop/src/routes/_layout.tsx", null, 1, 1),
        Sym(902, SymbolKind.Module, "shop/src/routes/_layout/index.tsx", "shop/src/routes/_layout/index.tsx", null, 1, 4),
        Sym(903, SymbolKind.Module, "shop/src/routes/_layout/items.$id.tsx", "shop/src/routes/_layout/items.$id.tsx", null, 1, 2),
        Sym(904, SymbolKind.Function, "shop/src/routes/_layout/items.$id.tsx:ItemDetail", "shop/src/routes/_layout/items.$id.tsx", 903, 2, 2),
        Sym(905, SymbolKind.Module, "shop/src/routes/login.tsx", "shop/src/routes/login.tsx", null, 1, 6),
        Sym(906, SymbolKind.Function, "shop/src/routes/login.tsx:Login", "shop/src/routes/login.tsx", 905, 6, 6),
        Sym(907, SymbolKind.Module, "shop/src/components/Dashboard.tsx", "shop/src/components/Dashboard.tsx", null, 1, 1),
        Sym(908, SymbolKind.Function, "shop/src/components/Dashboard.tsx:Dashboard", "shop/src/components/Dashboard.tsx", 907, 1, 1, "export"),
        Sym(910, SymbolKind.Module, "admin/src/App.tsx", "admin/src/App.tsx", null, 1, 13),
        Sym(911, SymbolKind.Function, "admin/src/App.tsx:App", "admin/src/App.tsx", 910, 1, 13, "export default"),
        Sym(917, SymbolKind.Function, "admin/src/pages.tsx:Login", "admin/src/pages.tsx", null, 6, 6, "export"),
        Sym(912, SymbolKind.Function, "admin/src/pages.tsx:Home", "admin/src/pages.tsx", null, 1, 1, "export"),
        Sym(913, SymbolKind.Function, "admin/src/pages.tsx:Orders", "admin/src/pages.tsx", null, 2, 2, "export"),
        Sym(914, SymbolKind.Function, "admin/src/pages.tsx:OrderDetail", "admin/src/pages.tsx", null, 3, 3, "export"),
        Sym(915, SymbolKind.Function, "admin/src/pages.tsx:NotFound", "admin/src/pages.tsx", null, 4, 4, "export"),
        Sym(916, SymbolKind.Function, "admin/src/pages.tsx:AdminLayout", "admin/src/pages.tsx", null, 5, 5, "export"),
        Sym(920, SymbolKind.Module, "admin/src/router.tsx", "admin/src/router.tsx", null, 1, 7),
        Sym(921, SymbolKind.Function, "admin/src/reports.tsx:ReportList", "admin/src/reports.tsx", null, 1, 1, "export"),
        Sym(922, SymbolKind.Function, "admin/src/reports.tsx:ReportDetail", "admin/src/reports.tsx", null, 2, 2, "export"),
        Sym(923, SymbolKind.Function, "admin/src/reports.tsx:Settings", "admin/src/reports.tsx", null, 3, 3, "export"),
        // 同名导出两个：认不出是哪个，不登记
        Sym(924, SymbolKind.Function, "admin/src/a.tsx:ReportsLayout", "admin/src/a.tsx", null, 1, 1, "export"),
    ];

    [Fact]
    public void Spa_TanStackFileRoutes_And_ReactRouter_JsxAndObjects_BecomePageEntries()
    {
        var entries = EntryDiscovery.Discover(SpaSymbols, [], [Container("shop", "frontend", "shop"), Container("admin", "frontend", "admin")],
            p => SpaFiles.TryGetValue(p, out var l) ? l : []);
        var pages = entries.Where(e => e.Kind == "page").OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal)
            .Select(e => (e.ContainerKey, e.Route, e.SymbolId, e.File, e.Line)).ToList();
        Assert.Equal(new (string, string?, long, string, int)[]
        {
            ("admin", "/", 912, "admin/src/App.tsx", 6),                          // index 路由取父路径
            ("admin", "/login", 917, "admin/src/App.tsx", 4),
            ("admin", "/orders", 913, "admin/src/App.tsx", 7),                    // 相对路径拼父路径
            ("admin", "/orders/{orderId}", 914, "admin/src/App.tsx", 8),
            ("admin", "/reports", 921, "admin/src/router.tsx", 3),
            ("admin", "/reports/{reportId}", 922, "admin/src/router.tsx", 4),
            ("admin", "/settings", 923, "admin/src/router.tsx", 6),
            ("admin", "/{*}", 915, "admin/src/App.tsx", 10),                       // Component={X}
            ("shop", "/", 908, "shop/src/routes/_layout/index.tsx", 2),            // 无路径布局段去掉；组件在别的文件（唯一导出）
            ("shop", "/items/{id}", 904, "shop/src/routes/_layout/items.$id.tsx", 1),
            ("shop", "/login", 906, "shop/src/routes/login.tsx", 2),               // 注释里那条不算
        }, pages);
        // 布局（AdminLayout、ReportsLayout、_layout）与 __root 不算页面
        Assert.DoesNotContain(entries, e => e.SymbolId is 916 or 924);
        Assert.All(entries.Where(e => e.Kind == "page"), e => Assert.Equal("frontend.route", e.RuleId));
    }

    [Theory]
    [InlineData("/_layout", null)]
    [InlineData("__root", null)]
    [InlineData("/_layout/", "/")]
    [InlineData("/_auth/settings/$tab", "/settings/{tab}")]
    [InlineData("/files/$", "/files/{*}")]
    public void TanStackRoute_StripsPathlessLayouts_AndConvertsParams(string raw, string? expected) =>
        Assert.Equal(expected, EntryDiscovery.TanStackRoute(raw));
}
