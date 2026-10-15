using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Tests.Architecture;

public class FrontendHttpRuleTests : IDisposable
{
    private readonly TsApiFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private (RuleContext Ctx, FrontendHttpRule Rule) Run()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        var ctx = new RuleContext(inv, ContainerDiscovery.Discover(inv));
        var rule = new FrontendHttpRule();
        rule.Apply(ctx);
        return (ctx, rule);
    }

    [Fact]
    public void WrappedClient_ResolvesRouteConstants_PrefixesBaseUrl_MarksProxy()
    {
        var (ctx, _) = Run();
        var web = ctx.Outbound.Where(o => o.ContainerKey == "web").OrderBy(o => o.File, StringComparer.Ordinal).ThenBy(o => o.Line)
            .Select(o => (o.File, o.Line, o.ClientName, o.Url)).ToList();
        Assert.Equal(new (string, int, string?, string?)[]
        {
            // 服务端 route.ts 直连后端：${baseUrl} 是函数调用结果 → 当作主机名去掉；查询串去掉；不经代理
            ("web/src/app/api/chat/route.ts", 4, "GET", "/api/v5/system/brands"),
            // const api = useApi() → useApi.ts 调 createApiClient → apiClient.ts 里 axios.create({ baseURL: PROXY_BASE_PATH })
            // 行号是 api.get( 那一行（第一个实参在下一行）
            ("web/src/hooks/useMT5Group.ts", 12, "GET 经代理 /api/proxy-gateway", "/api/proxy-gateway/api/v5/mt5/groups"),
            ("web/src/hooks/useMT5Group.ts", 23, "DELETE 经代理 /api/proxy-gateway", "/api/proxy-gateway/api/v5/mt5/groups/${name}"),
            ("web/src/hooks/useMT5Group.ts", 24, "POST 经代理 /api/proxy-gateway", "/api/proxy-gateway/api/v5/mt5/groups/export"),
            ("web/src/hooks/useMT5Group.ts", 26, "PUT 经代理 /api/proxy-gateway", "/api/proxy-gateway/api/v5/jobs/list"), // 局部 const url
            // fetch 的方法取 options 里的 method；常量里已经带了代理前缀
            ("web/src/services/realtime.ts", 3, "POST 经代理 /api/proxy-gateway", "/api/proxy-gateway/api/v5/realtime/ticket"),
        }, web);
        // 第 8 行 Map.get、第 27 行注释、第 28 行解析不了的成员、realtime.ts 第 4 行调本应用的 /api/health、测试文件：都不是出口
        Assert.All(ctx.Outbound, o => Assert.Equal(("frontend-http", "http", "external:http-service:api-api-v5"), (o.RuleId, o.Protocol, o.TargetKey)));
    }

    [Fact]
    public void Unresolved_RecordsSiteAndReason()
    {
        var (_, rule) = Run();
        var un = rule.Unresolved.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).Select(u => (u.File, u.Line, u.Reason)).ToList();
        Assert.Equal(new (string, int, string)[]
        {
            ("web/src/app/api/proxy-gateway/[...path]/route.ts", 5, "不是字符串: 对象字面量"),  // axios.request({ … })
            ("web/src/hooks/useMT5Group.ts", 28, "没有成员: API_ROUTES.CCP_MT5.NOPE"),
            ("web/src/services/apiClient.ts", 5, "未定义: url"),                              // 封装方法内部透传参数
            ("web/src/services/realtime.ts", 4, "本应用 API 路由: /api/health"),
        }, un);
    }

    [Fact]
    public void SameFileFactory_OnlyItsOwnBodyCounts_MapInAComponentThatAlsoFetchesIsNotHttp()
    {
        // ccs-ui 实测的误判：组件文件里 errorTabs = 本地函数()，文件别处有 fetch( —— 按整个文件判会把 errorTabs.get(…) 当 HTTP
        _fx.Write("web/src/components/Tabs.tsx", """
            const buildTabs = () => new Map<string, string>();
            export const Tabs = ({ url }: { url: string }) => {
              const errorTabs = buildTabs();
              const label = errorTabs.get("/api/v5/not-http-either");
              fetch(url);
              return <div title={label}>Don't stop</div>;
            };
            """);
        var (ctx, rule) = Run();
        Assert.DoesNotContain(ctx.Outbound, o => o.File == "web/src/components/Tabs.tsx");
        var u = Assert.Single(rule.Unresolved, u => u.File == "web/src/components/Tabs.tsx");
        Assert.Equal((5, "未定义: url"), (u.Line, u.Reason)); // fetch 本身照认，只是地址是解构参数
    }

    [Fact]
    public void Proxies_CatchAllRouteThatForwardsRequestPath_LibraryFactoryRouteIsNot()
    {
        var (_, rule) = Run();
        var p = Assert.Single(rule.Proxies);
        Assert.Equal(("web", "/api/proxy-gateway", "web/src/app/api/proxy-gateway/[...path]/route.ts"), (p.ContainerKey, p.Prefix, p.Evidence));
    }

    [Fact]
    public void Placeholder_OneNodePerApiRoot_EvidenceListsPathsAndBusinessSummary()
    {
        var (ctx, _) = Run();
        var api = Assert.Single(ctx.Externals.Values);
        Assert.Equal(("external:http-service:api-api-v5", "API /api/v5"), (api.Key, api.Name));
        // 6 条不同路径（按代理剥前缀后的业务路径，次数降序、路径序）+ 1 条汇总
        Assert.Equal(new[]
        {
            "PUT /api/v5/jobs/list（1 处）", "GET /api/v5/mt5/groups（1 处）", "DELETE /api/v5/mt5/groups/${name}（1 处）", "POST /api/v5/mt5/groups/export（1 处）",
            "POST /api/v5/realtime/ticket（1 处）", "GET /api/v5/system/brands（1 处）",
            "共 6 处调用、6 条路径；按业务段：mt5 3、jobs 1、realtime 1、system 1；经 /api/proxy-gateway 代理 5 处",
        }, api.Evidence.Select(e => e.Note));
        Assert.Equal(("web/src/hooks/useMT5Group.ts", 26), (api.Evidence[0].File, api.Evidence[0].Line!.Value));
    }

    [Fact]
    public void DeclaredProxy_FromArchitectureYaml_MarksMatchingCalls()
    {
        _fx.Write(".codeatlas/architecture.yaml", """
            proxies:
              - prefix: /bff
            """);
        _fx.Write("web/src/services/bff.ts", "export const users = () => fetch(\"/bff/api/v5/users\");\n");
        var (ctx, rule) = Run();
        var bff = Assert.Single(ctx.Outbound, o => o.File == "web/src/services/bff.ts");
        Assert.Equal(("GET 经代理 /bff", "/bff/api/v5/users", 1), (bff.ClientName, bff.Url, bff.Line));
        Assert.Equal(new[] { "/api/proxy-gateway", "/bff" }, rule.Proxies.Select(p => p.Prefix).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ClientName_FormatAndParse_RoundTrip()
    {
        Assert.Equal("GET 经代理 /api/proxy-gateway", FrontendHttpRule.FormatCall("GET", "/api/proxy-gateway"));
        Assert.Equal(("GET", "/api/proxy-gateway"), FrontendHttpRule.ParseCall("GET 经代理 /api/proxy-gateway"));
        Assert.Equal(("POST", (string?)null), FrontendHttpRule.ParseCall("POST"));
        Assert.Equal(((string?)null, (string?)null), FrontendHttpRule.ParseCall(null)); // 旧库里的前端出口没有方法
        Assert.Equal("/api/v5/orders/${id}", FrontendHttpRule.PathOf("${base}/api/v5/orders/${id}?x=1")); // 只去掉开头的未知主机段
        // 占位节点按前两段聚合，遇到运行时段就停：不让 ${process.env.REALM} 进节点名
        Assert.Equal("/api/v5", FrontendHttpRule.Group("/api/v5/mt5/groups"));
        Assert.Equal("/realms", FrontendHttpRule.Group("/realms/${process.env.KEYCLOAK_REALM}/protocol/openid-connect/token"));
    }

    [Fact]
    public void ArchitectureYaml_ProxiesSection_DefaultsStripTrue()
    {
        var d = ArchitectureDeclarations.Parse("""
            proxies:
              - prefix: /api/proxy-gateway
              - prefix: /keep
                strip: false
            """);
        Assert.Equal(new[] { ("/api/proxy-gateway", true), ("/keep", false) }, d.Document.Proxies.Select(p => (p.Prefix, p.Strip)));
    }
}
