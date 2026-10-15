using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Java 的 DSM / 地图分组键：按包（类型限定名去掉类型名；嵌套类归外层类型的包），走与 C# 命名空间同一套 DominantPrefix 逻辑，不是 Go / Python 的目录分支。
/// 没有类型的文件（package-info.java）从 src/main/java/ 之后的路径推包。</summary>
public partial class DsmTests
{
    private const string PetJava = "petclinic/src/main/java/org/sp/petclinic/";

    private static readonly List<MapSymbol> JavaSymbols =
    [
        Sym(4000, SymbolKind.Module, PetJava + "owner/OwnerController.java", PetJava + "owner/OwnerController.java"),
        Sym(4001, SymbolKind.Class, "org.sp.petclinic.owner.OwnerController", PetJava + "owner/OwnerController.java", 4000),
        Sym(4002, SymbolKind.Method, "org.sp.petclinic.owner.OwnerController.showOwner(int)", PetJava + "owner/OwnerController.java", 4001),
        Sym(4003, SymbolKind.Interface, "org.sp.petclinic.owner.OwnerRepository", PetJava + "owner/OwnerRepository.java"),
        Sym(4004, SymbolKind.Class, "org.sp.petclinic.owner.Owner", PetJava + "owner/Owner.java"),
        Sym(4005, SymbolKind.Class, "org.sp.petclinic.owner.Owner.Builder", PetJava + "owner/Owner.java", 4004),   // 嵌套类：包取外层类型的
        Sym(4006, SymbolKind.Field, "org.sp.petclinic.owner.Owner.Builder.name", PetJava + "owner/Owner.java", 4005),
        Sym(4010, SymbolKind.Class, "org.sp.petclinic.vet.VetController", PetJava + "vet/VetController.java"),
        Sym(4011, SymbolKind.Method, "org.sp.petclinic.vet.VetController.showVetList()", PetJava + "vet/VetController.java", 4010),
        Sym(4020, SymbolKind.Class, "org.sp.petclinic.model.BaseEntity", PetJava + "model/BaseEntity.java"),
        Sym(4021, SymbolKind.Module, PetJava + "model/package-info.java", PetJava + "model/package-info.java"),
        Sym(4030, SymbolKind.Class, "org.sp.petclinic.PetClinicApplication", PetJava + "PetClinicApplication.java"),
        Sym(4031, SymbolKind.Method, "org.sp.petclinic.PetClinicApplication.main(String[])", PetJava + "PetClinicApplication.java", 4030),
        Sym(4099, SymbolKind.External, "external:JpaRepository.findById", "<external>"),
        // Maven 布局的测试代码在同一个模块目录里（src/test/java）：不进 DSM
        Sym(4040, SymbolKind.Class, "org.sp.petclinic.owner.OwnerControllerTests", "petclinic/src/test/java/org/sp/petclinic/owner/OwnerControllerTests.java"),
        Sym(4041, SymbolKind.Method, "org.sp.petclinic.owner.OwnerControllerTests.testShow()", "petclinic/src/test/java/org/sp/petclinic/owner/OwnerControllerTests.java", 4040),
        Sym(4042, SymbolKind.Class, "org.sp.petclinic.service.ClinicServiceTests", "petclinic/src/test/java/org/sp/petclinic/service/ClinicServiceTests.java"),
    ];

    private static readonly List<MapEdge> JavaEdges =
    [
        new(4002, 4003, EdgeKind.Calls),
        new(4002, 4004, EdgeKind.References),
        new(4004, 4020, EdgeKind.Inherits),
        new(4011, 4020, EdgeKind.References),
        new(4031, 4001, EdgeKind.References),
        new(4003, 4099, EdgeKind.Calls),
        new(4002, 4011, EdgeKind.Calls, 0.9, "impl"),   // 派生边不算依赖
        new(4041, 4002, EdgeKind.Calls),
        new(4042, 4003, EdgeKind.References),
    ];

    [Fact]
    public void Java_GroupKeys_ArePackages_NestedTypesUseOuterPackage_TypelessFileFromPath()
    {
        var keys = MapBuilder.GroupKeys(JavaSymbols);
        Assert.Equal("org.sp.petclinic.owner", keys[4002]);
        Assert.Equal("org.sp.petclinic.owner", keys[4006]);
        Assert.Equal("org.sp.petclinic.owner", keys[4000]);   // 模块：文件里类型的包
        Assert.Equal("org.sp.petclinic.model", keys[4021]);   // package-info.java：src/main/java/ 之后的目录
        Assert.Equal("org.sp.petclinic", keys[4031]);
        Assert.False(keys.ContainsKey(4099));
    }

    [Fact]
    public void Java_ContainerLevel_OneComponentPerMember_DrillIntoBasePackage()
    {
        var c = new ArchNode("petclinic", ArchNodeKind.Container, "java", "petclinic", "petclinic", ArchSource.Discovered, [], ["petclinic"], []);
        var top = DsmBuilder.Build(JavaSymbols, JavaEdges, c, "", null);
        var only = Assert.Single(top.Components); // 命名空间分支：一个成员一个组件（不是 Go / Python 那样把顶层子目录摊开）
        Assert.Equal(("petclinic", "petclinic", 11, true), (only.Key, only.Label, only.Symbols, only.HasChildren));

        var d = DsmBuilder.Build(JavaSymbols, JavaEdges, c, "petclinic", null);
        // 主包 org.sp.petclinic（DominantPrefix）；model 被 owner / vet 依赖排最前；owner（6 个符号）先于同时就绪的本级与 vet；本级与 vet 同为 2 个符号，按键排
        Assert.Equal(new[] { "org.sp.petclinic.model", "org.sp.petclinic.owner", "org.sp.petclinic", "org.sp.petclinic.vet" }, d.Components.Select(x => x.Key));
        Assert.Equal(new[] { "model", "owner", DsmBuilder.SelfLabel, "vet" }, d.Components.Select(x => x.Label));
        Assert.Equal(new[] { 1, 6, 2, 2 }, d.Components.Select(x => x.Symbols));
        Assert.Equal(new[] { (1, 0, 1, "inherits"), (2, 1, 1, "references"), (3, 0, 1, "references") }, d.Cells.Select(x => (x.Row, x.Col, x.Count, x.Kinds.Keys.Single())));
        Assert.Empty(d.Cycles);
    }
}
