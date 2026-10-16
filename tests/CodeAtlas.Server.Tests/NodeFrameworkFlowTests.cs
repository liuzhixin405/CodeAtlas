using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · Node（NodeFrameworkFlow）：Express（app.use 书写顺序、registerSecurity(app) 摊进来、路由挂载与 router 级中间件、next 前后拆分、
/// res.on('finish')、路由之后的 404、四个参数的错误中间件）、Koa（洋葱、try/catch、koa-router 前缀、app.on('error')）、
/// NestJS（main.ts 全局注册、APP_* provider、consumer.apply、固定生命周期、next.handle() 拆分、@Catch、控制器级装饰器统计）、
/// Fastify（钩子按生命周期、fastify-plugin 与封装作用域、第三方插件）、纯 gRPC 与认不出框架的容器。
/// 夹具里调用的位置（pos）按源码文本算，与 tree-sitter 一致：pos 是被调用名字的偏移。
/// </summary>
public class NodeFrameworkFlowTests
{
    // ================= 夹具构造 =================

    /// <summary>一次调用：needle 是源码里的一段（以调用名加 '(' 结尾，例如 "logger.debug("），目标为 null 时是外部调用（索引器记成 external:名字）。</summary>
    private sealed record C(string Needle, MapSymbol? Target = null, string? Ctx = null);

    private sealed class Fx(string key, long baseId)
    {
        public readonly string Key = key;
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public readonly List<MapSymbol> Symbols = [];
        public readonly List<MapEdge> Edges = [];
        public readonly Dictionary<long, List<FrameworkCall>> Calls = [];
        public readonly List<EntryPoint> Entries = [];
        private readonly Dictionary<string, long> _modules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _externals = new(StringComparer.Ordinal);
        private long _next = baseId;

        public string P(string rel) => Key + "/" + rel;

        public void File(string rel, string text)
        {
            var path = P(rel);
            text = text.Replace("\r\n", "\n");
            Files[path] = text;
            if (rel.EndsWith(".json", StringComparison.Ordinal)) return;
            var id = _next++;
            _modules[path] = id;
            Symbols.Add(new MapSymbol(id, SymbolKind.Module, path, path, null, 1, text.Count(c => c == '\n') + 1));
        }

        public long ModuleId(string rel) => _modules[P(rel)];

        /// <summary>函数 / 方法：从 marker 所在行起，到它的体（marker 之后第一个 ") {" / "=&gt; {" / "): T {"）配对的 '}' 止。</summary>
        public MapSymbol Fn(string rel, string name, string marker, MapSymbol? owner = null, string? vis = null, SymbolKind kind = SymbolKind.Function)
        {
            var path = P(rel);
            var text = Files[path];
            var from = owner is null ? 0 : Offset(text, owner.StartLine);
            var at = text.IndexOf(marker, from, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{rel} 里没有 {marker}");
            var body = Regex.Match(text[at..], @"\)\s*(?::\s*[^{;=]+)?\{|=>\s*\{");
            Assert.True(body.Success, $"{marker} 后面没有函数体");
            var end = MatchBrace(text, at + body.Index + body.Length - 1);
            var qn = owner is null ? $"{path}:{name}" : $"{owner.QualifiedName}.{name}";
            var s = new MapSymbol(_next++, kind, qn, path, owner?.Id ?? _modules[path], Line(text, at), Line(text, end), vis);
            Symbols.Add(s);
            return s;
        }

        public MapSymbol Class(string rel, string name, string marker)
        {
            var path = P(rel);
            var text = Files[path];
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{rel} 里没有 {marker}");
            var end = MatchBrace(text, text.IndexOf('{', at));
            var s = new MapSymbol(_next++, SymbolKind.Class, $"{path}:{name}", path, _modules[path], Line(text, at), Line(text, end), "export");
            Symbols.Add(s);
            return s;
        }

        public MapSymbol Method(MapSymbol cls, string name, string marker) => Fn(cls.Path[(Key.Length + 1)..], name, marker, cls, null, SymbolKind.Method);

        /// <summary>符号体里的调用（Sources.Calls 给的那份），按 needle 在符号范围里依次往后找。</summary>
        public void CallsOf(MapSymbol fn, params C[] calls)
        {
            var text = Files[fn.Path];
            var from = Offset(text, fn.StartLine);
            Calls[fn.Id] = calls.Select(c => Make(text, c, ref from)).ToList();
        }

        /// <summary>文件模块的调用边（顶层代码里内联函数的调用归模块），从 after 之后依次往后找。</summary>
        public void ModuleCalls(string rel, string after, params C[] calls)
        {
            var path = P(rel);
            var text = Files[path];
            var from = text.IndexOf(after, StringComparison.Ordinal);
            Assert.True(from >= 0);
            foreach (var c in calls)
            {
                var call = Make(text, c, ref from);
                Edges.Add(new MapEdge(_modules[path], call.TargetId!.Value, EdgeKind.Calls, 1.0, null, call.Line, call.Pos));
            }
        }

        public void Entry(MapSymbol handler, string kind, string route, string? method, string rel, string marker)
        {
            var path = P(rel);
            var at = Files[path].IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{rel} 里没有 {marker}");
            Entries.Add(new EntryPoint(handler.Id, Key, "node.test", kind, route, method, path, Line(Files[path], at)));
        }

        public FrameworkInput Input() => new(Symbols, Edges,
            new ArchNode(Key, ArchNodeKind.Container, "node-service", Key, Key, ArchSource.Discovered, [], [Key], []), Entries, [],
            new FrameworkSources
            {
                Calls = id => Calls.TryGetValue(id, out var l) ? l : [],
                Signature = _ => null,
                ReadFile = p => Files.GetValueOrDefault(p),
            });

        private FrameworkCall Make(string text, C c, ref int from)
        {
            var idx = text.IndexOf(c.Needle, from, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"找不到调用 {c.Needle}");
            var callee = Regex.Replace(c.Needle.TrimEnd('('), @"<[^<>]*>$", "");
            var name = Regex.Match(callee, @"[\w$]+$").Value;
            var pos = idx + callee.Length - name.Length;
            from = pos + 1;
            var line = Line(text, pos);
            if (c.Target is not { } t)
            {
                if (!_externals.TryGetValue(name, out var ext))
                {
                    _externals[name] = ext = _next++;
                    Symbols.Add(new MapSymbol(ext, SymbolKind.External, "external:" + name, "<external>", null));
                }
                return new FrameworkCall(ext, "external", name, "external:" + name, line, pos, c.Ctx, false, []);
            }
            var owner = t.ParentId is { } p ? Symbols.FirstOrDefault(s => s.Id == p && s.Kind == SymbolKind.Class) : null;
            return new FrameworkCall(t.Id, "call", owner is null ? t.Name : owner.Name + "." + t.Name, t.QualifiedName, line, pos, c.Ctx, true, []);
        }

        private static int Line(string text, int pos) => text.AsSpan(0, Math.Min(pos, text.Length)).Count('\n') + 1;

        private static int Offset(string text, int line)
        {
            var k = 0;
            for (var l = 1; l < line; l++) k = text.IndexOf('\n', k) + 1;
            return k;
        }

        private static int MatchBrace(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return i;
            }
            return text.Length - 1;
        }
    }

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    private static void AssertTree(FrameworkFlowDto f)
    {
        Assert.All(All(f.Flow.Root), n => Assert.All(n.Children, c => Assert.Equal(n.Depth + 1, c.Depth)));
        Assert.Equal(All(f.Flow.Root).Count, f.Flow.Nodes);
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key).Where(k => f.Stages.Any(s => s.Key == k)));
        Assert.All(All(f.Flow.Root), n => Assert.Contains(f.Flow.Actors, a => a.Key == n.Actor));
    }

    // ================= Express =================

    private static Fx ExpressFixture()
    {
        var fx = new Fx("shop-api", 1000);
        fx.File("package.json", """
            {
              "name": "shop-api",
              "main": "src/server.js",
              "scripts": { "start": "node src/server.js" },
              "dependencies": { "express": "^4.19.2", "cors": "^2.8.5", "helmet": "^7.1.0", "hpp": "^0.2.3" }
            }
            """);
        fx.File("src/server.js", """
            const app = require('./app');
            const logger = require('./lib/logger');

            const port = process.env.PORT || 3000;
            app.listen(port, () => logger.info(`shop-api listening on ${port}`));
            """);
        fx.File("src/app.js", """
            'use strict';
            const express = require('express');
            const cors = require('cors');
            const helmet = require('helmet');
            const { requestId } = require('./middleware/requestId');
            const requestLogger = require('./middleware/requestLogger');
            const rateLimit = require('./middleware/rateLimit');
            const notFound = require('./middleware/notFound');
            const errorHandler = require('./middleware/errorHandler');
            const registerSecurity = require('./config/security');
            const apiRouter = require('./routes');
            const metrics = require('./lib/metrics');

            const app = express();
            const banner = 'app.use(debugPanel)';

            app.use(helmet());
            // app.use(compression());
            /* app.use(legacyCookies()); */
            app.use(cors({ origin: process.env.CORS_ORIGIN, credentials: true }));
            app.use(express.json({ limit: '1mb' }));
            app.use(requestId);
            app.use(requestLogger);
            registerSecurity(app);
            app.use((req, res, next) => {
              res.locals.startedAt = Date.now();
              metrics.inFlight(1);
              next();
              metrics.inFlight(-1);
            });

            app.get('/health', (req, res) => res.json({ status: 'ok', banner }));
            app.use('/api', rateLimit({ windowMs: 60000, max: 120 }), apiRouter);
            app.use(notFound);
            app.use(errorHandler);

            module.exports = app;
            """);
        fx.File("src/middleware/requestId.js", """
            const { randomUUID } = require('crypto');

            function requestId(req, res, next) {
              const id = req.get('x-request-id') || randomUUID();
              req.id = id;
              res.setHeader('x-request-id', id);
              next();
            }

            module.exports = { requestId };
            """);
        fx.File("src/middleware/requestLogger.js", """
            const logger = require('../lib/logger');

            module.exports = function requestLogger(req, res, next) {
              const started = Date.now();
              logger.debug(`--> ${req.method} ${req.originalUrl}`);
              res.on('finish', () => {
                logger.info({ status: res.statusCode, ms: Date.now() - started }, 'request completed');
              });
              next();
            };
            """);
        fx.File("src/config/security.js", """
            const hpp = require('hpp');
            const { verifyApiKey } = require('../middleware/apiKey');

            module.exports = function registerSecurity(app) {
              app.disable('x-powered-by');
              app.use(hpp());
              app.use(verifyApiKey);
            };
            """);
        fx.File("src/middleware/apiKey.js", """
            const keyStore = require('../lib/keyStore');

            exports.verifyApiKey = function verifyApiKey(req, res, next) {
              if (req.path.startsWith('/health')) return next();
              const key = req.get('x-api-key');
              if (!keyStore.isValid(key)) {
                return res.status(401).json({ error: 'invalid_api_key' });
              }
              next();
            };
            """);
        fx.File("src/middleware/rateLimit.js", """
            const counter = require('../lib/counter');

            module.exports = function rateLimit({ windowMs, max }) {
              return (req, res, next) => {
                const hits = counter.hit(req.ip, windowMs);
                if (hits > max) {
                  res.set('Retry-After', String(Math.ceil(windowMs / 1000)));
                  return res.status(429).json({ error: 'too_many_requests' });
                }
                next();
              };
            };
            """);
        fx.File("src/middleware/notFound.js", """
            module.exports = function notFound(req, res) {
              res.status(404).json({ error: 'not_found', path: req.originalUrl });
            };
            """);
        fx.File("src/middleware/errorHandler.js", """
            const logger = require('../lib/logger');

            module.exports = function errorHandler(err, req, res, next) {
              if (res.headersSent) {
                return next(err);
              }
              const status = err.status || 500;
              logger.error({ err, reqId: req.id }, 'unhandled error');
              res.status(status).json({ error: status >= 500 ? 'internal_error' : err.message });
            };
            """);
        fx.File("src/middleware/auth.js", """
            const tokens = require('../lib/tokens');

            async function authenticate(req, res, next) {
              try {
                req.user = await tokens.verify(req.get('authorization'));
                next();
              } catch (err) {
                next(err);
              }
            }

            module.exports = { authenticate };
            """);
        fx.File("src/routes/index.js", """
            const express = require('express');
            const { authenticate } = require('../middleware/auth');
            const ordersRouter = require('./orders');
            const productsRouter = require('./products');

            const router = express.Router();

            router.use(authenticate);
            router.use('/orders', ordersRouter);
            router.use('/products', productsRouter);

            module.exports = router;
            """);
        fx.File("src/routes/orders.js", """
            const express = require('express');
            const { listOrders, createOrder } = require('../controllers/orders');
            const validateBody = require('../middleware/validateBody');

            const router = express.Router();

            router.use(express.urlencoded({ extended: false }));
            router.get('/', listOrders);
            router.post('/', validateBody('order'), createOrder);

            module.exports = router;
            """);
        fx.File("src/routes/products.js", """
            const { Router } = require('express');
            const { listProducts } = require('../controllers/products');

            const router = Router();
            router.get('/', listProducts);

            module.exports = router;
            """);
        fx.File("src/controllers/orders.js", """
            exports.listOrders = async function listOrders(req, res) {
              res.json(await req.services.orders.list(req.user.id));
            };

            exports.createOrder = async function createOrder(req, res) {
              res.status(201).json(await req.services.orders.create(req.body));
            };
            """);
        fx.File("src/controllers/products.js", """
            exports.listProducts = async function listProducts(req, res) {
              res.json(await req.services.catalog.search(req.query));
            };
            """);

        var requestId = fx.Fn("src/middleware/requestId.js", "requestId", "function requestId(", vis: "export");
        fx.CallsOf(requestId, new C("req.get("), new C("randomUUID("), new C("res.setHeader("), new C("next("));
        var requestLogger = fx.Fn("src/middleware/requestLogger.js", "requestLogger", "function requestLogger(", vis: "export default");
        fx.CallsOf(requestLogger, new C("Date.now("), new C("logger.debug("), new C("res.on("), new C("logger.info(", Ctx: CallContext.Callback),
            new C("Date.now(", Ctx: CallContext.Callback), new C("next("));
        var registerSecurity = fx.Fn("src/config/security.js", "registerSecurity", "function registerSecurity(", vis: "export default");
        fx.CallsOf(registerSecurity, new C("app.disable("), new C("app.use("), new C("hpp("), new C("app.use("));
        var verifyApiKey = fx.Fn("src/middleware/apiKey.js", "verifyApiKey", "function verifyApiKey(", vis: "export");
        fx.CallsOf(verifyApiKey, new C("startsWith("), new C("next(", Ctx: CallContext.Cond), new C("req.get("), new C("keyStore.isValid("),
            new C("res.status(", Ctx: CallContext.Cond), new C(".json(", Ctx: CallContext.Cond), new C("next("));
        var rateLimit = fx.Fn("src/middleware/rateLimit.js", "rateLimit", "function rateLimit(", vis: "export default");
        fx.CallsOf(rateLimit, new C("counter.hit(", Ctx: CallContext.Callback), new C("res.set(", Ctx: CallContext.Callback), new C("String(", Ctx: CallContext.Callback),
            new C("Math.ceil(", Ctx: CallContext.Callback), new C("res.status(", Ctx: CallContext.Callback), new C(".json(", Ctx: CallContext.Callback), new C("next(", Ctx: CallContext.Callback));
        var notFound = fx.Fn("src/middleware/notFound.js", "notFound", "function notFound(", vis: "export default");
        fx.CallsOf(notFound, new C("res.status("), new C(".json("));
        var errorHandler = fx.Fn("src/middleware/errorHandler.js", "errorHandler", "function errorHandler(", vis: "export default");
        fx.CallsOf(errorHandler, new C("next(", Ctx: CallContext.Cond), new C("logger.error("), new C("res.status("), new C(".json("));
        var authenticate = fx.Fn("src/middleware/auth.js", "authenticate", "async function authenticate(");
        fx.CallsOf(authenticate, new C("tokens.verify(", Ctx: CallContext.Try), new C("req.get(", Ctx: CallContext.Try), new C("next(", Ctx: CallContext.Try), new C("next(", Ctx: CallContext.Catch));
        var listOrders = fx.Fn("src/controllers/orders.js", "listOrders", "function listOrders(", vis: "export");
        var createOrder = fx.Fn("src/controllers/orders.js", "createOrder", "function createOrder(", vis: "export");
        var listProducts = fx.Fn("src/controllers/products.js", "listProducts", "function listProducts(", vis: "export");

        // app.js 顶层的内联中间件与 /health 处理函数：调用记在文件模块上
        fx.ModuleCalls("src/app.js", "app.use((req, res, next)", new C("Date.now("), new C("metrics.inFlight("), new C("next("), new C("metrics.inFlight("), new C("res.json("));

        fx.Entry(fx.Symbols.Single(s => s.Kind == SymbolKind.Module && s.Path == fx.P("src/app.js")), "http", "/health", "GET", "src/app.js", "app.get('/health'");
        fx.Entry(listOrders, "http", "/api/orders", "GET", "src/routes/orders.js", "router.get('/'");
        fx.Entry(createOrder, "http", "/api/orders", "POST", "src/routes/orders.js", "router.post('/'");
        fx.Entry(listProducts, "http", "/api/products", "GET", "src/routes/products.js", "router.get('/'");
        return fx;
    }

    [Fact]
    public void Express_UseOrder_Helpers_Mounts_NextSplit_Tail()
    {
        var fx = ExpressFixture();
        var f = FrameworkFlowBuilder.Build(fx.Input());
        AssertTree(f);

        Assert.Equal(["middleware", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "路由", "收尾"], f.Stages.Select(s => s.Label));
        Assert.True(f.Stages[0].OrderKnown);
        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        // 书写顺序；注释、字符串里的 app.use 不算；registerSecurity(app) 里的两个按调用位置摊进来
        Assert.Equal(["helmet", "cors", "express.json", "requestId", "requestLogger", "hpp", "verifyApiKey", "app.use（内联中间件）", "rateLimit()", "路由"],
            root.Children.Select(c => c.Label));
        Assert.All(root.Children.Take(9), c => Assert.Equal("middleware", c.Actor));

        var helmet = root.Children[0];
        Assert.Equal(("external-group", (long?)null, "第三方中间件（helmet）（app.js:17）"), (helmet.Kind, helmet.SymbolId, helmet.Note));
        Assert.Equal("Express 内置（app.js:21）", root.Children[2].Note);

        var reqId = root.Children[3];
        Assert.Equal(("call", fx.Symbols.Single(s => s.Name == "requestId" && s.Kind == SymbolKind.Function).Id), (reqId.Kind, reqId.SymbolId!.Value));
        Assert.Equal("自研中间件（app.js:22）", reqId.Note);
        Assert.Equal(["req.get", "randomUUID", "res.setHeader"], reqId.Children.Select(c => c.Label)); // next 之前；接收者从源码补上

        var logger = root.Children[4];
        Assert.Equal(["logger.debug"], logger.Children.Select(c => c.Label)); // res.on 本身与 finish 回调里的不在前置

        var hpp = root.Children[5];
        Assert.Equal("经 registerSecurity()；第三方中间件（hpp）（security.js:6）", hpp.Note);
        var apiKey = root.Children[6];
        Assert.StartsWith("经 registerSecurity()；自研中间件（security.js:7）", apiKey.Note);
        Assert.Contains("有条件", apiKey.Note);
        Assert.Equal(["req.get", "keyStore.isValid", "res.status", "json"], apiKey.Children.Select(c => c.Label));
        Assert.Equal("条件分支里", apiKey.Children[2].Note);

        var inline = root.Children[7];
        Assert.Equal(("external-group", (long?)null), (inline.Kind, inline.SymbolId));
        Assert.Equal(["metrics.inFlight"], inline.Children.Select(c => c.Label)); // Date.now 是噪声；/health 里的 res.json 不在这个内联函数里
        Assert.DoesNotContain("有条件", inline.Note);

        var limiter = root.Children[8];
        Assert.Equal(fx.Symbols.Single(s => s.Name == "rateLimit").Id, limiter.SymbolId);
        Assert.Contains("只对 /api 生效（app.js:33）", limiter.Note);
        Assert.Contains("中间件工厂", limiter.Note);
        Assert.Contains("有条件", limiter.Note);                       // 分支里直接 return，不调 next
        Assert.Contains("注册在 GET /health 之后", limiter.Note);
        Assert.Equal(["counter.hit", "res.set", "res.status", "json"], limiter.Children.Select(c => c.Label));
        Assert.Null(limiter.Children[0].Note);                          // 内联函数自己的体里：不是"回调里"
        Assert.Equal("条件分支里", limiter.Children[1].Note);

        // 路由占位：挂载 → 后置（倒序）→ 路由之后 → 出错时 → 响应发完时
        var routes = root.Children[9];
        Assert.Equal(("controllers", 4, "controller"), (routes.Kind, routes.Count, routes.Actor));
        Assert.Equal(["挂载 /api（apiRouter）", "metrics.inFlight", "notFound", "errorHandler", "logger.info"], routes.Children.Select(c => c.Label));
        var mount = routes.Children[0];
        Assert.Equal(("call", "controller", fx.ModuleId("src/routes/index.js")), (mount.Kind, mount.Actor, mount.SymbolId!.Value));
        Assert.Equal("路由挂载（app.js:33）", mount.Note);
        Assert.Equal(["authenticate", "挂载 /api/orders（ordersRouter）", "挂载 /api/products（productsRouter）"], mount.Children.Select(c => c.Label));
        var auth = mount.Children[0];
        Assert.Equal(("middleware", "router 级中间件（只对 /api 下的路由生效，index.js:8）"), (auth.Actor, auth.Note));
        Assert.Equal(["tokens.verify", "req.get"], auth.Children.Select(c => c.Label)); // catch 里的 next(err) 不是切分点
        var orders = mount.Children[1];
        Assert.Equal("路由挂载（index.js:9）：orders.js 里 2 条路由", orders.Note);
        var urlencoded = Assert.Single(orders.Children);
        Assert.Equal(("express.urlencoded", "middleware"), (urlencoded.Label, urlencoded.Actor));
        Assert.Contains("只对 /api/orders 下的路由生效", urlencoded.Note);
        Assert.Empty(mount.Children[2].Children);

        var tail = routes.Children.Skip(1).ToList();
        Assert.All(tail, t => Assert.Equal("tail", t.Actor));
        Assert.Equal("app.use（内联中间件） 后置", tail[0].Note);
        Assert.StartsWith("路由之后注册（app.js:34）", tail[1].Note);
        Assert.Contains("404 兜底", tail[1].Note);
        Assert.Equal(["res.status", "json"], tail[1].Children.Select(c => c.Label));
        Assert.Equal("出错时（四个参数的错误中间件，app.js:35）", tail[2].Note);
        Assert.Equal(["logger.error", "res.status", "json"], tail[2].Children.Select(c => c.Label));
        Assert.Equal("requestLogger：响应发完时（res.on('finish')） · 回调 / lambda 里", tail[3].Note);

        // 阶段的符号（ 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序）
        Assert.Equal(["requestId", "requestLogger", "verifyApiKey", "rateLimit", "authenticate"], f.Stages[0].Symbols.Where(s => s.Id != 0).Select(s => s.Name));
        Assert.Equal(["helmet", "cors", "express.json"], f.Stages[0].Symbols.Take(3).Select(s => (s.Id, s.Kind, s.Name)).Select(t => t.Id == 0 && t.Kind == "external" ? t.Name : "?"));
        Assert.Equal(4, f.Stages[1].Symbols.Count);
        Assert.Contains(f.Stages[2].Symbols, s => s.Name == "errorHandler");

        Assert.Equal("认出 Express（app.js:14 的 app = express()）", f.Notes[0]);
        Assert.Contains(f.Notes, n => n.StartsWith("中间件按 app.js、security.js 里 app.use(...) 的书写顺序") && n.Contains("registerSecurity(app)"));
        Assert.Contains(f.Notes, n => n.StartsWith("路由挂载 3 处") && n.Contains("router 级中间件 2 个"));
        Assert.Contains(f.Notes, n => n.StartsWith("路由级中间件 validateBody('order')") && n.Contains("1 条路由"));
        Assert.Contains(f.Notes, n => n.StartsWith("notFound 注册在路由之后"));
        Assert.Contains(f.Notes, n => n == "出错时：errorHandler（四个参数的错误中间件，按注册顺序）");
    }

    [Fact]
    public void Express_BranchOnlyNext_NoErrorMiddleware_NoMounts_SaysSo()
    {
        var fx = new Fx("tiny-api", 3000);
        fx.File("package.json", """{ "dependencies": { "express": "4.19.2" } }""");
        fx.File("index.js", """
            const express = require('express');
            const flags = require('./flags');
            const app = express();
            app.use(express.json());
            app.use(maintenance);
            app.get('/ping', (req, res) => res.send('pong'));
            app.listen(8080);

            function maintenance(req, res, next) {
              if (!flags.isOn('maintenance')) {
                next();
              } else {
                res.status(503).send('down for maintenance');
              }
              audit.record(req.path);
            }
            """);
        var maintenance = fx.Fn("index.js", "maintenance", "function maintenance(");
        fx.CallsOf(maintenance, new C("flags.isOn("), new C("next(", Ctx: CallContext.Cond), new C("res.status(", Ctx: CallContext.Cond), new C(".send(", Ctx: CallContext.Cond),
            new C("audit.record("));
        fx.Entry(fx.Symbols.Single(s => s.Kind == SymbolKind.Module), "http", "/ping", "GET", "index.js", "app.get(");
        var f = FrameworkFlowBuilder.Build(fx.Input());
        AssertTree(f);
        Assert.Equal(["express.json", "maintenance", "路由"], f.Flow.Root.Children.Select(c => c.Label));
        // next 只在 if 分支里（没有提前 return）：有条件；else 分支里的是不调 next 的那条路，算前置；if 之后的照常算后置
        var mw = f.Flow.Root.Children[1];
        Assert.Equal("自研中间件（index.js:5）；有条件（不是每条路都调 next：有分支直接返回 / 抛异常）", mw.Note);
        Assert.Equal(["flags.isOn", "res.status", "send"], mw.Children.Select(c => c.Label));
        var post = Assert.Single(f.Flow.Root.Children[2].Children);
        Assert.Equal(("audit.record", "maintenance 后置"), (post.Label, post.Note));
        Assert.Equal(["middleware", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Contains(f.Notes, n => n.StartsWith("没找到 app.use('/前缀', router)"));
        Assert.Contains(f.Notes, n => n.StartsWith("没找到错误中间件"));
    }

    [Fact]
    public void Express_TypeScript_ChainedRouters_AnonymousDefaultRouter_InlineTypedErrorMiddleware()
    {
        // 与 realworld 示例同样的写法：ES import（含 import * as）、const api = Router().use(a).use(b)、export default Router().use('/api', api)、
        // 条件分支里注册的中间件、挂载之后注册的 express.static、顶层内联的带类型四参数错误中间件、路由级 auth.optional / auth.required
        var fx = new Fx("blog-api", 14000);
        fx.File("package.json", """{ "name": "blog-api", "dependencies": { "express": "^4.18.2", "cors": "^2.8.5", "body-parser": "^1.20.2", "morgan": "^1.10.0", "express-jwt": "^8.4.1" } }""");
        fx.File("src/main.ts", """
            import express from 'express';
            import cors from 'cors';
            import morgan from 'morgan';
            import * as bodyParser from 'body-parser';
            import routes from './app/routes/routes';
            import HttpException from './app/models/http-exception.model';

            const app = express();

            app.use(cors());
            app.use(bodyParser.json());
            if (process.env.NODE_ENV !== 'production') {
              app.use(morgan('dev'));
            }
            app.use(routes);

            app.use(express.static(__dirname + '/assets'));

            app.get('/', (req: express.Request, res: express.Response) => {
              res.json({ status: 'API is running on /api' });
            });

            app.use(
              (
                err: Error | HttpException,
                req: express.Request,
                res: express.Response,
                next: express.NextFunction,
              ) => {
                if (err && err.name === 'UnauthorizedError') {
                  return res.status(401).json({ status: 'error', message: 'missing authorization credentials' });
                } else if (err) {
                  res.status(500).json(err.message);
                }
              },
            );

            app.listen(process.env.PORT || 3000);
            """);
        fx.File("src/app/routes/routes.ts", """
            import { Router } from 'express';
            import tagsController from './tag/tag.controller';
            import articlesController from './article/article.controller';

            const api = Router()
              .use(tagsController)
              .use(articlesController);

            export default Router().use('/api', api);
            """);
        fx.File("src/app/routes/tag/tag.controller.ts", """
            import { NextFunction, Request, Response, Router } from 'express';
            import auth from '../auth/auth';
            import getTags from './tag.service';

            const router = Router();

            router.get('/tags', auth.optional, async (req: Request, res: Response, next: NextFunction) => {
              try {
                res.json({ tags: await getTags(req.auth?.user?.id) });
              } catch (error) {
                next(error);
              }
            });

            export default router;
            """);
        fx.File("src/app/routes/article/article.controller.ts", """
            import { NextFunction, Request, Response, Router } from 'express';
            import auth from '../auth/auth';
            import { createArticle, getArticles } from './article.service';

            const router = Router();

            router.get('/articles', auth.optional, async (req: Request, res: Response, next: NextFunction) => {
              try {
                res.json(await getArticles(req.query, req.auth?.user?.id));
              } catch (error) {
                next(error);
              }
            });

            router.post('/articles', auth.required, async (req: Request, res: Response, next: NextFunction) => {
              try {
                res.status(201).json({ article: await createArticle(req.body.article, req.auth?.user?.id) });
              } catch (error) {
                next(error);
              }
            });

            export default router;
            """);
        fx.ModuleCalls("src/main.ts", "app.get('/'", new C("res.json("), new C("res.status("), new C(".json("), new C("res.status("), new C(".json("));
        fx.Entry(fx.Symbols.Single(s => s.Kind == SymbolKind.Module && s.Path == fx.P("src/main.ts")), "http", "/", "GET", "src/main.ts", "app.get('/'");
        foreach (var (file, marker, route, method) in new[]
                 {
                     ("src/app/routes/tag/tag.controller.ts", "router.get('/tags'", "/api/tags", "GET"),
                     ("src/app/routes/article/article.controller.ts", "router.get('/articles'", "/api/articles", "GET"),
                     ("src/app/routes/article/article.controller.ts", "router.post('/articles'", "/api/articles", "POST"),
                 })
            fx.Entry(fx.Symbols.Single(s => s.Kind == SymbolKind.Module && s.Path == fx.P(file)), "http", route, method, file, marker);

        var f = FrameworkFlowBuilder.Build(fx.Input());
        AssertTree(f);
        var root = f.Flow.Root;
        Assert.Equal(["cors", "bodyParser.json", "morgan", "express.static", "路由"], root.Children.Select(c => c.Label));
        Assert.Equal("第三方中间件（body-parser）（main.ts:11）", root.Children[1].Note);
        Assert.Equal("第三方中间件（morgan）（main.ts:13）；条件分支里注册", root.Children[2].Note);
        // 挂载之后注册、但后面还有 app.get('/')：仍在中间件列，只是对前面挂的路由不生效
        Assert.Equal("Express 内置（main.ts:17）；注册在 挂载 /（routes） 之后：对它前面注册的路由不生效", root.Children[3].Note);

        var routes = root.Children[4];
        Assert.Equal(["挂载 /（routes）", "app.use（内联错误中间件）"], routes.Children.Select(c => c.Label));
        var top = routes.Children[0];
        Assert.Equal("路由挂载（main.ts:15）", top.Note);
        var api = Assert.Single(top.Children);
        Assert.Equal(("挂载 /api（api）", "路由挂载（routes.ts:9）"), (api.Label, api.Note));
        Assert.Equal(["挂载 /api（tagsController）", "挂载 /api（articlesController）"], api.Children.Select(c => c.Label));
        Assert.Equal("路由挂载（routes.ts:6）：tag.controller.ts 里 1 条路由", api.Children[0].Note);
        Assert.Equal("路由挂载（routes.ts:7）：article.controller.ts 里 2 条路由", api.Children[1].Note);

        var error = routes.Children[1];
        Assert.Equal(("tail", "出错时（四个参数的错误中间件，main.ts:23）"), (error.Actor, error.Note));
        Assert.Equal(["res.status", "json", "res.status", "json"], error.Children.Select(c => c.Label)); // app.get('/') 里的 res.json 不算
        Assert.All(error.Children, c => Assert.Equal("条件分支里", c.Note));

        Assert.Contains(f.Notes, n => n.StartsWith("路由挂载 4 处（/ → routes.ts、/api → routes.ts、/api → tag.controller.ts、/api → article.controller.ts）"));
        Assert.Contains(f.Notes, n => n.StartsWith("路由级中间件 auth.optional、auth.required 写在 3 条路由的注册里"));
    }

    // ================= Koa =================

    private static Fx KoaFixture()
    {
        var fx = new Fx("shop-gateway", 4000);
        fx.File("package.json", """
            { "name": "shop-gateway", "main": "src/server.js",
              "dependencies": { "koa": "^2.15.3", "@koa/router": "^12.0.1", "koa-bodyparser": "^4.4.1" } }
            """);
        fx.File("src/server.js", """
            const app = require('./app');
            app.listen(process.env.PORT || 4000);
            """);
        fx.File("src/app.js", """
            const Koa = require('koa');
            const bodyParser = require('koa-bodyparser');
            const router = require('./router');
            const { errorBoundary } = require('./middleware/errors');
            const logger = require('./logger');

            const app = new Koa();

            app.use(errorBoundary);
            app.use(async (ctx, next) => {
              const start = Date.now();
              await next();
              const ms = Date.now() - start;
              ctx.set('X-Response-Time', `${ms}ms`);
            });
            app.use(bodyParser());
            app.use(router.routes());
            app.use(router.allowedMethods());

            app.on('error', (err, ctx) => {
              logger.error({ err, path: ctx && ctx.path }, 'request failed');
            });

            module.exports = app;
            """);
        fx.File("src/middleware/errors.js", """
            exports.errorBoundary = async function errorBoundary(ctx, next) {
              try {
                await next();
              } catch (err) {
                ctx.status = err.status || 500;
                ctx.body = { error: err.expose ? err.message : 'internal_error' };
                ctx.app.emit('error', err, ctx);
              }
            };
            """);
        fx.File("src/router.js", """
            const Router = require('@koa/router');
            const { getCart, addItem } = require('./handlers/carts');

            const router = new Router({ prefix: '/v1' });

            router.use(requireTenant);
            router.get('/carts/:id', getCart);
            router.post('/carts/:id/items', addItem);

            async function requireTenant(ctx, next) {
              if (!ctx.get('x-tenant-id')) {
                ctx.throw(400, 'tenant header required');
              }
              ctx.state.tenant = ctx.get('x-tenant-id');
              await next();
            }

            module.exports = router;
            """);
        fx.File("src/handlers/carts.js", """
            exports.getCart = async function getCart(ctx) {
              ctx.body = await ctx.services.carts.get(ctx.params.id, ctx.state.tenant);
            };

            exports.addItem = async function addItem(ctx) {
              ctx.body = await ctx.services.carts.add(ctx.params.id, ctx.request.body);
            };
            """);
        var errorBoundary = fx.Fn("src/middleware/errors.js", "errorBoundary", "function errorBoundary(", vis: "export");
        fx.CallsOf(errorBoundary, new C("next(", Ctx: CallContext.Try), new C("ctx.app.emit(", Ctx: CallContext.Catch));
        var requireTenant = fx.Fn("src/router.js", "requireTenant", "async function requireTenant(");
        fx.CallsOf(requireTenant, new C("ctx.get("), new C("ctx.throw(", Ctx: CallContext.Cond), new C("ctx.get("), new C("next("));
        var getCart = fx.Fn("src/handlers/carts.js", "getCart", "function getCart(", vis: "export");
        var addItem = fx.Fn("src/handlers/carts.js", "addItem", "function addItem(", vis: "export");
        fx.ModuleCalls("src/app.js", "app.use(async", new C("Date.now("), new C("next("), new C("Date.now("), new C("ctx.set("), new C("logger.error("));
        fx.Entry(getCart, "http", "/v1/carts/:id", "GET", "src/router.js", "router.get(");
        fx.Entry(addItem, "http", "/v1/carts/:id/items", "POST", "src/router.js", "router.post(");
        return fx;
    }

    [Fact]
    public void Koa_Onion_TryCatch_RouterPrefix_ErrorEvent()
    {
        var f = FrameworkFlowBuilder.Build(KoaFixture().Input());
        AssertTree(f);
        Assert.Equal(["middleware", "controller", "tail"], f.Stages.Select(s => s.Key));
        var root = f.Flow.Root;
        Assert.Equal(["errorBoundary", "app.use（内联中间件）", "bodyParser", "路由"], root.Children.Select(c => c.Label));
        Assert.Empty(root.Children[0].Children);                       // try 里只有 await next()
        Assert.Empty(root.Children[1].Children);                       // Date.now 是噪声
        Assert.Equal("第三方中间件（koa-bodyparser）（app.js:16）", root.Children[2].Note);

        var routes = root.Children[3];
        Assert.Equal(("controllers", 2), (routes.Kind, routes.Count));
        Assert.Equal(["挂载 /v1（router）", "ctx.set", "app.emit", "router.allowedMethods", "app.on('error')"], routes.Children.Select(c => c.Label));
        var mount = routes.Children[0];
        Assert.Equal("路由挂载（app.js:17）：router.js 里 2 条路由", mount.Note);
        var tenant = Assert.Single(mount.Children);
        Assert.Equal("requireTenant", tenant.Label);
        Assert.StartsWith("router 级中间件（只对 /v1 下的路由生效，router.js:6）", tenant.Note);
        Assert.Contains("有条件", tenant.Note);                         // 分支里 ctx.throw(…) 直接结束请求
        Assert.Equal(["ctx.get", "ctx.throw", "ctx.get"], tenant.Children.Select(c => c.Label));

        // 洋葱往回走：内联中间件（后注册）的后置先出来，再是 errorBoundary 的 catch
        Assert.Equal("app.use（内联中间件） 后置", routes.Children[1].Note);
        Assert.Equal("errorBoundary 后置 · catch 里（出错时）", routes.Children[2].Note);
        Assert.StartsWith("路由之后注册（app.js:18）", routes.Children[3].Note);
        var onError = routes.Children[4];
        Assert.Equal(("tail", "出错时（app.on('error')：Koa 的错误事件，app.js:20）"), (onError.Actor, onError.Note));
        Assert.Equal(["logger.error"], onError.Children.Select(c => c.Label));

        Assert.Equal("认出 Koa（app.js:7 的 app = new Koa()）", f.Notes[0]);
        Assert.Contains(f.Notes, n => n.StartsWith("出错时：中间件 try { await next() } catch 里的调用、app.on('error')"));
    }

    // ================= NestJS =================

    private static Fx NestFixture()
    {
        var fx = new Fx("acme-orders", 6000);
        fx.File("package.json", """
            { "name": "acme-orders", "scripts": { "start": "nest start", "start:prod": "node dist/main" },
              "dependencies": { "@nestjs/common": "^10.3.0", "@nestjs/core": "^10.3.0", "@nestjs/platform-express": "^10.3.0", "helmet": "^7.1.0", "rxjs": "^7.8.1" } }
            """);
        fx.File("src/main.ts", """
            import { NestFactory, Reflector } from '@nestjs/core';
            import { ValidationPipe } from '@nestjs/common';
            import helmet from 'helmet';
            import { AppModule } from './app.module';
            import { requestContext } from './common/middleware/request-context';
            import { JwtAuthGuard } from './auth/jwt-auth.guard';
            import { LoggingInterceptor } from './common/interceptors/logging.interceptor';
            import { AllExceptionsFilter } from './common/filters/all-exceptions.filter';

            async function bootstrap() {
              const app = await NestFactory.create(AppModule, { bufferLogs: true });
              app.setGlobalPrefix('api');
              app.use(helmet());
              app.use(requestContext);
              app.enableCors({ origin: process.env.WEB_ORIGIN });
              app.useGlobalGuards(new JwtAuthGuard(app.get(Reflector)));
              app.useGlobalInterceptors(new LoggingInterceptor());
              app.useGlobalPipes(new ValidationPipe({ whitelist: true, transform: true }));
              app.useGlobalFilters(new AllExceptionsFilter());
              await app.listen(3000);
            }
            bootstrap();
            """);
        fx.File("src/app.module.ts", """
            import { MiddlewareConsumer, Module, NestModule, RequestMethod } from '@nestjs/common';
            import { APP_FILTER, APP_GUARD, APP_INTERCEPTOR, APP_PIPE } from '@nestjs/core';
            import { OrdersModule } from './orders/orders.module';
            import { RolesGuard } from './auth/roles.guard';
            import { TimeoutInterceptor } from './common/interceptors/timeout.interceptor';
            import { TrimPipe } from './common/pipes/trim.pipe';
            import { HttpExceptionFilter } from './common/filters/http-exception.filter';
            import { CorrelationIdMiddleware } from './common/middleware/correlation-id.middleware';
            import { TenantMiddleware } from './common/middleware/tenant.middleware';

            @Module({
              imports: [OrdersModule],
              providers: [
                { provide: APP_GUARD, useClass: RolesGuard },
                { provide: APP_INTERCEPTOR, useClass: TimeoutInterceptor },
                { provide: APP_PIPE, useClass: TrimPipe },
                { provide: APP_FILTER, useClass: HttpExceptionFilter },
              ],
            })
            export class AppModule implements NestModule {
              configure(consumer: MiddlewareConsumer) {
                consumer
                  .apply(CorrelationIdMiddleware, TenantMiddleware)
                  .exclude({ path: 'health', method: RequestMethod.GET })
                  .forRoutes('*');
              }
            }
            """);
        fx.File("src/common/middleware/request-context.ts", """
            import { NextFunction, Request, Response } from 'express';
            import { contextStore } from './context-store';

            export function requestContext(req: Request, res: Response, next: NextFunction) {
              contextStore.run({ requestId: req.header('x-request-id') }, () => next());
            }
            """);
        fx.File("src/common/middleware/correlation-id.middleware.ts", """
            import { Injectable, NestMiddleware } from '@nestjs/common';
            import { randomUUID } from 'crypto';
            import { NextFunction, Request, Response } from 'express';

            @Injectable()
            export class CorrelationIdMiddleware implements NestMiddleware {
              use(req: Request, res: Response, next: NextFunction) {
                const id = req.header('x-correlation-id') ?? randomUUID();
                res.setHeader('x-correlation-id', id);
                next();
              }
            }
            """);
        fx.File("src/common/middleware/tenant.middleware.ts", """
            import { Injectable, NestMiddleware, NotFoundException } from '@nestjs/common';
            import { NextFunction, Request, Response } from 'express';
            import { TenantService } from '../../tenants/tenant.service';

            @Injectable()
            export class TenantMiddleware implements NestMiddleware {
              constructor(private readonly tenants: TenantService) {}

              async use(req: Request, res: Response, next: NextFunction) {
                const tenant = await this.tenants.resolve(req.hostname);
                if (!tenant) {
                  throw new NotFoundException('unknown tenant');
                }
                req['tenant'] = tenant;
                next();
              }
            }
            """);
        fx.File("src/auth/jwt-auth.guard.ts", """
            import { CanActivate, ExecutionContext, Injectable } from '@nestjs/common';
            import { Reflector } from '@nestjs/core';
            import { TokenService } from './token.service';

            @Injectable()
            export class JwtAuthGuard implements CanActivate {
              constructor(private readonly reflector: Reflector, private readonly tokens: TokenService) {}

              canActivate(context: ExecutionContext): boolean {
                const isPublic = this.reflector.getAllAndOverride<boolean>('isPublic', [context.getHandler(), context.getClass()]);
                if (isPublic) return true;
                const request = context.switchToHttp().getRequest();
                return this.tokens.verify(request.headers.authorization);
              }
            }
            """);
        fx.File("src/auth/roles.guard.ts", """
            import { CanActivate, ExecutionContext, Injectable } from '@nestjs/common';
            import { Reflector } from '@nestjs/core';

            @Injectable()
            export class RolesGuard implements CanActivate {
              constructor(private readonly reflector: Reflector) {}

              canActivate(context: ExecutionContext): boolean {
                const roles = this.reflector.get<string[]>('roles', context.getHandler());
                return !roles || roles.some((r) => context.switchToHttp().getRequest().user?.roles.includes(r));
              }
            }
            """);
        fx.File("src/common/interceptors/logging.interceptor.ts", """
            import { CallHandler, ExecutionContext, Injectable, Logger, NestInterceptor } from '@nestjs/common';
            import { Observable, tap } from 'rxjs';

            @Injectable()
            export class LoggingInterceptor implements NestInterceptor {
              private readonly logger = new Logger('HTTP');

              intercept(context: ExecutionContext, next: CallHandler): Observable<unknown> {
                const started = Date.now();
                const req = context.switchToHttp().getRequest();
                this.logger.log(`--> ${req.method} ${req.url}`);
                return next.handle().pipe(
                  tap(() => this.logger.log(`<-- ${req.method} ${req.url} ${Date.now() - started}ms`)),
                );
              }
            }
            """);
        fx.File("src/common/interceptors/timeout.interceptor.ts", """
            import { CallHandler, ExecutionContext, Injectable, NestInterceptor, RequestTimeoutException } from '@nestjs/common';
            import { Observable, TimeoutError, catchError, throwError, timeout } from 'rxjs';

            @Injectable()
            export class TimeoutInterceptor implements NestInterceptor {
              intercept(_context: ExecutionContext, next: CallHandler): Observable<unknown> {
                return next.handle().pipe(
                  timeout(5000),
                  catchError((err) => throwError(() => (err instanceof TimeoutError ? new RequestTimeoutException() : err))),
                );
              }
            }
            """);
        fx.File("src/common/pipes/trim.pipe.ts", """
            import { Injectable, PipeTransform } from '@nestjs/common';
            import { deepTrim } from '../util/deep-trim';

            @Injectable()
            export class TrimPipe implements PipeTransform {
              transform(value: unknown) {
                return typeof value === 'string' ? value.trim() : deepTrim(value);
              }
            }
            """);
        fx.File("src/common/filters/all-exceptions.filter.ts", """
            import { ArgumentsHost, Catch, ExceptionFilter, HttpException, HttpStatus, Logger } from '@nestjs/common';
            import { Response } from 'express';

            @Catch()
            export class AllExceptionsFilter implements ExceptionFilter {
              private readonly logger = new Logger('Exceptions');

              catch(exception: unknown, host: ArgumentsHost) {
                const ctx = host.switchToHttp();
                const response = ctx.getResponse<Response>();
                const status = exception instanceof HttpException ? exception.getStatus() : HttpStatus.INTERNAL_SERVER_ERROR;
                this.logger.error(exception);
                response.status(status).json({ statusCode: status, timestamp: new Date().toISOString() });
              }
            }
            """);
        fx.File("src/common/filters/http-exception.filter.ts", """
            import { ArgumentsHost, Catch, ExceptionFilter, HttpException } from '@nestjs/common';

            @Catch(HttpException)
            export class HttpExceptionFilter implements ExceptionFilter {
              catch(exception: HttpException, host: ArgumentsHost) {
                const res = host.switchToHttp().getResponse();
                res.status(exception.getStatus()).json(exception.getResponse());
              }
            }
            """);
        fx.File("src/orders/orders.controller.ts", """
            import { Body, Controller, Get, Post, UseGuards, UseInterceptors } from '@nestjs/common';
            import { OwnerGuard } from '../auth/owner.guard';
            import { ThrottlerGuard } from '@nestjs/throttler';
            import { IdempotencyInterceptor } from '../common/interceptors/idempotency.interceptor';
            import { OrdersService } from './orders.service';

            @Controller('orders')
            @UseGuards(OwnerGuard)
            export class OrdersController {
              constructor(private readonly orders: OrdersService) {}

              @Get()
              list() {
                return this.orders.list();
              }

              @Post()
              @UseInterceptors(IdempotencyInterceptor)
              @UseGuards(ThrottlerGuard)
              create(@Body() dto: unknown) {
                return this.orders.create(dto);
              }
            }
            """);

        var requestContext = fx.Fn("src/common/middleware/request-context.ts", "requestContext", "function requestContext(", vis: "export");
        fx.CallsOf(requestContext, new C("contextStore.run("), new C("req.header("), new C("next(", Ctx: CallContext.Callback));
        var corr = fx.Class("src/common/middleware/correlation-id.middleware.ts", "CorrelationIdMiddleware", "export class CorrelationIdMiddleware");
        fx.CallsOf(fx.Method(corr, "use", "use(req"), new C("req.header("), new C("randomUUID(", Ctx: CallContext.Cond), new C("res.setHeader("), new C("next("));
        var tenant = fx.Class("src/common/middleware/tenant.middleware.ts", "TenantMiddleware", "export class TenantMiddleware");
        fx.Method(tenant, "constructor", "constructor(");
        fx.CallsOf(fx.Method(tenant, "use", "async use("), new C("this.tenants.resolve("), new C("NotFoundException(", Ctx: CallContext.Cond), new C("next("));
        var jwt = fx.Class("src/auth/jwt-auth.guard.ts", "JwtAuthGuard", "export class JwtAuthGuard");
        fx.CallsOf(fx.Method(jwt, "canActivate", "canActivate("), new C("this.reflector.getAllAndOverride<boolean>("), new C("context.getHandler("), new C("context.getClass("),
            new C("context.switchToHttp("), new C(".getRequest("), new C("this.tokens.verify("));
        var roles = fx.Class("src/auth/roles.guard.ts", "RolesGuard", "export class RolesGuard");
        fx.CallsOf(fx.Method(roles, "canActivate", "canActivate("), new C("this.reflector.get<string[]>("), new C("context.getHandler("),
            new C("roles.some(", Ctx: CallContext.Cond), new C("context.switchToHttp(", Ctx: CallContext.Callback), new C(".getRequest(", Ctx: CallContext.Callback),
            new C(".includes(", Ctx: CallContext.Callback));
        var logging = fx.Class("src/common/interceptors/logging.interceptor.ts", "LoggingInterceptor", "export class LoggingInterceptor");
        fx.CallsOf(fx.Method(logging, "intercept", "intercept("), new C("Date.now("), new C("context.switchToHttp("), new C(".getRequest("), new C("this.logger.log("),
            new C("next.handle("), new C(".pipe("), new C("tap("), new C("this.logger.log(", Ctx: CallContext.Callback), new C("Date.now(", Ctx: CallContext.Callback));
        var timeoutI = fx.Class("src/common/interceptors/timeout.interceptor.ts", "TimeoutInterceptor", "export class TimeoutInterceptor");
        fx.CallsOf(fx.Method(timeoutI, "intercept", "intercept("), new C("next.handle("), new C(".pipe("), new C("timeout("), new C("catchError("),
            new C("throwError(", Ctx: CallContext.Callback));
        var trim = fx.Class("src/common/pipes/trim.pipe.ts", "TrimPipe", "export class TrimPipe");
        fx.CallsOf(fx.Method(trim, "transform", "transform("), new C("value.trim(", Ctx: CallContext.Cond), new C("deepTrim(", Ctx: CallContext.Cond));
        var all = fx.Class("src/common/filters/all-exceptions.filter.ts", "AllExceptionsFilter", "export class AllExceptionsFilter");
        fx.CallsOf(fx.Method(all, "catch", "catch(exception"), new C("host.switchToHttp("), new C("ctx.getResponse<Response>("), new C("exception.getStatus(", Ctx: CallContext.Cond),
            new C("this.logger.error("), new C("response.status("), new C(".json("), new C(".toISOString("));
        var http = fx.Class("src/common/filters/http-exception.filter.ts", "HttpExceptionFilter", "export class HttpExceptionFilter");
        fx.CallsOf(fx.Method(http, "catch", "catch(exception"), new C("host.switchToHttp("), new C(".getResponse("), new C("res.status("), new C("exception.getStatus("),
            new C(".json("), new C("exception.getResponse("));
        var ctrl = fx.Class("src/orders/orders.controller.ts", "OrdersController", "export class OrdersController");
        var list = fx.Method(ctrl, "list", "list() {");
        var create = fx.Method(ctrl, "create", "create(@Body()");
        fx.Entry(list, "http", "/api/orders", "GET", "src/orders/orders.controller.ts", "list() {");
        fx.Entry(create, "http", "/api/orders", "POST", "src/orders/orders.controller.ts", "create(@Body()");
        return fx;
    }

    [Fact]
    public void Nest_FixedLifecycle_GlobalAndAppProviders_InterceptorSplit_Filters()
    {
        var f = FrameworkFlowBuilder.Build(NestFixture().Input());
        AssertTree(f);
        Assert.Equal(["middleware", "aspect", "interceptor", "pipe", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(["中间件", "守卫", "拦截器", "管道", "控制器", "收尾"], f.Stages.Select(s => s.Label));
        Assert.All(f.Stages, s => Assert.True(s.OrderKnown));

        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "middleware"), (root.Label, root.Actor));
        Assert.Equal(
            ["helmet", "requestContext", "enableCors", "CorrelationIdMiddleware.use", "TenantMiddleware.use",
             "JwtAuthGuard.canActivate", "RolesGuard.canActivate", "LoggingInterceptor.intercept", "TimeoutInterceptor.intercept",
             "ValidationPipe", "TrimPipe.transform", "控制器"],
            root.Children.Select(c => c.Label));
        Assert.Equal(["middleware", "middleware", "middleware", "middleware", "middleware", "aspect", "aspect", "interceptor", "interceptor", "pipe", "pipe", "controller"],
            root.Children.Select(c => c.Actor));

        Assert.Equal("第三方中间件（helmet）（main.ts:13）", root.Children[0].Note);
        Assert.Equal(["contextStore.run", "req.header"], root.Children[1].Children.Select(c => c.Label));
        Assert.Equal("Nest 内置（main.ts:15）", root.Children[2].Note);
        var corr = root.Children[3];
        Assert.Equal("模块中间件（app.module.ts:23，forRoutes *，exclude 1 条）", corr.Note);
        Assert.Equal(["req.header", "randomUUID", "res.setHeader"], corr.Children.Select(c => c.Label));
        var tenantMw = root.Children[4];
        Assert.Contains("有条件", tenantMw.Note);                       // 分支里 throw，不调 next
        Assert.Equal(["tenants.resolve", "NotFoundException"], tenantMw.Children.Select(c => c.Label));

        Assert.Equal("useGlobalGuards（main.ts:16）", root.Children[5].Note);
        Assert.Equal(["reflector.getAllAndOverride", "context.getHandler", "context.getClass", "context.switchToHttp", "getRequest", "tokens.verify"],
            root.Children[5].Children.Select(c => c.Label));
        Assert.Equal("APP_GUARD provider（app.module.ts:14）", root.Children[6].Note);
        var logging = root.Children[7];
        Assert.Equal(["context.switchToHttp", "getRequest", "logger.log"], logging.Children.Select(c => c.Label)); // next.handle() 之前
        Assert.Equal(("external-group", "Nest 内置 / 第三方（useGlobalPipes（main.ts:18））"), (root.Children[9].Kind, root.Children[9].Note));
        Assert.Equal(["deepTrim"], root.Children[10].Children.Select(c => c.Label));

        // 控制器下面：拦截器后置（后注册的先回来）→ 异常过滤器
        var ctrl = root.Children[11];
        Assert.Equal(("controllers", 2, "控制器"), (ctrl.Kind, ctrl.Count, ctrl.Label));
        Assert.Equal(["timeout", "catchError", "throwError", "tap", "logger.log", "AllExceptionsFilter.catch", "HttpExceptionFilter.catch"], ctrl.Children.Select(c => c.Label));
        Assert.Equal("TimeoutInterceptor 后置（next.handle() 之后）", ctrl.Children[0].Note);
        Assert.Equal("LoggingInterceptor 后置（next.handle() 之后） · 回调 / lambda 里", ctrl.Children[4].Note);
        Assert.Equal("出错时（异常过滤器，useGlobalFilters（main.ts:19））；@Catch() 接全部异常", ctrl.Children[5].Note);
        Assert.Equal(["host.switchToHttp", "ctx.getResponse", "exception.getStatus", "logger.error", "response.status", "json"], ctrl.Children[5].Children.Select(c => c.Label));
        Assert.Equal("出错时（异常过滤器，APP_FILTER provider（app.module.ts:17））；只接 HttpException", ctrl.Children[6].Note);
        Assert.All(ctrl.Children, c => Assert.Equal("tail", c.Actor));

        // 阶段的符号按骨架里的行数：外部的（没有符号）也算一条、Id = 0，阶段头上的个数才和下面列的行数一致；仓库里的照旧按顺序
        Assert.Equal(["requestContext", "use", "use"], f.Stages[0].Symbols.Where(s => s.Id != 0).Select(s => s.Name));
        Assert.Equal(["helmet", "requestContext", "enableCors", "use", "use"], f.Stages[0].Symbols.Select(s => s.Name));
        Assert.Equal(["canActivate", "canActivate"], f.Stages[1].Symbols.Select(s => s.Name));
        Assert.Equal("认出 NestJS（main.ts:11 的 app = await NestFactory.create(AppModule)）；全局前缀 /api", f.Notes[0]);
        Assert.Contains(f.Notes, n => n == "控制器 / 方法级：@UseGuards 2 处（OwnerGuard、ThrottlerGuard），@UseInterceptors 1 处（IdempotencyInterceptor）——只对所在控制器 / 方法生效，没画进骨架");
        Assert.DoesNotContain(f.Notes, n => n.StartsWith("没找到全局的"));
    }

    [Fact]
    public void Nest_ModuleMiddlewareAcrossModules_CreateOptionsCors_NoGlobals()
    {
        // 与 realworld 示例同样的写法：create 的选项变量里 cors: true，多个模块各自 consumer.apply(AuthMiddleware).forRoutes({ path, method })
        var fx = new Fx("blog-api", 8000);
        fx.File("package.json", """{ "dependencies": { "@nestjs/core": "^7.0.5", "@nestjs/common": "^7.0.5" } }""");
        fx.File("src/main.ts", """
            import { NestFactory } from '@nestjs/core';
            import { ApplicationModule } from './app.module';

            async function bootstrap() {
              const appOptions = { cors: true };
              const app = await NestFactory.create(ApplicationModule, appOptions);
              app.setGlobalPrefix('api');
              await app.listen(3000);
            }
            bootstrap();
            """);
        fx.File("src/app.module.ts", """
            @Module({ imports: [UserModule, ArticleModule] })
            export class ApplicationModule {}
            """);
        fx.File("src/user/user.module.ts", """
            @Module({ controllers: [UserController] })
            export class UserModule implements NestModule {
              public configure(consumer: MiddlewareConsumer) {
                consumer
                  .apply(AuthMiddleware)
                  .forRoutes({ path: 'user', method: RequestMethod.GET }, { path: 'user', method: RequestMethod.PUT });
              }
            }
            """);
        fx.File("src/article/article.module.ts", """
            @Module({ controllers: [ArticleController] })
            export class ArticleModule implements NestModule {
              public configure(consumer: MiddlewareConsumer) {
                consumer
                  .apply(AuthMiddleware)
                  .forRoutes(
                    { path: 'articles/feed', method: RequestMethod.GET },
                    { path: 'articles', method: RequestMethod.POST },
                    { path: 'articles/:slug', method: RequestMethod.DELETE },
                    { path: 'articles/:slug', method: RequestMethod.PUT });
              }
            }
            """);
        fx.File("src/user/auth.middleware.ts", """
            @Injectable()
            export class AuthMiddleware implements NestMiddleware {
              constructor(private readonly userService: UserService) {}

              async use(req: Request, res: Response, next: NextFunction) {
                const authHeaders = req.headers.authorization;
                if (authHeaders && (authHeaders as string).split(' ')[1]) {
                  const token = (authHeaders as string).split(' ')[1];
                  const decoded: any = jwt.verify(token, SECRET);
                  const user = await this.userService.findById(decoded.id);
                  if (!user) {
                    throw new HttpException('User not found.', HttpStatus.UNAUTHORIZED);
                  }
                  req.user = user.user;
                  next();
                } else {
                  throw new HttpException('Not authorized.', HttpStatus.UNAUTHORIZED);
                }
              }
            }
            """);
        fx.File("src/user/user.controller.ts", """
            @Controller()
            export class UserController {
              @UsePipes(new ValidationPipe())
              @Post('users')
              async create(@Body('user') userData: CreateUserDto) {
                return this.userService.create(userData);
              }
            }
            """);
        var auth = fx.Class("src/user/auth.middleware.ts", "AuthMiddleware", "export class AuthMiddleware");
        fx.CallsOf(fx.Method(auth, "use", "async use("), new C("split(", Ctx: CallContext.Cond), new C("split(", Ctx: CallContext.Cond), new C("jwt.verify(", Ctx: CallContext.Cond),
            new C("this.userService.findById(", Ctx: CallContext.Cond), new C("HttpException(", Ctx: CallContext.Cond), new C("next(", Ctx: CallContext.Cond),
            new C("HttpException(", Ctx: CallContext.Cond));
        var ctrl = fx.Class("src/user/user.controller.ts", "UserController", "export class UserController");
        var create = fx.Method(ctrl, "create", "async create(");
        fx.Entry(create, "http", "/api/users", "POST", "src/user/user.controller.ts", "async create(");

        var f = FrameworkFlowBuilder.Build(fx.Input());
        AssertTree(f);
        Assert.Equal(["middleware", "controller"], f.Stages.Select(s => s.Key));
        Assert.False(f.Stages[0].OrderKnown);                           // 两个模块的中间件之间没有依据
        var root = f.Flow.Root;
        Assert.Equal(["cors（create 选项）", "AuthMiddleware.use", "AuthMiddleware.use", "控制器"], root.Children.Select(c => c.Label));
        Assert.Equal("模块中间件（article.module.ts:5，forRoutes GET articles/feed、POST articles、DELETE articles/:slug 等 4 条）；有条件（不是每条路都调 next：有分支直接返回 / 抛异常）",
            root.Children[1].Note);
        Assert.StartsWith("模块中间件（user.module.ts:5，forRoutes GET user、PUT user）", root.Children[2].Note);
        // else 分支里的 throw 是"不调 next 的那条路"，算前置，不进收尾
        Assert.Equal(["jwt.verify", "userService.findById", "HttpException", "HttpException"], root.Children[1].Children.Select(c => c.Label));
        Assert.Empty(root.Children[3].Children);
        Assert.Contains(f.Notes, n => n.StartsWith("模块中间件来自 2 个模块（article.module.ts、user.module.ts）"));
        Assert.Contains(f.Notes, n => n == "没找到全局的守卫、拦截器、管道（useGlobalXxx / APP_XXX provider）");
        Assert.Contains(f.Notes, n => n.StartsWith("没有全局异常过滤器"));
        Assert.Contains(f.Notes, n => n.StartsWith("控制器 / 方法级：@UsePipes 1 处（ValidationPipe）"));
    }

    // ================= Fastify =================

    private static Fx FastifyFixture()
    {
        var fx = new Fx("catalog-svc", 10000);
        fx.File("package.json", """
            { "name": "catalog-svc", "main": "src/app.js",
              "dependencies": { "fastify": "^4.26.2", "@fastify/cors": "^9.0.1", "fastify-plugin": "^4.5.1" } }
            """);
        fx.File("src/app.js", """
            const Fastify = require('fastify');
            const cors = require('@fastify/cors');
            const authPlugin = require('./plugins/auth');
            const { startTrace, finishTrace } = require('./hooks/tracing');
            const errorHandler = require('./errors');

            const app = Fastify({ logger: true });

            app.register(cors, { origin: true });
            app.register(authPlugin);
            app.addHook('onResponse', finishTrace);
            app.addHook('preHandler', async (request, reply) => {
              request.log.debug({ url: request.url }, 'pre handler');
            });
            app.addHook('onRequest', startTrace);
            app.addHook('onSend', async (request, reply, payload) => {
              reply.header('x-served-by', 'catalog-svc');
              return payload;
            });
            app.addHook('onError', async (request, reply, error) => {
              request.log.error(error);
            });
            app.setErrorHandler(errorHandler);
            app.register(require('./routes/products'), { prefix: '/products' });

            module.exports = app;
            """);
        fx.File("src/plugins/auth.js", """
            const fp = require('fastify-plugin');
            const { verifyToken } = require('../lib/tokens');

            async function authPlugin(fastify, opts) {
              fastify.decorateRequest('user', null);
              fastify.addHook('onRequest', async (request, reply) => {
                request.user = await verifyToken(request.headers.authorization);
              });
            }

            module.exports = fp(authPlugin);
            """);
        fx.File("src/lib/tokens.js", """
            async function verifyToken(header) {
              return header ? decode(header.replace('Bearer ', '')) : null;
            }

            module.exports = { verifyToken };
            """);
        fx.File("src/hooks/tracing.js", """
            const tracer = require('./tracer');

            function startTrace(request, reply, done) {
              request.span = tracer.startSpan(request.routerPath);
              done();
            }

            function finishTrace(request, reply, done) {
              request.span.end();
              done();
            }

            module.exports = { startTrace, finishTrace };
            """);
        fx.File("src/errors.js", """
            module.exports = function errorHandler(error, request, reply) {
              request.log.error(error);
              reply.status(error.statusCode || 500).send({ error: 'internal_error' });
            };
            """);
        fx.File("src/routes/products.js", """
            module.exports = async function productRoutes(fastify) {
              fastify.addHook('preHandler', loadTenant);
              fastify.get('/', listProducts);
              fastify.get('/:id', getProduct);
            };

            async function loadTenant(request, reply) {
              request.tenant = await tenants.byHost(request.hostname);
            }

            async function listProducts(request, reply) {
              return request.server.catalog.list(request.tenant);
            }

            async function getProduct(request, reply) {
              return request.server.catalog.get(request.params.id);
            }
            """);
        var authPlugin = fx.Fn("src/plugins/auth.js", "authPlugin", "async function authPlugin(", vis: "export default");
        var verifyToken = fx.Fn("src/lib/tokens.js", "verifyToken", "async function verifyToken(", vis: "export");
        fx.CallsOf(authPlugin, new C("fastify.decorateRequest("), new C("fastify.addHook("), new C("verifyToken(", verifyToken, CallContext.Callback));
        var start = fx.Fn("src/hooks/tracing.js", "startTrace", "function startTrace(", vis: "export");
        fx.CallsOf(start, new C("tracer.startSpan("), new C("done("));
        var finish = fx.Fn("src/hooks/tracing.js", "finishTrace", "function finishTrace(", vis: "export");
        fx.CallsOf(finish, new C("request.span.end("), new C("done("));
        var errorHandler = fx.Fn("src/errors.js", "errorHandler", "function errorHandler(", vis: "export default");
        fx.CallsOf(errorHandler, new C("request.log.error("), new C("reply.status("), new C(".send("));
        var routes = fx.Fn("src/routes/products.js", "productRoutes", "async function productRoutes(", vis: "export default");
        var loadTenant = fx.Fn("src/routes/products.js", "loadTenant", "async function loadTenant(");
        fx.CallsOf(loadTenant, new C("tenants.byHost("));
        var list = fx.Fn("src/routes/products.js", "listProducts", "async function listProducts(");
        var get = fx.Fn("src/routes/products.js", "getProduct", "async function getProduct(");
        fx.ModuleCalls("src/app.js", "app.addHook('preHandler'", new C("request.log.debug("), new C("reply.header("), new C("request.log.error("));
        fx.Entry(list, "http", "/products", "GET", "src/routes/products.js", "fastify.get('/',");
        fx.Entry(get, "http", "/products/:id", "GET", "src/routes/products.js", "fastify.get('/:id'");
        _ = routes;
        return fx;
    }

    [Fact]
    public void Fastify_HooksByLifecycle_PluginScopes_ExternalPlugins()
    {
        var f = FrameworkFlowBuilder.Build(FastifyFixture().Input());
        AssertTree(f);
        Assert.Equal(["middleware", "controller", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal("钩子", f.Stages[0].Label);
        Assert.False(f.Stages[0].OrderKnown);                           // 有第三方插件，里面的钩子看不到
        var root = f.Flow.Root;
        // onRequest（插件里的先注册；startTrace 写在 preHandler 之后也排在前）→ preHandler（app 的、再是 products 插件里的）；onResponse 注册得早也排在收尾
        Assert.Equal(["@fastify/cors", "onRequest（内联）", "startTrace", "preHandler（内联）", "loadTenant", "路由"], root.Children.Select(c => c.Label));
        Assert.Equal("第三方插件（@fastify/cors）（app.js:9）：内部挂的钩子看不到", root.Children[0].Note);
        var authHook = root.Children[1];
        Assert.Equal("onRequest 钩子（auth.js:6）；经插件 authPlugin（fastify-plugin 包过，作用于外层）", authHook.Note);
        var verify = Assert.Single(authHook.Children);
        Assert.Equal(("verifyToken", "call", (string?)null), (verify.Label, verify.Kind, verify.Note));
        Assert.Equal(["tracer.startSpan"], root.Children[2].Children.Select(c => c.Label)); // done() 不算
        Assert.Equal(["log.debug"], root.Children[3].Children.Select(c => c.Label));
        Assert.Equal("preHandler 钩子（products.js:2）；插件 productRoutes 里注册：封装作用域，只对它注册的路由生效（前缀 /products）", root.Children[4].Note);

        var routes = root.Children[5];
        Assert.Equal(("controllers", 2), (routes.Kind, routes.Count));
        Assert.Equal(["onSend（内联）", "finishTrace", "onError（内联）", "errorHandler"], routes.Children.Select(c => c.Label));
        Assert.Equal(["reply.header"], routes.Children[0].Children.Select(c => c.Label));
        Assert.Equal("onResponse 钩子（app.js:11）", routes.Children[1].Note);
        Assert.Equal("出错时：onError 钩子（app.js:20）", routes.Children[2].Note);
        Assert.Equal("出错时（setErrorHandler，app.js:23）", routes.Children[3].Note);
        Assert.Equal(["log.error", "reply.status", "send"], routes.Children[3].Children.Select(c => c.Label));

        Assert.Equal("认出 Fastify（app.js:7 的 app = Fastify(…)）", f.Notes[0]);
        Assert.Contains(f.Notes, n => n == "仓库插件：authPlugin（fastify-plugin 包过，钩子作用于外层）、productRoutes（封装作用域，前缀 /products）");
        Assert.Contains(f.Notes, n => n.StartsWith("第三方插件 @fastify/cors"));
    }

    // ================= gRPC 与认不出的 =================

    [Fact]
    public void Grpc_OnlyServiceMethodsPlaceholder_AndNote()
    {
        var fx = new Fx("pricing-svc", 12000);
        fx.File("package.json", """{ "name": "pricing-svc", "main": "server.js", "dependencies": { "@grpc/grpc-js": "^1.10.6", "@grpc/proto-loader": "^0.7.12" } }""");
        fx.File("server.js", """
            const grpc = require('@grpc/grpc-js');
            const protoLoader = require('@grpc/proto-loader');
            const { quote, convert } = require('./pricing');

            function main() {
              const definition = protoLoader.loadSync(`${__dirname}/proto/pricing.proto`);
              const proto = grpc.loadPackageDefinition(definition);
              const server = new grpc.Server({ interceptors: [authInterceptor] });
              server.addService(proto.acme.PricingService.service, { quote, convert });
              server.bindAsync('0.0.0.0:50051', grpc.ServerCredentials.createInsecure(), () => server.start());
            }

            main();
            """);
        fx.File("pricing.js", """
            exports.quote = function quote(call, callback) {
              callback(null, { amount: 42 });
            };

            exports.convert = function convert(call, callback) {
              callback(null, { amount: 7 });
            };
            """);
        var quote = fx.Fn("pricing.js", "quote", "function quote(", vis: "export");
        var convert = fx.Fn("pricing.js", "convert", "function convert(", vis: "export");
        fx.Entry(quote, "grpc", "PricingService/Quote", null, "pricing.js", "function quote(");
        fx.Entry(convert, "grpc", "PricingService/Convert", null, "pricing.js", "function convert(");

        var f = FrameworkFlowBuilder.Build(fx.Input());
        AssertTree(f);
        Assert.Equal(("gRPC 调用", "entry"), (f.Flow.Root.Label, f.Flow.Root.Kind));
        Assert.Equal("gRPC 调用", f.Flow.EntryLabel);
        var stage = Assert.Single(f.Stages);
        Assert.Equal(("controller", "服务方法", 2), (stage.Key, stage.Label, stage.Symbols.Count));
        var placeholder = Assert.Single(f.Flow.Root.Children);
        Assert.Equal(("服务方法", "controllers", 2), (placeholder.Label, placeholder.Kind, placeholder.Count));
        Assert.Empty(placeholder.Children);
        Assert.Equal("认出 gRPC 服务（@grpc/grpc-js 的 server.addService，pricing.js）：PricingService 2 个方法", f.Notes[0]);
        Assert.Contains(f.Notes, n => n.StartsWith("grpc-js 服务端没有中间件链"));
        Assert.Contains(f.Notes, n => n == "server.js 的 Server 选项里配置了服务端拦截器（authInterceptor），没画");
    }

    [Fact]
    public void UnknownFramework_EmptyWithNote()
    {
        var fx = new Fx("mailer-worker", 13000);
        fx.File("package.json", """{ "name": "mailer-worker", "main": "worker.js", "dependencies": { "bullmq": "^5.7.0", "express": "^4.19.2" } }""");
        fx.File("worker.js", """
            const { Worker } = require('bullmq');
            const worker = new Worker('mail', async (job) => sendMail(job.data));
            worker.on('failed', (job, err) => console.error(job.id, err));
            """);
        var f = FrameworkFlowBuilder.Build(fx.Input());
        Assert.Empty(f.Stages);
        Assert.Empty(f.Flow.Root.Children);
        Assert.Equal("package.json 依赖 Express，但没找到创建应用的地方（const app = express()）", f.Notes[0]);
        Assert.StartsWith("没认出 Node 的 HTTP 框架", f.Notes[1]);
    }
}
