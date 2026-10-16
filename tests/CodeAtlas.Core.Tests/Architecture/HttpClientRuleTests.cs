using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// HttpClientRule 的两处补充：只占名字、业务代码没人按名字取的命名客户端不算出口；地址取自配置对象属性的 HttpClient 调用算出口。
/// 夹具：Shop.Api（Web 宿主）引用 Shop.Infra（共享库），另有一个测试项目。
/// </summary>
public sealed class HttpClientRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-httpclient-" + Guid.NewGuid().ToString("N"));

    public HttpClientRuleTests()
    {
        Write("Shop.Api/Shop.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Shop.Infra\Shop.Infra.csproj" /></ItemGroup>
            </Project>
            """);
        Write("Shop.Api/appsettings.json", """
            {
              "MailOptions": { "ApiUrl": "https://mail.acme.test/v3/send", "ApiKey": "x" },
              "Payments": { "Endpoint": "https://pay.acme.test/" }
            }
            """);
        Write("Shop.Infra/Shop.Infra.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("Shop.Infra/Names.cs", """
            namespace Shop.Infra;
            public static class Names
            {
                public const string Search = "SearchClient";
                public const string LEGACY = "LegacyClient";
            }
            """);
        Write("Shop.Infra/Setup.cs", """
            namespace Shop.Infra;
            public static class Setup
            {
                public static IServiceCollection AddClients(this IServiceCollection services, IConfiguration configuration)
                {
                    services.Configure<MailOptions>(configuration.GetSection(nameof(MailOptions)));
                    services.Configure<PaymentOptions>(configuration.GetSection("Payments"));
                    services.AddHttpClient("LegacyClient");
                    services.AddHttpClient(Names.Search);
                    services.AddHttpClient<CatalogClient>();
                    return services;
                }
            }
            """);
        Write("Shop.Infra/SearchService.cs", """
            namespace Shop.Infra;
            public sealed class SearchService(IHttpClientFactory factory)
            {
                // factory.CreateClient("LegacyClient") 注释里的不算
                public HttpClient Client() => factory.CreateClient(Names.Search);
            }
            """);
        Write("Shop.Infra/MailService.cs", """
            namespace Shop.Infra;
            public sealed class MailService
            {
                private readonly MailOptions _mail;
                private readonly PaymentOptions _payments;
                private readonly IHttpClientFactory _factory;
                public MailService(IOptions<MailOptions> mail, IOptions<PaymentOptions> payments, IHttpClientFactory factory) { _mail = mail.Value; _payments = payments.Value; _factory = factory; }
                public async Task SendAsync(object body)
                {
                    using var client = _factory.CreateClient();
                    var response = await client.PostAsJsonAsync(_mail.ApiUrl, body);
                    await client.PostAsync(new Uri(_payments.Endpoint), null);
                }
                public Task NotifyAsync(Order order) => _factory.CreateClient().GetAsync(order.CallbackUrl);
            }
            """);
        Write("Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <ProjectReference Include="..\Shop.Infra\Shop.Infra.csproj" />
              </ItemGroup>
            </Project>
            """);
        // 只有测试按名字取 LegacyClient：不让它把注册"救"回来
        Write("Shop.Tests/SetupTests.cs", "public class SetupTests { void M(IHttpClientFactory f) { f.CreateClient(\"LegacyClient\"); f.CreateClient(Names.LEGACY); } }\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private RuleContext Run()
    {
        var inv = ProjectInventory.Load(_root);
        var ctx = new RuleContext(inv, ContainerDiscovery.Discover(inv));
        new HttpClientRule().Apply(ctx);
        return ctx;
    }

    [Fact]
    public void NameOnlyRegistration_KeptWhenBusinessCodeRequestsIt_DroppedWhenOnlyTestsDo()
    {
        var ctx = Run();
        var registrations = ctx.Outbound.Where(o => o.File == "Shop.Infra/Setup.cs").OrderBy(o => o.Line).Select(o => (o.Line, o.ClientName)).ToList();
        // 第 8 行 LegacyClient：业务代码没人取（常量 LEGACY 只在定义处、测试里出现）→ 不是出口；类型化客户端照旧（按类型注入，不按名字找）
        Assert.Equal(new (int, string?)[] { (9, "Search"), (10, "CatalogClient") }, registrations);
    }

    [Fact]
    public void ConfigObjectUrl_SectionFromConfigureBinding_OnlyWhenTheKeyExists()
    {
        var ctx = Run();
        var calls = ctx.Outbound.Where(o => o.File == "Shop.Infra/MailService.cs").OrderBy(o => o.Line).Select(o => (o.ContainerKey, o.Line, o.ConfigKey, o.Url)).ToList();
        Assert.Equal(new (string, int, string?, string?)[]
        {
            ("Shop.Api", 11, "MailOptions:ApiUrl", null),   // Configure<MailOptions>(GetSection(nameof(MailOptions)))
            ("Shop.Api", 12, "Payments:Endpoint", null),    // Configure<PaymentOptions>(GetSection("Payments"))；new Uri(…) 包一层也认
        }, calls); // 第 14 行 order.CallbackUrl：Order 不是绑定配置的选项类，配置里也没有 Order:CallbackUrl
        Assert.All(ctx.Outbound.Where(o => o.File == "Shop.Infra/MailService.cs"), o => Assert.Equal(("http-client", "http", (string?)null), (o.RuleId, o.Protocol, o.ClientName)));
    }
}
