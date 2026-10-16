using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · Python（PythonFrameworkFlow）：三个夹具——FastAPI（add_middleware 倒序、@app.middleware 的 call_next 拆分、BaseHTTPMiddleware、
/// 依赖别名 / 内联 Annotated / = Depends() / 装饰器 / APIRouter / include_router / 应用级依赖、依赖闭包计数、yield 依赖、exception_handler），
/// Django + DRF（MIDDLEWARE 顺序、新式 __call__ / MiddlewareMixin / 函数式中间件、REST_FRAMEWORK 各键、DRF 默认值、视图覆盖计数），
/// Flask（wsgi_app 包装、before / after / teardown / errorhandler 的顺序、蓝图）；外加没认出框架的容器与源码剥注释。
/// </summary>
public class PythonFrameworkFlowTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null, int start = 1, int end = 1) => new(id, kind, qname, path, parent, start, end);

    /// <summary>第 line 行（1 起）的调用：夹具里 pos = (line - 1) * 100 + 列，Line = pos / 100 + 1。</summary>
    private static int At(int line, int col = 0) => (line - 1) * 100 + col;

    private static FrameworkCall Call(long? id, string label, string qn, int pos, string? ctx = null, string kind = "call") =>
        new(id, kind, label, qn, pos / 100 + 1, pos, ctx, true, []);

    private static FrameworkCall Ext(string label, string qn, int pos, string? ctx = null) => new(null, "external", label, qn, pos / 100 + 1, pos, ctx, false, []);

    private static FrameworkCall Unresolved(string name, int pos, string? ctx = null) => new(null, "unresolved", name, name, pos / 100 + 1, pos, ctx, false, []);

    private static ArchNode Container(string key, params string[] members) =>
        new(key, ArchNodeKind.Container, "python-service", key, members[0], ArchSource.Discovered, [], members, []);

    private static FrameworkSources Sources(Dictionary<long, IReadOnlyList<FrameworkCall>> calls, Dictionary<long, string> signatures, Dictionary<string, string> files) => new()
    {
        Calls = id => calls.GetValueOrDefault(id) ?? [],
        Signature = id => signatures.GetValueOrDefault(id),
        ReadFile = path => files.GetValueOrDefault(path),
    };

    private static int LineOf(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "夹具里找不到 " + marker);
        return text[..i].Count(c => c == '\n') + 1;
    }

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static FlowNodeDto Child(FlowNodeDto n, string label) => n.Children.Single(c => c.Label == label);

    // ================= 夹具一：FastAPI =================

    private const string ApiMain = """"
        """Acme 商城 API。

        app.add_middleware(DocstringMiddleware)
        """
        from fastapi import Depends, FastAPI
        from starlette.middleware.cors import CORSMiddleware

        from app.api import api_router
        from app.deps import verify_tenant
        from app.middleware import TimingMiddleware

        app = FastAPI(title="Acme Shop #1", dependencies=[Depends(verify_tenant)])

        # app.add_middleware(CommentedOutMiddleware)
        app.add_middleware(CORSMiddleware, allow_origins=["*"])
        app.add_middleware(TimingMiddleware)


        @app.middleware("http")
        async def add_request_id(request, call_next):
            request_id = new_request_id()
            response = await call_next(request)
            write_access_log(request_id)
            return response


        app.include_router(api_router, prefix="/api", dependencies=[Depends(require_api_key)])


        @app.exception_handler(OrderError)
        async def order_error_handler(request, exc):
            return render_error(exc)
        """";

    private const string ApiMiddleware = """
        import time
        from starlette.middleware.base import BaseHTTPMiddleware


        class TimingMiddleware(BaseHTTPMiddleware):
            async def dispatch(self, request, call_next):
                started = time.perf_counter()
                if request.url.path == "/health":
                    return await call_next(request)
                response = await call_next(request)
                record_metric(started)
                return response


        def record_metric(started):
            pass
        """;

    private const string ApiDeps = """
        from typing import Annotated

        from fastapi import Depends, HTTPException
        from fastapi.security import OAuth2PasswordBearer
        from sqlalchemy.orm import Session

        oauth2_scheme = OAuth2PasswordBearer(tokenUrl="/api/login")


        def get_db():
            db = Session()
            try:
                yield db
            finally:
                db.close()


        SessionDep = Annotated[Session, Depends(get_db)]
        TokenDep = Annotated[str, Depends(oauth2_scheme)]


        def get_current_user(session: SessionDep, token: TokenDep):
            payload = decode_token(token)
            user = session.get(User, payload["sub"])
            if not user:
                raise HTTPException(status_code=401)
            return user


        CurrentUser = Annotated[User, Depends(get_current_user)]


        def get_current_admin(user: CurrentUser):
            if not user.is_admin:
                raise HTTPException(status_code=403)
            return user


        def verify_tenant(x_tenant: str):
            check_tenant(x_tenant)


        def require_api_key(x_api_key: str):
            pass


        def rate_limit():
            pass


        def require_admin(user: Annotated[User, Depends(get_current_admin)]):
            pass


        class Pager:
            def __init__(self, page: int = 1, size: int = 20):
                self.page = page
        """;

    private const string ApiOrders = """
        from typing import Annotated

        from fastapi import APIRouter, Depends
        from sqlalchemy.orm import Session

        from app.deps import CurrentUser, SessionDep, get_db, rate_limit

        router = APIRouter(prefix="/orders", dependencies=[Depends(rate_limit)])


        @router.get("/")
        def list_orders(session: SessionDep, user: CurrentUser):
            return []


        @router.post("/")
        def create_order(*, session: SessionDep, user: CurrentUser, body: OrderIn):
            return body


        @router.get("/{order_id}")
        def get_order(order_id: int, session: Annotated[Session, Depends(get_db)]):
            return None
        """;

    private const string ApiAdmin = """
        from fastapi import APIRouter, Depends

        from app.deps import Pager, get_current_admin

        router = APIRouter(prefix="/admin")


        @router.delete(
            "/cache",
            dependencies=[Depends(get_current_admin)],
        )
        def clear_cache(pager: Pager = Depends()):
            return pager
        """;

    private const string ApiRouterFile = """
        from fastapi import APIRouter, Depends

        from app.deps import require_admin
        from app.routes import admin, orders

        api_router = APIRouter()
        api_router.include_router(orders.router)
        api_router.include_router(admin.router, dependencies=[Depends(require_admin)])
        """;

    private const string P = "shop-api/app/";

    private static FrameworkInput FastApiFixture()
    {
        int L(string text, string marker) => LineOf(text, marker);
        var mw = L(ApiMiddleware, "async def dispatch");
        var getDb = L(ApiDeps, "def get_db");
        var gcu = L(ApiDeps, "def get_current_user");
        var gca = L(ApiDeps, "def get_current_admin");
        var addRid = L(ApiMain, "async def add_request_id");
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, P + "main.py", P + "main.py", null, 1, 32),
            Sym(2, SymbolKind.Function, "shop-api.app.main.add_request_id", P + "main.py", 1, addRid, addRid + 5),
            Sym(3, SymbolKind.Function, "shop-api.app.main.order_error_handler", P + "main.py", 1, L(ApiMain, "async def order_error_handler"), L(ApiMain, "async def order_error_handler") + 1),
            Sym(5, SymbolKind.Function, "shop-api.app.ids.new_request_id", P + "ids.py", null, 1, 2),
            Sym(6, SymbolKind.Function, "shop-api.app.logs.write_access_log", P + "logs.py", null, 1, 2),
            Sym(7, SymbolKind.Function, "shop-api.app.errors.render_error", P + "errors.py", null, 1, 2),
            Sym(10, SymbolKind.Module, P + "middleware.py", P + "middleware.py", null, 1, 16),
            Sym(11, SymbolKind.Class, "shop-api.app.middleware.TimingMiddleware", P + "middleware.py", 10, mw - 1, mw + 6),
            Sym(12, SymbolKind.Method, "shop-api.app.middleware.TimingMiddleware.dispatch", P + "middleware.py", 11, mw, mw + 6),
            Sym(13, SymbolKind.Function, "shop-api.app.middleware.record_metric", P + "middleware.py", 10, L(ApiMiddleware, "def record_metric"), L(ApiMiddleware, "def record_metric") + 1),
            Sym(20, SymbolKind.Module, P + "deps.py", P + "deps.py", null, 1, 60),
            Sym(21, SymbolKind.Function, "shop-api.app.deps.get_db", P + "deps.py", 20, getDb, getDb + 5),
            Sym(22, SymbolKind.TypeAlias, "shop-api.app.deps.SessionDep", P + "deps.py", 20, L(ApiDeps, "SessionDep ="), L(ApiDeps, "SessionDep =")),
            Sym(23, SymbolKind.Function, "shop-api.app.deps.get_current_user", P + "deps.py", 20, gcu, gcu + 5),
            Sym(24, SymbolKind.Function, "shop-api.app.deps.get_current_admin", P + "deps.py", 20, gca, gca + 3),
            Sym(25, SymbolKind.Function, "shop-api.app.deps.verify_tenant", P + "deps.py", 20, L(ApiDeps, "def verify_tenant"), L(ApiDeps, "def verify_tenant") + 1),
            Sym(26, SymbolKind.Function, "shop-api.app.deps.require_api_key", P + "deps.py", 20, L(ApiDeps, "def require_api_key"), L(ApiDeps, "def require_api_key") + 1),
            Sym(27, SymbolKind.Function, "shop-api.app.deps.rate_limit", P + "deps.py", 20, L(ApiDeps, "def rate_limit"), L(ApiDeps, "def rate_limit") + 1),
            Sym(28, SymbolKind.Function, "shop-api.app.deps.require_admin", P + "deps.py", 20, L(ApiDeps, "def require_admin"), L(ApiDeps, "def require_admin") + 1),
            Sym(29, SymbolKind.Class, "shop-api.app.deps.Pager", P + "deps.py", 20, L(ApiDeps, "class Pager"), L(ApiDeps, "class Pager") + 2),
            Sym(30, SymbolKind.Method, "shop-api.app.deps.Pager.__init__", P + "deps.py", 29, L(ApiDeps, "def __init__"), L(ApiDeps, "def __init__") + 1),
            Sym(31, SymbolKind.Function, "shop-api.app.deps.check_tenant", P + "tenancy.py", null, 1, 2),
            Sym(32, SymbolKind.Function, "shop-api.app.security.decode_token", P + "security.py", null, 1, 3),
            Sym(40, SymbolKind.Module, P + "routes/orders.py", P + "routes/orders.py", null, 1, 24),
            Sym(41, SymbolKind.Function, "shop-api.app.routes.orders.list_orders", P + "routes/orders.py", 40, L(ApiOrders, "def list_orders"), L(ApiOrders, "def list_orders") + 1),
            Sym(42, SymbolKind.Function, "shop-api.app.routes.orders.create_order", P + "routes/orders.py", 40, L(ApiOrders, "def create_order"), L(ApiOrders, "def create_order") + 1),
            Sym(43, SymbolKind.Function, "shop-api.app.routes.orders.get_order", P + "routes/orders.py", 40, L(ApiOrders, "def get_order"), L(ApiOrders, "def get_order") + 1),
            Sym(50, SymbolKind.Module, P + "routes/admin.py", P + "routes/admin.py", null, 1, 14),
            Sym(51, SymbolKind.Function, "shop-api.app.routes.admin.clear_cache", P + "routes/admin.py", 50, L(ApiAdmin, "def clear_cache"), L(ApiAdmin, "def clear_cache") + 1),
            Sym(60, SymbolKind.Module, P + "api.py", P + "api.py", null, 1, 9),
            // 测试里的 FastAPI 实例不算
            Sym(90, SymbolKind.Module, "shop-api/tests/test_main.py", "shop-api/tests/test_main.py", null, 1, 3),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [2] = [Call(5, "new_request_id", "shop-api.app.ids.new_request_id", At(addRid + 1)),
                   Unresolved("call_next", At(addRid + 2, 20)),
                   Call(6, "write_access_log", "shop-api.app.logs.write_access_log", At(addRid + 3))],
            [3] = [Call(7, "render_error", "shop-api.app.errors.render_error", At(L(ApiMain, "return render_error")))],
            // if 分支里的 call_next（短路）、主 call_next 在后面
            [12] = [Ext("time.perf_counter", "external:time.perf_counter", At(mw + 1)),
                    Unresolved("call_next", At(mw + 3, 20), CallContext.Cond),
                    Unresolved("call_next", At(mw + 4, 20)),
                    Call(13, "record_metric", "shop-api.app.middleware.record_metric", At(mw + 5))],
            [21] = [Ext("sqlalchemy.orm.Session", "external:sqlalchemy.orm.Session", At(getDb + 1)),
                    Ext("sqlalchemy.orm.Session.close", "external:sqlalchemy.orm.Session.close", At(getDb + 5))],
            [23] = [Call(32, "decode_token", "shop-api.app.security.decode_token", At(gcu + 1)),
                    Ext("sqlalchemy.orm.Session.get", "external:sqlalchemy.orm.Session.get", At(gcu + 2)),
                    Ext("len", "external:len", At(gcu + 2, 50)),
                    Ext("fastapi.HTTPException", "external:fastapi.HTTPException", At(gcu + 4), CallContext.Cond)],
            [24] = [Ext("fastapi.HTTPException", "external:fastapi.HTTPException", At(gca + 2), CallContext.Cond)],
            [25] = [Call(31, "check_tenant", "shop-api.app.deps.check_tenant", At(L(ApiDeps, "check_tenant(x_tenant)")))],
            [32] = [Ext("jwt.decode", "external:jwt.decode", At(2))],
        };
        var signatures = new Dictionary<long, string>
        {
            [2] = "async def add_request_id(request, call_next):",
            [12] = "async def dispatch(self, request, call_next):",
            [21] = "def get_db():",
            [23] = "def get_current_user(session: SessionDep, token: TokenDep):",
            [24] = "def get_current_admin(user: CurrentUser):",
            [25] = "def verify_tenant(x_tenant: str):",
            [28] = "def require_admin(user: Annotated[User, Depends(get_current_admin)]):",
            [30] = "def __init__(self, page: int = 1, size: int = 20):",
            [41] = "def list_orders(session: SessionDep, user: CurrentUser):",
            [42] = "def create_order(*, session: SessionDep, user: CurrentUser, body: OrderIn):",
            [43] = "def get_order(order_id: int, session: Annotated[Session, Depends(get_db)]):",
            [51] = "def clear_cache(pager: Pager = Depends()):",
        };
        var files = new Dictionary<string, string>
        {
            [P + "main.py"] = ApiMain,
            [P + "middleware.py"] = ApiMiddleware,
            [P + "deps.py"] = ApiDeps,
            [P + "routes/orders.py"] = ApiOrders,
            [P + "routes/admin.py"] = ApiAdmin,
            [P + "api.py"] = ApiRouterFile,
            ["shop-api/tests/test_main.py"] = "app = FastAPI()\napp.add_middleware(TestOnlyMiddleware)\n",
        };
        List<EntryPoint> entries =
        [
            new(41, "shop-api", "python.fastapi", "http", "/api/orders/", "GET", P + "routes/orders.py", L(ApiOrders, "def list_orders")),
            new(42, "shop-api", "python.fastapi", "http", "/api/orders/", "POST", P + "routes/orders.py", L(ApiOrders, "def create_order")),
            new(43, "shop-api", "python.fastapi", "http", "/api/orders/{order_id}", "GET", P + "routes/orders.py", L(ApiOrders, "def get_order")),
            new(51, "shop-api", "python.fastapi", "http", "/api/admin/cache", "DELETE", P + "routes/admin.py", L(ApiAdmin, "def clear_cache")),
            new(25, "shop-api", "python.celery", "job", "verify_tenant", null, P + "deps.py", L(ApiDeps, "def verify_tenant")),
        ];
        return new FrameworkInput(symbols, [], Container("shop-api", "shop-api"), entries, [], Sources(calls, signatures, files));
    }

    [Fact]
    public void FastApi_Stages_MiddlewareInReverseRegistration_CallNextSplit()
    {
        var f = FrameworkFlowBuilder.Build(FastApiFixture());

        Assert.Equal(["middleware", "controller", "dependency", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "路由", "依赖", "收尾"], f.Stages.Select(s => s.Label));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        Assert.Empty(f.Handlers);
        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        // 后加的在最外层：@app.middleware（第三个注册）→ TimingMiddleware → CORSMiddleware；docstring、注释、测试文件里的不算
        Assert.Equal(["add_request_id", "TimingMiddleware.dispatch", "CORSMiddleware", "路由", "order_error_handler", "record_metric", "write_access_log"],
            root.Children.Select(c => c.Label));
        var rid = root.Children[0];
        Assert.Equal((2L, "call", "middleware"), (rid.SymbolId!.Value, rid.Kind, rid.Actor));
        Assert.Contains("@app.middleware(\"http\")", rid.Note);
        Assert.Equal(["new_request_id"], rid.Children.Select(c => c.Label));
        var timing = root.Children[1];
        Assert.Equal(12L, timing.SymbolId);
        Assert.Contains("BaseHTTPMiddleware.dispatch", timing.Note);
        Assert.Contains("有条件", timing.Note);
        Assert.Equal(["time.perf_counter"], timing.Children.Select(c => c.Label));
        var cors = root.Children[2];
        Assert.Equal(("external-group", (long?)null), (cors.Kind, cors.SymbolId));
        Assert.Contains("跨域", cors.Note);
        Assert.Contains("main.py:15", cors.Note);
        // 后置：里层（Timing）的先、外层（add_request_id）的后；都在收尾列
        Assert.Equal(("tail", "TimingMiddleware.dispatch 后置（响应往回走）"), (root.Children[5].Actor, root.Children[5].Note));
        Assert.Equal(("tail", "add_request_id 后置（响应往回走）"), (root.Children[6].Actor, root.Children[6].Note));
        Assert.Contains(f.Notes, n => n.Contains("后加的在最外层", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("认出 FastAPI：main.py:12 app = FastAPI(...)", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("另有 1 个非 HTTP 入口", StringComparison.Ordinal));
        // 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序
        Assert.Equal(["TimingMiddleware", "add_request_id"], f.Stages[0].Symbols.Where(s => s.Id != 0).Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Contains(f.Stages[0].Symbols, s => s is { Name: "CORSMiddleware", Id: 0L, Kind: "external" });
        Assert.Equal(3, f.Stages[0].Symbols.Count);
    }

    [Fact]
    public void FastApi_Dependencies_ByRouteCount_ScopesFirst_SubDependenciesAndCache()
    {
        var f = FrameworkFlowBuilder.Build(FastApiFixture());
        var route = f.Flow.Root.Children.Single(c => c.Label == "路由");
        Assert.Equal(("controllers", "controller", 4), (route.Kind, route.Actor, route.Count));
        // 作用于全部路由的（应用级、include_router）在前，再按路由数（get_db 4 > get_current_user 3 = rate_limit 3，同数按首次出现），最后是 yield 收尾
        Assert.Equal(["verify_tenant", "require_api_key", "get_db", "get_current_user", "rate_limit", "require_admin", "Pager", "get_db（yield 之后）"],
            route.Children.Select(c => c.Label));
        Assert.All(route.Children.Take(7), c => Assert.Equal("dependency", c.Actor));
        Assert.Equal("4 个路由用到；应用级依赖 FastAPI(dependencies=…)（main.py:12）", route.Children[0].Note);
        Assert.Equal(["check_tenant"], route.Children[0].Children.Select(c => c.Label));
        Assert.StartsWith("4 个路由用到；include_router 依赖（main.py:27）", route.Children[1].Note);

        var getDb = Child(route, "get_db");
        Assert.Equal(21L, getDb.SymbolId);
        Assert.Contains("经 SessionDep", getDb.Note);
        Assert.Contains("yield 依赖", getDb.Note);
        Assert.Equal(["sqlalchemy.orm.Session"], getDb.Children.Select(c => c.Label));

        // get_current_user：先是它自己的依赖（参数顺序：get_db 已解过 → 缓存行；oauth2_scheme 框架安全依赖），再是它的调用（仓库函数再进一层；len 一类内置函数不要）
        var user = Child(route, "get_current_user");
        Assert.Equal("3 个路由用到；经 CurrentUser", user.Note);
        Assert.Equal(["get_db", "oauth2_scheme = OAuth2PasswordBearer(…)", "decode_token", "sqlalchemy.orm.Session.get", "fastapi.HTTPException"], user.Children.Select(c => c.Label));
        Assert.Contains("只执行一次", user.Children[0].Note);
        Assert.Empty(user.Children[0].Children);
        Assert.Equal("external-group", user.Children[1].Kind);
        Assert.Contains("Bearer token", user.Children[1].Note);
        Assert.Contains("经 TokenDep", user.Children[1].Note);
        Assert.StartsWith("3 个路由用到", user.Children[1].Note);
        Assert.Equal(["jwt.decode"], user.Children[2].Children.Select(c => c.Label));
        Assert.Equal("条件分支里", user.Children[4].Note);

        Assert.StartsWith("3 个路由用到；APIRouter 依赖（orders.py:8）", Child(route, "rate_limit").Note);
        // require_admin（include_router(admin.router, dependencies=…)）只管 admin 的 1 个路由；它的依赖 get_current_admin（内联 Annotated）在它下面
        var admin = Child(route, "require_admin");
        Assert.StartsWith("1 个路由用到；include_router 依赖（api.py:8）", admin.Note);
        var gca = Assert.Single(admin.Children);
        Assert.Equal(("get_current_admin", 24L), (gca.Label, gca.SymbolId!.Value));
        Assert.Equal(["get_current_user", "fastapi.HTTPException"], gca.Children.Select(c => c.Label));
        Assert.Contains("只执行一次", gca.Children[0].Note);
        // = Depends()：依赖是参数的类型，类依赖展开 __init__
        var pager = Child(route, "Pager");
        Assert.Equal((30L, "1 个路由用到"), (pager.SymbolId!.Value, pager.Note));

        // yield 之后：finally 里的清理进收尾
        var teardown = route.Children[^1];
        Assert.Equal(("tail", 21L), (teardown.Actor, teardown.SymbolId!.Value));
        Assert.Contains("finally", teardown.Note);
        Assert.Equal(["sqlalchemy.orm.Session.close"], teardown.Children.Select(c => c.Label));

        var stage = f.Stages.Single(s => s.Key == "dependency");
        Assert.False(stage.OrderKnown);
        Assert.Contains(f.Notes, n => n.Contains("依赖按被多少路由用到排序", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("认出 3 个依赖别名", StringComparison.Ordinal));
    }

    [Fact]
    public void FastApi_ExceptionHandler_InTailWithExceptionType()
    {
        var f = FrameworkFlowBuilder.Build(FastApiFixture());
        var handler = f.Flow.Root.Children.Single(c => c.Label == "order_error_handler");
        Assert.Equal(("tail", 3L), (handler.Actor, handler.SymbolId!.Value));
        Assert.Equal("出错时：OrderError（@app.exception_handler，main.py:30）", handler.Note);
        Assert.Equal(["render_error"], handler.Children.Select(c => c.Label));
        Assert.Contains(f.Notes, n => n == "1 个 exception_handler 放在收尾「出错时」");
    }

    [Fact]
    public void FastApi_WithoutMiddlewareOrDepends_RouteIsRootActorAndNotesExplain()
    {
        const string main = "from fastapi import FastAPI\n\napp = FastAPI()\n\n\n@app.get(\"/ping\")\ndef ping():\n    return 1\n";
        List<MapSymbol> symbols = [Sym(1, SymbolKind.Module, "svc/main.py", "svc/main.py", null, 1, 8), Sym(2, SymbolKind.Function, "svc.main.ping", "svc/main.py", 1, 7, 8)];
        var input = new FrameworkInput(symbols, [], Container("svc", "svc"), [new(2, "svc", "python.fastapi", "http", "/ping", "GET", "svc/main.py", 7)], [],
            Sources([], new() { [2] = "def ping():" }, new() { ["svc/main.py"] = main }));
        var f = FrameworkFlowBuilder.Build(input);
        Assert.Equal(["controller"], f.Stages.Select(s => s.Key));
        Assert.Equal(("controller", "路由"), (f.Flow.Root.Actor, Assert.Single(f.Flow.Root.Children).Label));
        Assert.Contains(f.Notes, n => n.Contains("没有 app.add_middleware", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("没有依赖阶段", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("没有 exception_handler", StringComparison.Ordinal));
    }

    // ================= 夹具二：Django + DRF =================

    private const string DjSettings = """"
        """
        Acme 商城的 settings。
        MIDDLEWARE = ['docstring.Middleware']
        """

        INSTALLED_APPS = [
            'django.contrib.auth',
            'rest_framework',
            'shop.orders',
        ]

        MIDDLEWARE = [
            'django.middleware.security.SecurityMiddleware',
            'shop.middleware.TimingMiddleware',
            # 'shop.middleware.DisabledMiddleware',
            'django.contrib.sessions.middleware.SessionMiddleware',
            'shop.middleware.AuditMiddleware',
            'shop.middleware.tenant_middleware',
            'corsheaders.middleware.CorsMiddleware',
        ]

        ROOT_URLCONF = 'shop.urls'

        REST_FRAMEWORK = {
            'EXCEPTION_HANDLER': 'shop.exceptions.shop_exception_handler',
            'DEFAULT_AUTHENTICATION_CLASSES': (
                'shop.auth.ApiTokenAuthentication',
                'rest_framework.authentication.SessionAuthentication',
            ),
            'DEFAULT_PERMISSION_CLASSES': ['rest_framework.permissions.IsAuthenticated'],
            'DEFAULT_RENDERER_CLASSES': ['shop.renderers.ShopJSONRenderer'],
            'PAGE_SIZE': 20,
        }
        """";

    private const string DjMiddleware = """
        import time

        from django.utils.deprecation import MiddlewareMixin


        class TimingMiddleware:
            def __init__(self, get_response):
                self.get_response = get_response

            def __call__(self, request):
                started = time.monotonic()
                response = self.get_response(request)
                record_duration(started)
                return response


        class AuditMiddleware(MiddlewareMixin):
            def process_request(self, request):
                audit_start(request)

            def process_view(self, request, view_func, view_args, view_kwargs):
                audit_view(view_func)

            def process_response(self, request, response):
                audit_finish(request, response)
                return response

            def process_exception(self, request, exception):
                audit_error(exception)


        def tenant_middleware(get_response):
            def middleware(request):
                bind_tenant(request)
                response = get_response(request)
                unbind_tenant()
                return response
            return middleware
        """;

    private const string DjViews = """
        from rest_framework import viewsets
        from rest_framework.permissions import AllowAny


        class OrderViewSet(viewsets.ModelViewSet):
            permission_classes = (AllowAny,)

            def list(self, request):
                return []

            def create(self, request):
                return None


        class PingView(APIView):
            def get(self, request):
                return None
        """;

    private static FrameworkInput DjangoFixture(string settings, bool withDrfFiles = true)
    {
        int M(string marker) => LineOf(DjMiddleware, marker);
        int V(string marker) => LineOf(DjViews, marker);
        var call = M("def __call__");
        var inner = M("def middleware(request)");
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "shop/settings.py", "shop/settings.py", null, 1, 40),
            Sym(2, SymbolKind.Module, "shop/settings_dev.py", "shop/settings_dev.py", null, 1, 2),
            Sym(10, SymbolKind.Module, "shop/middleware.py", "shop/middleware.py", null, 1, 40),
            Sym(11, SymbolKind.Class, "shop.middleware.TimingMiddleware", "shop/middleware.py", 10, M("class TimingMiddleware"), call + 4),
            Sym(12, SymbolKind.Method, "shop.middleware.TimingMiddleware.__init__", "shop/middleware.py", 11, M("def __init__"), M("def __init__") + 1),
            Sym(13, SymbolKind.Method, "shop.middleware.TimingMiddleware.__call__", "shop/middleware.py", 11, call, call + 4),
            Sym(14, SymbolKind.Class, "shop.middleware.AuditMiddleware", "shop/middleware.py", 10, M("class AuditMiddleware"), M("def process_exception") + 1),
            Sym(15, SymbolKind.Method, "shop.middleware.AuditMiddleware.process_request", "shop/middleware.py", 14, M("def process_request"), M("def process_request") + 1),
            Sym(16, SymbolKind.Method, "shop.middleware.AuditMiddleware.process_view", "shop/middleware.py", 14, M("def process_view"), M("def process_view") + 1),
            Sym(17, SymbolKind.Method, "shop.middleware.AuditMiddleware.process_response", "shop/middleware.py", 14, M("def process_response"), M("def process_response") + 2),
            Sym(18, SymbolKind.Method, "shop.middleware.AuditMiddleware.process_exception", "shop/middleware.py", 14, M("def process_exception"), M("def process_exception") + 1),
            Sym(19, SymbolKind.Function, "shop.middleware.tenant_middleware", "shop/middleware.py", 10, M("def tenant_middleware"), inner + 5),
            Sym(20, SymbolKind.Function, "shop.middleware.tenant_middleware.middleware", "shop/middleware.py", 19, inner, inner + 4),
            Sym(30, SymbolKind.Module, "shop/auth.py", "shop/auth.py", null, 1, 10),
            Sym(31, SymbolKind.Class, "shop.auth.ApiTokenAuthentication", "shop/auth.py", 30, 4, 10),
            Sym(32, SymbolKind.Method, "shop.auth.ApiTokenAuthentication.authenticate", "shop/auth.py", 31, 5, 7),
            Sym(33, SymbolKind.Method, "shop.auth.ApiTokenAuthentication._check", "shop/auth.py", 31, 9, 10),
            Sym(40, SymbolKind.Module, "shop/exceptions.py", "shop/exceptions.py", null, 1, 5),
            Sym(41, SymbolKind.Function, "shop.exceptions.shop_exception_handler", "shop/exceptions.py", 40, 3, 5),
            Sym(45, SymbolKind.Module, "shop/renderers.py", "shop/renderers.py", null, 1, 5),
            Sym(46, SymbolKind.Class, "shop.renderers.ShopJSONRenderer", "shop/renderers.py", 45, 3, 5),
            Sym(47, SymbolKind.Method, "shop.renderers.ShopJSONRenderer.render", "shop/renderers.py", 46, 4, 5),
            Sym(50, SymbolKind.Module, "shop/views.py", "shop/views.py", null, 1, 20),
            Sym(51, SymbolKind.Class, "shop.views.OrderViewSet", "shop/views.py", 50, V("class OrderViewSet"), V("def create") + 1),
            Sym(52, SymbolKind.Method, "shop.views.OrderViewSet.list", "shop/views.py", 51, V("def list"), V("def list") + 1),
            Sym(53, SymbolKind.Method, "shop.views.OrderViewSet.create", "shop/views.py", 51, V("def create"), V("def create") + 1),
            Sym(54, SymbolKind.Class, "shop.views.PingView", "shop/views.py", 50, V("class PingView"), V("class PingView") + 2),
            Sym(55, SymbolKind.Method, "shop.views.PingView.get", "shop/views.py", 54, V("def get"), V("def get") + 1),
            Sym(60, SymbolKind.Function, "shop.audit.record_duration", "shop/audit.py", null, 1, 2),
            Sym(61, SymbolKind.Function, "shop.audit.audit_start", "shop/audit.py", null, 3, 4),
            Sym(62, SymbolKind.Function, "shop.audit.audit_view", "shop/audit.py", null, 5, 6),
            Sym(63, SymbolKind.Function, "shop.audit.audit_finish", "shop/audit.py", null, 7, 8),
            Sym(64, SymbolKind.Function, "shop.audit.audit_error", "shop/audit.py", null, 9, 10),
            Sym(65, SymbolKind.Function, "shop.tenancy.bind_tenant", "shop/tenancy.py", null, 1, 2),
            Sym(66, SymbolKind.Function, "shop.tenancy.unbind_tenant", "shop/tenancy.py", null, 3, 4),
            Sym(67, SymbolKind.Function, "shop.tokens.read_token", "shop/tokens.py", null, 1, 2),
            Sym(68, SymbolKind.Function, "shop.tokens.lookup_token", "shop/tokens.py", null, 3, 4),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [13] = [Ext("time.monotonic", "external:time.monotonic", At(call + 1)),
                    Ext("get_response", "external:get_response", At(call + 2, 30)),
                    Call(60, "record_duration", "shop.audit.record_duration", At(call + 3))],
            [15] = [Call(61, "audit_start", "shop.audit.audit_start", At(M("audit_start(request)")))],
            [16] = [Call(62, "audit_view", "shop.audit.audit_view", At(M("audit_view(view_func)")))],
            [17] = [Call(63, "audit_finish", "shop.audit.audit_finish", At(M("audit_finish(request")))],
            [18] = [Call(64, "audit_error", "shop.audit.audit_error", At(M("audit_error(exception)")))],
            [20] = [Call(65, "bind_tenant", "shop.tenancy.bind_tenant", At(inner + 1)),
                    Unresolved("get_response", At(inner + 2, 30)),
                    Call(66, "unbind_tenant", "shop.tenancy.unbind_tenant", At(inner + 3))],
            [32] = [Call(67, "read_token", "shop.tokens.read_token", At(6)),
                    Call(33, "ApiTokenAuthentication._check", "shop.auth.ApiTokenAuthentication._check", At(7))],
            [33] = [Call(68, "lookup_token", "shop.tokens.lookup_token", At(10))],
            [41] = [Ext("rest_framework.views.exception_handler", "external:rest_framework.views.exception_handler", At(4))],
            [47] = [Ext("json.dumps", "external:json.dumps", At(5))],
        };
        var signatures = new Dictionary<long, string> { [19] = "def tenant_middleware(get_response):", [20] = "def middleware(request):" };
        var files = new Dictionary<string, string>
        {
            ["shop/settings.py"] = settings,
            ["shop/settings_dev.py"] = "from .settings import *\nMIDDLEWARE += ['debug_toolbar.middleware.DebugToolbarMiddleware']\n",
            ["shop/middleware.py"] = DjMiddleware,
            ["shop/views.py"] = DjViews,
        };
        if (!withDrfFiles) symbols.RemoveAll(s => s.Path is "shop/auth.py" or "shop/exceptions.py" or "shop/renderers.py");
        List<EntryPoint> entries =
        [
            new(52, "shop", "python.drf", "http", "/api/orders/", "GET", "shop/views.py", V("def list")),
            new(53, "shop", "python.drf", "http", "/api/orders/", "POST", "shop/views.py", V("def create")),
            new(55, "shop", "python.django", "http", "/api/ping", "GET", "shop/views.py", V("def get")),
        ];
        return new FrameworkInput(symbols, [], Container("shop", "shop"), entries, [], Sources(calls, signatures, files));
    }

    [Fact]
    public void Django_MiddlewareInListOrder_NewStyleMixinAndFunction_TailReversed()
    {
        var f = FrameworkFlowBuilder.Build(DjangoFixture(DjSettings));

        Assert.Equal(["middleware", "controller", "interceptor", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "视图", "DRF 认证 / 权限", "收尾"], f.Stages.Select(s => s.Label));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        var root = f.Flow.Root;
        // MIDDLEWARE 列表顺序（注释掉的、docstring 里的不算）；之后：process_exception（倒序）、各中间件的后置（倒序）
        Assert.Equal(["SecurityMiddleware", "TimingMiddleware.__call__", "SessionMiddleware", "AuditMiddleware.process_request", "tenant_middleware", "CorsMiddleware",
                      "视图", "AuditMiddleware.process_exception", "unbind_tenant", "AuditMiddleware.process_response", "record_duration"],
            root.Children.Select(c => c.Label));
        Assert.Equal(("external-group", "middleware"), (root.Children[0].Kind, root.Children[0].Actor));
        Assert.Contains("Django 内置（settings.py:13）", root.Children[0].Note);
        Assert.Equal("第三方 corsheaders（settings.py:19）：跨域响应头", root.Children[5].Note);
        var timing = root.Children[1];
        Assert.Equal(13L, timing.SymbolId);
        Assert.Contains("MIDDLEWARE 第 2 个", timing.Note);
        Assert.Equal(["time.monotonic"], timing.Children.Select(c => c.Label));
        var audit = root.Children[3];
        Assert.Contains("MiddlewareMixin", audit.Note);
        Assert.Equal(["audit_start", "AuditMiddleware.process_view"], audit.Children.Select(c => c.Label));
        Assert.Equal(["audit_view"], audit.Children[1].Children.Select(c => c.Label));
        var tenant = root.Children[4];
        Assert.Equal((20L, "自研函数中间件（MIDDLEWARE 第 5 个，settings.py:18）"), (tenant.SymbolId!.Value, tenant.Note));
        Assert.Equal(["bind_tenant"], tenant.Children.Select(c => c.Label));
        Assert.All(root.Children.Skip(7), c => Assert.Equal("tail", c.Actor));
        Assert.Contains("出错时", root.Children[7].Note);
        Assert.Equal("tenant_middleware 后置（响应往回走）", root.Children[8].Note);
        Assert.Contains(f.Notes, n => n.StartsWith("认出 Django：settings.py:12 MIDDLEWARE（6 个）", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("settings_dev.py", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n == "路由：ROOT_URLCONF = shop.urls，3 个 HTTP 入口");
    }

    [Fact]
    public void Django_Drf_SettingsClasses_ExceptionHandlerAndRenderer_ViewOverrides()
    {
        var f = FrameworkFlowBuilder.Build(DjangoFixture(DjSettings));
        var view = f.Flow.Root.Children.Single(c => c.Label == "视图");
        Assert.Equal(("controllers", "controller", 3), (view.Kind, view.Actor, view.Count));
        Assert.Equal(["ApiTokenAuthentication.authenticate", "SessionAuthentication", "IsAuthenticated", "shop_exception_handler", "ShopJSONRenderer.render"],
            view.Children.Select(c => c.Label));
        Assert.Equal(["interceptor", "interceptor", "interceptor", "tail", "tail"], view.Children.Select(c => c.Actor));
        var auth = view.Children[0];
        Assert.Equal(32L, auth.SymbolId);
        Assert.StartsWith("认证（DEFAULT_AUTHENTICATION_CLASSES，settings.py:27）", auth.Note);
        // 仓库里的方法再进一层
        Assert.Equal(["read_token", "ApiTokenAuthentication._check"], auth.Children.Select(c => c.Label));
        Assert.Equal(["lookup_token"], auth.Children[1].Children.Select(c => c.Label));
        Assert.Equal("external-group", view.Children[1].Kind);
        Assert.Contains("没登录", view.Children[2].Note);
        Assert.Equal((41L, "出错时：DRF EXCEPTION_HANDLER（settings.py:25），把异常变成错误响应"), (view.Children[3].SymbolId!.Value, view.Children[3].Note));
        Assert.Equal(["rest_framework.views.exception_handler"], view.Children[3].Children.Select(c => c.Label));
        Assert.Equal(47L, view.Children[4].SymbolId);
        Assert.Contains(f.Notes, n => n.StartsWith("认出 DRF（settings.py:24 REST_FRAMEWORK）", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n == "视图自己设的（覆盖上面的默认）：permission_classes 1 个");
        Assert.DoesNotContain(f.Notes, n => n.Contains("画的是 DRF 默认值", StringComparison.Ordinal));
    }

    [Fact]
    public void Django_DrfDefaults_WhenNotConfigured_AndPlainDjangoWithoutDrf()
    {
        const string withDrf = "INSTALLED_APPS = ['rest_framework']\n\nMIDDLEWARE = [\n    'django.middleware.common.CommonMiddleware',\n]\n";
        var f = FrameworkFlowBuilder.Build(DjangoFixture(withDrf, withDrfFiles: false));
        var view = f.Flow.Root.Children.Single(c => c.Label == "视图");
        Assert.Equal(["SessionAuthentication", "BasicAuthentication", "AllowAny", "JSONRenderer / BrowsableAPIRenderer"], view.Children.Select(c => c.Label));
        Assert.All(view.Children, c => Assert.Contains("DRF 默认", c.Note));
        Assert.Contains(f.Notes, n => n.Contains("没配 DEFAULT_AUTHENTICATION_CLASSES、DEFAULT_PERMISSION_CLASSES、DEFAULT_RENDERER_CLASSES、EXCEPTION_HANDLER", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("INSTALLED_APPS 里有 rest_framework", StringComparison.Ordinal));

        // 没有 DRF：没有认证阶段；视图入口的规则也去掉 drf
        var plain = DjangoFixture("MIDDLEWARE = ['django.middleware.common.CommonMiddleware']\n", withDrfFiles: false);
        plain = plain with { Entries = plain.Entries.Select(e => e with { RuleId = "python.django" }).ToList() };
        var g = FrameworkFlowBuilder.Build(plain);
        Assert.Equal(["middleware", "controller"], g.Stages.Select(s => s.Key));
        Assert.Empty(g.Flow.Root.Children.Single(c => c.Label == "视图").Children);
        Assert.Contains(g.Notes, n => n.StartsWith("没用 DRF", StringComparison.Ordinal));
    }

    // ================= 夹具三：Flask =================

    private const string FlaskApp = """
        from flask import Flask
        from werkzeug.middleware.proxy_fix import ProxyFix

        from acme.orders import bp as orders_bp
        from acme.wsgi import RequestIdMiddleware

        app = Flask(__name__)
        app.wsgi_app = ProxyFix(app.wsgi_app)
        app.wsgi_app = RequestIdMiddleware(app.wsgi_app)


        @app.before_request
        def load_user():
            fetch_user()


        @app.before_request
        def check_maintenance():
            pass


        @app.after_request
        def add_headers(response):
            return response


        @app.after_request
        def log_response(response):
            write_log(response)
            return response


        @app.teardown_request
        def close_db(exc):
            release_db()


        @app.errorhandler(404)
        def not_found(e):
            return "not found", 404


        def handle_order_error(e):
            return "bad order", 400


        app.register_error_handler(OrderError, handle_order_error)
        app.register_blueprint(orders_bp, url_prefix="/orders")
        """;

    private const string FlaskOrders = """
        from flask import Blueprint

        bp = Blueprint("orders", __name__)


        @bp.before_request
        def require_login():
            ensure_login()


        @bp.after_request
        def bp_after(response):
            return response


        @bp.route("/")
        def list_orders():
            return []
        """;

    private const string FlaskWsgi = """
        class RequestIdMiddleware:
            def __init__(self, app):
                self.app = app

            def __call__(self, environ, start_response):
                environ["HTTP_X_REQUEST_ID"] = new_id()
                result = self.app(environ, start_response)
                flush_metrics()
                return result
        """;

    private static FrameworkInput FlaskFixture()
    {
        long id = 100;
        var symbols = new List<MapSymbol>
        {
            Sym(1, SymbolKind.Module, "acme/app.py", "acme/app.py", null, 1, 50),
            Sym(2, SymbolKind.Module, "acme/orders.py", "acme/orders.py", null, 1, 18),
            Sym(3, SymbolKind.Module, "acme/wsgi.py", "acme/wsgi.py", null, 1, 10),
            Sym(4, SymbolKind.Class, "acme.wsgi.RequestIdMiddleware", "acme/wsgi.py", 3, 1, 9),
            Sym(5, SymbolKind.Method, "acme.wsgi.RequestIdMiddleware.__call__", "acme/wsgi.py", 4, 5, 9),
        };
        var ids = new Dictionary<string, long>();
        foreach (var (path, text) in new[] { ("acme/app.py", FlaskApp), ("acme/orders.py", FlaskOrders) })
            foreach (var name in new[] { "load_user", "check_maintenance", "add_headers", "log_response", "close_db", "not_found", "handle_order_error", "require_login", "bp_after", "list_orders" })
                if (text.Contains("def " + name + "(", StringComparison.Ordinal))
                {
                    var line = LineOf(text, "def " + name + "(");
                    ids[name] = ++id;
                    symbols.Add(Sym(id, SymbolKind.Function, path[..^3].Replace('/', '.') + "." + name, path, path == "acme/app.py" ? 1 : 2, line, line + 2));
                }
        symbols.Add(Sym(200, SymbolKind.Function, "acme.users.fetch_user", "acme/users.py", null, 1, 2));
        symbols.Add(Sym(201, SymbolKind.Function, "acme.ids.new_id", "acme/ids.py", null, 1, 2));
        symbols.Add(Sym(202, SymbolKind.Function, "acme.metrics.flush_metrics", "acme/metrics.py", null, 1, 2));
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [ids["load_user"]] = [Call(200, "fetch_user", "acme.users.fetch_user", At(LineOf(FlaskApp, "fetch_user()")))],
            [5] = [Call(201, "new_id", "acme.ids.new_id", At(6, 40)),
                   Unresolved("self.app", At(7, 17)),
                   Call(202, "flush_metrics", "acme.metrics.flush_metrics", At(8))],
        };
        var files = new Dictionary<string, string> { ["acme/app.py"] = FlaskApp, ["acme/orders.py"] = FlaskOrders, ["acme/wsgi.py"] = FlaskWsgi };
        List<EntryPoint> entries = [new(ids["list_orders"], "acme", "python.flask", "http", "/orders/", "GET", "acme/orders.py", LineOf(FlaskOrders, "def list_orders"))];
        return new FrameworkInput(symbols, [], Container("acme", "acme"), entries, [], Sources(calls, [], files));
    }

    [Fact]
    public void Flask_WsgiWrapOrder_HooksAppThenBlueprint_TailBlueprintFirstReversed()
    {
        var f = FrameworkFlowBuilder.Build(FlaskFixture());

        Assert.Equal(["middleware", "interceptor", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "请求钩子", "路由", "收尾"], f.Stages.Select(s => s.Label));
        var root = f.Flow.Root;
        Assert.Equal(["RequestIdMiddleware.__call__", "ProxyFix",
                      "load_user", "check_maintenance", "require_login",
                      "路由",
                      "not_found", "handle_order_error",
                      "bp_after", "log_response", "add_headers",
                      "close_db",
                      "flush_metrics"],
            root.Children.Select(c => c.Label));
        Assert.Equal(["new_id"], root.Children[0].Children.Select(c => c.Label));
        Assert.Equal("external-group", root.Children[1].Kind);
        Assert.Equal(["fetch_user"], root.Children[2].Children.Select(c => c.Label));
        Assert.All(root.Children.Skip(2).Take(3), c => Assert.Equal("interceptor", c.Actor));
        Assert.Contains("只对蓝图 bp 的路由", root.Children[4].Note);
        Assert.StartsWith("出错时：404（@app.errorhandler", root.Children[6].Note);
        Assert.StartsWith("出错时：OrderError（app.register_error_handler", root.Children[7].Note);
        Assert.Contains("只对蓝图 bp", root.Children[8].Note);
        Assert.All(root.Children.Skip(6), c => Assert.Equal("tail", c.Actor));
        Assert.Equal("RequestIdMiddleware.__call__ 后置（响应往回走）", root.Children[^1].Note);
        Assert.Contains(f.Notes, n => n.StartsWith("认出 Flask：app.py:7 app = Flask(...)", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("1 个蓝图（bp），register_blueprint 1 处", StringComparison.Ordinal));
    }

    // ================= 其他 =================

    [Fact]
    public void NoWebFramework_EntryPlaceholderAndNote()
    {
        List<MapSymbol> symbols = [Sym(1, SymbolKind.Module, "worker/tasks.py", "worker/tasks.py", null, 1, 5), Sym(2, SymbolKind.Function, "worker.tasks.send_mail", "worker/tasks.py", 1, 3, 5)];
        var input = new FrameworkInput(symbols, [], Container("worker", "worker"), [new(2, "worker", "python.celery", "job", "send_mail", null, "worker/tasks.py", 3)], [],
            Sources([], [], new() { ["worker/tasks.py"] = "from celery import shared_task\n\n@shared_task\ndef send_mail():\n    pass\n" }));
        var f = FrameworkFlowBuilder.Build(input);
        Assert.Equal(["controller"], f.Stages.Select(s => s.Key));
        var entry = Assert.Single(f.Flow.Root.Children);
        Assert.Equal(("入口（任务 / 命令行 / 常驻进程）", "controllers", 1), (entry.Label, entry.Kind, entry.Count));
        Assert.StartsWith("没认出 Python Web 框架", f.Notes[0]);
    }

    [Fact]
    public void StripPython_BlanksCommentsAndTripleQuotes_KeepsStringsAndOffsets()
    {
        const string src = "x = 'a#b'  # 注释 app.add_middleware(X)\n\"\"\"doc\napp.add_middleware(Y)\n\"\"\"\ny = \"z\"\n";
        var s = PythonFrameworkFlow.StripPython(src);
        Assert.Equal(src.Length, s.Length);
        Assert.Equal(src.Count(c => c == '\n'), s.Count(c => c == '\n'));
        Assert.Contains("'a#b'", s);
        Assert.DoesNotContain("add_middleware", s);
        Assert.Contains("y = \"z\"", s);
    }

    [Fact]
    public void KeepPy_DropsBuiltinsAndReceiverlessCalls_KeepsModuleCalls()
    {
        static FrameworkCall E(string qn) => new(null, "external", qn, qn, 1, 0, null, false, []);
        Assert.True(PythonFrameworkFlow.KeepPy(E("external:jwt.decode")));
        Assert.False(PythonFrameworkFlow.KeepPy(E("external:len")));
        Assert.False(PythonFrameworkFlow.KeepPy(E("external:split")));
        Assert.False(PythonFrameworkFlow.KeepPy(E("external:typing.cast")));
        Assert.False(PythonFrameworkFlow.KeepPy(new FrameworkCall(null, "unresolved", "call_next", "call_next", 1, 0, null, false, [])));
    }
}
