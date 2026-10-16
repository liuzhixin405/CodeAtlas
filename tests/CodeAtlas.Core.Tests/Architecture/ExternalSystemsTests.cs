using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>审计漏掉 / 认错的外部系统与容器：SSM、Redis 分布式缓存、内嵌数据库、共享契约 DLL、JWT 身份提供方、AI 端点、using static 的 gRPC 客户端、VS Code 扩展、Node 端口。</summary>
public sealed class ExternalSystemsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-ext-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Edge(ArchEdge e) => $"{e.FromKey} → {e.ToKey} {e.Protocol} {e.Confidence:0.0#}";

    [Fact]
    public void Packages_SsmRedisCacheAndEmbeddedDatabases_SharedContractsDllIsNotExternal()
    {
        Write("Shop.Api/Shop.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="AWSSDK.SimpleSystemsManagement" Version="4.0.7" />
                <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.0" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
                <PackageReference Include="Google.Cloud.Spanner.Data" Version="5.0.0" />
              </ItemGroup>
              <ItemGroup>
                <Reference Include="ShopSharedContracts"><HintPath>..\libs\ShopSharedContracts.dll</HintPath></Reference>
                <Reference Include="Vendor.TradeApi64"><HintPath>..\libs\Vendor.TradeApi64.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        var model = ArchitectureScanner.Scan(_root);
        var externals = model.Nodes.Where(n => n.Kind == ArchNodeKind.External).Select(n => (n.Key, n.SubKind, n.Name)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("external:cache:redis", "cache", "Redis"),
            ("external:config-center:aws-ssm-parameter-store", "config-center", "AWS SSM Parameter Store"),
            ("external:database:cloud-spanner", "database", "Cloud Spanner"),
            ("external:database:sqlite-内嵌", "database", "SQLite（内嵌）"),
            ("external:native-sdk:vendor-tradeapi", "native-sdk", "Vendor.TradeApi"),   // 厂商 SDK 仍算；共享契约 DLL 不算
        }, externals);
    }

    [Fact]
    public void JwtAuthority_FromVariableAndConfig_NamedKeycloak()
    {
        Write("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Shop.Api/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            var keyClockUrl = builder.Configuration.GetValue<string>("AuthServer:Authority");
            if (!string.IsNullOrEmpty(keyClockUrl))
            {
                builder.Services.AddAuthentication("JWT")
                    .AddJwtBearer("JWT", options =>
                    {
                        // options.Authority = "https://commented.example";
                        options.Authority = keyClockUrl;
                    });
            }
            var other = new SomethingElse { Authority = "https://not-auth.example" };
            """);
        var model = ArchitectureScanner.Scan(_root);
        var edge = Assert.Single(model.Edges);
        Assert.Equal(("Shop.Api", "external:identity:keycloak", "oidc", "AuthServer:Authority"), (edge.FromKey, edge.ToKey, edge.Protocol, edge.ConfigKey));
        Assert.Equal(("Shop.Api/Program.cs", 9), (edge.Evidence[0].File, edge.Evidence[0].Line));
        Assert.Equal("identity", model.Nodes.Single(n => n.Key == edge.ToKey).SubKind);
    }

    /// <summary>前端规则按路径认出的 http-service「Keycloak」（假规则代替，免得依赖前端规则的细节）。</summary>
    private sealed class FrontendKeycloakRule : CodeAtlas.Core.Architecture.Rules.IDiscoveryRule
    {
        public string Id => "fake-frontend";
        public string Description => "";
        public void Apply(CodeAtlas.Core.Architecture.Rules.RuleContext ctx)
        {
            var ev = new Evidence("web/src/auth.ts", 3, "/realms/shop/protocol/openid-connect/token");
            var key = ctx.External("http-service", "Keycloak", ev);
            ctx.Outbound.Add(new Outbound("web", Id, "http", "POST", null, "/realms/shop/protocol/openid-connect/token", ev.File, 3, key));
        }
    }

    [Fact]
    public void SameNamedExternals_GenericMergesIntoSpecific()
    {
        JwtAuthority_FromVariableAndConfig_NamedKeycloak_Files();
        Write("web/package.json", """{ "name": "web", "scripts": { "build": "next build" }, "dependencies": { "next": "15" } }""");
        var model = ArchitectureScanner.Scan(_root, [new FrontendKeycloakRule(), new CodeAtlas.Core.Architecture.Rules.IdentityProviderRule()]);
        var keycloak = Assert.Single(model.Nodes, n => n.Name == "Keycloak");
        Assert.Equal(("external:identity:keycloak", "identity"), (keycloak.Key, keycloak.SubKind));
        Assert.Equal(new[] { "Shop.Api → external:identity:keycloak oidc 0.5", "web → external:identity:keycloak http 0.5" }, model.Edges.Select(Edge).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, keycloak.Evidence.Count);
        Assert.All(model.Outbound, o => Assert.Equal("external:identity:keycloak", o.TargetKey));
    }

    private void JwtAuthority_FromVariableAndConfig_NamedKeycloak_Files()
    {
        Write("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Shop.Api/Program.cs", """
            var keycloakUrl = builder.Configuration.GetValue<string>("AuthServer:Authority");
            builder.Services.AddAuthentication().AddJwtBearer(options =>
            {
                options.Authority = keycloakUrl;
            });
            """);
    }

    [Fact]
    public void JwtAuthority_Unresolved_GoesToTheInRepoIdentityServer()
    {
        Write("Shop.Identity/Shop.Identity.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Duende.IdentityServer" Version="7.0.0" /></ItemGroup></Project>""");
        Write("Shop.Web/Shop.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Shop.Web/Auth.cs", """
            public static class Auth
            {
                public static void AddAuth(this IServiceCollection services, IConfiguration configuration)
                {
                    var identityUrl = configuration.GetRequiredValue("IdentityUrl");
                    services.AddAuthentication().AddOpenIdConnect(options =>
                    {
                        options.Authority = identityUrl;
                    });
                }
            }
            """);
        var model = ArchitectureScanner.Scan(_root);
        Assert.Contains("Shop.Web → Shop.Identity oidc 0.5", model.Edges.Select(Edge));
    }

    [Fact]
    public void AiEndpoints_FromRequestPaths_NotFromPresetHostLists()
    {
        Write("Shop.Cli/Shop.Cli.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        Write("Shop.Cli/Chat.cs", """
            public sealed class Chat
            {
                // 注释里的 "https://api.example/chat/completions" 不算
                public HttpRequestMessage Build(string endpoint) => new(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions");
            }
            public sealed class Claude
            {
                public HttpRequestMessage Build(string endpoint)
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, endpoint + "/messages");
                    r.Headers.Add("anthropic-version", "2023-06-01");
                    return r;
                }
            }
            """);
        // 飞书的 im/v1/messages 不是 Anthropic；只列主机名的预设表不是调用
        Write("Shop.Cli/Lark.cs", """public sealed class Lark { public Task Send(HttpClient c) => c.PostAsync("im/v1/messages?receive_id_type=chat_id", null); }""");
        Write("Shop.Cli/Presets.cs", """public static class Presets { public static readonly string[] Hosts = ["https://api.openai.com/v1", "https://api.deepseek.com/v1"]; }""");
        // 规则表 / 说明文字里的路径字符串不是在拼请求地址
        Write("Shop.Cli/Markers.cs", """public static class Markers { public static readonly (string, string)[] Paths = [("/chat/completions", "openai")]; public const string Help = "调 /chat/completions 接口"; }""");
        var model = ArchitectureScanner.Scan(_root);
        Assert.Equal(new[] { "Shop.Cli → external:http-service:ai-端点-openai-兼容 http 0.5", "Shop.Cli → external:http-service:anthropic http 0.5" },
            model.Edges.Select(Edge).OrderBy(x => x, StringComparer.Ordinal));
        var compatible = Assert.Single(model.Edges.Single(e => e.ToKey.EndsWith("兼容", StringComparison.Ordinal)).Evidence);   // Markers.cs 的规则表不算
        Assert.Equal(("Shop.Cli/Chat.cs", 4), (compatible.File, compatible.Line));
    }

    [Fact]
    public void GrpcClient_UnderUsingStatic_WithoutOuterClassName()
    {
        Write("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Shop.Api/ConnectorFactory.cs", """
            using Grpc.Net.Client;
            using static Shop.Connector.TradeService;
            public sealed class ConnectorFactory
            {
                public TradeServiceClient Create(string url) => new(GrpcChannel.ForAddress(url));
                public TradeServiceClient Other(GrpcChannel channel) { var client = new TradeServiceClient(channel); return client; }
                public OtherClient NotGrpc() => new();
            }
            """);
        var model = ArchitectureScanner.Scan(_root);
        var edge = Assert.Single(model.Edges);
        Assert.Equal(("Shop.Api", "external:grpc-service:tradeservice", "grpc"), (edge.FromKey, edge.ToKey, edge.Protocol));
        Assert.Equal(new int?[] { 5, 6 }, edge.Evidence.Select(e => e.Line));
    }

    [Fact]
    public void VsCodeExtension_IsAContainer_ThatTalksToTheCliItSpawns()
    {
        Write("src/Shop.Cli/Shop.Cli.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>shopctl</AssemblyName></PropertyGroup></Project>""");
        Write("extension/package.json", """{ "name": "shopctl-vscode", "displayName": "Shop Tools", "engines": { "vscode": "^1.100.0" }, "main": "./dist/extension.js", "scripts": { "build": "node esbuild.mjs" } }""");
        Write("extension/src/server.ts", """
            import { spawn } from "node:child_process";
            const exe = process.platform === "win32" ? "shopctl.exe" : "shopctl";
            export function start(root: string) {
              return spawn(exe, ["serve", root, "--port", "0"]);
            }
            """);
        Write("extension/webview/api.ts", """
            export async function health(base: string) {
              const r = await fetch(`${base}/api/health`);
              return r.json();
            }
            """);
        var model = ArchitectureScanner.Scan(_root);
        var ext = model.Nodes.Single(n => n.Key == "extension");
        Assert.Equal(("vscode-extension", "Shop Tools", "extension"), (ext.SubKind, ext.Name, Assert.Single(ext.Members)));
        var edge = Assert.Single(model.Edges);
        Assert.Equal("extension → src/Shop.Cli http 0.7", Edge(edge));
        Assert.Equal(new[] { ("extension/src/server.ts", 4), ("extension/webview/api.ts", 2) }, edge.Evidence.Select(e => (e.File, e.Line ?? 0)).OrderBy(x => x.File, StringComparer.Ordinal));
    }

    [Fact]
    public void ExternalName_ConfigClassSectionNames_UseTheServiceBehindTheUrl()
    {
        Write("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("Shop.Api/appsettings.json", """
            {
              "LarkBotConfig": { "Url": "https://open.larksuite.com/open-apis/" },
              "MailOptions": { "Url": "https://email.us-east-1.amazonaws.com/" },
              "ReportSettings": { "Url": "https://reports.example.net/api/" },
              "Billing": { "Url": "https://billing.example.net/" }
            }
            """);
        Write("Shop.Api/Clients.cs", """
            public static class Clients
            {
                public static void Add(IServiceCollection services, IConfiguration configuration)
                {
                    services.AddHttpClient("Lark", c => c.BaseAddress = new Uri(configuration["LarkBotConfig:Url"]!));
                    services.AddHttpClient("Mail", c => c.BaseAddress = new Uri(configuration["MailOptions:Url"]!));
                    services.AddHttpClient("Reports", c => c.BaseAddress = new Uri(configuration["ReportSettings:Url"]!));
                    services.AddHttpClient("Billing", c => c.BaseAddress = new Uri(configuration["Billing:Url"]!));
                    services.AddHttpClient("Push", c => c.BaseAddress = new Uri(configuration["PushConfig:Url"]!));
                }
            }
            """);
        var model = ArchitectureScanner.Scan(_root);
        var names = model.Nodes.Where(n => n.Kind == ArchNodeKind.External).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        // 认得出的服务名 / 认不出用主机名 / 普通节名照旧 / 没有地址时去掉 Config 后缀
        Assert.Equal(new[] { "AWS SES", "Billing", "Lark Open API", "Push", "reports.example.net" }, names);
    }

    [Fact]
    public void NodeService_PortsFromEntryFiles()
    {
        Write("nest/package.json", """{ "name": "nest", "main": "index.js", "scripts": { "start": "node index.js" }, "dependencies": { "@nestjs/core": "10" } }""");
        Write("nest/src/main.ts", "async function bootstrap() {\n  const app = await NestFactory.create(AppModule);\n  await app.listen(3000);\n}\n");
        Write("express/package.json", """{ "name": "express", "scripts": { "start": "nx serve" }, "dependencies": { "express": "4" } }""");
        Write("express/src/main.ts", "const app = express();\nconst PORT = process.env.PORT || 3001;\n// app.listen(9999)\napp.listen(PORT);\n");
        var inv = ProjectInventory.Load(_root);
        Assert.Equal([3000], inv.NodeServices.Single(n => n.Dir == "nest").Ports);
        Assert.Equal([3001], inv.NodeServices.Single(n => n.Dir == "express").Ports);
        var containers = ContainerDiscovery.Discover(inv);
        Assert.Contains(containers.Single(c => c.Key == "nest").Evidence, e => e.File == "nest/src/main.ts" && e.Line == 3);
    }
}
