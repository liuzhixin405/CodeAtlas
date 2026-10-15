using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// Java 整仓索引后的边。夹具 Fixtures/java：Maven 多模块（根 pom + common / app 两个模块）。
/// common：BaseEntity（受保护字段 audit 被子类继承）、AuditLog、util.Strings（静态方法、重载）。
/// app：model.Owner（跨模块继承 BaseEntity，嵌套类 Address）、repo.OwnerRepository（Spring Data，extends JpaRepository，没有实现类）、
/// service（OwnerService 单实现 OwnerServiceImpl —— Lombok 构造注入的 final 字段；Notifier 两个实现 EmailNotifier / SmsNotifier；
/// EmailNotifier 用通配 import 与外部 RestTemplate）、web.OwnerController（字段注入、局部 var、多行注解）、web.CustomersClient（WebClient.Builder 调用链、记录组件 / Lombok 访问器）；
/// src/test/java 里的 OwnerControllerTest（同包不 import、static import）。
/// legacy/src/com/legacy：不是 Maven 布局（源码根按 package 推断），同名的 Owner / OwnerService 是干扰。
/// </summary>
public class JavaResolutionTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = LanguageAdapters.Create();

    public JavaResolutionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-java-" + Guid.NewGuid().ToString("N"));
        var from = TypeScriptAdapterTests.Fixture("java");
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_repo, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // 构建产物与工具目录：不索引（生成的同名类会让 OwnerController 变成两个候选）
        const string decoy = "package com.shop.app.web;\n\npublic class OwnerController {\n    public void create(Object o) {\n    }\n}\n";
        WriteFile("app/target/generated-sources/com/shop/app/web/OwnerController.java", decoy);
        WriteFile("common/build/generated/com/shop/app/web/OwnerController.java", decoy);
        WriteFile(".gradle/caches/com/shop/app/web/OwnerController.java", decoy);
        WriteFile(".mvn/wrapper/MavenWrapperDownloader.java", "public class MavenWrapperDownloader {\n}\n");
    }

    private void WriteFile(string rel, string content)
    {
        var path = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<IndexReader> IndexAsync()
    {
        var report = await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        Assert.Empty(report.Errors);
        return new IndexReader(_db);
    }

    private static List<(string To, double Confidence, string? Via)> Out(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .Select(n => (n.Edge.ToName, n.Edge.Confidence, n.Edge.Via))
            .OrderBy(x => x.ToName, StringComparer.Ordinal).ThenByDescending(x => x.Confidence).ToList();

    private static List<string> In(IndexReader reader, string qualifiedName) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Incoming, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Select(n => n.Other!.QualifiedName).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private const string Svc = "com.shop.app.service.";

    [Fact]
    public async Task BuildOutputAndToolDirectories_AreNotIndexed()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            "app/src/main/java/com/shop/app/model/Owner.java", "app/src/main/java/com/shop/app/repo/OwnerRepository.java",
            "app/src/main/java/com/shop/app/service/EmailNotifier.java", "app/src/main/java/com/shop/app/service/Notifier.java",
            "app/src/main/java/com/shop/app/service/OwnerService.java", "app/src/main/java/com/shop/app/service/OwnerServiceImpl.java",
            "app/src/main/java/com/shop/app/service/SmsNotifier.java", "app/src/main/java/com/shop/app/web/CustomersClient.java",
            "app/src/main/java/com/shop/app/web/OwnerController.java", "app/src/main/java/com/shop/app/web/OwnerRequest.java",
            "app/src/test/java/com/shop/app/web/OwnerControllerTest.java",
            "common/src/main/java/com/shop/common/AuditLog.java", "common/src/main/java/com/shop/common/BaseEntity.java",
            "common/src/main/java/com/shop/common/util/Strings.java",
            "legacy/src/com/legacy/Owner.java", "legacy/src/com/legacy/OwnerService.java",
        }, reader.GetAllFiles().Where(f => f.Language == "java").Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Controller_FieldInjection_LocalVar_NestedClass_AndInterfaceCallsDeriveToSingleImplementation()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("com.shop.app.model.Owner.Address", 0.95, (string?)null),           // new Owner.Address()：连到嵌套类
            ("com.shop.app.model.Owner.Address.format()", 0.95, null),           // var address = new Owner.Address()
            ("com.shop.app.model.Owner.getLastName()", 0.7, null),               // service.find(name).getLastName()：接收者类型未知，import 范围里唯一（legacy 的同名不在范围）
            (Svc + "OwnerService.find(String)", 0.95, null),                    // 两行各一条
            (Svc + "OwnerService.find(String)", 0.95, null),
            (Svc + "OwnerServiceImpl.find(String)", 0.9, "impl"),               // 接口只有一个实现：派生边 0.9
            (Svc + "OwnerServiceImpl.find(String)", 0.9, "impl"),
        }, Out(reader, "com.shop.app.web.OwnerController.show(String)"));
        Assert.Equal(new[]
        {
            ("com.shop.app.model.Owner.isNew()", 0.95, (string?)null),           // 参数类型 Owner（不是 legacy 的 Owner）
            (Svc + "OwnerService.save(Owner)", 0.95, null),
            (Svc + "OwnerServiceImpl.save(Owner)", 0.9, "impl"),
            ("com.shop.app.web.OwnerController.audit(Owner)", 0.95, null),      // this.audit(owner)
        }, Out(reader, "com.shop.app.web.OwnerController.create(Owner)"));
        Assert.Equal(new[] { ("com.shop.app.model.Owner.Address.format()", 0.95, (string?)null) }, Out(reader, "com.shop.app.web.OwnerController.audit(Owner)"));
        Assert.Empty(In(reader, "com.legacy.OwnerService.find(String)"));
        Assert.Empty(In(reader, "com.legacy.Owner.isNew()"));
        Assert.Empty(In(reader, "com.legacy.Owner.getLastName()"));
    }

    [Fact]
    public async Task Service_SpringDataRepository_InheritedMethodIsExternal_AndMultipleImplementationsAreCandidates()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("com.shop.app.repo.OwnerRepository.findByLastName(String)", 0.95, (string?)null), // Lombok 构造注入的 final 字段；接口没有实现类 → 不派生
            ("external:List.get", 0.5, null),                                                // 局部变量 List<Owner> found
            ("external:List.isEmpty", 0.5, null),
        }, Out(reader, Svc + "OwnerServiceImpl.find(String)"));
        Assert.Equal(new[]
        {
            (Svc + "EmailNotifier.send(Owner)", 0.5, (string?)"impl"),          // Notifier 两个实现：各一条 0.5
            (Svc + "Notifier.send(Owner)", 0.95, null),                         // 局部变量 Notifier notifier（同包，不用 import）
            (Svc + "OwnerServiceImpl.pick()", 0.95, null),
            (Svc + "SmsNotifier.send(Owner)", 0.5, "impl"),
            ("external:JpaRepository.save", 0.5, null),                         // 继承自 JpaRepository、接口里没声明
        }, Out(reader, Svc + "OwnerServiceImpl.save(Owner)"));
        Assert.Equal(new[] { (Svc + "EmailNotifier", 0.95, (string?)null) }, Out(reader, Svc + "OwnerServiceImpl.pick()"));
    }

    [Fact]
    public async Task StaticCalls_WildcardImport_OverloadsByArgumentCount_AndExternalReceiverTypes()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("com.shop.app.model.Owner.getLastName()", 0.95, (string?)null),
            ("com.shop.common.util.Strings.join(String,String)", 0.5, null),    // 两个 2 参数重载：各 0.5
            ("com.shop.common.util.Strings.join(String,int)", 0.5, null),
            ("com.shop.common.util.Strings.pad(String)", 0.95, null),           // import com.shop.common.util.*；按参数个数挑 pad(String)
            ("external:RestTemplate.postForObject", 0.5, null),                // 字段 RestTemplate rest：外部类型 → external:<类名>.<方法>
        }, Out(reader, Svc + "EmailNotifier.send(Owner)"));
        Assert.Equal(new[] { ("external:RestTemplate", 0.5, (string?)null) }, Out(reader, Svc + "EmailNotifier.rest"));
        Assert.Equal(new[]
        {
            ("external:List.stream", 0.5, (string?)null),
            ("external:WebClient.Builder.build", 0.5, null),                  // 外部嵌套类型保留外层类名
            ("external:WebClient.get", 0.5, null),                            // 链根是 import 进来的第三方类型：链上的调用归到它的顶层类
            ("external:WebClient.retrieve", 0.5, null),
            ("external:WebClient.uri", 0.5, null),
            ("external:count", 0.5, null),                                    // 链根是 JDK 类型（java.util.List）：不猜，按常见方法名记
        }, Out(reader, "com.shop.app.web.CustomersClient.load(List)"));
        // 记录组件访问器 request.lastName()、Lombok 生成的 owner.getAddress()（Owner 有字段 address、没有声明 getter）：不记边；真找不到的才 external
        Assert.Equal(new[] { ("external:missing", 0.5, (string?)null) }, Out(reader, "com.shop.app.web.CustomersClient.name(OwnerRequest,Owner)"));
        Assert.Equal(new[] { ("com.shop.app.web.OwnerRequest.age", "private int age"), ("com.shop.app.web.OwnerRequest.lastName", "private String lastName") },
            reader.GetChildren(reader.FindByQualifiedName("com.shop.app.web.OwnerRequest").Single().Id).Select(c => (c.QualifiedName, c.Signature ?? "")).OrderBy(x => x.QualifiedName, StringComparer.Ordinal));
        Assert.Equal(new[] { ("com.shop.common.util.Strings.pad(String,int)", 0.95, (string?)null) }, Out(reader, "com.shop.common.util.Strings.pad(String)"));
    }

    [Fact]
    public async Task InheritedMembers_BareCall_SuperCall_AndInheritedField_AcrossModules()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("com.shop.common.AuditLog.record(String)", 0.95, (string?)null),   // audit 是 BaseEntity 的 protected 字段
            ("com.shop.common.BaseEntity.touch()", 0.95, null),                 // 裸调用 touch()：沿父类找
        }, Out(reader, "com.shop.app.model.Owner.rename(String)"));
        Assert.Equal(new[] { ("com.shop.common.BaseEntity.isNew()", 0.95, (string?)null) }, Out(reader, "com.shop.app.model.Owner.isNew()")); // super.isNew()
        Assert.Equal(new[] { ("com.shop.common.AuditLog.record(String)", 0.95, (string?)null) }, Out(reader, "com.shop.common.BaseEntity.touch()"));
        Assert.Equal(new[] { ("com.shop.app.model.Owner.rename(String)", 0.95, (string?)null) }, Out(reader, Svc + "SmsNotifier.send(Owner)"));
    }

    [Fact]
    public async Task TestSources_SeeMainSources_SamePackageWithoutImport_AndStaticImports()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("com.shop.app.web.OwnerController.create(Owner)", 0.95, (string?)null), // src/test/java 与 src/main/java 同包
            ("com.shop.common.util.Strings.pad(String)", 0.95, null),                // import static …Strings.pad
            ("external:Assertions.assertThat", 0.5, null),                           // import static org.assertj…Assertions.assertThat
            ("external:isEqualTo", 0.5, null),                                       // 接收者是调用结果、仓库里没有同名方法
        }, Out(reader, "com.shop.app.web.OwnerControllerTest.showsOwner()"));
        Assert.Equal(new[] { ("com.shop.app.web.OwnerController", 0.95, (string?)null) }, Out(reader, "com.shop.app.web.OwnerControllerTest.controller"));
    }

    [Fact]
    public async Task InheritsImplementsReferences_AndImports()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("com.shop.common.BaseEntity", 0.95, (string?)null) }, Out(reader, "com.shop.app.model.Owner", EdgeKind.Inherits));
        Assert.Equal(new[] { ("external:JpaRepository", 0.5, (string?)null) }, Out(reader, "com.shop.app.repo.OwnerRepository", EdgeKind.Inherits));
        Assert.Equal(new[] { (Svc + "OwnerService", 0.95, (string?)null) }, Out(reader, Svc + "OwnerServiceImpl", EdgeKind.Implements));
        Assert.Equal(new[] { (Svc + "Notifier", 0.95, (string?)null) }, Out(reader, Svc + "SmsNotifier", EdgeKind.Implements));
        // references 只连本仓库的类型：List / String 不记
        Assert.Equal(new[] { ("com.shop.app.repo.OwnerRepository", 0.95, (string?)null) }, Out(reader, Svc + "OwnerServiceImpl.owners", EdgeKind.References));
        Assert.Equal(new[] { (Svc + "Notifier", 0.95, (string?)null) }, Out(reader, Svc + "OwnerServiceImpl.notifiers", EdgeKind.References));
        Assert.Equal(new[] { ("com.shop.app.model.Owner", 0.95, (string?)null), ("com.shop.app.model.Owner", 0.95, null) },
            Out(reader, "com.shop.app.web.OwnerController.show(String)", EdgeKind.References)); // 返回类型 + 局部变量
        Assert.Equal(new[]
        {
            ("app/src/main/java/com/shop/app/model/Owner.java", 1.0, (string?)null),
            ("app/src/main/java/com/shop/app/service/OwnerService.java", 1.0, null),
            ("external:org.springframework.beans.factory.annotation.Autowired", 0.5, null),
            ("external:org.springframework.web.bind.annotation.GetMapping", 0.5, null),
            ("external:org.springframework.web.bind.annotation.RestController", 0.5, null),
        }, Out(reader, "app/src/main/java/com/shop/app/web/OwnerController.java", EdgeKind.Imports));
        Assert.Equal(new[]
        {
            ("app/src/main/java/com/shop/app/model/Owner.java", 1.0, (string?)null),
            ("common/src/main/java/com/shop/common/util/Strings.java", 1.0, null),   // 通配 import：包里的每个文件
            ("external:org.springframework.web.client.RestTemplate", 0.5, null),
        }, Out(reader, "app/src/main/java/com/shop/app/service/EmailNotifier.java", EdgeKind.Imports));
        Assert.Equal(new[]
        {
            ("common/src/main/java/com/shop/common/util/Strings.java", 1.0, (string?)null), // static import 的成员 → 所在类型的文件
            ("external:org.assertj.core.api.Assertions.assertThat", 0.5, null),
        }, Out(reader, "app/src/test/java/com/shop/app/web/OwnerControllerTest.java", EdgeKind.Imports));
    }

    [Fact]
    public async Task Incremental_UnchangedFilesSupertypesAndImplementations_ComeFromStoredEdges()
    {
        await IndexAsync();
        // 只改调用方：OwnerServiceImpl implements OwnerService、BaseEntity 的字段 audit 都在未变的文件里，要从库里已有的边读
        foreach (var rel in new[] { "app/src/main/java/com/shop/app/web/OwnerController.java", "app/src/main/java/com/shop/app/model/Owner.java" })
            File.AppendAllText(Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar)), "// touched\n");
        var report = await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        Assert.Equal(2, report.FilesParsed);
        var reader = new IndexReader(_db);
        Assert.Equal(new[]
        {
            (Svc + "OwnerService.save(Owner)", 0.95, (string?)null),
            (Svc + "OwnerServiceImpl.save(Owner)", 0.9, "impl"),
        }, Out(reader, "com.shop.app.web.OwnerController.create(Owner)").Where(e => e.To.StartsWith(Svc, StringComparison.Ordinal)));
        Assert.Equal(new[]
        {
            ("com.shop.common.AuditLog.record(String)", 0.95, (string?)null),
            ("com.shop.common.BaseEntity.touch()", 0.95, null),
        }, Out(reader, "com.shop.app.model.Owner.rename(String)"));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
