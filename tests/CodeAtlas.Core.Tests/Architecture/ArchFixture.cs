namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// 临时目录里的一个小仓库：两个 .NET 宿主（Web + Worker）共用一个库，一个测试项目，一个独立的 Billing 服务，
/// 一个带厂商原生 DLL 引用的库，一个 Next.js 前端。各规则的正例与反例都放在这里。
/// </summary>
public sealed class ArchFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-arch-" + Guid.NewGuid().ToString("N"));

    public ArchFixture()
    {
        Write("Acme.sln", "");
        Write("Acme.Api/Acme.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Acme.App\Acme.App.csproj" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
                <PackageReference Include="Grpc.Net.Client" Version="2.70.0" />
              </ItemGroup>
            </Project>
            """);
        Write("Acme.Api/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5001" },
                            "IIS Express": { "commandName": "IISExpress", "applicationUrl": "https://localhost:44301;http://localhost:5001" } } }
            """);
        Write("Acme.Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nEXPOSE 8080\nENTRYPOINT [\"dotnet\", \"Acme.Api.dll\"]\n");
        Write("Acme.Api/appsettings.json", """
            {
              "ConnectionStrings": { "Db": "Host=db;Database=acme" },
              "Billing": { "Url": "http://localhost:5002/" },
              "Lark": { "Url": "https://open.larksuite.com/open-apis/" },
              "Kafka": { "BootstrapServers": "kafka:9092", "Topics": ["events"] },
              "Reports": { "Url": "http://localhost:5099/" }
            }
            """);
        Write("Acme.Api/appsettings.Development.json", """{ "Billing": { "Url": "http://localhost:5002/" } }""");
        Write("Acme.Api/Setup.cs", """
            using Microsoft.Extensions.DependencyInjection;
            namespace Acme.Api;
            public static class Setup
            {
                public static IServiceCollection AddBillingClient(this IServiceCollection services, IConfiguration configuration)
                {
                    var billing = configuration.GetSection("Billing").Get<BillingOptions>();
                    var baseUrl = billing.Url.TrimEnd('/') + "/";
                    services.AddHttpClient("Billing", client =>
                    {
                        client.BaseAddress = new Uri(baseUrl);
                    });
                    // services.AddHttpClient("Commented");
                    var ignored = "services.AddHttpClient(\"InString\")";
                    services.AddHttpClient(Names.LarkClient);
                    services.AddHttpClient("Reports", client => client.BaseAddress = new Uri(configuration["Reports:Url"]!));
                    services.AddHttpClient("Inline", client => client.BaseAddress = new Uri("http://localhost:5002/api/"));
                    var channel = GrpcChannel.ForAddress(configuration["Pricing:Grpc"]!);
                    return services;
                }
            }
            """);
        Write("Acme.Worker/Acme.Worker.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Acme.App/Acme.App.csproj" /></ItemGroup>
            </Project>
            """);
        Write("Acme.Worker/Worker.cs", """
            namespace Acme.Worker;
            public sealed class EventConsumer : BackgroundService
            {
                private readonly IConsumer<string, string> _consumer;
                protected override Task ExecuteAsync(CancellationToken ct) { _consumer.Subscribe("events"); return Task.CompletedTask; }
            }
            """);
        Write("Acme.App/Acme.App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
                <PackageReference Include="StackExchange.Redis" Version="2.8.0" />
                <PackageReference Include="Confluent.Kafka" Version="2.6.0" />
              </ItemGroup>
            </Project>
            """);
        Write("Acme.App/Infra.cs", """
            namespace Acme.App;
            public sealed class AcmeDbContext : DbContext { }
            public sealed class Cache(IConnectionMultiplexer redis) { }
            public sealed class Publisher(IProducer<string, string> producer) { }
            """);
        Write("Acme.Tests/Acme.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <ProjectReference Include="..\Acme.Api\Acme.Api.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Acme.Tests/SetupTests.cs", "namespace Acme.Tests; public class T { void M(IServiceCollection s) { s.AddHttpClient(\"FromTests\"); } }\n");
        // MSTest.Sdk 项目：没有 PackageReference，靠 Sdk 名认出是测试（eShop 的写法）
        Write("Acme.MsTests/Acme.MsTests.csproj", """
            <Project Sdk="MSTest.Sdk/3.8.0">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Acme.Api\Acme.Api.csproj" /></ItemGroup>
            </Project>
            """);
        // Aspire 风格的服务：服务发现名 + 目标类型 new + Aspire 客户端包 + gRPC 客户端注册 + RabbitMQ 异步发布
        Write("services/Catalog.Api/Catalog.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Aspire.Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" /></ItemGroup>
            </Project>
            """);
        Write("services/Catalog.Api/Program.cs", "builder.AddNpgsqlDbContext<CatalogContext>(\"catalogdb\");\nchannel.BasicPublishAsync(exchange: \"x\", routingKey: \"k\", body: b);\n");
        Write("services/Web.App/Web.App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("services/Web.App/Extensions.cs", """
            public static class Extensions
            {
                public static void AddClients(this IServiceCollection services)
                {
                    services.AddHttpClient<CatalogService>(o => o.BaseAddress = new("https+http://catalog-api"));
                    services.AddGrpcClient<Basket.BasketClient>(o => o.Address = new("http://basket-api"));
                }
            }
            """);
        Write("billing/Billing.Api/Billing.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\..\vendor\Vendor.csproj" /></ItemGroup>
            </Project>
            """);
        Write("billing/Billing.Api/Properties/launchSettings.json", """{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5002" } } }""");
        Write("vendor/Vendor.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Reference Include="MetaQuotes.MT5ManagerAPI64">
                  <HintPath>..\libs\MetaQuotes.MT5ManagerAPI64.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        Write("vendor/Native.cs", "namespace Vendor; public static class N { [DllImport(\"mt4lib\")] public static extern int Ping(); }\n");
        Write("web/package.json", """{ "name": "acme-web", "scripts": { "build": "next build" }, "dependencies": { "next": "15.0.0", "react": "19.0.0", "axios": "1.7.0" } }""");
        Write("web/Dockerfile", "FROM node:22\nEXPOSE 3000\n");
        Write("web/src/api.ts", """
            const base = process.env.API_BASE;
            export async function orders() { return fetch(`${base}/api/v5/orders`); }
            export async function users() { return axios.get("/api/v5/users"); }
            export async function dynamic(url: string) { return fetch(url); }
            """);
        Write("node_modules/ignored/package.json", """{ "name": "ignored", "dependencies": { "next": "1" }, "scripts": { "build": "x" } }""");
    }

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
