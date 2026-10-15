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
        Assert.Equal("函数调用: getApiBaseUrl()", r.ResolveExpression("web/src/app/api/chat/route.ts", "getApiBaseUrl()").Failure); // 函数体不求值
        Assert.Equal("外部包: @tanstack/react-query", r.ResolveExpression(Hook, "useQuery").Failure);
    }
}
