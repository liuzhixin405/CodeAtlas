using System.Text;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · Go（GoFrameworkFlow）：gin（Default 内置、Use 顺序、路由组与"只对之后注册的路由生效"、注册函数里的 Use、c.Next 前后、Abort、defer、NoRoute）、
/// gorilla mux + 手工包装 http.Handler（最后包的最先执行、&amp;T{next: h}、Server{Handler: …}、NotFoundHandler）、echo（Pre / Use / Group 参数 / HTTPErrorHandler、next(c)）、
/// chi（Route / Group 闭包里的 r 不是根）、DefaultServeMux 包装、不是 HTTP 服务的容器。
/// 调用的行号与 Pos（UTF-8 字节偏移）从夹具源码里算，文件头有中文注释，字节偏移与字符偏移不一致。
/// </summary>
public class GoFrameworkFlowTests
{
    // ================= 夹具工具 =================

    private sealed class Fixture
    {
        public readonly List<MapSymbol> Symbols = [];
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<long, List<FrameworkCall>> Calls = [];
        public readonly List<EntryPoint> Entries = [];
        private long nextExternal = 9000;

        public void File(string path, string text)
        {
            Files[path] = text;
            Symbols.Add(new MapSymbol(Symbols.Count + 5000, SymbolKind.Module, path, path, null, 1, text.Split('\n').Length));
        }

        /// <summary>函数 / 方法 / 类型：起止行按源码里 decl 那一行和配对的右花括号算。</summary>
        public MapSymbol Fn(long id, SymbolKind kind, string qn, string path, string decl, long? parent = null)
        {
            var text = Files[path];
            var at = text.IndexOf(decl, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{path} 里没有 {decl}");
            var brace = text.IndexOf('{', at);
            var depth = 0;
            var end = brace;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) break;
            }
            var s = new MapSymbol(id, kind, qn, path, parent, LineOf(text, at), LineOf(text, end));
            Symbols.Add(s);
            return s;
        }

        /// <summary>fn 里的一次调用：needle 是源码里的调用文本（第 occurrence 次出现），行号与字节偏移由它算。</summary>
        public void Call(long fn, string path, string needle, long? target, string label, string qn, string kind = "call", string? ctx = null, int occurrence = 0)
        {
            var text = Files[path];
            var at = -1;
            for (var i = 0; i <= occurrence; i++)
            {
                at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
                Assert.True(at >= 0, $"{path} 里没有第 {occurrence} 个 {needle}");
            }
            var bytePos = Encoding.UTF8.GetByteCount(text[..at]);
            if (kind == "external" && target is null) target = nextExternal++;
            if (!Calls.TryGetValue(fn, out var list)) Calls[fn] = list = [];
            list.Add(new FrameworkCall(target, kind, label, qn, LineOf(text, at), bytePos, ctx, kind == "call", []));
        }

        public void Ext(long fn, string path, string needle, string qn, string? ctx = null, int occurrence = 0) =>
            Call(fn, path, needle, null, OutlineLabel(qn), qn, "external", ctx, occurrence);

        private static string OutlineLabel(string qn)
        {
            var q = qn["external:".Length..];
            return q;
        }

        public FrameworkInput Input(string key = "shop") => new(Symbols, [], new ArchNode(key, ArchNodeKind.Container, "go-service", key, "", ArchSource.Discovered, [], [""], []),
            Entries, [], new FrameworkSources
            {
                Calls = id => Calls.TryGetValue(id, out var l) ? l : [],
                Signature = _ => null,
                ReadFile = p => Files.GetValueOrDefault(p),
            });

        private static int LineOf(string text, int pos)
        {
            var n = 1;
            for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }
    }

    private static EntryPoint Http(long id, string route, string method, string file) => new(id, "shop", "go.gin", "http", route, method, file, 1);

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    // ================= 夹具一：gin =================

    private const string GinMain = """
        // 中文注释：让字节偏移和字符偏移对不上
        package main

        import (
        	"github.com/gin-gonic/gin"

        	"example.com/shop/internal/auth"
        	"example.com/shop/internal/orders"
        )

        // r.Use(auth.Commented())
        func main() {
        	r := gin.Default()
        	r.Use(RequestID(), gin.CustomRecovery(onPanic))
        	r.NoRoute(notFound)
        	const help = `
        r.Use(Leaked())
        "`
        	api := r.Group("/api")
        	orders.RegisterPublic(api.Group("/catalog"))
        	api.Use(auth.Required(true))
        	orders.Register(api.Group("/orders"))
        	r.Run(":8080")
        }

        func notFound(c *gin.Context) {
        	renderMissing(c)
        }
        """;

    private const string GinMiddleware = """
        package main

        import "github.com/gin-gonic/gin"

        // 请求号中间件
        func RequestID() gin.HandlerFunc {
        	gen := newGenerator()
        	return func(c *gin.Context) {
        		c.Set("id", gen.Make())
        		ctx := context.WithValue(c.Request.Context(), key, 1)
        		c.Next()
        		observe(c, ctx)
        	}
        }
        """;

    private const string GinAuth = """
        package auth

        import "github.com/gin-gonic/gin"

        func Required(strict bool) gin.HandlerFunc {
        	return func(c *gin.Context) {
        		user, err := parseToken(c)
        		if err != nil && strict {
        			c.AbortWithStatus(401)
        			return
        		}
        		c.Set("user", user)
        	}
        }
        """;

    private const string GinOrders = """
        package orders

        import "github.com/gin-gonic/gin"

        func RegisterPublic(router *gin.RouterGroup) {
        	router.GET("", list)
        }

        func Register(router *gin.RouterGroup) {
        	router.Use(audit)
        	router.POST("", create)
        }

        func audit(c *gin.Context) {
        	start := time.Now()
        	defer logAccess(c, start)
        	c.Next()
        	writeAudit(c)
        }
        """;

    private const string GinTest = """
        package main

        import "github.com/gin-gonic/gin"

        func TestRouter(t *testing.T) {
        	r := gin.New()
        	r.Use(Fake())
        }
        """;

    private static FrameworkInput GinFixture()
    {
        var f = new Fixture();
        f.File("cmd/shop/main.go", GinMain);
        f.File("cmd/shop/middleware.go", GinMiddleware);
        f.File("cmd/shop/main_test.go", GinTest);
        f.File("internal/auth/auth.go", GinAuth);
        f.File("internal/orders/routes.go", GinOrders);
        f.Fn(10, SymbolKind.Function, "cmd/shop.main", "cmd/shop/main.go", "func main()");
        f.Fn(11, SymbolKind.Function, "cmd/shop.notFound", "cmd/shop/main.go", "func notFound(");
        f.Fn(20, SymbolKind.Function, "cmd/shop.RequestID", "cmd/shop/middleware.go", "func RequestID()");
        f.Fn(30, SymbolKind.Function, "auth.Required", "internal/auth/auth.go", "func Required(");
        f.Fn(40, SymbolKind.Function, "orders.RegisterPublic", "internal/orders/routes.go", "func RegisterPublic(");
        f.Fn(41, SymbolKind.Function, "orders.Register", "internal/orders/routes.go", "func Register(");
        f.Fn(42, SymbolKind.Function, "orders.audit", "internal/orders/routes.go", "func audit(");
        f.Fn(90, SymbolKind.Function, "cmd/shop.TestRouter", "cmd/shop/main_test.go", "func TestRouter(");

        f.Call(11, "cmd/shop/main.go", "renderMissing(c)", 12, "renderMissing", "cmd/shop.renderMissing");
        // RequestID：newGenerator 在闭包外（构造时）；context.WithValue 是噪音；c.Next 之后的 observe 是后置
        f.Call(20, "cmd/shop/middleware.go", "newGenerator()", 21, "newGenerator", "cmd/shop.newGenerator");
        f.Ext(20, "cmd/shop/middleware.go", "Set(\"id\"", "external:gin.Set", CallContext.Callback);
        f.Call(20, "cmd/shop/middleware.go", "Make()", 22, "idGen.Make", "cmd/shop.idGen.Make", ctx: CallContext.Callback);
        f.Ext(20, "cmd/shop/middleware.go", "WithValue", "external:context.WithValue", CallContext.Callback);
        f.Ext(20, "cmd/shop/middleware.go", "Next()", "external:gin.Next", CallContext.Callback);
        f.Call(20, "cmd/shop/middleware.go", "observe(c, ctx)", 23, "observe", "cmd/shop.observe", ctx: CallContext.Callback);
        // auth.Required：没有 c.Next；AbortWithStatus 在分支里
        f.Call(30, "internal/auth/auth.go", "parseToken(c)", 31, "parseToken", "auth.parseToken", ctx: CallContext.Callback);
        f.Ext(30, "internal/auth/auth.go", "AbortWithStatus", "external:gin.AbortWithStatus", CallContext.Cond);
        f.Ext(30, "internal/auth/auth.go", "Set(\"user\"", "external:gin.Set", CallContext.Callback);
        // audit：defer 写在 c.Next 之前，执行在最后
        f.Ext(42, "internal/orders/routes.go", "Now()", "external:time.Now");
        f.Call(42, "internal/orders/routes.go", "logAccess(c, start)", 43, "logAccess", "orders.logAccess", ctx: CallContext.Callback);
        f.Ext(42, "internal/orders/routes.go", "Next()", "external:gin.Next");
        f.Call(42, "internal/orders/routes.go", "writeAudit(c)", 44, "writeAudit", "orders.writeAudit");

        f.Symbols.Add(new MapSymbol(50, SymbolKind.Function, "orders.list", "internal/orders/routes.go", null, 30, 32));
        f.Symbols.Add(new MapSymbol(51, SymbolKind.Function, "orders.create", "internal/orders/routes.go", null, 34, 36));
        f.Entries.Add(Http(50, "/api/catalog", "GET", "internal/orders/routes.go") with { Line = 6 });   // RegisterPublic 里的 router.GET
        f.Entries.Add(Http(51, "/api/orders", "POST", "internal/orders/routes.go") with { Line = 11 });  // Register 里的 router.POST
        f.Entries.Add(new EntryPoint(10, "shop", "go.main", "hosted", "shop", null, "cmd/shop/main.go", 12));
        return f.Input();
    }

    [Fact]
    public void Gin_DefaultBuiltins_UseOrder_Groups_NextSplit_Tail()
    {
        var f = GoFrameworkFlow.Build(GinFixture());

        Assert.Equal(["middleware", "group", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "路由组中间件", "路由", "收尾"], f.Stages.Select(s => s.Label));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        // gin.Default 的 Logger、Recovery 在最前；注释里的、原始字符串里的、测试文件里的 Use 都不算；注册函数里的 router.Use(audit) 进路由组
        Assert.Equal(
            ["gin.Logger()", "gin.Recovery()", "RequestID()", "gin.CustomRecovery(onPanic)", "auth.Required(true)", "audit", "路由",
             "writeAudit", "logAccess", "gin.CustomRecovery(onPanic)", "observe", "gin.Recovery()", "gin.Logger()"],
            root.Children.Select(c => c.Label));
        Assert.Equal(["middleware", "middleware", "middleware", "middleware", "group", "group", "controller", "tail", "tail", "tail", "tail", "tail", "tail"],
            root.Children.Select(c => c.Actor));

        var logger = root.Children[0];
        Assert.Equal(("external-group", (long?)null), (logger.Kind, logger.SymbolId));
        Assert.Contains("gin.Default() 内置", logger.Note);

        // RequestID：闭包里 c.Next 之前的是前置（context.WithValue 是噪音）；闭包外的 newGenerator 是构造时执行的，不画；回调标记去掉
        var requestId = root.Children[2];
        Assert.Equal((20L, "call", "middleware"), (requestId.SymbolId!.Value, requestId.Kind, requestId.Actor));
        Assert.Equal(["gin.Set", "idGen.Make"], requestId.Children.Select(c => c.Label));
        Assert.All(requestId.Children, c => Assert.Null(c.Note));
        Assert.Contains("构造时的 1 个调用", requestId.Note);
        Assert.Contains("r.Use（main.go:14）", requestId.Note);
        Assert.DoesNotContain("可能中断", requestId.Note);
        Assert.Equal(("observe", "tail", "RequestID 后置"), (root.Children[10].Label, root.Children[10].Actor, root.Children[10].Note));

        // 外部中间件：CustomRecovery 认得出是 panic 恢复，收尾里补一行
        Assert.Equal("external-group", root.Children[3].Kind);
        Assert.Contains("panic", root.Children[9].Note);

        // 路由组：前缀；gin 的 Use 只对之后建的组生效——按注册顺序点名：之前的注册函数（及它注册的路由数，按入口清单）不经过、之后的经过
        var required = root.Children[4];
        Assert.Equal((30L, "group"), (required.SymbolId!.Value, required.Actor));
        Assert.Contains("路由组 /api（api.Use", required.Note);
        Assert.Contains("gin 的 Use 只对这行之后建的组 / 注册的路由生效：之前的 RegisterPublic（/api/catalog，1 条路由） 不经过；之后的 Register（/api/orders，1 条路由） 经过", required.Note);
        Assert.Contains("可能中断（c.AbortWithStatus", required.Note);
        Assert.Contains("没调 c.Next()", required.Note);
        Assert.Equal(["parseToken", "gin.AbortWithStatus", "gin.Set"], required.Children.Select(c => c.Label));
        Assert.Equal([null, "条件分支里", null], required.Children.Select(c => c.Note));

        // 注册函数里的 Use：前缀跟着传进来的组走；defer 写在 c.Next 之前但在最后执行
        var audit = root.Children[5];
        Assert.Equal((42L, "group"), (audit.SymbolId!.Value, audit.Actor));
        Assert.Contains("路由组 /api/orders（router.Use", audit.Note);
        Assert.Equal(["time.Now"], audit.Children.Select(c => c.Label));
        Assert.Equal(["audit 后置", "audit 后置（defer）"], root.Children.Skip(7).Take(2).Select(c => c.Note));

        // 路由占位：HTTP 入口数；NoRoute 挂在下面
        var route = root.Children[6];
        Assert.Equal(("controllers", 2), (route.Kind, route.Count));
        var noRoute = Assert.Single(route.Children);
        Assert.Equal((11L, "notFound"), (noRoute.SymbolId!.Value, noRoute.Label));
        Assert.Contains("没匹配上路由时（r.NoRoute", noRoute.Note);
        Assert.Equal(["renderMissing"], noRoute.Children.Select(c => c.Label));

        // 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序
        Assert.Equal([20L, 30L, 42L], f.Stages.Where(s => s.Key is "middleware" or "group").SelectMany(s => s.Symbols).Select(s => s.Id).Where(id => id != 0));
        Assert.Equal([50L, 51L], f.Stages.Single(s => s.Key == "controller").Symbols.Select(s => s.Id).Order());
        Assert.Empty(f.Handlers);
        Assert.All(All(root), n => Assert.All(n.Children, c => Assert.Equal(n.Depth + 1, c.Depth)));
        Assert.Equal(All(root).Count, f.Flow.Nodes);

        Assert.Contains(f.Notes, n => n.StartsWith("认出 gin（main.go:13 r := gin.Default()）", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("gin.Default() 自带 Logger、Recovery"));
        Assert.Contains(f.Notes, n => n.Contains("全局中间件按 main.go 里 r.Use 的书写顺序"));
        Assert.Contains(f.Notes, n => n.Contains("/api/orders") && n.Contains("路由组中间件 2 个"));
        Assert.Contains(f.Notes, n => n.Contains("另有 1 个非 HTTP 入口"));
        Assert.DoesNotContain(f.Notes, n => n.Contains("个路由器没画")); // main_test.go 里的 gin.New() 不算
    }

    [Fact]
    public void Gin_New_HasNoBuiltins()
    {
        var input = GinFixture();
        var files = new Dictionary<string, string> { ["cmd/shop/main.go"] = GinMain.Replace("gin.Default()", "gin.New()") };
        var src = input.Sources;
        var f = GoFrameworkFlow.Build(input with { Sources = new FrameworkSources { Calls = src.Calls, Signature = src.Signature, ReadFile = p => files.GetValueOrDefault(p) ?? src.ReadFile(p) } });
        Assert.Equal("RequestID()", f.Flow.Root.Children[0].Label);
        Assert.DoesNotContain(f.Flow.Root.Children, c => c.Label.StartsWith("gin.Logger", StringComparison.Ordinal));
        Assert.Contains("gin.New() 不带内置中间件", f.Notes);
    }

    // ================= 夹具二：gorilla mux + 手工包装 =================

    private const string WebMain = """
        // 中文注释：店铺前台
        package main

        import (
        	"net/http"

        	"github.com/gorilla/mux"
        	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
        )

        func main() {
        	r := mux.NewRouter()
        	r.HandleFunc("/", home).Methods(http.MethodGet)
        	r.Use(recordMetrics)
        	api := r.PathPrefix("/api").Subrouter()
        	api.Use(requireJSON)
        	r.NotFoundHandler = http.HandlerFunc(notFound)

        	var handler http.Handler = r
        	handler = &logHandler{next: handler} // 先包的在里层
        	handler = ensureSession(handler)
        	handler = otelhttp.NewHandler(handler, "web")
        	srv := &http.Server{Addr: ":8080", Handler: withTimeout(handler)}
        	srv.ListenAndServe()
        }

        func notFound(w http.ResponseWriter, r *http.Request) {
        	http.Error(w, "missing", 404)
        }
        """;

    private const string WebMiddleware = """
        package main

        type logHandler struct {
        	next http.Handler
        }

        func (h *logHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
        	start := time.Now()
        	rec := &recorder{w: w}
        	defer func() {
        		logRequest(r, rec.status, time.Since(start))
        	}()
        	h.next.ServeHTTP(rec, r)
        	flushTrace(r)
        }

        func ensureSession(next http.Handler) http.HandlerFunc {
        	store := newStore()
        	return func(w http.ResponseWriter, r *http.Request) {
        		id, err := store.Lookup(r)
        		if err != nil {
        			http.Error(w, "bad session", 400)
        			return
        		}
        		next.ServeHTTP(w, r.WithContext(withSession(r.Context(), id)))
        	}
        }

        func recordMetrics(next http.Handler) http.Handler {
        	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        		timer := metrics.Start()
        		next.ServeHTTP(w, r)
        		timer.Stop()
        	})
        }

        func requireJSON(next http.Handler) http.Handler {
        	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        		if r.Header.Get("Content-Type") != "application/json" {
        			http.Error(w, "json only", 415)
        			return
        		}
        		next.ServeHTTP(w, r)
        	})
        }

        func withTimeout(h http.Handler) http.Handler {
        	return http.TimeoutHandler(h, 5, "timeout")
        }
        """;

    private static FrameworkInput WebFixture()
    {
        var f = new Fixture();
        const string main = "web/main.go", mw = "web/middleware.go";
        f.File(main, WebMain);
        f.File(mw, WebMiddleware);
        f.Fn(100, SymbolKind.Function, "web.main", main, "func main()");
        f.Fn(101, SymbolKind.Function, "web.notFound", main, "func notFound(");
        f.Fn(110, SymbolKind.Class, "web.logHandler", mw, "type logHandler struct");
        f.Fn(111, SymbolKind.Method, "web.logHandler.ServeHTTP", mw, "func (h *logHandler) ServeHTTP(", 110);
        f.Fn(120, SymbolKind.Function, "web.ensureSession", mw, "func ensureSession(");
        f.Fn(130, SymbolKind.Function, "web.recordMetrics", mw, "func recordMetrics(");
        f.Fn(140, SymbolKind.Function, "web.requireJSON", mw, "func requireJSON(");
        f.Fn(150, SymbolKind.Function, "web.withTimeout", mw, "func withTimeout(");

        f.Ext(101, main, "Error(w, \"missing\"", "external:http.Error");
        f.Ext(111, mw, "Now()", "external:time.Now");
        f.Call(111, mw, "logRequest(", 160, "logRequest", "web.logRequest", ctx: CallContext.Callback);
        f.Ext(111, mw, "Since(start)", "external:time.Since", CallContext.Callback);
        f.Ext(111, mw, "ServeHTTP(rec, r)", "external:http.ServeHTTP");
        f.Call(111, mw, "flushTrace(r)", 161, "flushTrace", "web.flushTrace");
        f.Call(120, mw, "newStore()", 162, "newStore", "web.newStore");
        f.Call(120, mw, "Lookup(r)", 163, "sessionStore.Lookup", "web.sessionStore.Lookup", ctx: CallContext.Callback);
        f.Ext(120, mw, "Error(w, \"bad session\"", "external:http.Error", CallContext.Cond);
        f.Ext(120, mw, "ServeHTTP(w, r.WithContext", "external:http.ServeHTTP", CallContext.Callback);
        f.Ext(120, mw, "WithContext(withSession", "external:http.WithContext", CallContext.Callback);
        f.Call(120, mw, "withSession(", 164, "withSession", "web.withSession", ctx: CallContext.Callback);
        f.Ext(120, mw, "Context(), id", "external:http.Context", CallContext.Callback);
        f.Ext(130, mw, "Start()", "external:metrics.Start", CallContext.Callback);
        f.Ext(130, mw, "ServeHTTP(w, r)", "external:http.ServeHTTP", CallContext.Callback);
        f.Ext(130, mw, "Stop()", "external:metrics.Stop", CallContext.Callback);
        f.Ext(140, mw, "Get(\"Content-Type\")", "external:http.Get", CallContext.Cond);
        f.Ext(140, mw, "Error(w, \"json only\"", "external:http.Error", CallContext.Cond);
        f.Ext(140, mw, "ServeHTTP(w, r)", "external:http.ServeHTTP", CallContext.Callback, occurrence: 1);
        f.Ext(150, mw, "TimeoutHandler", "external:http.TimeoutHandler");

        f.Entries.Add(new EntryPoint(170, "web", "go.nethttp", "http", "/", "GET", main, 13));
        return f.Input("web");
    }

    [Fact]
    public void Gorilla_ManualWrapChain_LastWrappedRunsFirst_UseAfterMatch_Subrouter()
    {
        var f = GoFrameworkFlow.Build(WebFixture());

        Assert.Equal(["middleware", "group", "controller", "tail"], f.Stages.Select(s => s.Key));
        var root = f.Flow.Root;
        // 最后包的最外层先执行：Server{Handler: withTimeout(h)} → otelhttp → ensureSession → logHandler → 路由器（r.Use 的在路由匹配之后）
        Assert.Equal(
            ["withTimeout", "otelhttp.NewHandler", "ensureSession", "logHandler.ServeHTTP", "recordMetrics", "requireJSON", "路由",
             "metrics.Stop", "flushTrace", "logRequest", "time.Since"],
            root.Children.Select(c => c.Label));

        var timeout = root.Children[0];
        Assert.Equal(150L, timeout.SymbolId);
        Assert.Contains("包在路由器外面的 http.Handler（main.go:23）", timeout.Note);
        Assert.Contains("没找到 next 调用", timeout.Note);
        Assert.Equal("external-group", root.Children[1].Kind);

        // ensureSession：闭包外的 newStore 不画；next 实参里的 withSession 在 next 之前求值，算前置；next 之前有 return → 可能中断
        var session = root.Children[2];
        Assert.Equal(["sessionStore.Lookup", "http.Error", "withSession"], session.Children.Select(c => c.Label));
        Assert.Equal([null, "条件分支里", null], session.Children.Select(c => c.Note));
        Assert.Contains("可能中断（next 之前有 return", session.Note);
        Assert.Contains("构造时的 1 个调用", session.Note);

        // logHandler：&T{next: h} 落到它的 ServeHTTP；defer 里的进收尾，排在普通后置之后
        var log = root.Children[3];
        Assert.Equal((111L, "middleware"), (log.SymbolId!.Value, log.Actor));
        Assert.Equal(["time.Now"], log.Children.Select(c => c.Label));
        Assert.DoesNotContain("可能中断", log.Note);
        Assert.Equal(["recordMetrics 后置", "logHandler 后置", "logHandler 后置（defer）", "logHandler 后置（defer）"], root.Children.Skip(7).Select(c => c.Note));

        Assert.Equal(["metrics.Start"], root.Children[4].Children.Select(c => c.Label));
        var json = root.Children[5];
        Assert.Equal((140L, "group"), (json.SymbolId!.Value, json.Actor));
        Assert.Contains("路由组 /api（api.Use", json.Note);
        Assert.Contains("可能中断", json.Note);

        // NotFoundHandler = http.HandlerFunc(notFound)：类型转换里的那个函数
        var notFound = Assert.Single(root.Children[6].Children);
        Assert.Equal((101L, "notFound"), (notFound.SymbolId!.Value, notFound.Label));
        Assert.Contains("r.NotFoundHandler", notFound.Note);

        Assert.Contains(f.Notes, n => n.StartsWith("认出 gorilla/mux（main.go:12", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("手工包装 http.Handler：最后包的最先执行，实际顺序 withTimeout → otelhttp.NewHandler → ensureSession → logHandler.ServeHTTP → 路由器"));
        Assert.Contains(f.Notes, n => n.Contains("gorilla/mux 的 Use 在路由匹配上之后"));
    }

    // ================= 夹具三：echo =================

    private const string EchoMain = """
        package main

        import (
        	"github.com/labstack/echo/v4"
        	"github.com/labstack/echo/v4/middleware"
        )

        func main() {
        	e := echo.New()
        	e.Pre(middleware.RemoveTrailingSlash())
        	e.Use(middleware.Logger(), tenant)
        	admin := e.Group("/admin", middleware.BasicAuth(check))
        	admin.GET("/stats", stats)
        	e.HTTPErrorHandler = renderError
        	e.Logger.Fatal(e.Start(":1323"))
        }

        func tenant(next echo.HandlerFunc) echo.HandlerFunc {
        	return func(c echo.Context) error {
        		id := lookupTenant(c)
        		if id == "" {
        			return echo.ErrBadRequest
        		}
        		err := next(c)
        		releaseTenant(id)
        		return err
        	}
        }

        func renderError(err error, c echo.Context) {
        	writeProblem(c, err)
        }
        """;

    [Fact]
    public void Echo_PreBeforeUse_GroupArgs_TextNext_ErrorHandlerInTail()
    {
        var f = new Fixture();
        const string main = "api/main.go";
        f.File(main, EchoMain);
        f.Fn(200, SymbolKind.Function, "api.main", main, "func main()");
        f.Fn(210, SymbolKind.Function, "api.tenant", main, "func tenant(");
        f.Fn(220, SymbolKind.Function, "api.renderError", main, "func renderError(");
        // next(c) 调的是参数：索引里没有这条边，按源码文本认
        f.Call(210, main, "lookupTenant(c)", 211, "lookupTenant", "api.lookupTenant", ctx: CallContext.Callback);
        f.Call(210, main, "releaseTenant(id)", 212, "releaseTenant", "api.releaseTenant", ctx: CallContext.Callback);
        f.Call(220, main, "writeProblem(c, err)", 221, "writeProblem", "api.writeProblem");
        f.Entries.Add(new EntryPoint(230, "shop", "go.echo", "http", "/admin/stats", "GET", main, 13));
        var flow = GoFrameworkFlow.Build(f.Input());

        Assert.Equal(["middleware", "group", "controller", "tail"], flow.Stages.Select(s => s.Key));
        var root = flow.Flow.Root;
        Assert.Equal(
            ["middleware.RemoveTrailingSlash()", "middleware.Logger()", "tenant", "middleware.BasicAuth(check)", "路由", "releaseTenant", "middleware.Logger()", "renderError"],
            root.Children.Select(c => c.Label));
        Assert.Contains("路由匹配之前执行", root.Children[0].Note);
        var tenant = root.Children[2];
        Assert.Equal(["lookupTenant"], tenant.Children.Select(c => c.Label));
        Assert.Contains("可能中断（next 之前有 return", tenant.Note);
        Assert.Contains("路由组 /admin（e.Group 的参数", root.Children[3].Note);
        Assert.Equal("tenant 后置", root.Children[5].Note);
        Assert.Contains("访问日志", root.Children[6].Note);
        var error = root.Children[7];
        Assert.Equal((220L, "tail"), (error.SymbolId!.Value, error.Actor));
        Assert.Contains("e.HTTPErrorHandler", error.Note);
        Assert.Equal(["writeProblem"], error.Children.Select(c => c.Label));
        Assert.Contains(flow.Notes, n => n.Contains("e.Pre 的 1 个在路由匹配之前执行"));
    }

    // ================= 夹具四：chi =================

    private const string ChiRoutes = """
        package server

        import (
        	"net/http"

        	"github.com/go-chi/chi/v5"
        	"github.com/go-chi/chi/v5/middleware"
        )

        func routes() http.Handler {
        	r := chi.NewRouter()
        	r.Use(middleware.RequestID, middleware.Recoverer)
        	r.Get("/health", health)
        	r.Route("/api", func(r chi.Router) {
        		r.Use(apiKey)
        		r.Group(func(r chi.Router) {
        			r.Use(adminOnly)
        		})
        	})
        	return r
        }

        func apiKey(next http.Handler) http.Handler {
        	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        		next.ServeHTTP(w, r)
        	})
        }

        func adminOnly(next http.Handler) http.Handler {
        	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        		next.ServeHTTP(w, r)
        	})
        }
        """;

    [Fact]
    public void Chi_RouteClosureShadowsRouter_UseInsideIsGroupLevel()
    {
        var f = new Fixture();
        const string path = "internal/server/routes.go";
        f.File(path, ChiRoutes);
        f.Fn(300, SymbolKind.Function, "server.routes", path, "func routes()");
        f.Fn(310, SymbolKind.Function, "server.apiKey", path, "func apiKey(");
        f.Fn(320, SymbolKind.Function, "server.adminOnly", path, "func adminOnly(");
        var flow = GoFrameworkFlow.Build(f.Input());

        var root = flow.Flow.Root;
        Assert.Equal(["middleware.RequestID", "middleware.Recoverer", "apiKey", "adminOnly", "路由", "middleware.Recoverer"], root.Children.Select(c => c.Label));
        Assert.Equal(["middleware", "middleware", "group", "group", "controller", "tail"], root.Children.Select(c => c.Actor));
        Assert.Contains("路由组 /api（r.Use", root.Children[2].Note);
        Assert.Contains("路由组 /api（r.Use", root.Children[3].Note);
        Assert.Contains("panic", root.Children[5].Note);
        Assert.Equal(0, root.Children[4].Count);
        Assert.Contains(flow.Notes, n => n.Contains("chi 要求 Use 写在所有路由之前"));
        Assert.Contains(flow.Notes, n => n.StartsWith("没有发现 HTTP 入口", StringComparison.Ordinal));
    }

    // ================= DefaultServeMux 与非 HTTP 容器 =================

    [Fact]
    public void DefaultServeMux_WrappedInListenAndServe()
    {
        var f = new Fixture();
        const string path = "main.go";
        f.File(path, """
            package main

            import "net/http"

            func main() {
            	http.HandleFunc("/", index)
            	http.ListenAndServe(":80", logging(http.DefaultServeMux))
            }

            func logging(next http.Handler) http.Handler {
            	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
            		next.ServeHTTP(w, r)
            		record(r)
            	})
            }
            """);
        f.Fn(400, SymbolKind.Function, "main", path, "func main()");
        f.Fn(410, SymbolKind.Function, "logging", path, "func logging(");
        f.Ext(410, path, "ServeHTTP(w, r)", "external:http.ServeHTTP", CallContext.Callback);
        f.Call(410, path, "record(r)", 411, "record", "record", ctx: CallContext.Callback);
        f.Entries.Add(new EntryPoint(420, "shop", "go.nethttp", "http", "/", null, path, 6));
        var flow = GoFrameworkFlow.Build(f.Input());

        Assert.Equal(["logging", "路由", "record"], flow.Flow.Root.Children.Select(c => c.Label));
        Assert.Equal("logging 后置", flow.Flow.Root.Children[2].Note);
        Assert.Contains(flow.Notes, n => n.StartsWith("没有显式的路由器：处理函数注册在 http.DefaultServeMux 上", StringComparison.Ordinal));
    }

    [Fact]
    public void NotHttpService_ReturnsNoteOnly()
    {
        var f = new Fixture();
        f.File("main.go", "package main\n\nfunc main() {\n\tserveGrpc()\n}\n");
        f.Fn(500, SymbolKind.Function, "main", "main.go", "func main()");
        f.Entries.Add(new EntryPoint(500, "shop", "go.main", "hosted", "shop", null, "main.go", 3));
        var flow = GoFrameworkFlow.Build(f.Input());
        Assert.Empty(flow.Stages);
        Assert.Contains(flow.Notes, n => n.StartsWith("这个容器没有 HTTP 入口，也没认出 Go Web 框架", StringComparison.Ordinal));
        Assert.Contains(flow.Notes, n => n.Contains("hosted 1"));
    }

    [Fact]
    public void Dispatch_GoServiceGoesToGoBuilder()
    {
        var flow = FrameworkFlowBuilder.Build(GinFixture());
        Assert.Contains(flow.Notes, n => n.StartsWith("认出 gin", StringComparison.Ordinal));
    }

    [Fact]
    public void StripGo_BlanksRawStringsCommentsAndRunes_KeepsOffsets()
    {
        const string text = "a := `x // \"y\n r.Use(z)` // c\nb := \"q\\\"(\" + '('\n/* r.Use(w) */ c()";
        var s = GoFrameworkFlow.StripGo(text);
        Assert.Equal(text.Length, s.Length);
        Assert.Equal(text.Split('\n').Length, s.Split('\n').Length);
        Assert.DoesNotContain("Use", s);
        Assert.DoesNotContain("(", s[..s.LastIndexOf('c')]);
        Assert.EndsWith("c()", s);
    }
}
