using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Tests.Architecture;

public class TsConstantResolverTests : IDisposable
{
    private readonly TsApiFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private TsConstantResolver Resolver() => TsConstantResolver.ForInventory(ProjectInventory.Load(_fx.Root));

    private const string Hook = "web/src/hooks/useMT5Group.ts";

    [Fact]
    public void ResolveModule_AliasFromTsconfigPaths_RelativeIndexAndBarePackages()
    {
        var r = Resolver();
        Assert.Equal("web/src/config/index.ts", r.ResolveModule(Hook, "@/config"));           // @/* → ./src/*，目录 → index.ts
        Assert.Equal("web/src/config/apiConfig.ts", r.ResolveModule(Hook, "@/config/apiConfig"));
        Assert.Equal("web/src/hooks/useApi.ts", r.ResolveModule(Hook, "./useApi"));
        Assert.Equal("web/src/config/base.ts", r.ResolveModule("web/src/config/apiConfig.ts", "../config/base"));
        Assert.Null(r.ResolveModule(Hook, "axios"));                                           // 外部包不跟
        Assert.Null(r.ResolveModule(Hook, "@/nope/missing"));
    }

    [Fact]
    public void ResolveExpression_NestedObjects_TemplatesImportsBarrelsConcatAndFunctions()
    {
        var r = Resolver();
        // @/config（index.ts 的 export *）→ apiConfig.ts 的 API_ROUTES → `${MT5}/groups` → MT5 = `${V5}/mt5` → V5 从 ./base 导入
        Assert.Equal(new TsResolution("/api/v5/mt5/groups", null), r.ResolveExpression(Hook, "API_ROUTES.CCP_MT5.GROUP.BASE"));
        // 箭头函数成员：参数保留为 ${name} 占位，调用实参不展开
        Assert.Equal("/api/v5/mt5/groups/${name}", r.ResolveExpression(Hook, "API_ROUTES.CCP_MT5.GROUP.DETAIL(groupName || \"\")").Value);
        Assert.Equal("/api/v5/mt5/groups/export", r.ResolveExpression(Hook, "API_ROUTES.CCP_MT5.GROUP.EXPORT").Value); // 字符串拼接
        Assert.Equal("/api/v5/jobs/list", r.ResolveExpression(Hook, "API_ROUTES[\"JOBS\"].LIST").Value);            // 带引号的键、下标访问
        // 模板里解析不了的运行时值保留原文占位
        Assert.Equal("/api/v5/jobs/list?id=${row.id}", r.ResolveExpression(Hook, "`${API_ROUTES.JOBS.LIST}?id=${row.id}`").Value);
        Assert.Equal("/api/proxy-gateway", r.ResolveExport("web/src/config/apiConfig.ts", "PROXY_BASE_PATH").Value);
    }

    [Fact]
    public void ResolveExpression_Failures_CarryReasons()
    {
        var r = Resolver();
        var missing = r.ResolveExpression(Hook, "API_ROUTES.CCP_MT5.NOPE.X");
        Assert.Equal((null, "没有成员: API_ROUTES.CCP_MT5.NOPE"), (missing.Value, missing.Failure));
        Assert.Equal("未定义: serverIdOrUrl", r.ResolveExpression(Hook, "serverIdOrUrl").Failure);
        Assert.Equal("函数调用: getApiBaseUrl()", r.ResolveExpression("web/src/app/api/chat/route.ts", "getApiBaseUrl()").Failure); // return 里有 ?? 运算：仍按函数调用报
        Assert.Equal("外部包: @tanstack/react-query", r.ResolveExpression(Hook, "useQuery").Failure);
    }

    private const string Tables = "web/src/config/shopTables.ts";

    private void WriteTables() => _fx.Write(Tables, """
        import { V5 } from "./base";
        export const enum Kind { Carts = "carts", Orders = "orders" }
        export const FORMAT_META: Record<Format, { endpoint: string; accept: string }> = {
          xlsx: { endpoint: `${V5}/shop/orders/export`, accept: "application/x" },
          json: { endpoint: `${V5}/shop/orders/export/json`, accept: "application/octet-stream" },
        };
        export const BACKUPS: Record<Kind, string> = {
          [Kind.Carts]: `${V5}/shop/carts/backups`,
          [Kind.Orders]: `${V5}/shop/orders/backups`,
        };
        export const ORIGINS = { dev: "https://api.dev.acme.test", prod: "https://api.acme.test" };
        const HUB_PATH = "/hubs/orders";
        export const hubUrl = (): string | null => {
          const origin: string | undefined = ORIGINS[currentEnv()];
          return origin ? `${origin}${HUB_PATH}` : null;
        };
        export const opEndpoint = (op: Op) => {
          switch (op) {
            case Op.Start:
              return `${V5}/shop/servers/start`;
            case Op.Stop: {
              return `${V5}/shop/servers/stop`;
            }
          }
        };
        export function legacyEndpoint(id: string): string {
          const names = ids.map(x => { return "/api/v5/not-this-one"; });
          if (id === "") { return `${V5}/shop/legacy`; }
          return `${V5}/shop/legacy/${id}`;
        }
        """);

    [Fact]
    public void Alternatives_TernaryTableLookupAndFunctionReturns_ExpandToEveryValue()
    {
        WriteTables();
        var r = Resolver();
        // 常量表的动态下标：每一项取 .endpoint；计算键 [Kind.Carts] 的表也能枚举
        Assert.Equal(new[] { "/api/v5/shop/orders/export", "/api/v5/shop/orders/export/json" }, r.ResolveAlternatives(Tables, "FORMAT_META[format].endpoint"));
        Assert.Equal(new[] { "/api/v5/shop/carts/backups", "/api/v5/shop/orders/backups" }, r.ResolveAlternatives(Tables, "BACKUPS[kind]"));
        // 三元：条件带运算也行；null 分支算"没有地址"不计入
        Assert.Equal(new[] { "/a", "/b" }, r.ResolveAlternatives(Tables, "kind === Kind.Carts ? \"/a\" : \"/b\""));
        Assert.Equal(new TsResolution("/a", null), r.ResolveExpression(Tables, "ready ? \"/a\" : null"));
        // 花括号函数体：switch 的两个 return；局部 const 的动态下标是三个主机之一 → 留成占位；嵌套回调里的 return 不算
        Assert.Equal(new[] { "/api/v5/shop/servers/start", "/api/v5/shop/servers/stop" }, r.ResolveAlternatives(Tables, "opEndpoint(op)"));
        Assert.Equal(new TsResolution("${origin}/hubs/orders", null), r.ResolveExpression(Tables, "hubUrl()"));
        Assert.Equal(new[] { "/api/v5/shop/legacy", "/api/v5/shop/legacy/${id}" }, r.ResolveAlternatives(Tables, "legacyEndpoint(x)"));
    }

    [Fact]
    public void Alternatives_AnyUnresolvableBranch_FailsTheWhole()
    {
        WriteTables();
        var r = Resolver();
        Assert.Equal("未定义: missingThing", r.ResolveExpression(Tables, "ready ? \"/a\" : missingThing").Failure); // 不挑能解析的那半
        Assert.Null(r.ResolveAlternatives(Tables, "ready ? \"/a\" : missingThing"));
        Assert.Equal("多个取值: /api/v5/shop/carts/backups | /api/v5/shop/orders/backups", r.ResolveExpression(Tables, "BACKUPS[kind]").Failure); // 单值接口照旧报失败
        Assert.Equal("动态下标: FORMAT_META[format].pick()", r.ResolveExpression(Tables, "FORMAT_META[format].pick()").Failure);  // 下标后再调用：不求值
        Assert.Equal("动态下标: ORIGINS_MISSING[env]", r.ResolveExpression(Tables, "ORIGINS_MISSING[env]").Failure);
        Assert.Null(r.ResolveAlternatives(Tables, "FORMAT_META[format]")); // 各项是对象，不是字符串
    }
}
