using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Tests.Architecture;

public class ContainerDiscoveryTests : IDisposable
{
    private readonly ArchFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Inventory_ReadsProjects_Packages_References_Ports_AndConfig()
    {
        var inv = ProjectInventory.Load(_fx.Root);

        Assert.Equal(new[] { "Acme.Api", "Acme.App", "Acme.MsTests", "Acme.Tests", "Acme.Worker", "billing/Billing.Api", "services/Catalog.Api", "services/Web.App", "vendor" }, inv.Projects.Select(p => p.Dir).OrderBy(d => d, StringComparer.Ordinal));
        Assert.True(inv.Projects.Single(p => p.Dir == "Acme.MsTests").IsTest); // MSTest.Sdk 没有 PackageReference，靠 Sdk 名
        var api = inv.Projects.Single(p => p.Dir == "Acme.Api");
        Assert.Equal("Microsoft.NET.Sdk.Web", api.Sdk);
        Assert.Equal(new[] { "Acme.App" }, api.ProjectRefs);
        Assert.Contains(api.Packages, p => p.Name == "Grpc.Net.Client" && p.Line == 8);
        Assert.Equal(new[] { 5001, 8080 }, api.Ports.OrderBy(p => p)); // launchSettings 两处 5001 去重 + Dockerfile EXPOSE；44301 是 https 也算吗？——不算：只取 http 端口
        Assert.Equal("Acme.Api/Dockerfile", api.Dockerfile);

        var worker = inv.Projects.Single(p => p.Dir == "Acme.Worker");
        Assert.Equal("Exe", worker.OutputType);
        Assert.Equal(new[] { "Acme.App" }, worker.ProjectRefs); // 正斜杠写法也要能解析

        Assert.True(inv.Projects.Single(p => p.Dir == "Acme.Tests").IsTest);
        var vendor = inv.Projects.Single(p => p.Dir == "vendor");
        var local = Assert.Single(vendor.LocalReferences);
        Assert.Equal(("MetaQuotes.MT5ManagerAPI64", 4), (local.Name, local.Line)); // 原始字符串去掉公共缩进后 <Reference> 在第 4 行

        var web = Assert.Single(inv.Frontends);
        Assert.Equal(("web", "acme-web", "next"), (web.Dir, web.Name, web.Framework));
        Assert.Equal(new[] { 3000 }, web.Ports);

        // appsettings 扁平化：主文件为准，环境文件另记
        Assert.Equal("http://localhost:5002/", inv.Config("Acme.Api", "Billing:Url"));
        Assert.Equal("kafka:9092", inv.Config("Acme.Api", "Kafka:BootstrapServers"));
        Assert.Null(inv.Config("Acme.Api", "Nope:Url"));
        Assert.Contains(inv.ConfigEntries, c => c.Key == "Billing:Url" && c.Environment == "Development");
        Assert.Equal("Acme.Api/appsettings.json", inv.ConfigEntries.First(c => c.Key == "Billing:Url" && c.Environment is null).File);
    }

    [Fact]
    public void Containers_AreExecutablesAndFrontends_NotTestsOrLibraries_WithTransitiveMembers()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        var containers = ContainerDiscovery.Discover(inv);

        Assert.Equal(new[] { "Acme.Api", "Acme.Worker", "billing/Billing.Api", "services/Catalog.Api", "services/Web.App", "web" }, containers.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal)); // Acme.MsTests 不是容器
        var api = containers.Single(c => c.Key == "Acme.Api");
        Assert.Equal(("dotnet-web", "Acme.Api", ArchNodeKind.Container, ArchSource.Discovered), (api.SubKind, api.Name, api.Kind, api.Source));
        Assert.Equal(new[] { "Acme.Api", "Acme.App" }, api.Members.OrderBy(m => m, StringComparer.Ordinal));
        Assert.Equal(new[] { 5001, 8080 }, api.Ports.OrderBy(p => p));
        Assert.Contains(api.Evidence, e => e.File == "Acme.Api/Acme.Api.csproj");
        Assert.Contains(api.Evidence, e => e.File == "Acme.Api/Dockerfile");
        Assert.Contains(api.Evidence, e => e.File == "Acme.Api/Properties/launchSettings.json");

        var worker = containers.Single(c => c.Key == "Acme.Worker");
        Assert.Equal("dotnet-exe", worker.SubKind);
        Assert.Equal(new[] { "Acme.App", "Acme.Worker" }, worker.Members.OrderBy(m => m, StringComparer.Ordinal)); // 同一个库进两个容器
        Assert.Empty(worker.Ports);

        var billing = containers.Single(c => c.Key == "billing/Billing.Api");
        Assert.Equal(new[] { "billing/Billing.Api", "vendor" }, billing.Members.OrderBy(m => m, StringComparer.Ordinal)); // 跨目录的 ..\..\ 引用也要解析到

        var web = containers.Single(c => c.Key == "web");
        Assert.Equal(("frontend", "acme-web"), (web.SubKind, web.Name));
        Assert.Equal(new[] { 3000 }, web.Ports);
    }
}
