using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// Java 出口：WebClient 的 .uri(…)（绝对地址的主机名 = 目标服务名；字段拼接；baseUrl）、RestTemplate 的 getForObject / postForObject / exchange(HttpMethod.X)、
/// @FeignClient 接口方法（name = 目标服务、path = 前缀）、gRPC 的 XxxGrpc.newBlockingStub。主机名按容器名 / 目录名后缀 / spring.application.name 对容器。
/// Spring Cloud Gateway：网关容器 application.yml 的 routes（Path=/api/customer/** + StripPrefix=2 → lb://customers-service），前端路径穿过网关对上后端入口。
/// </summary>
public partial class RouteLinkerTests
{
    private const string Gw = "spring-petclinic-api-gateway";
    private const string Customers = "spring-petclinic-customers-service";
    private const string Visits = "spring-petclinic-visits-service";

    private static readonly List<ArchNode> JavaContainers =
    [
        Container(Gw, "java", Gw),
        Container(Customers, "java", Customers),
        Container(Visits, "java", Visits),
        Container("vets", "java", "vets"),              // 容器名对不上主机名，靠 spring.application.name: vets-service
        Container("gateway-ui", "frontend", Gw + "/src/main/resources/static"),
        Container("shop", "java", "shop"),
        Container("adservice", "java", "adservice"),
    ];

    private static readonly List<EntryPoint> JavaEntries =
    [
        new(800, Customers, "java.spring-mvc", "http", "/owners", "POST", Customers + "/O.java", 59),
        new(801, Customers, "java.spring-mvc", "http", "/owners/{ownerId}", "GET", Customers + "/O.java", 68),
        new(802, Customers, "java.spring-mvc", "http", "/owners", "GET", Customers + "/O.java", 76),
        new(803, Customers, "java.spring-mvc", "http", "/owners/{ownerId}", "PUT", Customers + "/O.java", 85),
        new(804, Customers, "java.spring-mvc", "http", "/petTypes", "GET", Customers + "/P.java", 51),
        new(810, Visits, "java.spring-mvc", "http", "/owners/*/pets/{petId}/visits", "GET", Visits + "/V.java", 68),
        new(811, Visits, "java.spring-mvc", "http", "/owners/*/pets/{petId}/visits", "POST", Visits + "/V.java", 58),
        new(812, Visits, "java.spring-mvc", "http", "/pets/visits", "GET", Visits + "/V.java", 73),
        new(820, Gw, "java.spring-mvc", "http", "/api/gateway/owners/{ownerId}", "GET", Gw + "/G.java", 40),
        new(830, "vets", "java.spring-mvc", "http", "/vets", "GET", "vets/V.java", 46),
        new(840, "adservice", "java.grpc", "grpc", "AdService/GetAds", null, "adservice/A.java", 5),
    ];

    private const string CustomersClient = Gw + "/src/main/java/org/sp/api/application/CustomersServiceClient.java";
    private const string VisitsClient = Gw + "/src/main/java/org/sp/api/application/VisitsServiceClient.java";
    private const string PetTypesClient = Gw + "/src/main/java/org/sp/api/application/PetTypesClient.java";
    private const string VetsFeign = Gw + "/src/main/java/org/sp/api/application/VetsClient.java";
    private const string AdClient = "shop/src/main/java/org/acme/AdClient.java";

    private static readonly List<MapSymbol> JavaSymbols =
    [
        new(900, SymbolKind.Method, "org.sp.api.application.CustomersServiceClient.getOwner(int)", CustomersClient, 899, 6, 11),
        new(901, SymbolKind.Method, "org.sp.api.application.CustomersServiceClient.remember()", CustomersClient, 899, 12, 14),
        new(902, SymbolKind.Method, "org.sp.api.application.VisitsServiceClient.getVisitsForPets(List)", VisitsClient, 898, 5, 11),
        new(903, SymbolKind.Method, "org.sp.api.application.VisitsServiceClient.addVisit(int, int)", VisitsClient, 898, 12, 14),
        new(904, SymbolKind.Method, "org.sp.api.application.VisitsServiceClient.createOwner(OwnerRequest)", VisitsClient, 898, 15, 17),
        new(905, SymbolKind.Method, "org.sp.api.application.VisitsServiceClient.github()", VisitsClient, 898, 18, 20),
        new(906, SymbolKind.Method, "org.sp.api.application.PetTypesClient.types()", PetTypesClient, 897, 4, 6),
        new(907, SymbolKind.Interface, "org.sp.api.application.VetsClient", VetsFeign, null, 3, 8),
        new(908, SymbolKind.Method, "org.sp.api.application.VetsClient.all()", VetsFeign, 907, 5, 5),
        new(909, SymbolKind.Method, "org.sp.api.application.VetsClient.one(int)", VetsFeign, 907, 7, 7),
        new(910, SymbolKind.Method, "org.acme.AdClient.AdClient(ManagedChannel)", AdClient, 896, 4, 6),
        new(911, SymbolKind.Method, "org.acme.AdClient.getAds()", AdClient, 896, 7, 9),
    ];

    private static readonly Dictionary<string, string[]> JavaFiles = new()
    {
        [CustomersClient] =
        [
            "package org.sp.api.application;",                                         // 1
            "@Component",                                                              // 2
            "public class CustomersServiceClient {",                                   // 3
            "    private final WebClient.Builder webClientBuilder;",                   // 4
            "    private final Map<String, String> cache = new HashMap<>();",          // 5
            "    public Mono<OwnerDetails> getOwner(final int ownerId) {",             // 6
            "        return webClientBuilder.build().get()",                           // 7
            "            .uri(\"http://customers-service/owners/{ownerId}\", ownerId)", // 8：主机名 = 目标服务
            "            .retrieve()",                                                 // 9
            "            .bodyToMono(OwnerDetails.class);",                            // 10
            "    }",                                                                   // 11
            "    public void remember() {",                                            // 12
            "        cache.put(\"http://customers-service/owners\", \"x\");",          // 13：Map.put 不是 HTTP 调用
            "    }",                                                                   // 14
            "}",                                                                       // 15
        ],
        [VisitsClient] =
        [
            "package org.sp.api.application;",                                         // 1
            "public class VisitsServiceClient {",                                      // 2
            "    // Could be changed for testing purpose",                             // 3
            "    private String hostname = \"http://visits-service/\";",               // 4
            "    public Mono<Visits> getVisitsForPets(final List<Integer> petIds) {",  // 5
            "        return webClientBuilder.build()",                                 // 6
            "            .get()",                                                      // 7
            "            .uri(hostname + \"pets/visits?petId={petId}\", joinIds(petIds))", // 8：字段 + 字面量；查询串去掉
            "            .retrieve()",                                                 // 9
            "            .bodyToMono(Visits.class);",                                  // 10
            "    }",                                                                   // 11
            "    public void addVisit(int ownerId, int petId) {",                      // 12
            "        restTemplate.exchange(\"http://visits-service/owners/\" + ownerId + \"/pets/\" + petId + \"/visits\", HttpMethod.POST, null, Void.class);", // 13
            "    }",                                                                   // 14
            "    public void createOwner(OwnerRequest req) {",                         // 15
            "        restTemplate.postForObject(\"http://customers-service/owners\", req, Owner.class);", // 16：POST 挑 POST 的入口
            "    }",                                                                   // 17
            "    public void github() {",                                              // 18
            "        restTemplate.getForObject(\"https://api.github.com/users/x\", String.class);", // 19：外部主机，不是容器
            "    }",                                                                   // 20
            "}",                                                                       // 21
        ],
        [PetTypesClient] =
        [
            "package org.sp.api.application;",                                                             // 1
            "public class PetTypesClient {",                                                               // 2
            "    private final WebClient client = WebClient.builder().baseUrl(\"http://customers-service\").build();", // 3：文件里唯一的 baseUrl
            "    public Flux<PetType> types() {",                                                          // 4
            "        return client.get().uri(\"/petTypes\").retrieve().bodyToFlux(PetType.class);",        // 5
            "    }",                                                                                       // 6
            "}",                                                                                           // 7
        ],
        [VetsFeign] =
        [
            "package org.sp.api.application;",                          // 1
            "@FeignClient(name = \"vets-service\", path = \"/vets\")",  // 2
            "public interface VetsClient {",                             // 3
            "    @GetMapping",                                           // 4
            "    List<Vet> all();",                                      // 5
            "    @GetMapping(\"/{id}\")",                                // 6
            "    Vet one(@PathVariable int id);",                        // 7：vets 没有 /vets/{id} 入口
            "}",                                                         // 8
        ],
        [AdClient] =
        [
            "package org.acme;",                                                   // 1
            "public class AdClient {",                                             // 2
            "    private final AdServiceGrpc.AdServiceBlockingStub blockingStub;", // 3
            "    public AdClient(ManagedChannel channel) {",                       // 4
            "        blockingStub = AdServiceGrpc.newBlockingStub(channel);",      // 5
            "    }",                                                               // 6
            "    public void getAds() {",                                          // 7
            "        AdResponse r = blockingStub.getAds(request);",                // 8：Java 方法名小写开头，对 proto 的 GetAds
            "    }",                                                               // 9
            "}",                                                                   // 10
        ],
        [Gw + "/src/main/resources/application.yml"] =
        [
            "spring:",                                  // 1
            "  application:",                           // 2
            "    name: api-gateway",                    // 3
            "  cloud:",                                 // 4
            "    gateway:",                             // 5
            "      server:",                            // 6
            "        webflux:",                         // 7
            "          default-filters:",               // 8
            "            - name: CircuitBreaker",       // 9
            "              args:",                      // 10
            "                fallbackUri: forward:/fallback", // 11
            "          routes:",                        // 12
            "            - id: vets-service",           // 13
            "              uri: lb://vets-service",     // 14
            "              predicates:",                // 15
            "                - Path=/api/vet/**",       // 16
            "              filters:",                   // 17
            "                - StripPrefix=2",          // 18
            "            - id: visits-service",         // 19
            "              uri: lb://visits-service",   // 20
            "              predicates:",                // 21
            "                - Path=/api/visit/**",     // 22
            "              filters:",                   // 23
            "                - StripPrefix=2",          // 24
            "            - id: customers-service",      // 25
            "              uri: lb://customers-service", // 26
            "              predicates:",                // 27
            "                - Path=/api/customer/**",  // 28
            "              filters:",                   // 29
            "                - StripPrefix=2",          // 30
            "            - id: genai-service",          // 31
            "              uri: lb://genai-service",    // 32：没有这个容器
            "              predicates:",                // 33
            "                - Path=/api/genai/**",     // 34
            "              filters:",                   // 35
            "                - StripPrefix=2",          // 36
            "---",                                      // 37
            "spring:",                                  // 38
            "  config:",                                // 39
            "    activate:",                            // 40
            "      on-profile: docker",                 // 41
        ],
        ["vets/src/main/resources/application.yml"] =
        [
            "spring:",                 // 1
            "  application:",          // 2
            "    name: vets-service",  // 3
        ],
    };

    private const string UiPages = Gw + "/src/main/resources/static/scripts/pages/";

    private static readonly List<Outbound> UiOutbound =
    [
        new("gateway-ui", "frontend-http", "http", "GET", null, "/api/customer/owners", UiPages + "OwnersPage.js", 12, "external:http-service:api-api-customer"),
        new("gateway-ui", "frontend-http", "http", "POST", null, "/api/visit/owners/${ownerId}/pets/${petId}/visits", UiPages + "VisitsPage.js", 21, "external:http-service:api-api-visit"),
        new("gateway-ui", "frontend-http", "http", "GET", null, "/api/gateway/owners/${route.params.ownerId}", UiPages + "OwnerDetailsPage.js", 13, "external:http-service:api-api-gateway"),
        new("gateway-ui", "frontend-http", "http", "GET", null, "/api/vet/vets", UiPages + "VetsPage.js", 10, "external:http-service:api-api-vet"),
        new("gateway-ui", "frontend-http", "http", "POST", null, "/api/genai/chatclient", UiPages + "ChatBox.js", 47, "external:http-service:api-api-genai"),
    ];

    private static List<RouteMatch> JavaMatches(IReadOnlyList<Outbound>? outbound = null) =>
        RouteLinker.Match(outbound ?? [], JavaEntries, JavaContainers, JavaSymbols, p => JavaFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void Match_JavaWebClientAndRestTemplate_HostNameSelectsContainer_FieldConcat_BaseUrl_MethodHint()
    {
        var java = JavaMatches().Where(m => !m.Path.StartsWith("grpc:", StringComparison.Ordinal) && m.File != VetsFeign)
            .Select(m => (m.File[(m.File.LastIndexOf('/') + 1)..], m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            ("CustomersServiceClient.java", 8, "/owners/{ownerId}", 801L, Customers),               // 主机名 customers-service → spring-petclinic-customers-service
            ("PetTypesClient.java", 5, "/petTypes", 804L, Customers),                               // 相对 uri + 文件里的 baseUrl
            ("VisitsServiceClient.java", 8, "/pets/visits", 812L, Visits),                          // hostname 字段 + "pets/visits?…"
            ("VisitsServiceClient.java", 13, "/owners/${ownerId}/pets/${petId}/visits", 811L, Visits), // exchange(…, HttpMethod.POST)；模板里的 * 匹配任意一段
            ("VisitsServiceClient.java", 16, "/owners", 800L, Customers),                           // postForObject → POST
        }, java);
        Assert.All(JavaMatches().Where(m => m.File.StartsWith(Gw + "/src/main/java", StringComparison.Ordinal)), m => Assert.Equal(Gw, m.FromContainerKey));
        Assert.DoesNotContain(JavaMatches(), m => m.Line is 13 && m.File == CustomersClient); // cache.put 不是 HTTP 调用
        Assert.DoesNotContain(JavaMatches(), m => m.Line is 19);                               // api.github.com 不是容器
    }

    [Fact]
    public void Match_FeignClientInterfaceMethods_NameIsTargetService_PathIsPrefix()
    {
        var feign = JavaMatches().Where(m => m.File == VetsFeign).Select(m => (m.FromContainerKey, m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        // 行号是接口方法的声明行：时序图走到这个接口方法时就在这里续接；/vets/{id} 没有入口 → 不算
        Assert.Equal(new[] { (Gw, 5, "/vets", 830L, "vets") }, feign);
    }

    [Fact]
    public void Match_JavaGrpcBlockingStub_ToGrpcEntryInOtherContainer()
    {
        var grpc = JavaMatches().Where(m => m.Path.StartsWith("grpc:", StringComparison.Ordinal)).Select(m => (m.FromContainerKey, m.File, m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[] { ("shop", AdClient, 8, "grpc:AdService/GetAds", 840L, "adservice") }, grpc);
    }

    [Fact]
    public void Match_FrontendThroughSpringCloudGateway_StripPrefixThenResolveInRouteTarget()
    {
        var ui = JavaMatches(UiOutbound).Where(m => m.FromContainerKey == "gateway-ui")
            .Select(m => (m.File[(m.File.LastIndexOf('/') + 1)..], m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            ("OwnerDetailsPage.js", "/api/gateway/owners/${route.params.ownerId}", 820L, Gw),        // 网关自己的控制器：直接对上
            ("OwnersPage.js", "/api/customer/owners", 802L, Customers),                             // /api/customer/** 剥两段 → /owners（GET）
            ("VetsPage.js", "/api/vet/vets", 830L, "vets"),                                         // lb://vets-service → spring.application.name
            ("VisitsPage.js", "/api/visit/owners/${ownerId}/pets/${petId}/visits", 811L, Visits),   // POST
        }, ui);
        Assert.DoesNotContain(JavaMatches(UiOutbound), m => m.Path.StartsWith("/api/genai", StringComparison.Ordinal)); // 网关路由的目标不在容器里
    }

    [Fact]
    public void Match_StaticScriptsInsideBackendContainer_ApiLiteralCallsGoThroughGateway()
    {
        // 前端没有单独成容器（静态页面在网关项目的 src/main/resources/static 里），容器图规则也没登记 request(…) 这种包装：按脚本里以 /api 开头的调用实参兜底
        const string page = Gw + "/src/main/resources/static/scripts/pages/OwnersPage.js";
        var files = new Dictionary<string, string[]>(JavaFiles)
        {
            [page] =
            [
                "import { request, requireArray } from '../api/client.js';",                                   // 1
                "export function OwnersPage() {",                                                             // 2
                "  const load = async () => {",                                                               // 3
                "    owners.value = requireArray(await request('/api/customer/owners', { signal }), 'x');",    // 4
                "  };",                                                                                       // 5
                "  const save = async () => {",                                                               // 6
                "    await request(`/api/customer/owners/${ownerId}`, { ...jsonRequest('PUT', payload), signal });", // 7：方法取实参里的 'PUT'
                "    await request('/api/gateway/owners/1');",                                                // 8：网关自己的控制器，同一个容器 → 不算
                "    // request('/api/vet/vets')",                                                            // 9：注释
                "  };",                                                                                       // 10
                "}",                                                                                          // 11
            ],
        };
        List<MapSymbol> symbols = [.. JavaSymbols, new(920, SymbolKind.Module, page, page, null, 1, 11), new(921, SymbolKind.Function, page + ":OwnersPage", page, 920, 2, 11)];
        var containers = JavaContainers.Where(c => c.Key != "gateway-ui").ToList();
        var ui = RouteLinker.Match([], JavaEntries, containers, symbols, p => files.TryGetValue(p, out var l) ? l : [])
            .Where(m => m.File == page).Select(m => (m.FromContainerKey, m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            (Gw, 4, "/api/customer/owners", 802L, Customers),
            (Gw, 7, "/api/customer/owners/${ownerId}", 803L, Customers),
        }, ui);

        // 有前端容器包住这些脚本时由容器图规则的出口负责，兜底不再按网关容器重复记
        Assert.DoesNotContain(RouteLinker.Match([], JavaEntries, JavaContainers, symbols, p => files.TryGetValue(p, out var l) ? l : []), m => m.File == page);
    }

    [Theory]
    [InlineData("/owners/*/pets/{petId}/visits", "/owners/12/pets/3/visits", true)]
    [InlineData("/owners/*/pets/{petId}/visits", "/owners/12/visits", false)]
    [InlineData("/files/**", "/files/a/b", true)]
    public void PathMatches_SpringWildcards(string template, string path, bool expected) => Assert.Equal(expected, RouteLinker.PathMatches(template, path));
}
