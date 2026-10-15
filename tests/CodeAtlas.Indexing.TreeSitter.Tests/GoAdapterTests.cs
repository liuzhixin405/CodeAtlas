using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// Go 的符号、引用绑定与整仓解析。夹具 Fixtures/go：根目录 go.mod（module example.com/shop，replace 一个本地目录），
/// 两个业务包 cart / api、根目录的 main 包（跨文件 run.go）、third/util（经 replace 引入）、legacy（同名干扰，没人 import），
/// 以及 svc/ 下另一个 go.mod（多模块仓库）；orders/（字段类型、链式调用、range、泛型调用、包级变量、函数值实参）
/// 与 genproto/（目录名 genproto、package hipstershop，被不写别名地 import）。
/// </summary>
public class GoAdapterTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new CSharpAdapter(), new GoAdapter()];

    public GoAdapterTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-go-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("go"), _repo);
    }

    private static string Src(string rel) => File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("go"), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static ParsedFile Parse(string rel)
    {
        using var adapter = new GoAdapter();
        return adapter.Parse(rel, Src(rel));
    }

    private static string? ParentQ(ParsedFile f, ParsedSymbol s) => s.ParentIndex is { } p ? f.Symbols[p].QualifiedName : null;

    // ---------- 适配器：符号 ----------

    [Fact]
    public void Symbols_MethodsHangOnReceiverType_EvenWhenTypeIsDeclaredLater()
    {
        var f = Parse("cart/store.go");
        Assert.Equal((SymbolKind.Module, "store.go", "cart/store.go", "package cart"), (f.Symbols[0].Kind, f.Symbols[0].Name, f.Symbols[0].QualifiedName, f.Symbols[0].Signature));
        Assert.Equal("go", f.Language);
        var actual = f.Symbols.Skip(1)
            .Select(s => (s.Kind, s.Name, s.QualifiedName, Parent: ParentQ(f, s), s.Visibility, s.StartLine, s.EndLine))
            .OrderBy(x => x.QualifiedName, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            (SymbolKind.Class, "Base", "cart.Base", (string?)"cart/store.go", (string?)"public", 13, 13),
            (SymbolKind.Method, "touch", "cart.Base.touch", "cart.Base", "private", 15, 15),
            (SymbolKind.Field, "updated", "cart.Base.updated", "cart.Base", "private", 13, 13),   // struct 字段是 Field（解析器按它的类型解析 x.f.M()）
            (SymbolKind.Function, "NewStore", "cart.NewStore", "cart/store.go", "public", 17, 19),
            (SymbolKind.Class, "Store", "cart.Store", "cart/store.go", "public", 9, 11),
            (SymbolKind.Method, "Add", "cart.Store.Add", "cart.Store", "public", 3, 7),   // 方法写在类型前面：符号重排，父仍是 Store
            (SymbolKind.Field, "cart", "cart.Store.cart", "cart.Store", "private", 10, 10),
        }, actual);
        // 父符号一定排在子符号前面（入库要求）
        Assert.All(f.Symbols.Select((s, i) => (s, i)), x => Assert.True(x.s.ParentIndex is null || x.s.ParentIndex < x.i));
    }

    [Fact]
    public void Symbols_StructInterfaceNamedType_AndReceiverInOtherFile()
    {
        var f = Parse("cart/cart.go");
        var actual = f.Symbols.Skip(1)
            .Select(s => (s.Kind, s.QualifiedName, Parent: ParentQ(f, s), s.Visibility, s.StartLine, s.EndLine))
            .OrderBy(x => x.QualifiedName, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            (SymbolKind.Class, "cart.Cart", (string?)"cart/cart.go", (string?)"public", 11, 14),
            (SymbolKind.Method, "cart.Cart.Add", "cart.Cart", "public", 27, 30),
            (SymbolKind.Field, "cart.Cart.items", "cart.Cart", "private", 13, 13),           // 内嵌的 Base 没有字段名：是 inherits，不建字段
            (SymbolKind.Class, "cart.Item", "cart/cart.go", "public", 6, 9),
            (SymbolKind.Field, "cart.Item.Count", "cart.Item", "public", 8, 8),
            (SymbolKind.Field, "cart.Item.Name", "cart.Item", "public", 7, 7),
            (SymbolKind.Interface, "cart.ReadWriter", "cart/cart.go", "public", 22, 25),
            (SymbolKind.Interface, "cart.Reader", "cart/cart.go", "public", 18, 20),
            (SymbolKind.Method, "cart.Reader.Get", "cart.Reader", "public", 19, 19),
            (SymbolKind.Method, "cart.Store.Reset", "cart/cart.go", "public", 32, 32), // 接收者 Store 在 store.go：父是本文件 Module
            (SymbolKind.TypeAlias, "cart.Total", "cart/cart.go", "public", 16, 16),
            (SymbolKind.Function, "cart.newItem", "cart/cart.go", "private", 34, 34),
        }, actual);
    }

    [Fact]
    public void Symbols_RootPackage_HasNoDirectoryPrefix_AndMainIsAFunction()
    {
        var f = Parse("main.go");
        var main = f.Symbols.Single(s => s.Name == "main");
        Assert.Equal((SymbolKind.Function, "main", "main.go", "private", 14, 24), (main.Kind, main.QualifiedName, ParentQ(f, main), main.Visibility, main.StartLine, main.EndLine));
        Assert.Equal("setup", f.Symbols.Single(s => s.Name == "setup").QualifiedName);
        Assert.Equal("package main", f.Symbols[0].Signature);
    }

    // ---------- 适配器：引用 ----------

    [Fact]
    public void Imports_AreRecordedWithoutQuotes_NotPaths()
    {
        var f = Parse("main.go");
        Assert.Equal(new[]
        {
            ("fmt", false, 4), ("net/http", false, 5), ("example.com/libs/util", false, 7), ("example.com/shop/api", false, 8),
            ("example.com/shop/cart", false, 9), ("github.com/lib/pq", false, 10), ("gorm.io/gorm", false, 11),
        }, f.Imports.Select(i => (i.Target, i.IsPath, i.Line)));
    }

    [Fact]
    public void Calls_CarryImportAliasAndReceiverTypeBindings()
    {
        var f = Parse("main.go");
        var main = f.Symbols.IndexOf(f.Symbols.Single(s => s.Name == "main"));
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).ToList();
        Assert.All(calls, r => Assert.Equal(main, r.FromIndex));
        var byKey = calls.ToDictionary(r => (r.Name, r.Line), r => r.Script);
        Assert.Equal(new[]
        {
            ("Add", 19), ("HandleFunc", 17), ("NewServer", 16), ("NewStore", 18), ("Open", 15),
            ("Pad", 22), ("Println", 22), ("Start", 21), ("run", 23), ("setup", 22),
        }, byKey.Keys.OrderBy(k => k.Name, StringComparer.Ordinal));
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "gorm.io/gorm", "gorm"), byKey[("Open", 15)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "example.com/shop/cart", "c"), byKey[("NewStore", 18)]);           // 显式别名 c
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "example.com/shop/cart", "NewStore()"), byKey[("Add", 19)]);         // store := c.NewStore()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "example.com/shop/api", "Server"), byKey[("Start", 21)]);            // var s2 *api.Server
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "example.com/libs/util", "util"), byKey[("Pad", 22)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.None), byKey[("setup", 22)]);
    }

    [Fact]
    public void Calls_DotAndBlankImports_TypeAssertion_BuiltinsSkipped()
    {
        using var adapter = new GoAdapter();
        var f = adapter.Parse("pkg/sub/x.go", """
            package sub

            import (
            	. "example.com/dot"
            	_ "example.com/side"
            	str "strings"
            )

            func f(v interface{}, items []string) {
            	Helper()
            	u := v.(*User)
            	u.Save()
            	str.ToUpper("a")
            	n := len(items)
            	_ = string(rune(n))
            	err := u.Check()
            	err.Error()
            }
            """);
        Assert.Equal("pkg/sub.f", f.Symbols.Single(s => s.Name == "f").QualifiedName);
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => r.Name, r => r.Script);
        Assert.Equal(new[] { "Check", "Error", "Helper", "Save", "ToUpper" }, calls.Keys.OrderBy(k => k, StringComparer.Ordinal)); // len / string / rune 不算调用
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "example.com/dot"), calls["Helper"]);                // 点导入：裸名字可能来自它
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "User"), calls["Save"]);                      // u := v.(*User)
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "strings", "str"), calls["ToUpper"]);
        // err := u.Check()：方法返回值记成描述的一步（User 的 Check 的返回值），类型由解析器按 Check 的签名推（原先记 Other、类型未知）
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "User.Check()"), calls["Error"]);
        Assert.Equal(new[] { ("example.com/dot", 4), ("example.com/side", 5), ("strings", 6) }, f.Imports.Select(i => (i.Target, i.Line)));
    }

    [Fact]
    public void TypeReferences_EmbeddingIsInherits_PredeclaredAndReceiverSkipped()
    {
        var f = Parse("cart/cart.go");
        string From(ParsedReference r) => f.Symbols[r.FromIndex].QualifiedName;
        var inherits = f.References.Where(r => r.Kind == EdgeKind.Inherits).Select(r => (From(r), r.Name, r.Line, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("cart.Cart", "Base", 12, (ScriptBinding?)new ScriptBinding(ScriptReceiver.None)),
            ("cart.ReadWriter", "Reader", 23, new ScriptBinding(ScriptReceiver.None)),
            ("cart.ReadWriter", "Writer", 24, new ScriptBinding(ScriptReceiver.Import, "io", "io")),
        }, inherits);
        // 函数值实参（via = funcvalue，如 append(c.items, …) 里的 c.items）另有测试，这里只看类型引用
        var refs = f.References.Where(r => r.Kind == EdgeKind.References && r.Via is null).Select(r => (From(r), r.Name, r.Line)).ToList();
        // string / int / error 等预声明类型不记；接收者 (c *Cart) 不记；newItem 的返回类型与复合字面量同一行只记一次
        Assert.Equal(new[] { ("cart.Cart", "Item", 13), ("cart.Reader.Get", "Item", 19), ("cart.newItem", "Item", 34) }, refs);
        Assert.DoesNotContain(f.References, r => r.Kind == EdgeKind.Implements);
    }

    // ---------- 解析器（整仓索引） ----------

    private async Task<IndexReader> IndexAsync()
    {
        await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        return new IndexReader(_db);
    }

    private static List<(string To, double Confidence)> Out(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .Select(n => (n.Edge.ToName, n.Edge.Confidence))
            .OrderBy(x => x.ToName, StringComparer.Ordinal).ThenByDescending(x => x.Confidence).ToList();

    [Fact]
    public async Task Main_Calls_ResolveByAlias_Replace_SamePackageCrossFile_AndReceiverTypes()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("api.NewServer", 0.95),            // api.NewServer(db)
            ("api.Server.Start", 0.9),          // var s2 *api.Server
            ("cart.NewStore", 0.95),            // 别名 c；legacy.NewStore 不连
            ("cart.Store.Add", 0.9),            // store := c.NewStore()：按 NewStore 签名的返回类型 *Store
            ("external:fmt.Println", 0.5),
            ("external:gorm.Open", 0.5),        // 第三方：external:<包名>.<名字>
            ("external:http.HandleFunc", 0.5),  // 标准库 net/http
            ("run", 0.95),                      // 同包跨文件 run.go（根目录包没有目录前缀）
            ("setup", 0.95),                    // legacy.setup 不连
            ("third/util.Pad", 0.95),           // replace example.com/libs/util => ./third/util；legacy.Pad 不连
        }, Out(reader, "main"));
        Assert.Equal(new[] { ("api.Server.Start", 0.9) }, Out(reader, "run")); // 参数 s *api.Server
    }

    [Fact]
    public async Task Methods_ReceiverParam_LocalVar_EmbeddedPromotion_AndUnknownReceiverCandidates()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("cart.Base.touch", 0.9),           // c *Cart 没有 touch，经内嵌字段 Base 提升
            ("cart.newItem", 0.95),             // 同包裸调用；append 是内置函数不记
        }, Out(reader, "cart.Cart.Add"));
        Assert.Equal(new[]
        {
            ("cart.Cart.Add", 0.9),             // var c Cart
            ("cart.Cart.Add", 0.9),             // s.cart.Add：字段 cart *Cart 的类型读得出来（原先类型未知、本包两个 Add 各 0.3）
        }, Out(reader, "cart.Store.Add"));
        Assert.Equal(new[]
        {
            ("api.Server.Start", 0.9),          // s.Start()：接收者
            ("cart.NewStore", 0.95),
            ("cart.Store.Add", 0.9),            // st := cart.NewStore()
            ("external:gorm.Find", 0.5),        // s.db.Find：字段 db *gorm.DB → 第三方包名（原先类型未知，记 external:Find）
            ("external:http.WriteHeader", 0.5), // w http.ResponseWriter：类型在标准库
        }, Out(reader, "api.Server.Handle"));
        Assert.Equal(new[]
        {
            ("api.check", 0.95),
            ("external:Error", 0.5),            // err := check()，check 返回预声明的 error：不连到本包 apiError.Error
        }, Out(reader, "api.Server.Close"));
        Assert.Equal("api.apiError", reader.GetSymbol(reader.FindByQualifiedName("api.apiError.Error").Single().ParentId!.Value)!.QualifiedName); // 接收者没写名字
    }

    [Fact]
    public async Task MultiModuleRepo_EachGoModApplies_AndSiblingModuleResolves()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("cart.NewStore", 0.95), ("svc/handler.Serve", 0.95) }, Out(reader, "svc.main"));
    }

    [Fact]
    public async Task Inherits_References_AndImports()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[] { ("cart.Base", 0.95) }, Out(reader, "cart.Cart", EdgeKind.Inherits));
        Assert.Equal(new[] { ("cart.Reader", 0.95), ("external:io.Writer", 0.5) }, Out(reader, "cart.ReadWriter", EdgeKind.Inherits));
        Assert.Equal(new[] { ("api.Server", 0.95), ("api.Server", 0.95), ("external:gorm.DB", 0.5) }, Out(reader, "api.NewServer", EdgeKind.References));
        Assert.Empty(Out(reader, "cart.ReadWriter", EdgeKind.Implements));
        Assert.Equal(new[]
        {
            ("api/server.go", 1.0),
            ("cart/cart.go", 1.0),              // 包 = 目录：连到目录下每个文件的 Module
            ("cart/store.go", 1.0),
            ("external:fmt", 0.5),
            ("external:github.com/lib/pq", 0.5),
            ("external:gorm.io/gorm", 0.5),
            ("external:net/http", 0.5),
            ("third/util/util.go", 1.0),
        }, Out(reader, "main.go", EdgeKind.Imports));
    }

    // ---------- 类型推断：字段、链式调用、range、泛型调用、包名≠目录名、函数值 ----------
    // 夹具 orders/：service 的字段带类型（本仓库类型、第三方 gorm、package hipstershop 的 genproto 目录）；
    // vars.go 里的包级变量 defaultDB；Register 里把函数 / 方法当值传给注册函数。

    private static List<(string To, double Confidence, int Line, string? Via)> OutAt(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .Select(n => (n.Edge.ToName, n.Edge.Confidence, n.Edge.Line, n.Edge.Via))
            .OrderBy(x => x.Line).ThenBy(x => x.ToName, StringComparer.Ordinal).ToList();

    [Fact]
    public void Symbols_StructFieldsAndPackageVars_AreFieldsAndVariables()
    {
        var f = Parse("orders/orders.go");
        var fields = f.Symbols.Where(s => s.Kind == SymbolKind.Field)
            .Select(s => (s.QualifiedName, Parent: ParentQ(f, s), s.Signature, s.Visibility, s.StartLine, s.EndLine)).ToList();
        Assert.Equal(new[]
        {
            ("orders.service.db", (string?)"orders.service", (string?)"db *gorm.DB", (string?)"private", 16, 16),
            ("orders.service.repo", "orders.service", "repo Repo", "private", 17, 17),
            ("orders.service.cartSvc", "orders.service", "cartSvc hipstershop.CartServiceClient", "private", 18, 18),
            ("orders.service.store", "orders.service", "store *cart.Store", "private", 19, 19),
            ("orders.service.carts", "orders.service", "carts map[string]*cart.Cart", "private", 20, 20),
            ("orders.service.a", "orders.service", "a *cart.Store", "private", 21, 21),          // a, b *cart.Store：每个名字一个字段
            ("orders.service.b", "orders.service", "b *cart.Store", "private", 21, 21),
        }, fields);
        Assert.All(f.Symbols.Select((s, i) => (s, i)), x => Assert.True(x.s.ParentIndex is null || x.s.ParentIndex < x.i));

        var vars = Parse("orders/vars.go");
        Assert.Equal(new[]
        {
            ("orders.defaultDB", (string?)"var defaultDB *gorm.DB", (string?)"private", 8),
            ("orders.count", "var count int", "private", 11),
            ("orders.limit", "var limit int", "private", 11),
            ("orders.registry", "var registry = make(map[string]*cart.Store)", "private", 14),    // 没写类型：签名记下值，解析器从值里读类型
        }, vars.Symbols.Where(s => s.Kind == SymbolKind.Variable).Select(s => (s.QualifiedName, s.Signature, s.Visibility, s.StartLine)));
    }

    [Fact]
    public void Calls_ReceiverDescriptors_FieldsChainsRangeGenericsAndUnknownQualifiers()
    {
        var f = Parse("orders/orders.go");
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => (r.Name, r.Line), r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.db"), calls[("Where", 25)]);                 // 接收者 s 的字段 db
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.db.Where()"), calls[("First", 25)]);         // 方法返回值再调用
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.repo"), calls[("Get", 26)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.cartSvc.GetCart()"), calls[("GetItems", 28)]); // c, _ := s.cartSvc.GetCart(id)
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.cartSvc.GetCart().GetItems()[]"), calls[("GetProductId", 29)]); // range 的元素
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "service.carts[]"), calls[("Add", 32)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, "example.com/shop/cart", "NewStore()"), calls[("Add", 34)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.Other, null, "Map()"), calls[("Add", 37)]);                        // 泛型函数调用的返回值
        Assert.Equal(new ScriptBinding(ScriptReceiver.None), calls[("Map", 37)]);                                        // Map[string](nil) 是调用，不是类型转换
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "?hipstershop", "hipstershop"), calls[("NewCartServiceClient", 38)]); // 导入名对不上任何默认包名
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "?defaultDB", "defaultDB"), calls[("Exec", 39)]);          // 别的文件里的包级变量：解析器再分辨

        var funcValues = f.References.Where(r => r.Via == "funcvalue").Select(r => (r.Name, r.Line, r.Kind, r.Script)).ToList();
        Assert.Equal(new[]
        {
            ("Handle", 49, EdgeKind.References, (ScriptBinding?)new ScriptBinding(ScriptReceiver.Other, null, "service")),
            ("Health", 50, EdgeKind.References, new ScriptBinding(ScriptReceiver.None)),
            ("StatusOK", 53, EdgeKind.References, new ScriptBinding(ScriptReceiver.Import, "net/http", "http")),       // 常量也记下，解析器只连本仓库的函数
        }, funcValues);                                                                                                   // mux、s 这些局部变量不记
    }

    [Fact]
    public async Task FieldTypes_ResolveInRepoAndThirdParty_ChainsRangeGenericsPackageName()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:gorm.First", 0.5, 25, (string?)null),               // db.Where(...).First(...)：第三方链按包名
            ("external:gorm.Where", 0.5, 25, null),                        // s.db 是 *gorm.DB
            ("orders.Repo.Get", 0.9, 26, null),                            // 字段是本包接口
            ("genproto.CartServiceClient.GetCart", 0.9, 27, null),         // 字段类型 hipstershop.X：import 目录 genproto 的 package 子句是 hipstershop
            ("genproto.Cart.GetItems", 0.9, 28, null),                     // c 是 GetCart 的第一个返回值 *Cart
            ("genproto.CartItem.GetProductId", 0.9, 29, null),             // range []*CartItem
            ("cart.Cart.Add", 0.9, 32, null),                              // range map[string]*cart.Cart
            ("cart.NewStore", 0.95, 34, null),
            ("cart.Store.Add", 0.9, 34, null),                             // NewStore().Add()
            ("cart.Store.Add", 0.9, 35, null),                             // 字段 *cart.Store
            ("cart.Store.Add", 0.9, 36, null),                             // a, b *cart.Store 的 b
            ("cart.Store.Add", 0.9, 37, null),                             // Map[string](nil) 返回 *cart.Store
            ("orders.Map", 0.95, 37, null),                                // 泛型调用连到函数
            ("genproto.NewCartServiceClient", 0.95, 38, null),
            ("external:gorm.Exec", 0.5, 39, null),                         // 包级变量 defaultDB *gorm.DB 在 vars.go
        }, OutAt(reader, "orders.service.Load"));
        Assert.Equal(new[]
        {
            ("cart.Cart", 0.95), ("cart.Store", 0.95), ("cart.Store", 0.95), ("external:gorm.DB", 0.5),
            ("genproto.CartServiceClient", 0.95), ("orders.Repo", 0.95),
        }, Out(reader, "orders.service", EdgeKind.References));
        Assert.Equal(new[]
        {
            ("cart.Store.Add", 0.9, 58, (string?)null),                  // registry = make(map[string]*cart.Store) 在 vars.go；下标取元素
            ("external:Ping", 0.5, 60, null),                           // srv.(interface{ Ping() })：匿名接口，不猜同包的 service.Ping（原先会是 0.7）
        }, OutAt(reader, "orders.Lookup"));
    }

    [Fact]
    public async Task Incremental_FieldAndSignatureTypesOfUnchangedFiles_ComeFromStoredSymbols()
    {
        var first = OutAt(await IndexAsync(), "orders.service.Load");
        // 只改 orders.go：genproto 的方法签名、package 子句与 vars.go 的包级变量都从库里的符号读
        File.AppendAllText(Path.Combine(_repo, "orders", "orders.go"), "\n// touched\n");
        var second = OutAt(await IndexAsync(), "orders.service.Load");
        Assert.Equal(first, second);
        Assert.Contains(("genproto.Cart.GetItems", 0.9, 28, (string?)null), second);
        Assert.Contains(("external:gorm.Exec", 0.5, 39, (string?)null), second);
    }

    [Fact]
    public async Task FunctionValues_AreReferences_OnlyToRepoFunctions()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:http.ServeMux", 0.5, 48, (string?)null),
            ("orders.service", 0.95, 48, null),
            ("orders.service.Handle", 0.9, 49, "funcvalue"),             // mux.HandleFunc("/load", s.Handle)：方法值
            ("orders.Health", 0.95, 50, "funcvalue"),                    // http.HandleFunc("/health", Health)
        }, OutAt(reader, "orders.Register", EdgeKind.References));       // http.StatusOK 不是本仓库函数：不连
        // 注册函数里不出现"调用了 Handle / Health"
        Assert.DoesNotContain(OutAt(reader, "orders.Register"), e => e.To is "orders.service.Handle" or "orders.Health");
        Assert.Equal(new[] { ("api.Server", 0.95, 20, (string?)null), ("api.Server.Handle", 0.9, 17, "funcvalue") },
            OutAt(reader, "main", EdgeKind.References).OrderBy(e => e.To, StringComparer.Ordinal));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
