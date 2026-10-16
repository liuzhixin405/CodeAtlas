using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Java 入口：Spring MVC（类级 @RequestMapping 前缀、数组取第一个、多行注解、method = RequestMethod.X、路径常量）、JAX-RS（类 @Path + 方法 @GET / @Path）、
/// gRPC 服务端（extends XxxGrpc.XxxImplBase 的 public 方法、签名含 StreamObserver，route 用 proto 的 PascalCase）、@Scheduled / @KafkaListener 等、
/// @SpringBootApplication 的 main 与普通 main（hosted）、测试代码排除。符号的 StartLine 是声明行（不含注解）。</summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> JavaFiles = new()
    {
        ["petclinic/src/main/java/org/sp/petclinic/owner/OwnerController.java"] =
        [
            "package org.sp.petclinic.owner;",                                                       // 1
            "",                                                                                      // 2
            "import org.springframework.stereotype.Controller;",                                     // 3
            "",                                                                                      // 4
            "/**",                                                                                   // 5
            " * @author Juergen Hoeller",                                                            // 6：javadoc 里的 @author 不是注解
            " */",                                                                                   // 7
            "@Controller",                                                                           // 8
            "@RequestMapping(",                                                                      // 9：多行注解
            "    value = {\"/owners\", \"/people\"},",                                               // 10：数组取第一个
            "    produces = \"text/html\")",                                                         // 11
            "class OwnerController {",                                                               // 12
            "",                                                                                      // 13
            "\tprivate static final String VIEWS_OWNER_CREATE_OR_UPDATE_FORM = \"owners/createOrUpdateOwnerForm\";", // 14：视图名，不是路由
            "\tstatic final String API = \"/api\";",                                                 // 15
            "",                                                                                      // 16
            "\tpublic OwnerController(OwnerRepository owners) {",                                    // 17
            "\t}",                                                                                   // 18
            "",                                                                                      // 19
            "\t@InitBinder",                                                                         // 20
            "\tpublic void setAllowedFields(WebDataBinder dataBinder) {",                            // 21
            "\t}",                                                                                   // 22
            "",                                                                                      // 23
            "\t@GetMapping(\"/new\")",                                                               // 24
            "\tpublic String initCreationForm() {",                                                  // 25
            "\t\treturn VIEWS_OWNER_CREATE_OR_UPDATE_FORM;",                                         // 26
            "\t}",                                                                                   // 27
            "",                                                                                      // 28
            "\t@PostMapping(path = \"/new\")",                                                       // 29
            "\tpublic String processCreationForm(@Valid Owner owner,",                               // 30
            "\t\t\t@PathVariable(\"x\") BindingResult result) {",                                    // 31：参数续行以 @ 开头
            "\t\treturn \"redirect:/owners/\" + owner.getId();",                                     // 32
            "\t}",                                                                                   // 33
            "",                                                                                      // 34
            "\t/**",                                                                                 // 35
            "\t * Custom handler for displaying an owner.",                                          // 36
            "\t */",                                                                                 // 37
            "\t@GetMapping(\"/{ownerId}\")",                                                         // 38
            "\tpublic ModelAndView showOwner(@PathVariable(\"ownerId\") int ownerId) {",            // 39
            "\t\treturn null;",                                                                      // 40
            "\t}",                                                                                   // 41
            "",                                                                                      // 42
            "\t@RequestMapping(",                                                                    // 43
            "\t\tmethod = RequestMethod.POST,",                                                      // 44
            "\t\tvalue = \"/{ownerId}/edit\")",                                                      // 45
            "\tpublic String processUpdate() { return \"\"; }",                                      // 46
            "",                                                                                      // 47
            "\t@GetMapping",                                                                         // 48：没有路径 = 类级路径
            "\tpublic String list() { return \"\"; }",                                               // 49
            "",                                                                                      // 50
            "\t@RequestMapping(value = \"/any\", method = {RequestMethod.GET, RequestMethod.HEAD})", // 51
            "\tpublic String any() { return \"\"; }",                                                // 52
            "",                                                                                      // 53
            "\t@GetMapping(API + \"/count\") public long count() { return 0; }",                     // 54：注解与声明同一行；路径用常量拼接
            "",                                                                                      // 55
            "\t@ModelAttribute(\"owner\")",                                                          // 56
            "\tpublic Owner findOwner() { return null; }",                                           // 57
            "}",                                                                                     // 58
        ],
        ["petclinic/src/main/java/org/sp/petclinic/vet/VetController.java"] =
        [
            "package org.sp.petclinic.vet;",                                // 1
            "",                                                             // 2
            "@RestController",                                              // 3
            "class VetController {",                                        // 4
            "\t@GetMapping({ \"/vets.html\", \"/vets\" })",                 // 5：方法级数组 → 每条路径一个入口
            "\tpublic String showVetList() { return \"\"; }",               // 6
            "",                                                             // 7
            "\t@PostMapping(\"owners/*/pets/{petId}/visits\")",             // 8：不以 / 开头、带 *
            "\t@ResponseStatus(HttpStatus.CREATED)",                        // 9
            "\tpublic Visit create(",                                       // 10
            "\t\t@Valid @RequestBody Visit visit,",                         // 11
            "\t\t@PathVariable(\"petId\") int petId) {",                    // 12
            "\t\treturn null;",                                             // 13
            "\t}",                                                          // 14
            "\tpublic void notMapped() {}",                                 // 15
            "}",                                                            // 16
        ],
        ["shop/src/main/java/org/acme/ItemResource.java"] =
        [
            "package org.acme;",                                            // 1
            "@Path(\"/items\")",                                            // 2
            "@Produces(\"application/json\")",                              // 3
            "public class ItemResource {",                                  // 4
            "    @GET",                                                     // 5
            "    public List<Item> list() { return null; }",                // 6
            "    @GET",                                                     // 7
            "    @Path(\"/{id}\")",                                         // 8
            "    public Item get(@PathParam(\"id\") long id) { return null; }", // 9
            "    @DELETE @Path(\"{id}\")",                                  // 10
            "    public void remove(long id) { }",                          // 11
            "    public void helper() { }",                                 // 12
            "}",                                                            // 13
        ],
        ["shop/src/main/java/org/acme/GitHubApi.java"] =
        [
            "package org.acme;",                                            // 1
            "public interface GitHubApi {",                                 // 2：Retrofit：类上没有 @Path → 不是 JAX-RS
            "    @GET(\"users/{user}/repos\")",                             // 3
            "    Call<List<Repo>> listRepos(@Path(\"user\") String user);", // 4
            "}",                                                            // 5
        ],
        ["shop/src/main/java/org/acme/VetsClient.java"] =
        [
            "package org.acme;",                                            // 1
            "@FeignClient(name = \"vets-service\", path = \"/vets\")",     // 2：Feign 客户端是出口，不是入口
            "public interface VetsClient {",                                // 3
            "    @GetMapping",                                              // 4
            "    List<Vet> all();",                                         // 5
            "}",                                                            // 6
        ],
        ["shop/src/main/java/org/acme/Listeners.java"] =
        [
            "package org.acme;",                                                            // 1
            "@Component",                                                                   // 2
            "public class Listeners {",                                                     // 3
            "    @Scheduled(fixedRate = 5000)",                                             // 4
            "    public void tick() {}",                                                    // 5
            "    @KafkaListener(topics = {\"orders\", \"orders-retry\"}, groupId = \"g\")", // 6
            "    public void onOrder(String msg) {}",                                       // 7
            "    @RabbitListener(queues = \"mail\")",                                       // 8
            "    public void onMail(Message m) {}",                                         // 9
            "    @JmsListener(destination = \"audit.queue\")",                              // 10
            "    public void onAudit(String s) {}",                                         // 11
            "    @EventListener",                                                           // 12：事件类型取第一个参数的类型
            "    public void onStarted(ApplicationReadyEvent event) {}",                    // 13
            "    @StreamListener(Sink.INPUT)",                                              // 14：解析不了的常量原样
            "    public void onSink(String s) {}",                                          // 15
            "    @EventListener(classes = OrderPlaced.class)",                              // 16
            "    public void onPlaced() {}",                                                // 17
            "}",                                                                            // 18
        ],
        ["shop/src/main/java/org/acme/ShopApplication.java"] =
        [
            "package org.acme;",                                            // 1
            "@SpringBootApplication",                                       // 2
            "public class ShopApplication {",                               // 3
            "    public static void main(String[] args) {",                 // 4
            "        SpringApplication.run(ShopApplication.class, args);",  // 5
            "    }",                                                        // 6
            "}",                                                            // 7
        ],
        ["shop/src/main/java/org/acme/tools/Migrate.java"] =
        [
            "package org.acme.tools;",                                      // 1
            "public class Migrate {",                                       // 2
            "    public static void main(String[] args) { }",               // 3：容器已有 Spring Boot 的 main → 不再算
            "}",                                                            // 4
        ],
        ["adservice/src/main/java/hipstershop/AdService.java"] =
        [
            "package hipstershop;",                                                                     // 1
            "public final class AdService {",                                                           // 2
            "  private static class AdServiceImpl extends hipstershop.AdServiceGrpc.AdServiceImplBase {", // 3
            "    @Override",                                                                            // 4
            "    public void getAds(AdRequest req, StreamObserver<AdResponse> responseObserver) {",     // 5
            "    }",                                                                                    // 6
            "    @Override",                                                                            // 7
            "    public void listAds(",                                                                 // 8：参数跨行
            "        AdRequest req,",                                                                   // 9
            "        StreamObserver<AdResponse> obs) {",                                                // 10
            "    }",                                                                                    // 11
            "    private void helper(StreamObserver<AdResponse> o) {}",                                 // 12
            "    public void noObserver(AdRequest r) {}",                                               // 13
            "  }",                                                                                      // 14
            "  public static void main(String[] args) throws IOException {",                            // 15
            "  }",                                                                                      // 16
            "}",                                                                                        // 17
        ],
        ["shop/src/test/java/org/acme/FakeController.java"] =
        [
            "package org.acme;",                     // 1
            "@RestController",                       // 2
            "class FakeController {",                // 3
            "    @GetMapping(\"/fake\")",            // 4
            "    public String fake() { return \"\"; }", // 5
            "}",                                     // 6
        ],
        ["shop/src/integration/java/org/acme/ShopIT.java"] =
        [
            "package org.acme;",                     // 1
            "@RestController",                       // 2
            "class ShopIT {",                        // 3
            "    @GetMapping(\"/it\")",              // 4
            "    public String it() { return \"\"; }", // 5
            "}",                                     // 6
        ],
        ["shop/src/main/java/org/acme/RoutesConfig.java"] =
        [
            "package org.acme;",                                                                       // 1
            "@Configuration",                                                                          // 2
            "public class RoutesConfig {",                                                             // 3
            "    @Bean",                                                                               // 4
            "    public RouterFunction<ServerResponse> routes(PetHandler petHandler) {",               // 5
            "        return route(GET(\"/pets\").and(accept(APPLICATION_JSON)), petHandler::list)",    // 6
            "            .andRoute(POST(\"/pets\"), petHandler::create);",                             // 7
            "    }",                                                                                   // 8
            "    @Bean",                                                                               // 9
            "    public RouterFunction<ServerResponse> more(PetHandler h) {",                          // 10
            "        return RouterFunctions.route().GET(\"/pets/{id}\", h::get).build();",             // 11：builder 写法
            "    }",                                                                                   // 12
            "}",                                                                                       // 13
        ],
        ["shop/src/main/java/org/acme/PetHandler.java"] =
        [
            "package org.acme;",                                                     // 1
            "@Component",                                                            // 2
            "public class PetHandler {",                                             // 3
            "    public Mono<ServerResponse> list(ServerRequest r) { return null; }",   // 4：ItemResource 也有 list → 按变量类型 PetHandler 挑
            "    public Mono<ServerResponse> create(ServerRequest r) { return null; }", // 5
            "    public Mono<ServerResponse> get(ServerRequest r) { return null; }",    // 6
            "}",                                                                     // 7
        ],
        ["shop/src/main/java/org/acme/Rabbit.java"] =
        [
            "package org.acme;",                     // 1
            "@RestController",                       // 2
            "class Rabbit {",                        // 3：名字以 it 结尾，不是 *IT.java
            "    @GetMapping(\"/rabbits\")",         // 4
            "    public String all() { return \"\"; }", // 5
            "}",                                     // 6
        ],
    };

    private const string OwnerJava = "petclinic/src/main/java/org/sp/petclinic/owner/OwnerController.java";
    private const string VetJava = "petclinic/src/main/java/org/sp/petclinic/vet/VetController.java";

    private static readonly List<MapSymbol> JavaSymbols =
    [
        Sym(1000, SymbolKind.Module, OwnerJava, OwnerJava, null, 1, 58),
        Sym(1001, SymbolKind.Class, "org.sp.petclinic.owner.OwnerController", OwnerJava, 1000, 12, 58, "package"),
        Sym(1002, SymbolKind.Field, "org.sp.petclinic.owner.OwnerController.VIEWS_OWNER_CREATE_OR_UPDATE_FORM", OwnerJava, 1001, 14, 14, "private"),
        Sym(1003, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.OwnerController(OwnerRepository)", OwnerJava, 1001, 17, 18, "public"),
        Sym(1004, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.setAllowedFields(WebDataBinder)", OwnerJava, 1001, 21, 22, "public"),
        Sym(1005, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.initCreationForm()", OwnerJava, 1001, 25, 27, "public"),
        Sym(1006, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.processCreationForm(Owner, BindingResult)", OwnerJava, 1001, 30, 33, "public"),
        Sym(1007, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.showOwner(int)", OwnerJava, 1001, 39, 41, "public"),
        Sym(1008, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.processUpdate()", OwnerJava, 1001, 46, 46, "public"),
        Sym(1009, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.list()", OwnerJava, 1001, 49, 49, "public"),
        Sym(1010, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.any()", OwnerJava, 1001, 52, 52, "public"),
        Sym(1011, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.count()", OwnerJava, 1001, 54, 54, "public"),
        Sym(1012, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.findOwner()", OwnerJava, 1001, 57, 57, "public"),
        Sym(1020, SymbolKind.Class, "org.sp.petclinic.vet.VetController", VetJava, null, 4, 16, "package"),
        Sym(1021, SymbolKind.Method, "org.sp.petclinic.vet.VetController.showVetList()", VetJava, 1020, 6, 6, "public"),
        Sym(1022, SymbolKind.Method, "org.sp.petclinic.vet.VetController.create(Visit, int)", VetJava, 1020, 10, 14, "public"),
        Sym(1023, SymbolKind.Method, "org.sp.petclinic.vet.VetController.notMapped()", VetJava, 1020, 15, 15, "public"),
        Sym(1030, SymbolKind.Class, "org.acme.ItemResource", "shop/src/main/java/org/acme/ItemResource.java", null, 4, 13, "public"),
        Sym(1031, SymbolKind.Method, "org.acme.ItemResource.list()", "shop/src/main/java/org/acme/ItemResource.java", 1030, 6, 6, "public"),
        Sym(1032, SymbolKind.Method, "org.acme.ItemResource.get(long)", "shop/src/main/java/org/acme/ItemResource.java", 1030, 9, 9, "public"),
        Sym(1033, SymbolKind.Method, "org.acme.ItemResource.remove(long)", "shop/src/main/java/org/acme/ItemResource.java", 1030, 11, 11, "public"),
        Sym(1034, SymbolKind.Method, "org.acme.ItemResource.helper()", "shop/src/main/java/org/acme/ItemResource.java", 1030, 12, 12, "public"),
        Sym(1040, SymbolKind.Interface, "org.acme.GitHubApi", "shop/src/main/java/org/acme/GitHubApi.java", null, 2, 5, "public"),
        Sym(1041, SymbolKind.Method, "org.acme.GitHubApi.listRepos(String)", "shop/src/main/java/org/acme/GitHubApi.java", 1040, 4, 4, "public"),
        Sym(1045, SymbolKind.Interface, "org.acme.VetsClient", "shop/src/main/java/org/acme/VetsClient.java", null, 3, 6, "public"),
        Sym(1046, SymbolKind.Method, "org.acme.VetsClient.all()", "shop/src/main/java/org/acme/VetsClient.java", 1045, 5, 5, "public"),
        Sym(1050, SymbolKind.Class, "org.acme.Listeners", "shop/src/main/java/org/acme/Listeners.java", null, 3, 18, "public"),
        Sym(1051, SymbolKind.Method, "org.acme.Listeners.tick()", "shop/src/main/java/org/acme/Listeners.java", 1050, 5, 5, "public"),
        Sym(1052, SymbolKind.Method, "org.acme.Listeners.onOrder(String)", "shop/src/main/java/org/acme/Listeners.java", 1050, 7, 7, "public"),
        Sym(1053, SymbolKind.Method, "org.acme.Listeners.onMail(Message)", "shop/src/main/java/org/acme/Listeners.java", 1050, 9, 9, "public"),
        Sym(1054, SymbolKind.Method, "org.acme.Listeners.onAudit(String)", "shop/src/main/java/org/acme/Listeners.java", 1050, 11, 11, "public"),
        Sym(1055, SymbolKind.Method, "org.acme.Listeners.onStarted(ApplicationReadyEvent)", "shop/src/main/java/org/acme/Listeners.java", 1050, 13, 13, "public"),
        Sym(1056, SymbolKind.Method, "org.acme.Listeners.onSink(String)", "shop/src/main/java/org/acme/Listeners.java", 1050, 15, 15, "public"),
        Sym(1057, SymbolKind.Method, "org.acme.Listeners.onPlaced()", "shop/src/main/java/org/acme/Listeners.java", 1050, 17, 17, "public"),
        Sym(1060, SymbolKind.Class, "org.acme.ShopApplication", "shop/src/main/java/org/acme/ShopApplication.java", null, 3, 7, "public"),
        Sym(1061, SymbolKind.Method, "org.acme.ShopApplication.main(String[])", "shop/src/main/java/org/acme/ShopApplication.java", 1060, 4, 6, "public"),
        Sym(1065, SymbolKind.Class, "org.acme.tools.Migrate", "shop/src/main/java/org/acme/tools/Migrate.java", null, 2, 4, "public"),
        Sym(1066, SymbolKind.Method, "org.acme.tools.Migrate.main(String[])", "shop/src/main/java/org/acme/tools/Migrate.java", 1065, 3, 3, "public"),
        Sym(1070, SymbolKind.Class, "hipstershop.AdService", "adservice/src/main/java/hipstershop/AdService.java", null, 2, 17, "public"),
        Sym(1071, SymbolKind.Class, "hipstershop.AdService.AdServiceImpl", "adservice/src/main/java/hipstershop/AdService.java", 1070, 3, 14, "private"),
        Sym(1072, SymbolKind.Method, "hipstershop.AdService.AdServiceImpl.getAds(AdRequest, StreamObserver)", "adservice/src/main/java/hipstershop/AdService.java", 1071, 5, 6, "public"),
        Sym(1073, SymbolKind.Method, "hipstershop.AdService.AdServiceImpl.listAds(AdRequest, StreamObserver)", "adservice/src/main/java/hipstershop/AdService.java", 1071, 8, 11, "public"),
        Sym(1074, SymbolKind.Method, "hipstershop.AdService.AdServiceImpl.helper(StreamObserver)", "adservice/src/main/java/hipstershop/AdService.java", 1071, 12, 12, "private"),
        Sym(1075, SymbolKind.Method, "hipstershop.AdService.AdServiceImpl.noObserver(AdRequest)", "adservice/src/main/java/hipstershop/AdService.java", 1071, 13, 13, "public"),
        Sym(1076, SymbolKind.Method, "hipstershop.AdService.main(String[])", "adservice/src/main/java/hipstershop/AdService.java", 1070, 15, 16, "public"),
        Sym(1080, SymbolKind.Class, "org.acme.FakeController", "shop/src/test/java/org/acme/FakeController.java", null, 3, 6),
        Sym(1081, SymbolKind.Method, "org.acme.FakeController.fake()", "shop/src/test/java/org/acme/FakeController.java", 1080, 5, 5, "public"),
        Sym(1082, SymbolKind.Class, "org.acme.ShopIT", "shop/src/integration/java/org/acme/ShopIT.java", null, 3, 6),
        Sym(1083, SymbolKind.Method, "org.acme.ShopIT.it()", "shop/src/integration/java/org/acme/ShopIT.java", 1082, 5, 5, "public"),
        Sym(1090, SymbolKind.Class, "org.acme.RoutesConfig", "shop/src/main/java/org/acme/RoutesConfig.java", null, 3, 13, "public"),
        Sym(1091, SymbolKind.Method, "org.acme.RoutesConfig.routes(PetHandler)", "shop/src/main/java/org/acme/RoutesConfig.java", 1090, 5, 8, "public"),
        Sym(1092, SymbolKind.Method, "org.acme.RoutesConfig.more(PetHandler)", "shop/src/main/java/org/acme/RoutesConfig.java", 1090, 10, 12, "public"),
        Sym(1093, SymbolKind.Class, "org.acme.PetHandler", "shop/src/main/java/org/acme/PetHandler.java", null, 3, 7, "public"),
        Sym(1094, SymbolKind.Method, "org.acme.PetHandler.list(ServerRequest)", "shop/src/main/java/org/acme/PetHandler.java", 1093, 4, 4, "public"),
        Sym(1095, SymbolKind.Method, "org.acme.PetHandler.create(ServerRequest)", "shop/src/main/java/org/acme/PetHandler.java", 1093, 5, 5, "public"),
        Sym(1096, SymbolKind.Method, "org.acme.PetHandler.get(ServerRequest)", "shop/src/main/java/org/acme/PetHandler.java", 1093, 6, 6, "public"),
        Sym(1084, SymbolKind.Class, "org.acme.Rabbit", "shop/src/main/java/org/acme/Rabbit.java", null, 3, 6),
        Sym(1085, SymbolKind.Method, "org.acme.Rabbit.all()", "shop/src/main/java/org/acme/Rabbit.java", 1084, 5, 5, "public"),
    ];

    private static readonly List<ArchNode> JavaContainers = [Container("petclinic", "java", "petclinic"), Container("shop", "java", "shop"), Container("adservice", "java", "adservice")];

    private static List<EntryPoint> JavaEntries() =>
        EntryDiscovery.Discover(JavaSymbols, [], JavaContainers, p => JavaFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void Java_SpringMvc_ClassPrefix_ArrayFirst_MultiLineAnnotations_RequestMethod_Constants()
    {
        var mvc = JavaEntries().Where(e => e.RuleId == "java.spring-mvc" && e.ContainerKey == "petclinic" && e.File == OwnerJava)
            .Select(e => (e.Route!, e.HttpMethod, e.SymbolId, e.Line)).OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.HttpMethod, StringComparer.Ordinal).ToList();
        Assert.Equal(new (string, string?, long, int)[]
        {
            ("/owners", "GET", 1009, 49),                 // @GetMapping 没有路径 = 类级路径
            ("/owners/any", "GET", 1010, 52),             // method = {GET, HEAD} 各一个
            ("/owners/any", "HEAD", 1010, 52),
            ("/owners/api/count", "GET", 1011, 54),       // API + "/count"，API 是同文件常量
            ("/owners/new", "GET", 1005, 25),
            ("/owners/new", "POST", 1006, 30),            // path = "/new"
            ("/owners/{ownerId}", "GET", 1007, 39),       // 上面的 javadoc 不打断注解
            ("/owners/{ownerId}/edit", "POST", 1008, 46), // 多行 @RequestMapping(method = RequestMethod.POST, value = …)
        }, mvc);
        // 构造函数、@InitBinder、@ModelAttribute 不是入口
        Assert.DoesNotContain(JavaEntries(), e => e.SymbolId is 1003 or 1004 or 1012);
        Assert.All(JavaEntries().Where(e => e.RuleId == "java.spring-mvc"), e => Assert.Equal("http", e.Kind));
    }

    [Fact]
    public void Java_SpringMvc_RestControllerWithoutClassMapping_MethodArray_WildcardPathKept()
    {
        var vet = JavaEntries().Where(e => e.File == VetJava).Select(e => (e.Route!, e.HttpMethod, e.SymbolId, e.Line)).OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();
        Assert.Equal(new (string, string?, long, int)[]
        {
            ("/owners/*/pets/{petId}/visits", "POST", 1022, 10), // 不以 / 开头也补上；参数续行的 @Valid 不串到别的方法
            ("/vets", "GET", 1021, 6),
            ("/vets.html", "GET", 1021, 6),
        }, vet);
    }

    [Fact]
    public void Java_JaxRs_ClassPathPlusMethodPath_RetrofitAndFeignInterfacesAreNotEntries()
    {
        var entries = JavaEntries();
        Assert.Equal(new (string, string?, long, int)[]
        {
            ("/items", "GET", 1031, 6),
            ("/items/{id}", "DELETE", 1033, 11),   // @DELETE @Path("{id}") 同一行
            ("/items/{id}", "GET", 1032, 9),
        }, entries.Where(e => e.RuleId == "java.jaxrs").Select(e => (e.Route!, e.HttpMethod, e.SymbolId, e.Line)).OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.HttpMethod, StringComparer.Ordinal));
        Assert.DoesNotContain(entries, e => e.SymbolId is 1034 or 1041 or 1046); // helper 没有 HTTP 方法注解；Retrofit / Feign 接口是出口
    }

    [Fact]
    public void Java_WebFluxFunctionalRoutes_RouteAndBuilder_HandlerMethodByVariableType()
    {
        var fn = JavaEntries().Where(e => e.RuleId == "java.webflux-fn").Select(e => (e.ContainerKey, e.Kind, e.Route!, e.HttpMethod, e.SymbolId, e.File, e.Line))
            .OrderBy(e => e.Item3, StringComparer.Ordinal).ThenBy(e => e.HttpMethod, StringComparer.Ordinal).ToList();
        const string handler = "shop/src/main/java/org/acme/PetHandler.java";
        Assert.Equal(new (string, string, string, string?, long, string, int)[]
        {
            ("shop", "http", "/pets", "GET", 1094, handler, 4),     // route(GET("/pets").and(…), petHandler::list)：入口是处理方法
            ("shop", "http", "/pets", "POST", 1095, handler, 5),    // .andRoute(POST("/pets"), …)
            ("shop", "http", "/pets/{id}", "GET", 1096, handler, 6), // RouterFunctions.route().GET("/pets/{id}", h::get)
        }, fn);
    }

    [Fact]
    public void Java_GrpcImplBase_PublicStreamObserverMethods_PascalCaseRoute()
    {
        var grpc = JavaEntries().Where(e => e.RuleId == "java.grpc").Select(e => (e.ContainerKey, e.Kind, e.Route!, e.SymbolId, e.Line)).OrderBy(e => e.Item3, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("adservice", "grpc", "AdService/GetAds", 1072L, 5),   // getAds → proto 的 GetAds，与 Go / Python / C# 客户端一致
            ("adservice", "grpc", "AdService/ListAds", 1073L, 8),  // 参数跨行也认 StreamObserver
        }, grpc);
        Assert.DoesNotContain(JavaEntries(), e => e.SymbolId is 1074 or 1075); // private / 签名里没有 StreamObserver
    }

    [Fact]
    public void Java_ScheduledAndListeners_JobAndHandlerEntries()
    {
        var entries = JavaEntries().Where(e => e.File == "shop/src/main/java/org/acme/Listeners.java")
            .Select(e => (e.RuleId, e.Kind, e.Route!, e.SymbolId)).OrderBy(e => e.SymbolId).ThenBy(e => e.Item3, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("java.scheduled", "job", "Listeners.tick", 1051L),
            ("java.kafka", "handler", "orders", 1052L),              // topics 数组：每个 topic 一个入口
            ("java.kafka", "handler", "orders-retry", 1052L),
            ("java.rabbit", "handler", "mail", 1053L),
            ("java.jms", "handler", "audit.queue", 1054L),
            ("java.event", "handler", "ApplicationReadyEvent", 1055L), // 第一个参数的类型
            ("java.stream", "handler", "Sink.INPUT", 1056L),
            ("java.event", "handler", "OrderPlaced", 1057L),           // classes = OrderPlaced.class
        }, entries);
    }

    [Fact]
    public void Java_Hosted_SpringBootMainWins_PlainMainElsewhere_TestCodeExcluded()
    {
        var entries = JavaEntries();
        Assert.Equal(new[]
        {
            ("adservice", "java.main", "adservice", 1076L, 15),
            ("shop", "java.spring-boot", "shop", 1061L, 4),          // tools.Migrate.main 不再算
        }, entries.Where(e => e.Kind == "hosted").Select(e => (e.ContainerKey, e.RuleId, e.Route!, e.SymbolId, e.Line)));
        Assert.DoesNotContain(entries, e => e.SymbolId is 1066 or 1081 or 1083); // src/test/java、*IT.java
        Assert.Contains(entries, e => e.SymbolId == 1085 && e.Route == "/rabbits"); // Rabbit.java 不是 *IT.java
    }

    [Fact]
    public void Java_GraphQl_DgsAndSpringGraphQl_CodegenConstantsCasedBySchema()
    {
        Dictionary<string, string[]> files = new()
        {
            ["conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java"] =
            [
                "package org.acme.graphql;",                                                        // 1
                "import org.acme.graphql.DgsConstants.QUERY;",                                      // 2：codegen 生成的常量类，不在仓库里
                "import org.acme.graphql.DgsConstants.ARTICLEPAYLOAD;",                             // 3
                "@DgsComponent",                                                                    // 4
                "public class ArticleDatafetcher {",                                                // 5
                "  @DgsQuery(field = QUERY.Feed)",                                                  // 6
                "  public Object getFeed(@InputArgument(\"first\") Integer first) { return null; }", // 7
                "  @DgsData(parentType = ARTICLEPAYLOAD.TYPE_NAME, field = ARTICLEPAYLOAD.Article)", // 8：类型名按模式还原大小写
                "  public Object payloadArticle() { return null; }",                                // 9
                "  @DgsMutation",                                                                   // 10：没写 field → 方法名
                "  public Object createArticle() { return null; }",                                 // 11
                "  @DgsData(parentType = DgsConstants.QUERY_TYPE, field = \"tags\")",               // 12
                "  public Object getTags() { return null; }",                                       // 13
                "  public Object helper() { return null; }",                                        // 14
                "}",                                                                                // 15
            ],
            ["conduit/src/main/resources/schema/schema.graphqls"] =
            [
                "type Query {",
                "    feed(first: Int): ArticlesConnection",
                "    tags: [String]",
                "}",
                "type Mutation {",
                "    createArticle(input: CreateArticleInput!): ArticlePayload",
                "}",
                "type ArticlePayload {",
                "    article: Article",
                "}",
            ],
            ["conduit/src/main/java/org/acme/graphql/BookController.java"] =
            [
                "package org.acme.graphql;",                                // 1
                "@Controller",                                              // 2
                "public class BookController {",                            // 3
                "  @QueryMapping",                                          // 4
                "  public Book bookById(@Argument String id) { return null; }", // 5
                "  @SchemaMapping(typeName = \"Book\", field = \"author\")", // 6
                "  public Author author(Book book) { return null; }",       // 7
                "  @MutationMapping(\"addBook\")",                          // 8
                "  public Book create() { return null; }",                  // 9
                "}",                                                        // 10
            ],
        };
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Class, "org.acme.graphql.ArticleDatafetcher", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", null, 5, 15, "public"),
            Sym(2, SymbolKind.Method, "org.acme.graphql.ArticleDatafetcher.getFeed(Integer)", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", 1, 7, 7, "public"),
            Sym(3, SymbolKind.Method, "org.acme.graphql.ArticleDatafetcher.payloadArticle()", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", 1, 9, 9, "public"),
            Sym(4, SymbolKind.Method, "org.acme.graphql.ArticleDatafetcher.createArticle()", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", 1, 11, 11, "public"),
            Sym(5, SymbolKind.Method, "org.acme.graphql.ArticleDatafetcher.getTags()", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", 1, 13, 13, "public"),
            Sym(6, SymbolKind.Method, "org.acme.graphql.ArticleDatafetcher.helper()", "conduit/src/main/java/org/acme/graphql/ArticleDatafetcher.java", 1, 14, 14, "public"),
            Sym(7, SymbolKind.Class, "org.acme.graphql.BookController", "conduit/src/main/java/org/acme/graphql/BookController.java", null, 3, 10, "public"),
            Sym(8, SymbolKind.Method, "org.acme.graphql.BookController.bookById(String)", "conduit/src/main/java/org/acme/graphql/BookController.java", 7, 5, 5, "public"),
            Sym(9, SymbolKind.Method, "org.acme.graphql.BookController.author(Book)", "conduit/src/main/java/org/acme/graphql/BookController.java", 7, 7, 7, "public"),
            Sym(10, SymbolKind.Method, "org.acme.graphql.BookController.create()", "conduit/src/main/java/org/acme/graphql/BookController.java", 7, 9, 9, "public"),
        ];
        var entries = EntryDiscovery.Discover(symbols, [], [Container("conduit", "java", "conduit")], p => files.TryGetValue(p, out var l) ? l : []);
        Assert.Equal(new[]
        {
            ("java.dgs", "graphql:ArticlePayload.article", 3L, 9),             // ARTICLEPAYLOAD → 模式里的 ArticlePayload
            ("java.spring-graphql", "graphql:Book.author", 9L, 7),
            ("java.spring-graphql", "graphql:Mutation.addBook", 10L, 9),       // @MutationMapping("addBook")
            ("java.dgs", "graphql:Mutation.createArticle", 4L, 11),
            ("java.spring-graphql", "graphql:Query.bookById", 8L, 5),          // 没写名字 → 方法名
            ("java.dgs", "graphql:Query.feed", 2L, 7),                         // QUERY.Feed → feed
            ("java.dgs", "graphql:Query.tags", 5L, 13),                        // DgsConstants.QUERY_TYPE → Query
        }, entries.OrderBy(e => e.Route, StringComparer.Ordinal).Select(e => (e.RuleId, e.Route!, e.SymbolId, e.Line)));
        Assert.All(entries, e => Assert.Equal(("http", (string?)null, "conduit"), (e.Kind, e.HttpMethod, e.ContainerKey)));
    }

    [Fact]
    public void Java_Hosted_SampleClientMainDroppedWhenTheServerMainIsThere()
    {
        Dictionary<string, string[]> files = new()
        {
            ["ads/src/main/java/demo/AdServer.java"] = ["package demo;", "public class AdServer {", "  public static void main(String[] args) { }", "}"],
            ["ads/src/main/java/demo/AdServiceClient.java"] = ["package demo;", "public class AdServiceClient {", "  public static void main(String[] args) { }", "}"],
            ["tool/src/main/java/demo/SyncClient.java"] = ["package demo;", "public class SyncClient {", "  public static void main(String[] args) { }", "}"],
        };
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Class, "demo.AdServer", "ads/src/main/java/demo/AdServer.java", null, 2, 4, "public"),
            Sym(2, SymbolKind.Method, "demo.AdServer.main(String[])", "ads/src/main/java/demo/AdServer.java", 1, 3, 3, "public"),
            Sym(3, SymbolKind.Class, "demo.AdServiceClient", "ads/src/main/java/demo/AdServiceClient.java", null, 2, 4, "public"),
            Sym(4, SymbolKind.Method, "demo.AdServiceClient.main(String[])", "ads/src/main/java/demo/AdServiceClient.java", 3, 3, 3, "public"),
            Sym(5, SymbolKind.Class, "demo.SyncClient", "tool/src/main/java/demo/SyncClient.java", null, 2, 4, "public"),
            Sym(6, SymbolKind.Method, "demo.SyncClient.main(String[])", "tool/src/main/java/demo/SyncClient.java", 5, 3, 3, "public"),
        ];
        var entries = EntryDiscovery.Discover(symbols, [], [Container("ads", "java", "ads"), Container("tool", "java", "tool")], p => files.TryGetValue(p, out var l) ? l : []);
        Assert.Equal(new[]
        {
            ("ads", "java.main", 2L),  // AdServiceClient.main 是调本服务的示例客户端
            ("tool", "java.main", 6L), // 容器里只有它：照旧是入口
        }, entries.Where(e => e.Kind == "hosted").Select(e => (e.ContainerKey, e.RuleId, e.SymbolId)));
    }

    [Theory]
    [InlineData("shop/src/test/java/org/acme/A.java", true)]
    [InlineData("shop/src/main/java/org/acme/OwnerControllerTests.java", true)]
    [InlineData("shop/src/main/java/org/acme/OwnerControllerTest.java", true)]
    [InlineData("shop/src/it/java/org/acme/PetClinicIT.java", true)]
    [InlineData("shop/src/main/java/org/acme/Rabbit.java", false)]
    [InlineData("shop/src/main/java/org/acme/Latest.java", false)]
    [InlineData("shop/src/main/java/org/acme/Contest.java", false)]
    public void IsTest_JavaConventions(string path, bool expected) => Assert.Equal(expected, PipelineBuilder.IsTest(path, ""));
}
