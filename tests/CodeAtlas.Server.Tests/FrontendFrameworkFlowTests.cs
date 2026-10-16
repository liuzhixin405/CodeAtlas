using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · 前端（FrontendFrameworkFlow）：四个夹具——
/// TanStack Router + TanStack Query + 生成的 OpenAPI 客户端（createRoot 挂载、beforeLoad 守卫、QueryCache.onError）；
/// React Router + Redux + axios 拦截器（ReactDOM.render 旧 API、根组件 App 展开、&lt;Routes&gt; 布局路由里的守卫组件）；
/// Next.js App Router（middleware 守卫、布局链、布局之间的 Provider、error.tsx）；Vue（还不认）。
/// 行号与偏移都从夹具源码里现算，免得改了源码对不上。
/// </summary>
public class FrontendFrameworkFlowTests
{
    // ================= 夹具工具 =================

    private static MapSymbol Sym(long id, SymbolKind kind, string qn, string path, long? parent = null, (int Start, int End)? lines = null, string? vis = null) =>
        new(id, kind, qn, path, parent, lines?.Start ?? 1, lines?.End ?? 1, vis);

    private static MapSymbol Ext(long id, string qn) => new(id, SymbolKind.External, qn, "<external>", null);

    private static int Pos(string text, string needle, int occurrence = 0)
    {
        var p = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            p = text.IndexOf(needle, p + 1, StringComparison.Ordinal);
            if (p < 0) throw new ArgumentException("夹具里没有：" + needle);
        }
        return p;
    }

    private static int Line(string text, string needle, int occurrence = 0) => text[..Pos(text, needle, occurrence)].Count(c => c == '\n') + 1;

    /// <summary>函数的行范围：header 所在行最后一个 '{' 到配对的 '}'。</summary>
    private static (int, int) Lines(string text, string header)
    {
        var p = Pos(text, header);
        var lineEnd = text.IndexOf('\n', p);
        var open = text.LastIndexOf('{', lineEnd < 0 ? text.Length - 1 : lineEnd);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return (Line(text, header), text[..i].Count(c => c == '\n') + 1);
        }
        throw new ArgumentException("括号没配上：" + header);
    }

    private static MapEdge Call(long from, long to, string text, string needle, int occurrence = 0, string? via = null) =>
        new(from, to, EdgeKind.Calls, 0.95, via, Line(text, needle, occurrence), Pos(text, needle, occurrence));

    private static FrameworkCall FCall(long? id, string kind, string label, string qn, string text, string needle, string? ctx = null) =>
        new(id, kind, label, qn, Line(text, needle), Pos(text, needle), ctx, kind == "call", []);

    private static ArchNode Container(string key) => new(key, ArchNodeKind.Container, "frontend", key, key, ArchSource.Discovered, [], [key], []);

    private static FrameworkSources Sources(Dictionary<long, IReadOnlyList<FrameworkCall>> calls, Dictionary<string, string> files) => new()
    {
        Calls = id => calls.GetValueOrDefault(id) ?? [],
        Signature = _ => null,
        ReadFile = path => files.GetValueOrDefault(path),
    };

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static FlowNodeDto PageNode(FrameworkFlowDto f) => f.Flow.Root.Children.Single(c => c.Kind == "controllers");

    // ================= 夹具一：TanStack Router + Query + 生成的 OpenAPI 客户端 =================

    private const string ShopMain = """
        import { QueryCache, QueryClient, QueryClientProvider, MutationCache } from "@tanstack/react-query"
        import { createRouter, RouterProvider } from "@tanstack/react-router"
        import { StrictMode } from "react"
        import ReactDOM from "react-dom/client"
        import { ErrorBoundary } from "react-error-boundary"
        import { client } from "./client/client.gen"
        import { ThemeProvider } from "./components/theme-provider"
        import { Toaster } from "./components/toaster"
        import "./i18n"
        import "./index.css"
        import { routeTree } from "./routeTree.gen"

        // client.setConfig({ commented: true })
        client.setConfig({
          baseURL: import.meta.env.VITE_API_URL,
          auth: () => localStorage.getItem("access_token") || "",
        })

        const handleApiError = (error: Error) => {
          if ([401, 403].includes(error.response?.status ?? 0)) {
            localStorage.removeItem("access_token")
            window.location.href = "/login"
          }
        }
        const queryClient = new QueryClient({
          queryCache: new QueryCache({ onError: handleApiError }),
          mutationCache: new MutationCache({ onError: handleApiError }),
        })

        const router = createRouter({ routeTree })

        ReactDOM.createRoot(document.getElementById("root")!).render(
          <StrictMode>
            <ThemeProvider defaultTheme="dark">
              <ErrorBoundary fallback={<p>出错了</p>}>
                <QueryClientProvider client={queryClient}>
                  <RouterProvider router={router} />
                  <Toaster richColors />
                </QueryClientProvider>
              </ErrorBoundary>
            </ThemeProvider>
          </StrictMode>,
        )
        """;

    private const string ShopTheme = """
        import { createContext } from "react"
        const ThemeContext = createContext(null)
        export function ThemeProvider({ children, defaultTheme }) {
          const value = { theme: defaultTheme }
          return (
            <ThemeContext.Provider value={value}>
              {children}
            </ThemeContext.Provider>
          )
        }
        """;

    private const string ShopRoot = """
        import { createRootRoute, Outlet } from "@tanstack/react-router"
        import ErrorPage from "@/components/ErrorPage"

        export const Route = createRootRoute({
          component: () => (
            <>
              <Outlet />
            </>
          ),
          errorComponent: () => <ErrorPage />,
        })
        """;

    private const string ShopLayoutRoute = """
        import { createFileRoute, Outlet, redirect } from "@tanstack/react-router"
        import { isLoggedIn } from "@/hooks/auth"

        export const Route = createFileRoute("/_layout")({
          component: Layout,
          beforeLoad: async () => {
            if (!isLoggedIn()) {
              throw redirect({ to: "/login" })
            }
          },
        })

        function Layout() {
          return (
            <main>
              <Outlet />
            </main>
          )
        }
        """;

    private const string ShopIndexRoute = """
        import { createFileRoute } from "@tanstack/react-router"
        export const Route = createFileRoute("/_layout/")({ component: Dashboard })
        function Dashboard() {
          return <div />
        }
        """;

    private const string ShopOrdersRoute = """
        import { createFileRoute } from "@tanstack/react-router"
        export const Route = createFileRoute("/_layout/orders")({ component: Orders, loader: () => fetchOrders() })
        function Orders() {
          return <div />
        }
        """;

    private const string ShopLoginRoute = """
        import { createFileRoute } from "@tanstack/react-router"
        export const Route = createFileRoute("/login")({ component: Login })
        function Login() {
          return <form />
        }
        """;

    private const string ShopErrorPage = """
        export default function ErrorPage() {
          return <p>出错了</p>
        }
        """;

    private const string ShopGenClient = """
        // This file is auto-generated by @hey-api/openapi-ts

        import { createClient, createConfig } from './client';

        export const client = createClient(createConfig({ throwOnError: true }));
        """;

    private const string ShopGenAxios = """
        // This file is auto-generated by @hey-api/openapi-ts
        import axios from 'axios';
        export const createClient = (config) => {
          const instance = axios.create(config);
          return instance;
        };
        """;

    private const string ShopRouteTree = """
        /* eslint-disable */
        // This file was automatically generated by TanStack Router.
        export const routeTree = rootRoute._addFileChildren({})
        """;

    private static FrameworkInput ShopFixture()
    {
        const string w = "shop-web/src/";
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, w + "main.tsx", w + "main.tsx", null, (1, 44)),
            Sym(2, SymbolKind.Function, w + "main.tsx:handleApiError", w + "main.tsx", 1, Lines(ShopMain, "const handleApiError")),
            Sym(10, SymbolKind.Module, w + "components/theme-provider.tsx", w + "components/theme-provider.tsx", null, (1, 11)),
            Sym(11, SymbolKind.Function, w + "components/theme-provider.tsx:ThemeProvider", w + "components/theme-provider.tsx", 10, Lines(ShopTheme, "export function ThemeProvider"), "export"),
            Sym(12, SymbolKind.Module, w + "components/toaster.tsx", w + "components/toaster.tsx"),
            Sym(13, SymbolKind.Function, w + "components/toaster.tsx:Toaster", w + "components/toaster.tsx", 12, null, "export"),
            Sym(14, SymbolKind.Module, w + "i18n.ts", w + "i18n.ts"),
            Sym(15, SymbolKind.Module, w + "components/ErrorPage.tsx", w + "components/ErrorPage.tsx", null, (1, 3)),
            Sym(16, SymbolKind.Function, w + "components/ErrorPage.tsx:ErrorPage", w + "components/ErrorPage.tsx", 15, Lines(ShopErrorPage, "export default function ErrorPage"), "export default"),
            Sym(17, SymbolKind.Module, w + "hooks/auth.ts", w + "hooks/auth.ts"),
            Sym(18, SymbolKind.Function, w + "hooks/auth.ts:isLoggedIn", w + "hooks/auth.ts", 17, null, "export"),
            Sym(20, SymbolKind.Module, w + "routes/__root.tsx", w + "routes/__root.tsx", null, (1, 12)),
            Sym(21, SymbolKind.Module, w + "routes/_layout.tsx", w + "routes/_layout.tsx", null, (1, 20)),
            Sym(22, SymbolKind.Function, w + "routes/_layout.tsx:Layout", w + "routes/_layout.tsx", 21, Lines(ShopLayoutRoute, "function Layout")),
            Sym(23, SymbolKind.Module, w + "routes/_layout/index.tsx", w + "routes/_layout/index.tsx", null, (1, 5)),
            Sym(24, SymbolKind.Module, w + "routes/_layout/orders.tsx", w + "routes/_layout/orders.tsx", null, (1, 5)),
            Sym(25, SymbolKind.Module, w + "routes/login.tsx", w + "routes/login.tsx", null, (1, 5)),
            Sym(30, SymbolKind.Module, w + "client/client.gen.ts", w + "client/client.gen.ts", null, (1, 5)),
            Sym(31, SymbolKind.Module, w + "client/client/client.gen.ts", w + "client/client/client.gen.ts", null, (1, 6)),
            Sym(32, SymbolKind.Module, w + "routeTree.gen.ts", w + "routeTree.gen.ts", null, (1, 3)),
            Ext(900, "external:react-dom/client.createRoot"),
            Ext(901, "external:render"),
            Ext(902, "external:setConfig"),
            Ext(903, "external:@tanstack/react-query.QueryClient"),
            Ext(904, "external:@tanstack/react-router.createRouter"),
            Ext(905, "external:react.StrictMode"),
            Ext(906, "external:@tanstack/react-query.QueryClientProvider"),
            Ext(907, "external:@tanstack/react-router.RouterProvider"),
            Ext(908, "external:@tanstack/react-router.createRootRoute"),
            Ext(909, "external:@tanstack/react-router.createFileRoute"),
            Ext(910, "external:@tanstack/react-router.redirect"),
            Ext(911, "external:removeItem"),
            Ext(912, "external:axios"),
        ];
        List<MapEdge> edges =
        [
            Call(1, 902, ShopMain, "setConfig({\n"),
            Call(1, 903, ShopMain, "QueryClient({"),
            Call(1, 904, ShopMain, "createRouter({"),
            Call(1, 900, ShopMain, "createRoot("),
            Call(1, 901, ShopMain, "render("),
            Call(1, 905, ShopMain, "StrictMode>", 0, "render"),
            Call(1, 11, ShopMain, "ThemeProvider defaultTheme", 0, "render"),
            Call(1, 906, ShopMain, "QueryClientProvider client", 0, "render"),
            Call(1, 907, ShopMain, "RouterProvider router", 0, "render"),
            Call(1, 13, ShopMain, "Toaster richColors", 0, "render"),
            Call(2, 911, ShopMain, "removeItem"),
            Call(20, 908, ShopRoot, "createRootRoute({"),
            Call(21, 909, ShopLayoutRoute, "createFileRoute(\""),
            Call(21, 18, ShopLayoutRoute, "isLoggedIn()"),
            Call(21, 910, ShopLayoutRoute, "redirect({"),
            Call(23, 909, ShopIndexRoute, "createFileRoute(\""),
            Call(24, 909, ShopOrdersRoute, "createFileRoute(\""),
            Call(25, 909, ShopLoginRoute, "createFileRoute(\""),
            new(31, 912, EdgeKind.Imports),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [2] = [FCall(911, "external", "removeItem", "external:removeItem", ShopMain, "removeItem", CallContext.Cond)],
        };
        var files = new Dictionary<string, string>
        {
            ["shop-web/package.json"] = """
                { "name": "shop-web", "scripts": { "dev": "vite" },
                  "dependencies": { "@tanstack/react-query": "^5.0.0", "@tanstack/react-router": "^1.0.0", "axios": "1.7.0", "react": "^19.1.0", "react-dom": "^19.1.0" },
                  "devDependencies": { "@hey-api/openapi-ts": "0.60.0" } }
                """,
            [w + "main.tsx"] = ShopMain,
            [w + "components/theme-provider.tsx"] = ShopTheme,
            [w + "components/ErrorPage.tsx"] = ShopErrorPage,
            [w + "routes/__root.tsx"] = ShopRoot,
            [w + "routes/_layout.tsx"] = ShopLayoutRoute,
            [w + "routes/_layout/index.tsx"] = ShopIndexRoute,
            [w + "routes/_layout/orders.tsx"] = ShopOrdersRoute,
            [w + "routes/login.tsx"] = ShopLoginRoute,
            [w + "client/client.gen.ts"] = ShopGenClient,
            [w + "client/client/client.gen.ts"] = ShopGenAxios,
            [w + "routeTree.gen.ts"] = ShopRouteTree,
        };
        return new FrameworkInput(symbols, edges, Container("shop-web"), [], [], Sources(calls, files));
    }

    [Fact]
    public void TanStack_StagesInOrder_MountConfig_ProvidersOuterToInner()
    {
        var f = FrontendFrameworkFlow.Build(ShopFixture());

        Assert.Equal(["mount", "provider", "route", "controller", "request", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["挂载", "Provider", "路由", "页面", "请求封装", "收尾"], f.Stages.Select(s => s.Label));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        Assert.Empty(f.Handlers);
        var root = f.Flow.Root;
        Assert.Equal(("页面加载", "entry", "mount"), (root.Label, root.Kind, root.Actor));
        Assert.Equal("页面加载", f.Flow.EntryLabel);
        Assert.StartsWith("认出：React 19（react ^19.1.0）、TanStack Router、TanStack Query、axios、@hey-api/openapi-ts", f.Notes[0]);

        // 挂载：副作用导入（css 不算）、顶层调用（注释里的不算）按源码顺序，最后是 createRoot(...).render
        var mount = root.Children[0];
        Assert.Equal(("main.tsx", 1L, "call"), (mount.Label, mount.SymbolId!.Value, mount.Kind));
        Assert.Contains("React 19 createRoot", mount.Note);
        Assert.Contains("挂到 #root", mount.Note);
        Assert.Equal(["import \"./i18n\"", "client.setConfig", "new QueryClient", "createRouter", "createRoot(#root).render"], mount.Children.Select(c => c.Label));
        Assert.Equal(14L, mount.Children[0].SymbolId);
        Assert.Equal("请求客户端全局配置；配置：baseURL、auth；挂载前执行", mount.Children[1].Note);
        Assert.Equal(Line(ShopMain, "client.setConfig({\n"), mount.Children[1].Line);
        Assert.Contains("queryClient = …", mount.Children[2].Note);

        // Provider：JSX 由外到内；ThemeProvider 是仓库组件（它自己的 ThemeContext.Provider 不算）；RouterProvider 归路由；Toaster 只写进 Note
        var providers = root.Children.Where(c => c.Actor == "provider").ToList();
        Assert.Equal(["StrictMode", "ThemeProvider", "ErrorBoundary", "QueryClientProvider"], providers.Select(p => p.Label));
        Assert.Equal((11L, "call"), (providers[1].SymbolId!.Value, providers[1].Kind));
        Assert.StartsWith("出错边界：子树渲染抛错时显示兜底界面（见收尾）；react-error-boundary", providers[2].Note);
        Assert.Equal("external-group", providers[3].Kind);
        Assert.Equal("external:@tanstack/react-query.QueryClientProvider", providers[3].QualifiedName);
        Assert.Contains("client = queryClient", providers[3].Note);
        Assert.Contains("里面还挂着 Toaster", providers[3].Note);
        var qc = Assert.Single(providers[3].Children);
        Assert.Equal("new QueryClient", qc.Label);
        Assert.Equal($"查询客户端（main.tsx:{Line(ShopMain, "new QueryClient")}）；QueryCache.onError、MutationCache.onError 见收尾", qc.Note);
        Assert.DoesNotContain(All(root), n => n.Label.Contains("ThemeContext", StringComparison.Ordinal) || n.Label == "RouterProvider" || n.Label == "Toaster");
        // 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序
        Assert.Equal([0L, 11L, 0L, 0L], f.Stages.Single(s => s.Key == "provider").Symbols.Select(s => s.Id));
    }

    [Fact]
    public void TanStack_RootAndLayoutRoutes_BeforeLoadGuard_IndexRouteIsNotLayout()
    {
        var f = FrontendFrameworkFlow.Build(ShopFixture());
        var routes = f.Flow.Root.Children.Where(c => c.Actor == "route").ToList();

        Assert.Equal(["__root.tsx（根路由）", "/_layout（布局路由）"], routes.Select(r => r.Label));
        Assert.Equal(20L, routes[0].SymbolId);
        Assert.Contains("component 渲染 Outlet", routes[0].Note);
        var layout = routes[1];
        Assert.Equal($"无路径段的布局路由（_layout.tsx:{Line(ShopLayoutRoute, "createFileRoute(\"")}）；包住 2 个子路由：/_layout/、/_layout/orders", layout.Note);
        Assert.Equal(["beforeLoad（守卫）", "Layout"], layout.Children.Select(c => c.Label));
        var guard = layout.Children[0];
        Assert.Equal("守卫：!isLoggedIn() 时跳 /login（throw redirect）", guard.Note);
        Assert.Equal(["isLoggedIn", "redirect"], guard.Children.Select(c => c.Label));
        Assert.Equal((18L, "call"), (guard.Children[0].SymbolId!.Value, guard.Children[0].Kind));
        Assert.Equal("external-group", guard.Children[1].Kind);
        Assert.Equal((22L, "布局组件：渲染 Outlet（<Outlet/> 处放子路由）"), (layout.Children[1].SymbolId!.Value, layout.Children[1].Note));
        Assert.Contains(f.Notes, n => n == "路由：TanStack Router，5 个路由定义（5 个文件）；画了根路由与 1 个布局路由（按路径层级由外到内）；另有 1 个页面路由自带 beforeLoad / loader（只管自己，没画）");
    }

    [Fact]
    public void TanStack_PagePlaceholder_GeneratedClient_QueryOnErrorAndErrorComponentInTail()
    {
        var f = FrontendFrameworkFlow.Build(ShopFixture());
        var page = PageNode(f);

        Assert.Equal(("页面", "controller", 0, null), (page.Label, page.Actor, page.Count, page.SymbolId));
        // 收尾的先后：全局 onError 在出错边界前面（虽然 ErrorBoundary 在 Provider 树里先被看到）
        Assert.Equal(["client（生成的客户端）", "QueryCache.onError / MutationCache.onError → handleApiError", "ErrorBoundary", "errorComponent → ErrorPage"], page.Children.Select(c => c.Label));
        var client = page.Children[0];
        Assert.Equal(("request", "external-group"), (client.Actor, client.Kind));
        Assert.Equal($"@hey-api/openapi-ts 生成的 OpenAPI 客户端（client.gen.ts:{Line(ShopGenClient, "export const client")}）；底层 axios（client.gen.ts:{Line(ShopGenAxios, "axios.create")} axios.create）；"
                     + $"全局配置：main.tsx:{Line(ShopMain, "client.setConfig({\n")} client.setConfig（baseURL、auth）；令牌：配置里的 auth / TOKEN 在每个请求发出时带上；没有注册 interceptors", client.Note);

        var onError = page.Children[1];
        Assert.Equal(("tail", 2L), (onError.Actor, onError.SymbolId!.Value));
        Assert.Equal("TanStack Query 全局：查询 / 提交（mutation）出错时：涉及 401/403；清 access_token、跳 /login", onError.Note);
        Assert.Equal(["removeItem"], onError.Children.Select(c => c.Label));
        Assert.Equal(("tail", "external-group"), (page.Children[2].Actor, page.Children[2].Kind));
        Assert.Equal($"渲染出错时：ErrorBoundary（main.tsx:{Line(ShopMain, "<ErrorBoundary")}）显示兜底界面", page.Children[2].Note);
        var error = page.Children[3];
        Assert.Equal((16L, "tail"), (error.SymbolId!.Value, error.Actor));
        Assert.Contains("路由出错时显示（__root.tsx:", error.Note);
        // 生成的 axios.create 不单独成行；routeTree.gen.ts 不是 OpenAPI 客户端
        Assert.DoesNotContain(All(f.Flow.Root), n => n.Label == "axios.create" || n.Label.Contains("routeTree", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("页面：入口识别没有登记页面", StringComparison.Ordinal));
    }

    // ================= 夹具二：React Router + Redux + axios 拦截器（React 17 的 ReactDOM.render） =================

    private const string AcmeIndex = """
        import React from "react"
        import ReactDOM from "react-dom"
        import { Provider } from "react-redux"
        import { store } from "./store"
        import App from "./App"

        ReactDOM.render(
          <React.StrictMode>
            <Provider store={store}>
              <App />
            </Provider>
          </React.StrictMode>,
          document.getElementById("root"),
        )
        """;

    private const string AcmeApp = """
        import { BrowserRouter, Routes, Route } from "react-router-dom"
        import { AuthProvider } from "./auth/AuthProvider"
        import RequireAuth from "./auth/RequireAuth"
        import AppLayout from "./layout/AppLayout"
        import Home from "./pages/Home"
        import Orders, { ordersLoader } from "./pages/Orders"
        import Login from "./pages/Login"

        export default function App() {
          return (
            <BrowserRouter>
              <AuthProvider>
                <p>Don't panic</p>
                <Routes>
                  <Route path="/login" element={<Login />} />
                  <Route element={<RequireAuth><AppLayout /></RequireAuth>}>
                    <Route path="/" element={<Home />} />
                    <Route path="/orders" element={<Orders />} loader={ordersLoader} />
                  </Route>
                </Routes>
              </AuthProvider>
            </BrowserRouter>
          )
        }
        """;

    private const string AcmeAuthProvider = """
        import { createContext, useState } from "react"
        export const AuthContext = createContext(null)
        export function AuthProvider({ children }) {
          const [user, setUser] = useState(null)
          return <AuthContext.Provider value={{ user, setUser }}>{children}</AuthContext.Provider>
        }
        """;

    private const string AcmeRequireAuth = """
        import { Navigate } from "react-router-dom"
        import { useAuth } from "./useAuth"
        export default function RequireAuth({ children }) {
          const { user } = useAuth()
          if (!user) {
            return <Navigate to="/login" replace />
          }
          return children
        }
        """;

    private const string AcmeAppLayout = """
        import { Outlet } from "react-router-dom"
        export default function AppLayout() {
          return <section><Outlet /></section>
        }
        """;

    private const string AcmeHttp = """
        import axios from "axios"
        import toast from "react-hot-toast"
        import { getToken } from "../auth/token"

        const http = axios.create({ baseURL: "/api", timeout: 5000 })

        http.interceptors.request.use((config) => {
          config.headers.Authorization = `Bearer ${getToken()}`
          return config
        })

        http.interceptors.response.use(
          (res) => res.data,
          (error) => {
            if (error.response?.status === 401) {
              localStorage.removeItem("token")
              window.location.href = "/login"
            } else {
              toast.error("请求失败")
            }
            return Promise.reject(error)
          },
        )

        export default http
        """;

    private static FrameworkInput AcmeFixture()
    {
        const string w = "acme-admin/src/";
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, w + "index.jsx", w + "index.jsx", null, (1, 14)),
            Sym(2, SymbolKind.Module, w + "App.jsx", w + "App.jsx", null, (1, 25)),
            Sym(3, SymbolKind.Function, w + "App.jsx:App", w + "App.jsx", 2, Lines(AcmeApp, "export default function App"), "export default"),
            Sym(4, SymbolKind.Module, w + "auth/AuthProvider.jsx", w + "auth/AuthProvider.jsx", null, (1, 6)),
            Sym(5, SymbolKind.Function, w + "auth/AuthProvider.jsx:AuthProvider", w + "auth/AuthProvider.jsx", 4, Lines(AcmeAuthProvider, "export function AuthProvider"), "export"),
            Sym(6, SymbolKind.Module, w + "auth/RequireAuth.jsx", w + "auth/RequireAuth.jsx", null, (1, 9)),
            Sym(7, SymbolKind.Function, w + "auth/RequireAuth.jsx:RequireAuth", w + "auth/RequireAuth.jsx", 6, Lines(AcmeRequireAuth, "export default function RequireAuth"), "export default"),
            Sym(8, SymbolKind.Module, w + "layout/AppLayout.jsx", w + "layout/AppLayout.jsx", null, (1, 4)),
            Sym(9, SymbolKind.Function, w + "layout/AppLayout.jsx:AppLayout", w + "layout/AppLayout.jsx", 8, Lines(AcmeAppLayout, "export default function AppLayout"), "export default"),
            Sym(10, SymbolKind.Module, w + "api/http.js", w + "api/http.js", null, (1, 26)),
            Sym(11, SymbolKind.Module, w + "auth/token.js", w + "auth/token.js"),
            Sym(12, SymbolKind.Function, w + "auth/token.js:getToken", w + "auth/token.js", 11, null, "export"),
            Sym(13, SymbolKind.Function, w + "auth/useAuth.js:useAuth", w + "auth/useAuth.js", null, null, "export"),
            Ext(900, "external:react-dom.render"),
            Ext(901, "external:axios.create"),
            Ext(902, "external:use"),
            Ext(903, "external:removeItem"),
            Ext(904, "external:react-hot-toast.toast.error"),
            Ext(905, "external:axios"),
        ];
        List<MapEdge> edges =
        [
            Call(1, 900, AcmeIndex, "render("),
            Call(1, 3, AcmeIndex, "App />", 0, "render"),
            Call(10, 901, AcmeHttp, "create("),
            Call(10, 902, AcmeHttp, "use((config)"),
            Call(10, 12, AcmeHttp, "getToken()"),
            Call(10, 902, AcmeHttp, "use(\n"),
            Call(10, 903, AcmeHttp, "removeItem"),
            Call(10, 904, AcmeHttp, "toast.error"),
            new(10, 905, EdgeKind.Imports),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [7] = [FCall(13, "call", "useAuth", w + "auth/useAuth.js:useAuth", AcmeRequireAuth, "useAuth()")],
        };
        var files = new Dictionary<string, string>
        {
            ["acme-admin/package.json"] = """
                { "dependencies": { "@reduxjs/toolkit": "^2.0.0", "axios": "^1.6.0", "react": "^17.0.2", "react-dom": "^17.0.2", "react-redux": "^9.0.0", "react-router-dom": "^6.22.0" } }
                """,
            [w + "index.jsx"] = AcmeIndex,
            [w + "App.jsx"] = AcmeApp,
            [w + "auth/AuthProvider.jsx"] = AcmeAuthProvider,
            [w + "auth/RequireAuth.jsx"] = AcmeRequireAuth,
            [w + "layout/AppLayout.jsx"] = AcmeAppLayout,
            [w + "api/http.js"] = AcmeHttp,
        };
        return new FrameworkInput(symbols, edges, Container("acme-admin"), [], [], Sources(calls, files));
    }

    [Fact]
    public void ReactRouter_LegacyRender_AppExpanded_ProvidersAndRouterWrapper()
    {
        var f = FrontendFrameworkFlow.Build(AcmeFixture());

        Assert.Equal(["mount", "provider", "route", "controller", "request", "tail"], f.Stages.Select(s => s.Key));
        Assert.StartsWith("认出：React 17（react ^17.0.2）、React Router、Redux Toolkit、Redux、axios", f.Notes[0]);
        var root = f.Flow.Root;
        var mount = root.Children[0];
        Assert.Contains("ReactDOM.render（React 17 及以前的旧 API）", mount.Note);
        Assert.Contains("挂到 #root", mount.Note);
        Assert.Equal(["ReactDOM.render"], mount.Children.Select(c => c.Label));

        // 根组件 App 进去接着找：AuthProvider 标"经 App"，它自己的 AuthContext.Provider 不算；BrowserRouter 归路由
        var providers = root.Children.Where(c => c.Actor == "provider").ToList();
        Assert.Equal(["React.StrictMode", "Provider", "App", "AuthProvider"], providers.Select(p => p.Label));
        Assert.Contains("Redux：提供 store", providers[1].Note);
        Assert.Contains("store = store", providers[1].Note);
        Assert.Equal((3L, true), (providers[2].SymbolId!.Value, providers[2].Note!.StartsWith("根组件", StringComparison.Ordinal)));
        Assert.Equal(5L, providers[3].SymbolId);
        Assert.Contains("经 App", providers[3].Note);
    }

    [Fact]
    public void ReactRouter_LayoutRouteGuard_AxiosInterceptors_ErrorHandlingDescribed()
    {
        var f = FrontendFrameworkFlow.Build(AcmeFixture());
        var routes = f.Flow.Root.Children.Where(c => c.Actor == "route").ToList();

        // JSX 文本里的撇号（Don't）不当字符串：后面的 <Routes> 照样解析得出来
        Assert.Equal(["BrowserRouter", "Routes"], routes.Select(r => r.Label));
        Assert.Equal($"React Router 路由表（App.jsx:{Line(AcmeApp, "<Routes>")}）：4 条路由，1 个布局路由", routes[1].Note);
        var layout = Assert.Single(routes[1].Children);
        Assert.Equal("布局路由 （无路径）", layout.Label);
        Assert.StartsWith("包住 2 个子路由：/、/orders", layout.Note);
        Assert.Equal(["RequireAuth", "AppLayout"], layout.Children.Select(c => c.Label));
        Assert.Equal((7L, "守卫：!user 时跳 /login（Navigate）"), (layout.Children[0].SymbolId!.Value, layout.Children[0].Note));
        Assert.Equal(["useAuth"], layout.Children[0].Children.Select(c => c.Label));
        Assert.Equal((9L, "布局组件：<Outlet/> 处放子路由"), (layout.Children[1].SymbolId!.Value, layout.Children[1].Note));

        var page = PageNode(f);
        Assert.Equal(["axios.create", "http.interceptors.request.use", "http.interceptors.response.use（成功）", "http.interceptors.response.use（出错）"], page.Children.Select(c => c.Label));
        Assert.Equal(["request", "request", "tail", "tail"], page.Children.Select(c => c.Actor));
        Assert.Equal($"axios 实例（http.js:{Line(AcmeHttp, "axios.create")}）：baseURL、timeout", page.Children[0].Note);
        Assert.Equal($"请求拦截器（http.js:{Line(AcmeHttp, "http.interceptors.request")}）：每个请求发出前执行；设置请求头 Authorization", page.Children[1].Note);
        Assert.Equal(["getToken"], page.Children[1].Children.Select(c => c.Label));
        Assert.EndsWith("成功时把 response.data 拆出来给调用方", page.Children[2].Note);
        Assert.Equal($"响应出错时（http.js:{Line(AcmeHttp, "(error) =>")}）：涉及 401；清 token、跳 /login、弹出错提示、再抛给调用方", page.Children[3].Note);
        Assert.Equal(["removeItem"], page.Children[3].Children.Select(c => c.Label));
        Assert.Contains(f.Notes, n => n == "请求封装：1 个 axios 实例；1 个请求拦截器；响应拦截 2 段（进收尾）");
    }

    // ================= 夹具三：Next.js App Router =================

    private const string NextMiddleware = """
        import { NextResponse } from "next/server"

        export function middleware(req) {
          const session = req.cookies.get("session")
          if (!session) {
            if (req.nextUrl.pathname.startsWith("/en")) {
              return NextResponse.redirect(new URL("/en/login", req.url))
            }
            return NextResponse.redirect(new URL("/login", req.url))
          }
          return NextResponse.next()
        }

        export const config = { matcher: ["/((?!api|_next).*)"] }
        """;

    private const string NextRootLayout = """
        import { Providers } from "./providers"

        export default function RootLayout({ children }) {
          return (
            <html lang="en">
              <body>
                <Providers>{children}</Providers>
              </body>
            </html>
          )
        }
        """;

    private const string NextProviders = """
        "use client"
        import { useState } from "react"
        import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
        import { SessionProvider } from "next-auth/react"

        export function Providers({ children }) {
          const [queryClient] = useState(() => new QueryClient())
          return (
            <QueryClientProvider client={queryClient}>
              <SessionProvider>{children}</SessionProvider>
            </QueryClientProvider>
          )
        }
        """;

    private const string NextShopLayout = """
        import { Box } from "@mui/material"
        import { SidebarProvider } from "@/components/sidebar"
        import Header from "@/components/Header"

        export default function ShopLayout({ children }) {
          return (
            <Box>
              <SidebarProvider><Header /></SidebarProvider>
              <main>{children}</main>
            </Box>
          )
        }
        """;

    private const string NextError = """
        "use client"
        export default function ErrorPage({ reset }) {
          return <button onClick={() => reset()}>重试</button>
        }
        """;

    private const string NextApiRoute = """
        import axios from "axios"
        const upstream = axios.create({ baseURL: process.env.API_URL })
        export async function GET(req) {
          return Response.json(await upstream.get("/orders"))
        }
        """;

    private static FrameworkInput NextFixture()
    {
        const string w = "shop-next/";
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, w + "middleware.ts", w + "middleware.ts", null, (1, 14)),
            Sym(2, SymbolKind.Function, w + "middleware.ts:middleware", w + "middleware.ts", 1, Lines(NextMiddleware, "export function middleware"), "export"),
            Sym(3, SymbolKind.Module, w + "app/layout.tsx", w + "app/layout.tsx", null, (1, 11)),
            Sym(4, SymbolKind.Function, w + "app/layout.tsx:RootLayout", w + "app/layout.tsx", 3, Lines(NextRootLayout, "export default function RootLayout"), "export default"),
            Sym(5, SymbolKind.Module, w + "app/providers.tsx", w + "app/providers.tsx", null, (1, 13)),
            Sym(6, SymbolKind.Function, w + "app/providers.tsx:Providers", w + "app/providers.tsx", 5, Lines(NextProviders, "export function Providers"), "export"),
            Sym(7, SymbolKind.Module, w + "app/(shop)/layout.tsx", w + "app/(shop)/layout.tsx", null, (1, 12)),
            Sym(8, SymbolKind.Function, w + "app/(shop)/layout.tsx:ShopLayout", w + "app/(shop)/layout.tsx", 7, Lines(NextShopLayout, "export default function ShopLayout"), "export default"),
            Sym(9, SymbolKind.Module, w + "app/admin/layout.tsx", w + "app/admin/layout.tsx"),
            Sym(10, SymbolKind.Module, w + "app/error.tsx", w + "app/error.tsx", null, (1, 4)),
            Sym(11, SymbolKind.Function, w + "app/error.tsx:ErrorPage", w + "app/error.tsx", 10, Lines(NextError, "export default function ErrorPage"), "export default"),
            Sym(20, SymbolKind.Module, w + "app/(shop)/orders/page.tsx", w + "app/(shop)/orders/page.tsx"),
            Sym(21, SymbolKind.Function, w + "app/(shop)/orders/page.tsx:OrdersPage", w + "app/(shop)/orders/page.tsx", 20, null, "export default"),
            Sym(22, SymbolKind.Module, w + "app/(shop)/cart/page.tsx", w + "app/(shop)/cart/page.tsx"),
            Sym(23, SymbolKind.Function, w + "app/(shop)/cart/page.tsx:CartPage", w + "app/(shop)/cart/page.tsx", 22, null, "export default"),
            Sym(24, SymbolKind.Module, w + "app/login/page.tsx", w + "app/login/page.tsx"),
            Sym(25, SymbolKind.Function, w + "app/login/page.tsx:LoginPage", w + "app/login/page.tsx", 24, null, "export default"),
            Sym(26, SymbolKind.Module, w + "app/api/orders/route.ts", w + "app/api/orders/route.ts", null, (1, 6)),
            Sym(27, SymbolKind.Function, w + "app/api/orders/route.ts:GET", w + "app/api/orders/route.ts", 26, Lines(NextApiRoute, "export async function GET"), "export"),
            Ext(902, "external:axios.create"),
            Ext(900, "external:next/server.NextResponse.redirect"),
            Ext(901, "external:next/server.NextResponse.next"),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [2] = [FCall(900, "external", "NextResponse.redirect", "external:next/server.NextResponse.redirect", NextMiddleware, "redirect(new URL(\"/login\"", CallContext.Cond),
                   FCall(901, "external", "NextResponse.next", "external:next/server.NextResponse.next", NextMiddleware, "next()")],
        };
        var files = new Dictionary<string, string>
        {
            [w + "package.json"] = """{ "dependencies": { "next": "15.1.0", "react": "19.0.0", "@tanstack/react-query": "5.0.0", "next-auth": "5.0.0" } }""",
            [w + "middleware.ts"] = NextMiddleware,
            [w + "app/layout.tsx"] = NextRootLayout,
            [w + "app/providers.tsx"] = NextProviders,
            [w + "app/(shop)/layout.tsx"] = NextShopLayout,
            [w + "app/error.tsx"] = NextError,
            [w + "app/api/orders/route.ts"] = NextApiRoute,
        };
        List<EntryPoint> entries =
        [
            new(21, "shop-next", "frontend.route", "page", "/orders", null, w + "app/(shop)/orders/page.tsx", 1),
            new(23, "shop-next", "frontend.route", "page", "/cart", null, w + "app/(shop)/cart/page.tsx", 1),
            new(25, "shop-next", "frontend.route", "page", "/login", null, w + "app/login/page.tsx", 1),
            new(27, "shop-next", "frontend.route", "api-route", "/api/orders", "GET", w + "app/api/orders/route.ts", 4),
        ];
        return new FrameworkInput(symbols, [Call(26, 902, NextApiRoute, "create(")], Container("shop-next"), entries, [], Sources(calls, files));
    }

    [Fact]
    public void Next_MiddlewareFirst_LayoutChainWithProvidersBetween_ErrorTsxInTail()
    {
        var f = FrontendFrameworkFlow.Build(NextFixture());

        Assert.Equal(["route", "mount", "provider", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal("route", f.Flow.Root.Actor);
        // 布局之间夹着 Provider；ShopLayout 里 Box（组件库界面）穿过去，不包页面的 SidebarProvider 不算；admin 布局只包少数页面，不画
        Assert.Equal(["middleware（middleware.ts）", "RootLayout", "Providers", "QueryClientProvider", "SessionProvider", "ShopLayout", "页面"], f.Flow.Root.Children.Select(c => c.Label));
        Assert.Equal(["route", "mount", "provider", "provider", "provider", "mount", "controller"], f.Flow.Root.Children.Select(c => c.Actor));

        // 中间件：跳登录页、外面包的 if 最少的那处是主守卫（不是 /en 分支）；matcher 写进 Note
        var mw = f.Flow.Root.Children[0];
        Assert.Equal(2L, mw.SymbolId);
        Assert.Equal("Next 中间件（middleware.ts）：每个请求先在服务端过它，再渲染布局；matcher = [\"/((?!api|_next).*)\"]；守卫：!session 时跳 /login（NextResponse.redirect）", mw.Note);
        Assert.Equal(["NextResponse.redirect", "NextResponse.next"], mw.Children.Select(c => c.Label));

        var rootLayout = f.Flow.Root.Children[1];
        Assert.Equal((4L, "布局 app/layout.tsx：包住 3/3 个页面"), (rootLayout.SymbolId!.Value, rootLayout.Note));
        Assert.Equal("布局 app/(shop)/layout.tsx：包住 2/3 个页面", f.Flow.Root.Children[5].Note);
        var qcp = f.Flow.Root.Children[3];
        Assert.Contains("经 Providers", qcp.Note);
        Assert.Equal(["Providers → new QueryClient"], qcp.Children.Select(c => c.Label));
        Assert.Equal(6L, qcp.Children[0].SymbolId);
        Assert.DoesNotContain(All(f.Flow.Root), n => n.Label is "SidebarProvider" or "Box" or "Header");

        var page = PageNode(f);
        Assert.Equal(3, page.Count);
        Assert.Equal([21L, 23L, 25L], f.Stages.Single(s => s.Key == "controller").Symbols.Select(s => s.Id));
        var error = Assert.Single(page.Children);
        Assert.Equal(("error.tsx（app）", 11L, "tail"), (error.Label, error.SymbolId!.Value, error.Actor));
        Assert.Contains(f.Notes, n => n.StartsWith("挂载：Next.js App Router 没有 createRoot", StringComparison.Ordinal) && n.EndsWith("画了包住至少一半页面的 2 层布局（另有 1 个局部布局只包少数页面，没画）", StringComparison.Ordinal));
        // 服务端 API 路由里的 axios 实例不在页面的请求路径上：不画，Note 里交代
        Assert.Equal("没认出请求封装：页面这边没有 axios.create / interceptors / 生成的客户端 / fetch 封装；另有 1 个 axios 实例在服务端 API 路由 / 中间件里（不在页面的请求路径上，没画）",
            Assert.Single(f.Notes, n => n.StartsWith("没认出请求封装", StringComparison.Ordinal)));
        Assert.DoesNotContain(All(f.Flow.Root), n => n.Actor == "request");
    }

    // ================= 真实项目审计（2026-10-16）：代理路由、局部布局守卫、不是 Provider 的包装组件 =================

    private const string GwConfig = """
        export const PROXY_BASE = "/api/gw";
        """;

    private const string GwClient = """
        import axios from "axios"
        import { PROXY_BASE } from "@/config/api"
        export const client = axios.create({ baseURL: PROXY_BASE, timeout: 30000 })
        """;

    private const string GwRoute = """
        import axios from "axios"
        const createApi = (baseURL) => axios.create({ baseURL, timeout: 500000 })
        async function handle(req) {
          const token = await readToken(req)
          return runWithRefreshRetry(token, () => createApi(process.env.API_URL).request({ headers: { Authorization: `Bearer ${token}` } }))
        }
        export const GET = handle
        export const POST = handle
        """;

    private const string GuardedShopLayout = """
        import PageFrame from "@/components/PageFrame"
        import ServerSwitch from "@/components/ServerSwitch"

        export default function ShopLayout({ children }) {
          return (
            <PageFrame>
              <ServerSwitch>{children}</ServerSwitch>
            </PageFrame>
          )
        }
        """;

    private const string PageFrame = """
        export default function PageFrame({ children }) {
          return <div className="frame">{children}</div>
        }
        """;

    private const string ServerSwitch = """
        "use client"
        export default function ServerSwitch({ children }) {
          const router = useRouter()
          if (!current) {
            router.replace(target)
            return null
          }
          return <>{children}</>
        }
        """;

    private const string AdminLayout = """
        import { AdminPermissionGuard } from "@/components/AdminPermissionGuard"

        export default function AdminLayout({ children }) {
          return <AdminPermissionGuard category="Users" permission="Admin">{children}</AdminPermissionGuard>
        }
        """;

    /// <summary>NextFixture 加上：浏览器端 axios 的 baseURL 是常量 "/api/gw"（指向本应用的代理路由）、admin 局部布局套着守卫、(shop) 布局里两个不渲染 Provider 的包装组件。</summary>
    private static FrameworkInput GatewayFixture()
    {
        const string w = "shop-next/";
        var input = NextFixture();
        List<MapSymbol> extra =
        [
            Sym(40, SymbolKind.Module, w + "config/api.ts", w + "config/api.ts"),
            Sym(41, SymbolKind.Variable, w + "config/api.ts:PROXY_BASE", w + "config/api.ts", 40, null, "export"),
            Sym(42, SymbolKind.Module, w + "lib/api.ts", w + "lib/api.ts"),
            Sym(43, SymbolKind.Module, w + "app/api/gw/[...path]/route.ts", w + "app/api/gw/[...path]/route.ts", null, (1, 8)),
            Sym(44, SymbolKind.Function, w + "app/api/gw/[...path]/route.ts:handle", w + "app/api/gw/[...path]/route.ts", 43, Lines(GwRoute, "async function handle"), null),
            Sym(45, SymbolKind.Function, w + "app/api/gw/[...path]/route.ts:runWithRefreshRetry", w + "auth/refresh.ts", null, (1, 3), "export"),
            Sym(12, SymbolKind.Function, w + "app/admin/layout.tsx:AdminLayout", w + "app/admin/layout.tsx", 9, Lines(AdminLayout, "export default function AdminLayout"), "export default"),
            Sym(30, SymbolKind.Module, w + "app/admin/users/page.tsx", w + "app/admin/users/page.tsx"),
            Sym(31, SymbolKind.Function, w + "app/admin/users/page.tsx:UsersPage", w + "app/admin/users/page.tsx", 30, null, "export default"),
            Sym(50, SymbolKind.Module, w + "components/PageFrame.tsx", w + "components/PageFrame.tsx", null, (1, 3)),
            Sym(51, SymbolKind.Function, w + "components/PageFrame.tsx:PageFrame", w + "components/PageFrame.tsx", 50, Lines(PageFrame, "export default function PageFrame"), "export default"),
            Sym(52, SymbolKind.Module, w + "components/ServerSwitch.tsx", w + "components/ServerSwitch.tsx", null, (1, 9)),
            Sym(53, SymbolKind.Function, w + "components/ServerSwitch.tsx:ServerSwitch", w + "components/ServerSwitch.tsx", 52, Lines(ServerSwitch, "export default function ServerSwitch"), "export default"),
        ];
        var files = new Dictionary<string, string>
        {
            [w + "config/api.ts"] = GwConfig,
            [w + "lib/api.ts"] = GwClient,
            [w + "app/api/gw/[...path]/route.ts"] = GwRoute,
            [w + "app/(shop)/layout.tsx"] = GuardedShopLayout,
            [w + "app/admin/layout.tsx"] = AdminLayout,
            [w + "components/PageFrame.tsx"] = PageFrame,
            [w + "components/ServerSwitch.tsx"] = ServerSwitch,
        };
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [44] = [FCall(45, "call", "runWithRefreshRetry", w + "app/api/gw/[...path]/route.ts:runWithRefreshRetry", GwRoute, "runWithRefreshRetry(")],
        };
        var src = input.Sources;
        var sources = new FrameworkSources
        {
            Calls = id => calls.TryGetValue(id, out var c) ? c : src.Calls(id), Signature = src.Signature,
            ReadFile = p => files.TryGetValue(p, out var t) ? t : src.ReadFile(p),
        };
        // (shop) 布局换成包着两个包装组件的；它的符号行号跟着新文本走
        var symbols = input.Symbols.Where(s => s.Id != 8).Concat(extra)
            .Append(Sym(8, SymbolKind.Function, w + "app/(shop)/layout.tsx:ShopLayout", w + "app/(shop)/layout.tsx", 7, Lines(GuardedShopLayout, "export default function ShopLayout"), "export default")).ToList();
        List<MapEdge> edges = [.. input.Edges, Call(42, 902, GwClient, "create("), Call(43, 902, GwRoute, "create(")];
        List<EntryPoint> entries =
        [
            .. input.Entries,
            new(31, "shop-next", "frontend.route", "page", "/admin/users", null, w + "app/admin/users/page.tsx", 1),
            new(43, "shop-next", "frontend.route", "api-route", "/api/gw/{*path}", null, w + "app/api/gw/[...path]/route.ts", 1),
        ];
        return input with { Symbols = symbols, Edges = edges, Entries = entries, Sources = sources };
    }

    [Fact]
    public void Next_BrowserBaseUrlPointsAtOwnProxyRoute_ProxyHopDrawnInRequestStage()
    {
        var f = FrontendFrameworkFlow.Build(GatewayFixture());

        // 请求封装两行：浏览器端的 axios 实例、它 baseURL 指向的代理路由（服务端那一跳）
        var request = PageNode(f).Children.Where(n => n.Actor == "request").ToList();
        Assert.Equal(2, request.Count);
        var proxy = request[1];
        Assert.Equal("代理 /api/gw/{*path}", proxy.Label);
        Assert.Equal(44L, proxy.SymbolId);
        Assert.Equal("代理路由（服务端，app/api/gw/[...path]/route.ts）：浏览器端 axios 的 baseURL = PROXY_BASE = \"/api/gw\"（api.ts:3），每个请求先到这里；"
                     + "服务端 axios 实例（route.ts:2）转发到后端、注入 Authorization: Bearer 令牌、令牌过期 / 401 时刷新重试（runWithRefreshRetry）", proxy.Note);
        Assert.Equal(["runWithRefreshRetry"], proxy.Children.Select(c => c.Label));
        // 代理路由里的服务端 axios 不再说成"不在页面的请求路径上"：只剩 app/api/orders 那一个
        Assert.Contains(f.Notes, n => n.Contains("另有 1 个 axios 实例在服务端 API 路由 / 中间件里（不在页面的请求路径上，没画）", StringComparison.Ordinal));
        Assert.Contains("请求封装：浏览器端的 baseURL 指向本应用的代理路由 /api/gw/{*path}（route.ts），再由服务端转发到后端——代理这一跳画在请求封装里", f.Notes);
    }

    [Fact]
    public void Next_LocalLayoutGuardsListed_WrappersThatRenderNoProviderAreNotProviders()
    {
        var f = FrontendFrameworkFlow.Build(GatewayFixture());

        // admin 局部布局只包 1/4 个页面，不画进骨架，但它套着守卫：说明里点名（守卫组件、属性、前缀、页面数）
        Assert.Contains("守卫：1 个局部布局带守卫，共包住 1 个页面（只管它下面的路由，没画进骨架）：app/admin（<AdminPermissionGuard category=Users permission=Admin>，1 个页面）", f.Notes);
        // PageFrame 只是包一层 div（布局容器，挂载列）；ServerSwitch 会 router.replace 跳走（守卫，路由列）——都不是 Provider
        var rows = f.Flow.Root.Children;
        var frame = rows.Single(c => c.Label == "PageFrame");
        Assert.Equal(("mount", 51L), (frame.Actor, frame.SymbolId!.Value));
        Assert.StartsWith("布局容器（不是 Provider：不渲染 Context.Provider）", frame.Note);
        var sw = rows.Single(c => c.Label == "ServerSwitch");
        Assert.Equal(("route", 53L), (sw.Actor, sw.SymbolId!.Value));
        Assert.StartsWith("守卫 / 跳转：条件不满足时 router.replace(…（ServerSwitch.tsx:5），否则渲染子树（不是 Provider", sw.Note);
        // 真 Provider 照旧
        Assert.Equal("provider", rows.Single(c => c.Label == "QueryClientProvider").Actor);
    }

    // ================= 还不认的框架 =================

    [Fact]
    public void Vue_NotRecognizedYet_OnlyNote()
    {
        var files = new Dictionary<string, string> { ["shop-vue/package.json"] = """{ "dependencies": { "vue": "^3.4.0", "vue-router": "^4.0.0" } }""" };
        var f = FrontendFrameworkFlow.Build(new FrameworkInput([Sym(1, SymbolKind.Module, "shop-vue/src/main.ts", "shop-vue/src/main.ts")], [], Container("shop-vue"), [], [], Sources([], files)));

        Assert.Empty(f.Stages);
        Assert.Equal(["Vue 的框架流程还不认（package.json 里有 vue）：先只做了 React / Next.js"], f.Notes);
        Assert.Empty(f.Flow.Root.Children);
    }

    [Fact]
    public void DispatchedFromFrameworkFlowBuilder_ByFrontendSubKind()
    {
        var f = FrameworkFlowBuilder.Build(ShopFixture());
        Assert.Equal("页面加载", f.Flow.Root.Label);
        Assert.Equal("shop-web", f.ContainerKey);
    }
}
