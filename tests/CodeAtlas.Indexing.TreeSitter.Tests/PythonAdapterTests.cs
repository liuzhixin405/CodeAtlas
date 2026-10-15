using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// Python 适配器（只看单个文件的解析结果）：符号的限定名 / 种类 / 行号 / 可见性，引用的 import 绑定，import 记录。
/// 夹具 Fixtures/py 的结构见 <see cref="PythonResolutionTests"/>。
/// </summary>
public class PythonAdapterTests : IDisposable
{
    private readonly PythonAdapter _adapter = new();

    private ParsedFile Parse(string rel) =>
        _adapter.Parse(rel, File.ReadAllText(TypeScriptAdapterTests.Fixture(Path.Combine("py", rel.Replace('/', Path.DirectorySeparatorChar)))));

    [Fact]
    public void Language_IsPython_ForPyFiles()
    {
        Assert.Equal("python", _adapter.LanguageId);
        Assert.Equal(new[] { ".py" }, _adapter.Extensions);
        Assert.Contains(LanguageAdapters.Create(), a => a is PythonAdapter);
    }

    [Fact]
    public void Symbols_ClassesMethodsFunctions_WithDottedModuleNames()
    {
        var f = Parse("backend/app/crud.py");
        Assert.Equal(new[]
        {
            (SymbolKind.Module, "crud.py", "backend/app/crud.py", 1, 23, (int?)null, (string?)null),
            (SymbolKind.Class, "UserService", "backend.app.crud.UserService", 7, 16, 0, "public"),
            (SymbolKind.Method, "create", "backend.app.crud.UserService.create", 8, 12, 1, "public"),
            (SymbolKind.Method, "_save", "backend.app.crud.UserService._save", 14, 16, 1, "private"),
            (SymbolKind.Function, "create_user", "backend.app.crud.create_user", 19, 22, 0, "public"),
        }, f.Symbols.Select(s => (s.Kind, s.Name, s.QualifiedName, s.StartLine, s.EndLine, s.ParentIndex, s.Visibility)));
        Assert.Equal("def create(self, session: Session, user_in: UserCreate) -> User:", f.Symbols[2].Signature);
    }

    [Fact]
    public void InitPy_UsesPackageName()
    {
        var f = Parse("backend/app/__init__.py");
        Assert.Equal(new[] { "backend/app/__init__.py", "backend.app.version" }, f.Symbols.Select(s => s.QualifiedName));
        Assert.Equal("backend.app.api.routes.items", PythonModuleName("backend/app/api/routes/items.py"));
        Assert.Equal("backend.app", PythonModuleName("backend/app/__init__.py"));
    }

    private static string PythonModuleName(string path) => CodeAtlas.Core.Resolution.PythonModuleResolver.ModuleName(path);

    [Fact]
    public void DecoratedFunction_StartsAtDefLine_NotDecorator()
    {
        var f = Parse("backend/app/api/routes/users.py");
        var route = f.Symbols.Single(s => s.Name == "create_user_route");
        Assert.Equal((SymbolKind.Function, "backend.app.api.routes.users.create_user_route", 14, 1, 19, 0),
            (route.Kind, route.QualifiedName, route.StartLine, route.StartCol, route.EndLine, route.ParentIndex));
        Assert.Equal("def create_user_route(session, user_in: UC) -> User:", route.Signature);
        Assert.Equal(2, f.Symbols.Count); // router = APIRouter() 不是符号
    }

    [Fact]
    public void NestedFunction_AndPrivateClass()
    {
        var f = Parse("worker/tasks.py");
        Assert.Equal(new[]
        {
            (SymbolKind.Function, "worker.tasks.main", 6, (int?)0, (string?)"public"),
            (SymbolKind.Function, "worker.tasks.main.inner", 7, 1, "public"),
            (SymbolKind.Class, "worker.tasks._Servicer", 16, 0, "private"),
            (SymbolKind.Method, "worker.tasks._Servicer.Send", 17, 3, "public"),
        }, f.Symbols.Skip(1).Select(s => (s.Kind, s.QualifiedName, s.StartLine, s.ParentIndex, s.Visibility)));
    }

    [Fact]
    public void Imports_AreModuleSpecifiers_RelativeOnesArePaths()
    {
        var f = Parse("backend/app/api/routes/users.py");
        Assert.Equal(new[]
        {
            ("fastapi", false, 1), ("app", false, 3), ("app.models", false, 4), (".", true, 5), ("app.core.base", false, 6),
        }, f.Imports.Select(i => (i.Target, i.IsPath, i.Line)));
        var deps = Parse("backend/app/api/deps.py");
        Assert.Equal(new[] { "..core", "..core.base", ".." }, deps.Imports.Select(i => i.Target));
    }

    [Fact]
    public void Calls_CarryImportBindings_AndReceiverKind()
    {
        var f = Parse("backend/app/api/routes/users.py");
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).Select(r => (r.Name, r.Line, r.FromIndex, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("APIRouter", 8, 0, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "fastapi", "APIRouter")),
            // 装饰器里的调用归模块；router = APIRouter() 是模块级变量，记它的类型描述（原先记 Other + 名字 "router"）
            ("post", 11, 0, new ScriptBinding(ScriptReceiver.Other, "fastapi.APIRouter", "=APIRouter()")),
            ("create_user", 15, 1, new ScriptBinding(ScriptReceiver.Import, "app", "crud")),    // from app import crud
            ("list_items", 16, 1, new ScriptBinding(ScriptReceiver.Import, ".", "items")),      // from . import items
            ("helper", 17, 1, new ScriptBinding(ScriptReceiver.Import, "app.core.base", "*")),  // import app.core.base as base
            ("helper", 18, 1, new ScriptBinding(ScriptReceiver.None)),                          // 没有绑定
        }, calls);

        var types = f.References.Where(r => r.Kind == EdgeKind.References).Select(r => (r.Name, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("UC", (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "app.models", "UserCreate")),   // 别名导入记原名
            ("User", new ScriptBinding(ScriptReceiver.None, "app.models", "User")),
        }, types);
    }

    [Fact]
    public void SelfSuperAndModuleChains()
    {
        var models = Parse("backend/app/models.py");
        var calls = models.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => (r.Name, r.Line), r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.This), calls[("validate", 10)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.This, null, "super"), calls[("save", 17)]);
        Assert.DoesNotContain(("super", 17), calls.Keys);                                     // super() 本身不是调用边
        Assert.Equal(new ScriptBinding(ScriptReceiver.None), calls[("touch", 18)]);

        var tasks = Parse("worker/tasks.py");
        var t = tasks.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => (r.Name, r.Line), r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "helpers", "*"), t[("run", 8)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "grpc", "*"), t[("insecure_channel", 10)]);
        // Runner().start()：接收者记成类型描述"调用 helpers.Runner 的结果"（原先只记链根 + "()"，不推断）
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "helpers.Runner", "=Runner()"), t[("start", 12)]);

        var chain = _adapter.Parse("a.py", "import os.path\nimport x.y as z\n\ndef f():\n    os.path.join('a')\n    z.w.g()\n");
        var c = chain.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => r.Name, r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "os.path", "*"), c["join"]);         // 模块链 os.path
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "x.y.w", "*"), c["g"]);              // z = x.y，z.w → x.y.w
    }

    [Fact]
    public void CallChains_RememberWhereTheRootCallCameFrom()
    {
        var f = _adapter.Parse("a.py", "from sqlmodel import select\nimport json\n\ndef f(session):\n    select(A).where(B).limit(1)\n    json.loads('x').get('k')\n    session.exec(q).one()\n");
        var c = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => r.Name, r => r.Script);
        // 原先只记链根的模块路径 + "()"；现在记完整的类型描述（根 + 每一步），第三方根上的步由解析器仍归那个包
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "sqlmodel.select", "=select()"), c["where"]);          // 接收者是 select(...) 的返回值
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "sqlmodel.select", "=select().where()"), c["limit"]);  // 链再长也一步步记下
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "json.loads", "=loads()"), c["get"]);                  // 根是模块链 json.loads
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other), c["one"]);                                            // 根是没注解的参数：不知道
    }

    [Fact]
    public void Inherits_AreBaseClasses_NotKeywordArguments()
    {
        var f = Parse("backend/app/models.py");
        Assert.Equal(new[]
        {
            ("SQLModel", 8, "backend.app.models.Base"),
            ("Base", 13, "backend.app.models.User"),
            ("Base", 21, "backend.app.models.Tagged"),          // metaclass=Meta 不算
            ("SQLModel", 25, "backend.app.models.UserCreate"),
        }, f.References.Where(r => r.Kind == EdgeKind.Inherits).Select(r => (r.Name, r.Line, f.Symbols[r.FromIndex].QualifiedName)));
        // class _Servicer(object)：object 不记
        Assert.DoesNotContain(Parse("worker/tasks.py").References, r => r.Kind == EdgeKind.Inherits);
        // 内置类型注解（str / int）不记引用；obj: User 记
        Assert.Equal(new[] { ("User", 29) }, f.References.Where(r => r.Kind == EdgeKind.References).Select(r => (r.Name, r.Line)));
    }

    [Fact]
    public void ClassAttributeAliases_AreFields_ReferencingTheAliasedName()
    {
        var f = Parse("backend/app/api/routes/views.py");
        var field = f.Symbols.Single(s => s.Kind == SymbolKind.Field);                     // name = "base" 不是别名，不记
        Assert.Equal(("backend.app.api.routes.views.BaseView.service_class", 5, 1, "public"),
            (field.QualifiedName, field.StartLine, field.ParentIndex, field.Visibility));
        var r = Assert.Single(f.References, r => r.FromIndex == f.Symbols.IndexOf(field));
        Assert.Equal(("UserService", EdgeKind.References, new ScriptBinding(ScriptReceiver.None, "app.crud", "UserService")), (r.Name, r.Kind, r.Script));
    }

    // ---------- 类型推断：实例属性、局部变量、返回注解、import *、字符串注解、类型别名 ----------

    [Fact]
    public void Receivers_CarryTypeDescriptors_FromAttributesLocalsAndAnnotations()
    {
        var f = Parse("shop/services.py");
        var c = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => (r.Name, r.Line), r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository"), c[("get", 17)]);   // self.repo = repo，repo: UserRepository
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "httpx.Client", "=Client()"), c[("get", 18)]);                // self.client = httpx.Client()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository"), c[("all", 19)]);   // self.cache: UserRepository = …
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository()"), c[("get", 20)]); // self.other = UserRepository(db)
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository().get()"), c[("save", 20)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository"), c[("all", 29)]);   // 类体注解 repo: UserRepository（dataclass）
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "=make_repo()"), c[("get", 38)]);                       // repo = make_repo()：返回注解交给解析器
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository"), c[("all", 40)]);   // typed: "UserRepository"
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "=make_repo().all()"), c[("save", 42)]);                // for user in repo.all()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "repos.UserRepository", "=UserRepository()"), c[("get", 44)]); // with UserRepository(None) as r
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "stubs", "**"), c[("helper_from_star", 45)]);                  // 没有绑定、本文件有 from stubs import *
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "demo_pb2", "*"), c[("Empty", 47)]);
        // 字符串注解也记类型引用
        Assert.Contains(f.References, r => r is { Kind: EdgeKind.References, Name: "UserRepository", Line: 39 });
    }

    [Fact]
    public void TypeAliases_AllList_AndMultiLineSignatures()
    {
        var deps = Parse("shop/deps.py");
        var alias = deps.Symbols.Single(s => s.Name == "SessionDep");
        Assert.Equal((SymbolKind.TypeAlias, "shop.deps.SessionDep", 11, (int?)0), (alias.Kind, alias.QualifiedName, alias.StartLine, alias.ParentIndex));
        var aliasRefs = deps.References.Where(r => r.FromIndex == deps.Symbols.IndexOf(alias)).Select(r => (r.Name, r.Kind, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("Session", EdgeKind.References, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "sqlmodel", "Session")),   // Annotated[Session, …] 的第一个实参
            ("Depends", EdgeKind.Calls, new ScriptBinding(ScriptReceiver.None, "fastapi", "Depends")),
        }, aliasRefs);
        Assert.Equal("__all__ = helper_from_star", Parse("shop/stubs.py").Symbols[0].Signature);

        var f = _adapter.Parse("m.py", "Conf = settings['x']\nX = Optional[User]\n\ndef read(\n    session,\n    skip: int = 0,\n) -> \"User\":\n    pass\n");
        Assert.Equal(new[] { "m.py", "m.X", "m.read" }, f.Symbols.Select(s => s.QualifiedName));   // 普通的下标赋值不是类型别名
        Assert.Equal("def read( session, skip: int = 0, ) -> \"User\":", f.Symbols[2].Signature);    // 跨行签名收成一行，返回注解读得到
    }

    [Fact]
    public void DjangoManagerReceiver_ReferencesTheModelClass()
    {
        var f = Parse("shop/views.py");
        var refs = f.References.Where(r => r is { Kind: EdgeKind.References, Name: "Article" })
            .Select(r => (f.Symbols[r.FromIndex].QualifiedName, r.Line, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("shop.views.ArticleSerializer.create", 8, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None, "models", "Article")),
            ("shop.views.ArticleViewSet", 12, new ScriptBinding(ScriptReceiver.None, "models", "Article")),
            ("shop.views.ArticleViewSet.create", 19, new ScriptBinding(ScriptReceiver.None, "models", "Article")),
        }, refs);
    }

    public void Dispose() => _adapter.Dispose();
}
