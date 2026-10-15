using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Java 的时序图：方法标签"类型.方法"（含 enum 的方法），Controller → Service 接口 → 唯一实现（via=impl）→ Repository，
/// 实现里的 WebClient 调用对上另一服务的 Spring 入口后续接过去。</summary>
public partial class FlowBuilderTests
{
    [Fact]
    public void Java_ControllerToServiceImplToRepository_EnumMethodLabel_WebClientHopIntoOtherService()
    {
        const string dir = "app/src/main/java/org/sp/owner/";
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Class, "org.sp.owner.OwnerController", dir + "OwnerController.java", null, 10, 60),
            Sym(2, SymbolKind.Method, "org.sp.owner.OwnerController.showOwner(int)", dir + "OwnerController.java", 1, 40, 48),
            Sym(3, SymbolKind.Interface, "org.sp.owner.OwnerService", dir + "OwnerService.java", null, 3, 6),
            Sym(4, SymbolKind.Method, "org.sp.owner.OwnerService.find(int)", dir + "OwnerService.java", 3, 5, 5),
            Sym(5, SymbolKind.Class, "org.sp.owner.OwnerServiceImpl", dir + "OwnerServiceImpl.java", null, 5, 30),
            Sym(6, SymbolKind.Method, "org.sp.owner.OwnerServiceImpl.find(int)", dir + "OwnerServiceImpl.java", 5, 12, 20),
            Sym(7, SymbolKind.Interface, "org.sp.owner.OwnerRepository", dir + "OwnerRepository.java", null, 3, 8),
            Sym(8, SymbolKind.Method, "org.sp.owner.OwnerRepository.findById(Integer)", dir + "OwnerRepository.java", 7, 6, 6),
            Sym(9, SymbolKind.Enum, "org.sp.owner.PetKind", dir + "PetKind.java", null, 1, 10),
            Sym(10, SymbolKind.Method, "org.sp.owner.PetKind.label()", dir + "PetKind.java", 9, 5, 7),
            Sym(49, SymbolKind.Class, "org.sp.customers.OwnerResource", "customers/src/main/java/org/sp/customers/OwnerResource.java", null, 1, 30),
            Sym(50, SymbolKind.Method, "org.sp.customers.OwnerResource.findOwner(int)", "customers/src/main/java/org/sp/customers/OwnerResource.java", 49, 10, 14),
            Sym(90, SymbolKind.External, "external:JpaRepository.save", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 4, EdgeKind.Calls, 1.0),          // Controller → Service 接口方法
            new(2, 6, EdgeKind.Calls, 0.9, "impl"),  // → 唯一实现
            new(6, 8, EdgeKind.Calls, 1.0),
            new(6, 10, EdgeKind.Calls, 1.0),
            new(6, 90, EdgeKind.Calls, 0.5),
        ];
        ArchNode Java(string key) => new(key, ArchNodeKind.Container, "java", key, key, ArchSource.Discovered, [], [key], []);
        List<ArchNode> containers = [Java("app"), Java("customers")];
        var entry = new EntryPoint(2, "app", "java.spring-mvc", "http", "/owners/{ownerId}", "GET", dir + "OwnerController.java", 40);
        var remote = new EntryPoint(50, "customers", "java.spring-mvc", "http", "/owners/{ownerId}", "GET", "customers/src/main/java/org/sp/customers/OwnerResource.java", 10);
        List<RouteMatch> matches = [new("app", dir + "OwnerServiceImpl.java", 15, "/owners/{ownerId}", 50, "customers")];

        var f = FlowBuilder.Build(symbols, edges, entry, containers, matches, [entry, remote]);
        Assert.Equal("OwnerController.showOwner", f.Root.Label);
        Assert.Equal(new[] { ("OwnerServiceImpl.find", "impl"), ("OwnerService.find", (string?)null) }, f.Root.Children.Select(c => (c.Label, c.Via)));
        var impl = f.Root.Children[0];
        Assert.Equal(new[] { "OwnerRepository.findById", "PetKind.label", "HTTP /owners/{ownerId}", "save" }, impl.Children.Select(c => c.Label));
        Assert.Equal("ext|JpaRepository", impl.Children[3].Actor);
        var hop = impl.Children[2];
        Assert.Equal(("hop", "→ customers /owners/{ownerId}"), (hop.Kind, hop.Note));
        Assert.Equal(("OwnerResource.findOwner", "customers|customers"), (Assert.Single(hop.Children).Label, hop.Children[0].Actor));
    }
}
