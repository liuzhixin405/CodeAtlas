using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// Python 整仓索引后的边。夹具 Fixtures/py：
/// backend/（pyproject.toml，包 app：绝对导入 app.x、相对导入 . / ..、别名导入、跨文件继承、self / super 调用、装饰器路由）；
/// worker/（requirements.txt，平铺脚本：import helpers 找同目录模块，grpc 是第三方）；
/// lib/（src 布局：tests 里 from libpkg import util）；
/// legacy/app/crud.py 与 backend/app/api/routes/items.py 里的同名 create_user 是干扰；
/// shop/（平铺目录：实例属性 / 局部变量 / 返回注解 / 字符串注解 / import * / protobuf 生成模块 / FastAPI Annotated 别名 / Django 视图）。
/// </summary>
public class PythonResolutionTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = LanguageAdapters.Create();

    public PythonResolutionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-py-" + Guid.NewGuid().ToString("N"));
        var from = TypeScriptAdapterTests.Fixture("py");
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_repo, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // 虚拟环境与缓存目录：不索引（否则 site-packages 里的 sqlmodel.select 会把 external 抢走）
        WriteFile("backend/.venv/lib/site-packages/sqlmodel/__init__.py", "def select(x):\n    return x\n");
        WriteFile("worker/venv/lib/grpc/__init__.py", "def insecure_channel(t):\n    return t\n");
        WriteFile("worker/__pycache__/helpers.py", "def run():\n    pass\n");
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

    private static List<(string To, double Confidence)> Out(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .Select(n => (n.Edge.ToName, n.Edge.Confidence)).OrderBy(x => x.ToName, StringComparer.Ordinal).ThenBy(x => x.Confidence).ToList();

    private static List<string> In(IndexReader reader, string qualifiedName) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Incoming, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Select(n => n.Other!.QualifiedName).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task VirtualEnvAndPycache_AreNotIndexed()
    {
        var reader = await IndexAsync();
        var py = reader.GetAllFiles().Where(f => f.Language == "python").Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "backend/app/__init__.py", "backend/app/api/__init__.py", "backend/app/api/deps.py", "backend/app/api/routes/__init__.py",
            "backend/app/api/routes/items.py", "backend/app/api/routes/users.py", "backend/app/api/routes/views.py", "backend/app/core/__init__.py", "backend/app/core/base.py",
            "backend/app/crud.py", "backend/app/models.py", "legacy/app/__init__.py", "legacy/app/crud.py",
            "lib/src/libpkg/__init__.py", "lib/tests/test_util.py",
            // 新增的类型推断夹具 shop/
            "shop/demo_pb2.py", "shop/deps.py", "shop/models.py", "shop/repos.py", "shop/routes.py", "shop/services.py", "shop/stubs.py", "shop/views.py",
            "worker/helpers.py", "worker/tasks.py",
        }, py);
    }

    [Fact]
    public async Task Route_CallsResolveThroughImportBindings_NotDecoys()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("backend.app.api.routes.items.list_items", 0.95),   // from . import items
            ("backend.app.core.base.helper", 0.95),              // import app.core.base as base
            ("backend.app.crud.create_user", 0.95),              // from app import crud（items.create_user、legacy 的同名不连）
            ("external:helper", 0.5),                            // 裸名字、无绑定、本文件没有：不按全仓同名猜
        }, Out(reader, "backend.app.api.routes.users.create_user_route"));
        Assert.Equal(new[]
        {
            ("backend.app.models.User", 0.95),
            ("backend.app.models.UserCreate", 0.95),             // UserCreate as UC
        }, Out(reader, "backend.app.api.routes.users.create_user_route", EdgeKind.References));
        Assert.Equal(new[] { "backend.app.api.routes.users.create_user_route" }, In(reader, "backend.app.crud.create_user"));
        Assert.Empty(In(reader, "legacy.app.crud.create_user"));
        Assert.Empty(In(reader, "backend.app.api.routes.items.create_user"));
        // 装饰器里的调用归模块；router = APIRouter() 是模块变量，类型来自 fastapi → post 记 external:fastapi.post（原先类型未知，记 external:post）
        Assert.Equal(new[] { ("external:fastapi.APIRouter", 0.5), ("external:fastapi.post", 0.5) }, Out(reader, "backend/app/api/routes/users.py"));
    }

    [Fact]
    public async Task SelfCalls_FindOwnClassAndBaseClassInAnotherFile()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("backend.app.core.base.BaseService.audit", 0.9),    // 基类在别的文件
            ("backend.app.crud.UserService._save", 0.9),
            ("external:sqlmodel.model_validate", 0.5),           // User 及其基类里都没有：继承自 sqlmodel.SQLModel
        }, Out(reader, "backend.app.crud.UserService.create"));
        Assert.Equal(new[]
        {
            ("backend.app.crud.UserService", 0.95),              // 构造
            ("backend.app.crud.UserService.create", 0.9),        // UserService().create：接收者是本文件类的实例（原先类型未知、import 范围里唯一 0.7）
            ("external:sqlmodel.exec", 0.5),                     // session: Session（sqlmodel）→ 方法归那个包（原先 external:exec）
            ("external:sqlmodel.select", 0.5),
            ("external:sqlmodel.where", 0.5),                    // select(User).where(…)：第三方调用结果上的方法归那个包
        }, Out(reader, "backend.app.crud.create_user"));
    }

    [Fact]
    public async Task SelfCallOnClassAttributeAlias_CallsTheAliasedClass()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("backend.app.crud.UserService", 0.95) },
            Out(reader, "backend.app.api.routes.views.BaseView.service_class", EdgeKind.References));
        Assert.Equal(new[]
        {
            ("backend.app.crud.UserService", 0.9),               // self.service_class()：service_class = UserService
            ("backend.app.crud.UserService.create", 0.9),        // service = self.service_class()：别名 UserService 的实例（原先类型未知 0.7）
        }, Out(reader, "backend.app.api.routes.views.BaseView.handle"));
        Assert.Equal(new[]
        {
            ("backend.app.api.routes.views.BaseView.handle", 0.9),
            ("backend.app.crud.UserService", 0.9),               // 别名定义在基类上
        }, Out(reader, "backend.app.api.routes.views.UserView.post"));
    }

    [Fact]
    public async Task LocalClassReceiver_ResolvesMember()
    {
        var reader = await IndexAsync();
        // UserView.build()：接收者是本文件的类，按类成员找（不是"类型未知"的 0.7）
        Assert.Equal(new[] { ("backend.app.api.routes.views.UserView.build", 0.9) }, Out(reader, "backend.app.api.routes.views.make"));
    }

    [Fact]
    public async Task SuperCalls_SkipOwnClass_SameModuleFunctions()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("backend.app.models.Base.save", 0.9),               // super().save() 不连回自己
            ("backend.app.models.touch", 0.95),
        }, Out(reader, "backend.app.models.User.save"));
        Assert.Equal(new[] { ("external:sqlmodel.validate", 0.5) }, Out(reader, "backend.app.models.Base.save"));
    }

    [Fact]
    public async Task Inherits_ResolveAcrossFiles_ExternalBasesKeepPackage()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("backend.app.models.Base", 0.95) }, Out(reader, "backend.app.models.User", EdgeKind.Inherits));
        Assert.Equal(new[] { ("backend.app.models.Base", 0.95) }, Out(reader, "backend.app.models.Tagged", EdgeKind.Inherits));
        Assert.Equal(new[] { ("external:sqlmodel.SQLModel", 0.5) }, Out(reader, "backend.app.models.Base", EdgeKind.Inherits));
        Assert.Equal(new[] { ("backend.app.core.base.BaseService", 0.95) }, Out(reader, "backend.app.crud.UserService", EdgeKind.Inherits));
    }

    [Fact]
    public async Task RelativeImports_ResolveFromPackage()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("backend.app.core.base.helper", 0.95),              // from ..core import base as core_base; core_base.helper()
            ("backend.app.core.base.helper", 0.95),              // from ..core.base import helper as h; h()
            ("backend.app.version", 0.95),                       // from .. import version（__init__.py）
        }, Out(reader, "backend.app.api.deps.get_deps"));
    }

    [Fact]
    public async Task FlatScripts_FindSiblingModules_ThirdPartyIsExternalPackage()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:grpc.insecure_channel", 0.5),
            ("external:print", 0.5),
            ("worker.helpers.Runner", 0.95),
            ("worker.helpers.Runner.start", 0.9),                // Runner().start()：接收者是 Runner 的实例（原先类型未知，Job.start / Runner.start 各 0.3）
            ("worker.tasks.main.inner", 0.95),                   // 嵌套函数
        }, Out(reader, "worker.tasks.main"));
        Assert.Equal(new[] { ("worker.helpers.run", 0.95) }, Out(reader, "worker.tasks.main.inner"));
        Assert.Equal(new[] { ("worker.tasks.main", 0.95) }, Out(reader, "worker.tasks._Servicer.Send"));
        Assert.Equal(new[] { ("external:run", 0.5) }, Out(reader, "worker.helpers.Runner.start"));   // self.run：Runner 没有 run，不连模块级 run
    }

    [Fact]
    public async Task SrcLayout_IsAPackageRoot()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("lib.src.libpkg.util", 0.95) }, Out(reader, "lib.tests.test_util.test_util"));
    }

    [Fact]
    public async Task ImportEdges_GoToModules_ThirdPartyToTopLevelPackage()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("backend/app/__init__.py", 1.0),
            ("backend/app/api/routes/__init__.py", 1.0),
            ("backend/app/core/base.py", 1.0),
            ("backend/app/models.py", 1.0),
            ("external:fastapi", 0.5),
        }, Out(reader, "backend/app/api/routes/users.py", EdgeKind.Imports));
        Assert.Equal(new[] { ("external:grpc", 0.5), ("worker/helpers.py", 1.0), ("worker/helpers.py", 1.0) },
            Out(reader, "worker/tasks.py", EdgeKind.Imports));
    }

    // ---------- 类型推断（夹具 shop/：平铺脚本目录，repos / services / stubs / demo_pb2 / deps / routes / models / views） ----------

    [Fact]
    public async Task InstanceAttributes_ResolveByType()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("shop.repos.UserRepository.get", 0.9),              // self.repo = repo（__init__ 参数注解 repo: UserRepository）
            ("external:httpx.get", 0.5),                         // self.client = httpx.Client()：第三方
            ("shop.repos.UserRepository.all", 0.9),              // self.cache: UserRepository = make_repo()
            ("shop.repos.UserRepository.get", 0.9),              // self.other = UserRepository(db)
            ("shop.repos.User.save", 0.9),                       // .get() 的返回注解 -> "User"（字符串注解）
        }, OutByLine(reader, "shop.services.UserService.load"));
        Assert.Equal(new[] { ("shop.repos.UserRepository.all", 0.9) }, Out(reader, "shop.services.Settings.run")); // 类体注解 repo: UserRepository
    }

    [Fact]
    public async Task LocalVariables_ReturnAnnotations_With_For_StarImports_Pb2()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("shop.services.make_repo", 0.95),
            ("shop.repos.UserRepository.get", 0.9),              // repo = make_repo()，make_repo() -> UserRepository
            ("shop.services.make_repo", 0.95),
            ("shop.repos.UserRepository.all", 0.9),              // typed: "UserRepository"
            ("shop.repos.UserRepository.all", 0.9),              // for user in repo.all()
            ("shop.repos.User.save", 0.9),                       // all() -> list[User] 的元素
            ("shop.repos.UserRepository", 0.95),
            ("shop.repos.UserRepository.get", 0.9),              // with UserRepository(None) as r
            ("shop.stubs.helper_from_star", 0.9),                // from stubs import *（在 __all__ 里）
            ("external:not_exported", 0.5),                      // 不在 __all__ 里：星号导入不带出来
            ("external:demo_pb2.Empty", 0.5),                    // 运行时生成的 protobuf 类：模块名.类名
            ("external:demo_pb2.HelloRequest", 0.5),
            ("shop.repos.UserRepository.all", 0.9),
            ("external:append", 0.5),                            // found 是 list[User]，按核心类型是 User；User 没有 append：仍当内置方法
        }, OutByLine(reader, "shop.services.handler"));
        Assert.Equal(new[] { ("shop.repos.UserRepository", 0.95) }, Out(reader, "shop.services.handler", EdgeKind.References)); // 字符串注解
    }

    [Fact]
    public async Task FastApiAnnotatedAlias_ResolvesToSessionPackage()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("external:sqlmodel.exec", 0.5), ("external:sqlmodel.all", 0.5) }, OutByLine(reader, "shop.routes.read_items")); // session: SessionDep
        Assert.Equal(new[] { ("shop.deps.SessionDep", 0.95) }, Out(reader, "shop.routes.read_items", EdgeKind.References));
        Assert.Equal(new[] { ("external:sqlmodel.Session", 0.5) }, Out(reader, "shop.deps.SessionDep", EdgeKind.References));
        // 模块级 router = APIRouter()：装饰器里的 router.get 归 fastapi
        Assert.Equal(new[] { ("external:fastapi.APIRouter", 0.5), ("external:fastapi.get", 0.5) }, Out(reader, "shop/routes.py"));
    }

    [Fact]
    public async Task DjangoViewSet_SerializerLocal_AndModelReferences()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("shop.views.ArticleSerializer", 0.9),               // self.serializer_class(…)：类属性别名
            ("external:rest_framework.is_valid", 0.5),           // serializer 是 ArticleSerializer：自己没有，基类在 rest_framework
            ("external:rest_framework.save", 0.5),
            ("external:filter", 0.5),                            // Article.objects.filter：ORM 管理器方法保持 external
        }, OutByLine(reader, "shop.views.ArticleViewSet.create"));
        Assert.Equal(new[] { ("shop.models.Article", 0.95) }, Out(reader, "shop.views.ArticleViewSet.create", EdgeKind.References));
        Assert.Equal(new[] { ("shop.models.Article", 0.95) }, Out(reader, "shop.views.ArticleViewSet", EdgeKind.References));
        Assert.Equal(new[] { ("shop.models.Article", 0.95) }, Out(reader, "shop.views.ArticleSerializer.create", EdgeKind.References));
        // follower = self.request.user.profile：request 是框架基类塞进来的属性，类型不猜（不归 rest_framework），按未知接收者找同名方法
        Assert.Equal(new[] { ("shop.models.Profile.follow", 0.7) }, Out(reader, "shop.views.ProfileView.post"));
    }

    [Fact]
    public async Task Incremental_TypesFromUnchangedFiles_ComeFromStoredSymbolsAndEdges()
    {
        await IndexAsync();
        // 只改调用方；deps.py（SessionDep 的 references 边）与 repos.py（返回注解）不变，要从库里读
        File.AppendAllText(Path.Combine(_repo, "shop", "routes.py"), "\n# touched\n");
        File.AppendAllText(Path.Combine(_repo, "shop", "services.py"), "\n# touched\n");
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("external:sqlmodel.exec", 0.5), ("external:sqlmodel.all", 0.5) }, OutByLine(reader, "shop.routes.read_items"));
        Assert.Equal(new[]
        {
            ("shop.repos.UserRepository.get", 0.9), ("external:httpx.get", 0.5), ("shop.repos.UserRepository.all", 0.9),
            ("shop.repos.UserRepository.get", 0.9), ("shop.repos.User.save", 0.9),
        }, OutByLine(reader, "shop.services.UserService.load"));
    }

    private static List<(string To, double Confidence)> OutByLine(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .OrderBy(n => n.Edge.Line).ThenBy(n => n.Edge.Id).Select(n => (n.Edge.ToName, n.Edge.Confidence)).ToList();

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
