using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// Node 后端入口：Express（跨文件的 app.use 前缀、router.route 链、包装过的处理器）、Koa-router（prefix 与嵌套 use）、
/// Fastify（register 的 prefix、route 对象）、NestJS（@Controller + @Get…，setGlobalPrefix）、gRPC addService（简写 / 键值 / bind / 对象方法）。
/// 只看 node-service / node-cli 容器；框架按容器 package.json 的依赖定。
/// </summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> NodeFiles = new()
    {
        // ── Express ──
        ["api/package.json"] = ["{ \"dependencies\": { \"express\": \"4.19.0\" } }"],
        ["api/app.js"] =
        [
            "const express = require('express');",                    // 1
            "const users = require('./routes/users');",               // 2
            "import ordersRouter from './routes/orders';",            // 3
            "const app = express();",                                 // 4
            "app.get('/health', (req, res) => res.send('ok'));",     // 5：内联、顶层 → 文件模块
            "app.use('/api/users', users);",                          // 6
            "app.use('/api/orders', ordersRouter);",                  // 7
            "app.use('/static', express.static('public'));",          // 8：中间件，不是路由
            "app.get('env');",                                        // 9：一个实参是读设置
            "axios.get('/not-a-route', cb);",                         // 10：客户端调用
            "// app.post('/commented', h);",                          // 11
            "module.exports = app;",                                  // 12
        ],
        ["api/routes/users.js"] =
        [
            "const express = require('express');",                       // 1
            "const router = express.Router();",                          // 2
            "const ctrl = require('../controllers/users');",            // 3
            "router.get('/', ctrl.list);",                               // 4：导入模块的导出函数
            "router.post('/:id', auth, asyncHandler(ctrl.update));",     // 5：中间件在前、包装过的处理器
            "router.route('/:id/avatar').get(getAvatar).put(putAvatar);", // 6
            "function getAvatar(req, res) {}",                           // 7
            "function putAvatar(req, res) {}",                           // 8
            "module.exports = router;",                                  // 9
        ],
        ["api/controllers/users.js"] =
        [
            "exports.list = async (req, res) => {};",               // 1
            "exports.update = async function update(req, res) {};", // 2
        ],
        ["api/routes/orders.ts"] =
        [
            "import { Router } from 'express';",                                       // 1
            "import { createOrder } from '../services/orders.js';",                    // 2：TS 的 ESM 写法，实际文件是 .ts
            "const router = Router();",                                                // 3
            "router.post('/', createOrder);",                                          // 4
            "router.delete('/:id', async (req, res) => { await cancel(req.params.id); });", // 5：内联、顶层 → 文件模块
            "export default router;",                                                  // 6
        ],
        ["api/services/orders.ts"] =
        [
            "export async function createOrder(req, res) {}", // 1
            "export async function cancel(id) {}",            // 2
        ],
        // ── Koa ──
        ["koa/package.json"] = ["{ \"dependencies\": { \"koa\": \"2.15.0\", \"@koa/router\": \"12.0.0\" } }"],
        ["koa/server.js"] =
        [
            "const Koa = require('koa');",                       // 1
            "const Router = require('@koa/router');",            // 2
            "const app = new Koa();",                            // 3
            "const router = new Router({ prefix: '/users' });",  // 4
            "router.get('/:id', getUser);",                      // 5
            "const admin = new Router();",                       // 6
            "admin.post('/reindex', reindex);",                  // 7
            "router.use('/admin', admin.routes());",             // 8：嵌套挂载
            "app.use(router.routes());",                         // 9
            "async function getUser(ctx) {}",                    // 10
            "async function reindex(ctx) {}",                    // 11
        ],
        // ── Fastify ──
        ["fst/package.json"] = ["{ \"dependencies\": { \"fastify\": \"4.26.0\" } }"],
        ["fst/app.js"] =
        [
            "const fastify = require('fastify')({ logger: true });",                         // 1
            "fastify.register(require('./routes/items'), { prefix: '/v1' });",              // 2：插件文件加前缀
            "fastify.get('/ping', async () => 'pong');",                                     // 3
            "fastify.route({ method: ['GET', 'HEAD'], url: '/status', handler: status });",  // 4
            "function status(req, reply) {}",                                                // 5
        ],
        ["fst/routes/items.js"] =
        [
            "module.exports = async function (fastify, opts) {",   // 1
            "  fastify.get('/items', { schema: {} }, listItems);", // 2：选项对象在中间
            "  fastify.post('/items', createItem);",               // 3
            "};",                                                  // 4
            "async function listItems(req, reply) {}",             // 5
            "async function createItem(req, reply) {}",            // 6
        ],
        // ── NestJS ──
        ["nest/package.json"] = ["{ \"dependencies\": { \"@nestjs/core\": \"10.3.0\", \"@nestjs/common\": \"10.3.0\" } }"],
        ["nest/src/main.ts"] =
        [
            "import { NestFactory } from '@nestjs/core';",               // 1
            "async function bootstrap() {",                              // 2
            "  const app = await NestFactory.create(AppModule);",        // 3
            "  app.setGlobalPrefix('api');",                             // 4
            "  await app.listen(3000);",                                 // 5
            "}",                                                         // 6
        ],
        ["nest/src/cats.controller.ts"] =
        [
            "import { Controller, Get, Post, Param } from '@nestjs/common';", // 1
            "",                                                              // 2
            "@Controller('cats')",                                           // 3
            "export class CatsController {",                                 // 4
            "  constructor(private readonly svc: CatsService) {}",           // 5
            "",                                                              // 6
            "  @Get()",                                                      // 7
            "  findAll() { return this.svc.all(); }",                        // 8
            "",                                                              // 9
            "  @Get(':id')",                                                 // 10
            "  findOne(@Param('id') id: string) { return this.svc.one(id); }", // 11
            "",                                                              // 12
            "  @Post()",                                                     // 13
            "  @HttpCode(204)",                                              // 14
            "  create() {}",                                                 // 15
            "",                                                              // 16
            "  helper() {}",                                                 // 17：没有映射装饰器
            "}",                                                             // 18
        ],
        // ── gRPC ──
        ["grpcsvc/package.json"] = ["{ \"dependencies\": { \"@grpc/grpc-js\": \"1.10.0\" } }"],
        ["grpcsvc/server.js"] =
        [
            "const grpc = require('@grpc/grpc-js');",                                                              // 1
            "const impl = require('./impl');",                                                                     // 2
            "function getQuote(call, callback) {}",                                                                // 3
            "function main() {",                                                                                   // 4
            "  const server = new grpc.Server();",                                                                 // 5
            "  server.addService(proto.hipstershop.ShippingService.service, { getQuote, shipOrder: impl.shipOrder });", // 6
            "  server.addService(healthProto.Health.service, { check: () => {} });",                               // 7：健康检查不算
            "  const handlers = {",                                                                                // 8
            "    charge: Payments.charge.bind(this),",                                                             // 9
            "    async refund(call, cb) { await doRefund(); },",                                                   // 10
            "  };",                                                                                                // 11
            "  server.addService(proto.PaymentService.service, handlers);",                                        // 12：变量 → 同文件的对象字面量
            "}",                                                                                                   // 13
            "class Payments { static charge(call, cb) {} }",                                                       // 14
        ],
        ["grpcsvc/impl.js"] =
        [
            "exports.shipOrder = function shipOrder(call, callback) {};", // 1
        ],
        // 前端容器里的同样写法不算（只看 Node 后端 / 命令行容器）
        ["web/src/api.js"] =
        [
            "const app = express();",          // 1
            "app.get('/mock', handler);",      // 2
            "function handler() {}",           // 3
        ],
    };

    private static readonly List<MapSymbol> NodeSymbols =
    [
        Sym(4000, SymbolKind.Module, "api/app.js", "api/app.js", null, 1, 12),
        Sym(4001, SymbolKind.Module, "api/routes/users.js", "api/routes/users.js", null, 1, 9),
        Sym(4002, SymbolKind.Function, "api/routes/users.js:getAvatar", "api/routes/users.js", 4001, 7, 7),
        Sym(4003, SymbolKind.Function, "api/routes/users.js:putAvatar", "api/routes/users.js", 4001, 8, 8),
        Sym(4004, SymbolKind.Module, "api/controllers/users.js", "api/controllers/users.js", null, 1, 2),
        Sym(4005, SymbolKind.Function, "api/controllers/users.js:list", "api/controllers/users.js", 4004, 1, 1, "export"),
        Sym(4006, SymbolKind.Function, "api/controllers/users.js:update", "api/controllers/users.js", 4004, 2, 2, "export"),
        Sym(4007, SymbolKind.Module, "api/routes/orders.ts", "api/routes/orders.ts", null, 1, 6),
        Sym(4008, SymbolKind.Module, "api/services/orders.ts", "api/services/orders.ts", null, 1, 2),
        Sym(4009, SymbolKind.Function, "api/services/orders.ts:createOrder", "api/services/orders.ts", 4008, 1, 1, "export"),
        Sym(4010, SymbolKind.Function, "api/services/orders.ts:cancel", "api/services/orders.ts", 4008, 2, 2, "export"),
        Sym(4020, SymbolKind.Module, "koa/server.js", "koa/server.js", null, 1, 11),
        Sym(4021, SymbolKind.Function, "koa/server.js:getUser", "koa/server.js", 4020, 10, 10),
        Sym(4022, SymbolKind.Function, "koa/server.js:reindex", "koa/server.js", 4020, 11, 11),
        Sym(4030, SymbolKind.Module, "fst/app.js", "fst/app.js", null, 1, 5),
        Sym(4031, SymbolKind.Function, "fst/app.js:status", "fst/app.js", 4030, 5, 5),
        Sym(4032, SymbolKind.Module, "fst/routes/items.js", "fst/routes/items.js", null, 1, 6),
        Sym(4033, SymbolKind.Function, "fst/routes/items.js:default", "fst/routes/items.js", 4032, 1, 4, "export default"),
        Sym(4034, SymbolKind.Function, "fst/routes/items.js:listItems", "fst/routes/items.js", 4032, 5, 5),
        Sym(4035, SymbolKind.Function, "fst/routes/items.js:createItem", "fst/routes/items.js", 4032, 6, 6),
        Sym(4040, SymbolKind.Module, "nest/src/main.ts", "nest/src/main.ts", null, 1, 6),
        Sym(4041, SymbolKind.Function, "nest/src/main.ts:bootstrap", "nest/src/main.ts", 4040, 2, 6),
        Sym(4042, SymbolKind.Module, "nest/src/cats.controller.ts", "nest/src/cats.controller.ts", null, 1, 18),
        Sym(4043, SymbolKind.Class, "nest/src/cats.controller.ts:CatsController", "nest/src/cats.controller.ts", 4042, 4, 18, "export"),
        Sym(4044, SymbolKind.Method, "nest/src/cats.controller.ts:CatsController.constructor", "nest/src/cats.controller.ts", 4043, 5, 5),
        Sym(4045, SymbolKind.Method, "nest/src/cats.controller.ts:CatsController.findAll", "nest/src/cats.controller.ts", 4043, 8, 8), // 方法从名字行起，不含装饰器
        Sym(4046, SymbolKind.Method, "nest/src/cats.controller.ts:CatsController.findOne", "nest/src/cats.controller.ts", 4043, 11, 11),
        Sym(4047, SymbolKind.Method, "nest/src/cats.controller.ts:CatsController.create", "nest/src/cats.controller.ts", 4043, 15, 15),
        Sym(4048, SymbolKind.Method, "nest/src/cats.controller.ts:CatsController.helper", "nest/src/cats.controller.ts", 4043, 17, 17),
        Sym(4050, SymbolKind.Module, "grpcsvc/server.js", "grpcsvc/server.js", null, 1, 14),
        Sym(4051, SymbolKind.Function, "grpcsvc/server.js:getQuote", "grpcsvc/server.js", 4050, 3, 3),
        Sym(4052, SymbolKind.Function, "grpcsvc/server.js:main", "grpcsvc/server.js", 4050, 4, 13),
        Sym(4053, SymbolKind.Method, "grpcsvc/server.js:refund", "grpcsvc/server.js", 4050, 10, 10), // 对象字面量里的方法挂在模块上
        Sym(4054, SymbolKind.Class, "grpcsvc/server.js:Payments", "grpcsvc/server.js", 4050, 14, 14),
        Sym(4055, SymbolKind.Method, "grpcsvc/server.js:Payments.charge", "grpcsvc/server.js", 4054, 14, 14),
        Sym(4056, SymbolKind.Module, "grpcsvc/impl.js", "grpcsvc/impl.js", null, 1, 1),
        Sym(4057, SymbolKind.Function, "grpcsvc/impl.js:shipOrder", "grpcsvc/impl.js", 4056, 1, 1, "export"),
        Sym(4060, SymbolKind.Module, "web/src/api.js", "web/src/api.js", null, 1, 3),
        Sym(4061, SymbolKind.Function, "web/src/api.js:handler", "web/src/api.js", 4060, 3, 3),
    ];

    private static readonly List<ArchNode> NodeContainers =
    [
        Container("api", "node-service", "api"),
        Container("koa", "node-service", "koa"),
        Container("fst", "node-service", "fst"),
        Container("nest", "node-service", "nest"),
        Container("grpcsvc", "node-service", "grpcsvc"),
        Container("web", "frontend", "web"),
    ];

    private static List<EntryPoint> NodeEntries() =>
        EntryDiscovery.Discover(NodeSymbols, [], NodeContainers, p => NodeFiles.TryGetValue(p, out var l) ? l : []);

    private static IEnumerable<(string Container, string Route, string? Method, long Symbol, string File, int Line)> NodeRows(string ruleId) =>
        Rows(NodeEntries().Where(e => e.RuleId == ruleId));

    [Fact]
    public void Node_Express_MountPrefixesAcrossFiles_RouteChains_WrappedHandlers()
    {
        var express = NodeEntries().Where(e => e.RuleId == "node.express").ToList();
        Assert.All(express, e => Assert.Equal("http", e.Kind));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("api", "/api/orders", "POST", 4009, "api/routes/orders.ts", 4),             // import 的默认导出路由器 + services/orders.js → .ts
            ("api", "/api/orders/:id", "DELETE", 4007, "api/routes/orders.ts", 5),       // 内联处理器在顶层 → 文件模块
            ("api", "/api/users", "GET", 4005, "api/routes/users.js", 4),                // require 的路由器（module.exports = router）；ctrl.list
            ("api", "/api/users/:id", "POST", 4006, "api/routes/users.js", 5),           // asyncHandler(ctrl.update)
            ("api", "/api/users/:id/avatar", "GET", 4002, "api/routes/users.js", 6),     // router.route('/:id/avatar').get(…).put(…)
            ("api", "/api/users/:id/avatar", "PUT", 4003, "api/routes/users.js", 6),
            ("api", "/health", "GET", 4000, "api/app.js", 5),
        }, NodeRows("node.express"));
        Assert.DoesNotContain(NodeEntries(), e => e.Route is "/static" or "/not-a-route" or "/commented" or "/mock" || e.ContainerKey == "web");
    }

    [Fact]
    public void Node_Koa_RouterPrefixAndNestedUse()
    {
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("koa", "/users/:id", "GET", 4021, "koa/server.js", 5),             // new Router({ prefix: '/users' })
            ("koa", "/users/admin/reindex", "POST", 4022, "koa/server.js", 7),  // router.use('/admin', admin.routes())
        }, NodeRows("node.koa"));
    }

    [Fact]
    public void Node_Fastify_RegisterPrefix_RouteObject()
    {
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("fst", "/ping", "GET", 4030, "fst/app.js", 3),
            ("fst", "/status", "GET", 4031, "fst/app.js", 4),                 // route({ method: ['GET', 'HEAD'], url, handler })
            ("fst", "/status", "HEAD", 4031, "fst/app.js", 4),
            ("fst", "/v1/items", "GET", 4034, "fst/routes/items.js", 2),      // register(require('./routes/items'), { prefix: '/v1' })
            ("fst", "/v1/items", "POST", 4035, "fst/routes/items.js", 3),
        }, NodeRows("node.fastify"));
    }

    [Fact]
    public void Node_NestJs_ControllerDecorators_GlobalPrefix()
    {
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("nest", "/api/cats", "GET", 4045, "nest/src/cats.controller.ts", 8),
            ("nest", "/api/cats", "POST", 4047, "nest/src/cats.controller.ts", 15),
            ("nest", "/api/cats/:id", "GET", 4046, "nest/src/cats.controller.ts", 11),
        }, NodeRows("node.nestjs"));
        Assert.DoesNotContain(NodeEntries(), e => e.SymbolId is 4044 or 4048);
    }

    [Fact]
    public void Node_Grpc_AddService_ShorthandKeyValueBindAndObjectMethods()
    {
        var grpc = NodeEntries().Where(e => e.RuleId == "node.grpc").ToList();
        Assert.All(grpc, e => Assert.Equal(("grpc", (string?)null), (e.Kind, e.HttpMethod)));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("grpcsvc", "PaymentService/Charge", null, 4055, "grpcsvc/server.js", 14),   // Payments.charge.bind(this)
            ("grpcsvc", "PaymentService/Refund", null, 4053, "grpcsvc/server.js", 10),   // 对象方法 async refund(…) {}
            ("grpcsvc", "ShippingService/GetQuote", null, 4051, "grpcsvc/server.js", 3), // 简写 { getQuote }，方法名转 PascalCase
            ("grpcsvc", "ShippingService/ShipOrder", null, 4057, "grpcsvc/impl.js", 1),  // impl.shipOrder → require 的模块
        }, NodeRows("node.grpc"));
        Assert.DoesNotContain(grpc, e => e.Route!.StartsWith("Health/", StringComparison.Ordinal));
    }
}
