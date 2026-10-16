using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// TS/TSX 的 import 绑定、JSX 渲染边与默认导出。夹具 Fixtures/next/web 是一个缩小的 Next.js 应用：
/// tsconfig 的 "@/*" 别名、具名 / 默认 / 命名空间导入、index 桶文件 re-export，以及 legacy/ 下的同名干扰定义。
/// </summary>
public class ScriptBindingTests : IDisposable
{
    private const string Page = "web/src/app/[lang]/(main)/groups/page.tsx";
    private const string Table = "web/src/components/GroupTable.tsx";
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

    public ScriptBindingTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-next-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("next"), _repo);
    }

    private static string Src(string rel) => File.ReadAllText(Path.Combine(TypeScriptAdapterTests.Fixture("next"), rel));

    // ---------- 适配器 ----------

    [Fact]
    public void Tsx_JsxComponents_AreRenderCalls_WithImportBindings()
    {
        using var adapter = new TsxAdapter();
        var f = adapter.Parse(Page, Src(Page.Replace('/', Path.DirectorySeparatorChar)));
        var page = f.Symbols.IndexOf(f.Symbols.Single(s => s.Name == "GroupsPage"));

        var renders = f.References.Where(r => r.Via == "render").ToList();
        Assert.All(renders, r => Assert.Equal((page, EdgeKind.Calls), (r.FromIndex, r.Kind)));
        // 小写宿主元素 <div> 不算；作为 prop 传进去的 <GroupTable /> 算；</PermissionGuard> 闭合标签不重复
        Assert.Equal(new[] { "GroupTable", "PermissionGuard", "Stack", "Switcher" }, renders.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
        var byName = renders.ToDictionary(r => r.Name, r => r.Script);
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "@/components/common/Guard", "Guard"), byName["PermissionGuard"]); // import { Guard as PermissionGuard }
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "@/components/GroupTable", "default"), byName["GroupTable"]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "@mui/material", "Stack"), byName["Stack"]);
    }

    [Fact]
    public void Tsx_MemberCallsAndMemberTags_CarryReceiverKind()
    {
        using var adapter = new TsxAdapter();
        var f = adapter.Parse(Table, Src(Table.Replace('/', Path.DirectorySeparatorChar)));
        var refs = f.References.Where(r => r.Kind == EdgeKind.Calls).ToDictionary(r => (r.Name, r.Via), r => r.Script);

        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "./fmt", "*"), refs[("label", null)]);           // Fmt.label()，Fmt 是命名空间导入
        Assert.Equal(new ScriptBinding(ScriptReceiver.Import, "./ui", "*"), refs[("Panel", "render")]);        // <Ui.Panel>
        // data.filter()：data = useGroups(1)，接收者是 import 进来的 useGroups 的返回值（解析器再看 useGroups 返回什么）
        Assert.Equal(new ScriptBinding(ScriptReceiver.Typed, "@/hooks/useGroups", "useGroups()"), refs[("filter", null)]);
        Assert.Equal(new ScriptBinding(ScriptReceiver.None), refs[("has", null)]);                             // 解构出来的局部名字，没有 import
        Assert.Equal(new ScriptBinding(ScriptReceiver.None), refs[("Row", "render")]);                         // 本文件的组件
        Assert.Equal(new ScriptBinding(ScriptReceiver.None, "@/hooks/useGroups", "useGroups"), refs[("useGroups", null)]);
    }

    [Fact]
    public void DefaultExports_AreMarked_IncludingExportDefaultIdentifierAndAnonymous()
    {
        using var adapter = new TsxAdapter();
        var page = adapter.Parse(Page, Src(Page.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal("export default", page.Symbols.Single(s => s.Name == "GroupsPage").Visibility);

        var switcher = adapter.Parse("src/Switcher.tsx", Src("web/src/components/Switcher.tsx"));
        Assert.Equal("export default", switcher.Symbols.Single(s => s.Name == "Switcher").Visibility); // const Switcher = …; export default Switcher;

        var anon = adapter.Parse("src/a.tsx", "export default () => <div />;\nexport const Wrapped = memo(() => <span />);\nexport default memo(Wrapped);\n");
        var d = anon.Symbols.Single(s => s.Name == "default");
        Assert.Equal((SymbolKind.Function, "src/a.tsx:default", "export default"), (d.Kind, d.QualifiedName, d.Visibility));
        Assert.Equal(("src/a.tsx:Wrapped", "export default"), (anon.Symbols.Single(s => s.Name == "Wrapped").QualifiedName, anon.Symbols.Single(s => s.Name == "Wrapped").Visibility)); // memo(() => …) 也是组件定义；export default memo(Wrapped)
    }

    [Fact]
    public void Jsx_InJavaScript_RendersAndBindsImports()
    {
        using var adapter = new JavaScriptAdapter();
        var f = adapter.Parse("src/App.jsx", "import Card from './Card';\nimport * as UI from 'ui-kit';\nexport default function App() { return <UI.Box><Card /><p /></UI.Box>; }\n");
        var app = f.Symbols.Single(s => s.Name == "App");
        Assert.Equal("export default", app.Visibility);
        var renders = f.References.Where(r => r.Via == "render").Select(r => (r.Name, r.Script)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("Box", (ScriptBinding?)new ScriptBinding(ScriptReceiver.Import, "ui-kit", "*")),
            ("Card", new ScriptBinding(ScriptReceiver.None, "./Card", "default")),
        }, renders);
    }

    [Fact]
    public void ReExports_AreImports()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/ui/index.ts", "export { Panel } from \"./Panel\";\nexport * from './more';\n");
        Assert.Equal(new[] { ("./Panel", true, 1), ("./more", true, 2) }, f.Imports.Select(i => (i.Target, i.IsPath, i.Line)));
    }

    [Fact]
    public void TsConfig_PathsAlias_IsReadRelativeToConfigDirectory_Jsonc()
    {
        var configs = TsConfigReader.Discover([new ScannedFile("web/src/a.ts", Path.Combine(_repo, "web", "src", "a.ts"), "typescript", 1)]);
        var c = Assert.Single(configs);
        Assert.Equal(("web", (string?)null), (c.Directory, c.BaseUrl));
        var alias = Assert.Single(c.Paths);
        Assert.Equal(("@/*", "web/src/*"), (alias.Key, Assert.Single(alias.Value)));
    }

    // ---------- 解析器（整仓索引） ----------

    private async Task<IndexReader> IndexAsync()
    {
        await new TreeSitterIndexer(_db, _adapters, new HeuristicEdgeResolver()).IndexAsync(_repo);
        return new IndexReader(_db);
    }

    private static List<(string To, string? Via, double Confidence)> Out(IndexReader reader, string qualifiedName) =>
        reader.GetNeighbors(reader.FindByQualifiedName(qualifiedName).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Select(n => (n.Edge.ToName, n.Edge.Via, n.Edge.Confidence)).OrderBy(x => x.ToName, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Page_RendersImportedComponents_ResolvedThroughAlias_NotDecoys()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:@mui/material.Stack", (string?)"render", 0.5),                         // npm 包里的组件
            ("web/src/components/GroupTable.tsx:GroupTable", "render", 0.95),                // 默认导入；legacy/GroupTable.tsx 同名不连
            ("web/src/components/Switcher.tsx:Switcher", "render", 0.95),                    // export default Switcher;
            ("web/src/components/common/Guard.tsx:Guard", "render", 0.95),                   // import { Guard as PermissionGuard }
        }, Out(reader, Page + ":GroupsPage"));
    }

    [Fact]
    public async Task Component_Calls_FollowImports_UnboundNamesAreExternal()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:filter", (string?)null, 0.5),                                          // data.filter：内置方法
            ("external:has", null, 0.5),                                                      // 解构出来的局部函数：不按全仓同名连到 Viewer.has
            ("web/src/components/GroupTable.tsx:Row", "render", 0.9),                         // 本文件组件
            ("web/src/components/fmt.ts:label", null, 0.9),                                   // Fmt.label：命名空间导入；legacy/fmt.ts:label 不连
            ("web/src/components/ui/Panel.tsx:Panel", "render", 0.9),                         // <Ui.Panel>：经 ui/index.ts 的 re-export
            ("web/src/contexts/Permission.tsx:usePermissions", null, 0.95),                   // 相对路径
            ("web/src/hooks/useGroups.ts:useGroups", null, 0.95),                             // @/ 别名；legacy/useGroups.ts 不连
        }, Out(reader, Table + ":GroupTable"));
    }

    [Fact]
    public async Task Hook_QueryFnArrowCalls_BelongToHook_AndThrowNewErrorIsExternal()
    {
        var reader = await IndexAsync();
        Assert.Equal(new[]
        {
            ("external:@tanstack/react-query.useQuery", (string?)null, 0.5),
            ("external:get", null, 0.5),                                                      // api.get：queryFn 箭头函数里的调用归到 useGroups；legacy/fmt.ts:get 不连
            ("web/src/hooks/useApi.ts:useApi", null, 0.95),
        }, Out(reader, "web/src/hooks/useGroups.ts:useGroups"));
        Assert.Equal(new[]
        {
            ("external:Error", (string?)null, 0.5),                                           // new Error(…)：不连到 app/error.tsx 的 Error 页面
            ("external:react.useContext", null, 0.5),
        }, Out(reader, "web/src/contexts/Permission.tsx:usePermissions"));
    }

    [Fact]
    public async Task AliasImports_BecomeModuleImportEdges()
    {
        var reader = await IndexAsync();
        var module = reader.FindByQualifiedName(Page).Single();
        var imports = reader.GetNeighbors(module.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Imports })
            .Select(n => (n.Edge.ToName, n.Edge.Confidence)).OrderBy(x => x.ToName, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("external:@mui/material", 0.5),
            ("web/src/components/GroupTable.tsx", 1.0),
            ("web/src/components/Switcher.tsx", 1.0),
            ("web/src/components/common/Guard.tsx", 1.0),
        }, imports);
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
