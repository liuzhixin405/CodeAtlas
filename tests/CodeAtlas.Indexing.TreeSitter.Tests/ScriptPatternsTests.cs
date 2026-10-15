using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// TS/JS 的补充写法。夹具 Fixtures/patterns：
/// cjs/（Node CommonJS 小服务：require 各种形态、module.exports / exports.f、私有方法、生成器、getter/setter）、
/// react/（memo / forwardRef / observer 包装组件、onXxx={处理函数}、类组件 this 与父类）、
/// nest/（NestJS 风格构造注入、带类型字段、接口 / 抽象类单实现的 impl 派生边）。
/// </summary>
public class ScriptPatternsTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter()];

    public ScriptPatternsTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-patterns-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("patterns"), _repo);
    }

    private static ParsedFile Parse(ILanguageAdapter adapter, string rel) =>
        adapter.Parse(rel, File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("patterns"), rel.Replace('/', Path.DirectorySeparatorChar))));

    private static ParsedSymbol Sym(ParsedFile f, string qualifiedName) => f.Symbols.Single(s => s.QualifiedName == qualifiedName);

    // ---------- 适配器：CommonJS ----------

    [Fact]
    public void CommonJs_RequireForms_BecomeBindingsAndImports()
    {
        using var adapter = new JavaScriptAdapter();
        var f = Parse(adapter, "cjs/server.js");

        Assert.Equal(new[] { "./charge", "./data/rates.json", "./logger", "./store", "./tracing", "./util", "@grpc/grpc-js", "path", "uuid" },
            f.Imports.Select(i => i.Target).OrderBy(t => t, StringComparer.Ordinal));
        Assert.All(f.Imports, i => Assert.Equal(i.Target.StartsWith('.'), i.IsPath));
        Assert.DoesNotContain(f.References, r => r.Name == "require"); // require(...) 本身是 import，不是调用

        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "./util", "round"), calls["rnd"]);             // const { round: rnd } = require('./util')
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "./util", "format"), calls["format"]);          // const { format } = require('./util')
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "./logger", "child"), calls["child"]);          // const child = require('./logger').child
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "./charge", "default"), calls["charge"]);       // const charge = require('./charge')
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "uuid", "v4"), calls["uuidv4"]);               // const { v4: uuidv4 } = require('uuid')
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "./store", "default"), calls["save"]);        // store.save()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "./tracing", "default"), calls["start"]);     // require('./tracing').start()
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "@grpc/grpc-js", "default"), calls["loadPackageDefinition"]);
    }

    [Fact]
    public void CommonJs_Exports_AreMarked_AndAssignedFunctionsAreSymbols()
    {
        using var adapter = new JavaScriptAdapter();
        var charge = Sym(Parse(adapter, "cjs/charge.js"), "cjs/charge.js:charge");                         // module.exports = function charge() {}
        Assert.Equal((SymbolKind.Function, "export default"), (charge.Kind, charge.Visibility));
        var util = Parse(adapter, "cjs/util.js");                                                          // module.exports = { format, round }
        Assert.Equal(("export", "export"), (Sym(util, "cjs/util.js:format").Visibility, Sym(util, "cjs/util.js:round").Visibility));
        var child = Sym(Parse(adapter, "cjs/logger.js"), "cjs/logger.js:child");                           // exports.child = function () {}
        Assert.Equal((SymbolKind.Function, "export"), (child.Kind, child.Visibility));
        var start = Sym(Parse(adapter, "cjs/tracing.js"), "cjs/tracing.js:start");                         // module.exports.start = () => 1
        Assert.Equal((SymbolKind.Function, "export"), (start.Kind, start.Visibility));
    }

    [Fact]
    public void JavaScript_PrivateMethods_Generators_Accessors()
    {
        using var adapter = new JavaScriptAdapter();
        var f = Parse(adapter, "cjs/store/index.js");
        Assert.Equal(new[]
        {
            (SymbolKind.Module, "cjs/store/index.js", (string?)null),
            (SymbolKind.Class, "cjs/store/index.js:Store", "export default"),     // module.exports = new Store()
            (SymbolKind.Method, "cjs/store/index.js:Store.save", null),
            (SymbolKind.Method, "cjs/store/index.js:Store.#write", null),         // 私有方法
            (SymbolKind.Method, "cjs/store/index.js:Store.rows", null),           // async *rows()
            (SymbolKind.Property, "cjs/store/index.js:Store.size", null),         // get size()；同名 setter 不再重复出符号
            (SymbolKind.Function, "cjs/store/index.js:scan", "export"),           // async function* scan()；module.exports.scan = scan
        }, f.Symbols.Select(s => (s.Kind, s.QualifiedName, s.Visibility)));
        var write = f.References.Single(r => r.Name == "#write");
        Assert.Equal((f.Symbols.IndexOf(Sym(f, "cjs/store/index.js:Store.save")), EdgeKind.Calls, new ScriptBinding(ScriptReceiver.This)), (write.FromIndex, write.Kind, write.Script));
    }

    // ---------- 适配器：包装组件、事件处理函数、类组件、构造注入 ----------

    [Fact]
    public void WrappedComponents_AreFunctions_NamedAfterVariableOrInnerFunctionOrFile()
    {
        using var adapter = new TsxAdapter();
        var button = Parse(adapter, "react/Button.tsx");
        Assert.Equal(new[]
        {
            (SymbolKind.Module, "react/Button.tsx", (string?)null),
            (SymbolKind.Function, "react/Button.tsx:Button", "export"),     // memo(forwardRef((props, ref) => …))
            (SymbolKind.Function, "react/Button.tsx:Icon", "export"),       // React.memo(function Icon() {})
            (SymbolKind.Function, "react/Button.tsx:CardInner", null),
            (SymbolKind.Variable, "react/Button.tsx:Card", "export"),       // memo(CardInner)：别名，渲染到 CardInner
        }, button.Symbols.Select(s => (s.Kind, s.QualifiedName, s.Visibility)));
        var card = button.Symbols.IndexOf(Sym(button, "react/Button.tsx:Card"));
        Assert.Contains(button.References, r => (r.FromIndex, r.Name, r.Via) == (card, "CardInner", "render"));

        var panel = Sym(Parse(adapter, "react/Panel.tsx"), "react/Panel.tsx:Panel");                     // export default memo(function Panel() {})
        Assert.Equal((SymbolKind.Function, "export default"), (panel.Kind, panel.Visibility));
        var anon = Parse(adapter, "react/Anon.tsx");                                                      // export default observer(() => …)：匿名取文件名
        Assert.Equal(new[] { ("react/Anon.tsx:Anon", (string?)"export default") }, anon.Symbols.Skip(1).Select(s => (s.QualifiedName, s.Visibility)));
    }

    [Fact]
    public void NonWrapperCalls_AreNotComponents_StyledIsVariable()
    {
        using var adapter = new TsxAdapter();
        var f = adapter.Parse("src/x.tsx",
            "const NAMES = Object.fromEntries(Object.entries(x).map(([a]) => [a, 1]));\n" +             // 两层调用但外层不是 memo / forwardRef / observer
            "const Root = styled(Box)(({ theme }) => ({ p: theme.spacing(1) }));\n" +                    // styled 不算组件
            "export const Btn = memo(forwardRef(function Btn(p, r) { return <b />; }));\n");
        Assert.Equal(new[] { (SymbolKind.Variable, "src/x.tsx:Root"), (SymbolKind.Function, "src/x.tsx:Btn") }, f.Symbols.Skip(1).Select(s => (s.Kind, s.QualifiedName)));
    }

    [Fact]
    public void JsxEventHandlers_OnAttributesOnly_AreEventCalls()
    {
        using var adapter = new TsxAdapter();
        var f = Parse(adapter, "react/Form.tsx");
        var form = f.Symbols.IndexOf(Sym(f, "react/Form.tsx:Form"));
        var events = f.References.Where(r => r.Via == "event").ToList();
        Assert.All(events, r => Assert.Equal((form, EdgeKind.Calls), (r.FromIndex, r.Kind)));
        // title={handleClick} 不是 on 开头，不算；onChange={(e) => setV(…)} 里的调用照常归 Form
        Assert.Equal(new[] { ("handleClick", 16), ("onClose", 16), ("onSubmit", 18), ("save", 16), ("track", 16) },
            events.Select(r => (r.Name, r.Line)).OrderBy(x => x.Name, StringComparer.Ordinal));
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "./analytics", "track"), events.Single(r => r.Name == "track").Script);
        Assert.Contains(f.References, r => (r.FromIndex, r.Name, r.Via, r.Line) == (form, "setV", null, 17));

        using var js = new JavaScriptAdapter();
        var jsx = js.Parse("src/A.jsx", "function A() {\n  const h = () => 1;\n  return <b onClick={h} />;\n}\n");
        Assert.Equal(("h", "event", 3), jsx.References.Where(r => r.Via == "event").Select(r => (r.Name, r.Via, r.Line)).Single());
    }

    [Fact]
    public void ClassComponents_ArrowFieldsAreMethods_ThisCallsCarryBaseClass()
    {
        using var adapter = new TsxAdapter();
        var f = Parse(adapter, "react/Counter.tsx");
        Assert.Equal(SymbolKind.Method, Sym(f, "react/Counter.tsx:Counter.handleClick").Kind);              // handleClick = () => {…}
        var calls = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => r.Name, r => (r.Script, r.Via));
        Assert.Equal((new ScriptBinding(ScriptReceiver.This, "./BaseView", "BaseView"), (string?)null), calls["log"]);
        Assert.Equal((new ScriptBinding(ScriptReceiver.This, "./BaseView", "BaseView"), (string?)"event"), calls["handleClick"]); // onClick={this.handleClick}
        Assert.Equal((new ScriptBinding(ScriptReceiver.This), (string?)null), calls["stop"]);                                    // extends React.Component：父类不在仓库
    }

    [Fact]
    public void ConstructorInjection_AndTypedFields_GiveReceiverType()
    {
        using var adapter = new TypeScriptAdapter();
        var controller = Parse(adapter, "nest/users.controller.ts");
        var calls = controller.References.Where(r => r.Kind == EdgeKind.Calls && r.Name is "get" or "list").Select(r => (r.Name, r.Line, r.Script)).OrderBy(x => x.Line).ToList();
        Assert.Equal(new[]
        {
            ("get", 12, (ScriptBinding?)new ScriptBinding(ScriptReceiver.Typed, "@nestjs/axios", "HttpService")),  // 构造参数属性
            ("list", 13, new ScriptBinding(ScriptReceiver.Typed, "./users.service", "UsersService")),               // 带类型标注的字段；import { UsersService as Svc }
            ("list", 14, new ScriptBinding(ScriptReceiver.Typed, "./users.service", "UsersService")),
        }, calls);
        var service = Parse(adapter, "nest/users.service.ts");
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, null, "Notifier"), service.References.Single(r => r.Name == "send").Script); // 同文件的类型
    }

    // ---------- 解析器（整仓索引） ----------

    private async Task<IndexReader> IndexAsync()
    {
        await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        return new IndexReader(_db);
    }

    private static List<(string To, string? Via, double Confidence)> Out(IndexReader reader, string qualifiedName, EdgeKind kind = EdgeKind.Calls) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { kind })
            .Select(n => (n.Edge.ToName, n.Edge.Via, n.Edge.Confidence)).OrderBy(x => x.ToName, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task CommonJs_CallsResolveThroughRequireBindings()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("cjs/server.js:_getData", (string?)null, 0.9),                  // 回调箭头函数里的调用归 convert
            ("cjs/util.js:format", null, 0.95),                              // const { format } = require('./util')
            ("cjs/util.js:round", null, 0.95),                               // const { round: rnd } = require('./util')
            ("external:callback", null, 0.5),
        }, Out(reader, "cjs/server.js:convert"));
        Assert.Equal(new[]
        {
            ("cjs/charge.js:charge", (string?)null, 0.95),                   // module.exports = function charge
            ("cjs/logger.js:child", null, 0.95),                             // require('./logger').child；exports.child = …
            ("cjs/store/index.js:Store.save", null, 0.9),                    // store.save()：module.exports = new Store()，目录 index.js
            ("external:uuid.v4", null, 0.5),                                 // npm 包具名
        }, Out(reader, "cjs/server.js:pay"));
        Assert.Equal(new[]
        {
            ("cjs/tracing.js:start", (string?)null, 0.9),                    // require('./tracing').start()
            ("external:@grpc/grpc-js.loadPackageDefinition", null, 0.5),
            ("external:path.join", null, 0.5),
        }, Out(reader, "cjs/server.js:main"));
        Assert.Equal(new[] { ("cjs/server.js:main", (string?)null, 0.9) }, Out(reader, "cjs/server.js")); // 没有 external:require
        Assert.Equal(new[] { ("cjs/charge.js:validate", (string?)null, 0.9) }, Out(reader, "cjs/charge.js:charge"));
        Assert.Equal(new[] { ("cjs/store/index.js:Store.#write", (string?)null, 0.9) }, Out(reader, "cjs/store/index.js:Store.save"));
        Assert.Equal(new[]
        {
            ("cjs/charge.js", (string?)null, 1.0), ("cjs/logger.js", null, 1.0), ("cjs/store/index.js", null, 1.0), ("cjs/tracing.js", null, 1.0), ("cjs/util.js", null, 1.0),
            ("external:./data/rates.json", null, 0.5), ("external:@grpc/grpc-js", null, 0.5), ("external:path", null, 0.5), ("external:uuid", null, 0.5),
        }, Out(reader, "cjs/server.js", EdgeKind.Imports));
    }

    [Fact]
    public async Task Component_EventHandlers_And_WrappedComponents_Resolve()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:handleSubmit", (string?)null, 0.5),
            ("external:react-hook-form.useForm", null, 0.5),
            ("external:react.useState", null, 0.5),
            ("external:setV", null, 0.5),                                    // onChange={(e) => setV(…)}：箭头函数里的调用归 Form
            ("react/Button.tsx:Button", "render", 0.95),                     // memo(forwardRef(…)) 包装的组件
            ("react/Button.tsx:Card", "render", 0.95),                       // memo(CardInner) 别名
            ("react/Form.tsx:Form.handleClick", "event", 0.9),               // onClick={handleClick}：组件内局部函数
            ("react/Form.tsx:Form.onSubmit", "event", 0.9),                  // onSubmit={form.handleSubmit(onSubmit)}
            ("react/Form.tsx:save", "event", 0.9),                           // onBlur={save}：本文件函数
            ("react/Panel.tsx:Panel", "render", 0.95),                       // export default memo(function Panel)
            ("react/analytics.ts:track", "event", 0.9),                      // onFocus={track}：import 的函数，事件边一律 0.9；onKeyDown={onClose} 是 prop，不连
        }, Out(reader, "react/Form.tsx:Form"));
        Assert.Equal(new[]
        {
            ("external:react.memo", (string?)null, 0.5),
            ("react/Button.tsx:CardInner", "render", 0.9),
        }, Out(reader, "react/Button.tsx:Card"));
    }

    [Fact]
    public async Task ClassComponent_ThisCalls_ResolveToOwnAndBaseClassMembers()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("react/BaseView.tsx:BaseView.log", (string?)null, 0.9),         // 父类（import 进来）的方法
            ("react/Counter.tsx:Counter.inc", null, 0.9),
        }, Out(reader, "react/Counter.tsx:Counter.handleClick"));
        Assert.Equal(new[] { ("react/BaseView.tsx:BaseView.refresh", (string?)null, 0.9) }, Out(reader, "react/Counter.tsx:Counter.inc"));
        Assert.Equal(new[] { ("react/Counter.tsx:Counter.handleClick", (string?)"event", 0.9) }, Out(reader, "react/Counter.tsx:Counter.render")); // onBlur={this.missing} 不连
        Assert.Equal(new[] { ("react/Counter.tsx:Plain.stop", (string?)null, 0.9) }, Out(reader, "react/Counter.tsx:Plain.go"));
    }

    [Fact]
    public async Task ConstructorInjection_ResolvesByParameterType_WithImplEdgeForSingleImplementation()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:@nestjs/axios.HttpService.get", (string?)null, 0.5), // 类型来自 npm 包
            ("nest/users.service.ts:UsersService.list", null, 0.9),          // this.cache.list()：字段类型
            ("nest/users.service.ts:UsersService.list", null, 0.9),          // this.users.list()：构造参数属性
        }, Out(reader, "nest/users.controller.ts:UsersController.findAll"));
        Assert.Equal(new[]
        {
            ("nest/mail.notifier.ts:MailNotifier.send", (string?)"impl", 0.9), // 抽象类唯一子类
            ("nest/users.repo.ts:UsersRepo.findAll", "impl", 0.9),             // 接口唯一实现
            ("nest/users.service.ts:IClock.now", null, 0.9),                   // 两个实现：不派生
            ("nest/users.service.ts:IUsersRepo.findAll", null, 0.9),
            ("nest/users.service.ts:Notifier.send", null, 0.9),
        }, Out(reader, "nest/users.service.ts:UsersService.list"));
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
