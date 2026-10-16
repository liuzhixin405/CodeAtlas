using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>调用大纲的步骤标签（局部函数、顶级语句）与按"类型.方法"找符号（CLI query 用的 OutlineQuery.FindSymbols）。</summary>
public class OutlineBuilderTests
{
    [Fact]
    public void StepLabels_LocalFunctionShowsItsOwnName_TopLevelShowsProgramMain()
    {
        using var r = new OutlineRepo();
        var svc = r.Type("Svc");
        var build = r.Method(svc, "Build", parameters: "IReadOnlyList<int>, int");
        // 局部函数：父符号是外层方法，限定名 = 外层方法(参数).局部(参数)；原来显示成外层方法名 Build
        var reach = r.Symbol(SymbolKind.Function, "Reach", "App.Svc.Build(IReadOnlyList<int>, int).Reach(Node)", parent: build);
        var main = r.Symbol(SymbolKind.Function, "<Main>$", "Program.<Main>$(string[])");
        var ns = r.Symbol(SymbolKind.Namespace, "Flows", "App.Flows");
        var front = r.Symbol(SymbolKind.Method, "Build", "App.Flows.FrontFlow.Build(Input)", parent: ns); // 分部类的方法挂在命名空间下
        var usage = r.Symbol(SymbolKind.Function, "Usage", "Program.<Main>$(string[]).Usage()", parent: main);
        var caller = r.Method(svc, "Start");
        r.Edge(build, reach, line: 30);
        r.Edge(main, build, line: 3);
        r.Edge(main, usage, line: 4);
        r.Edge(caller, main, line: 5);
        r.Edge(caller, front, line: 6);
        Assert.Equal(new[] { "Reach" }, r.Outline(build).Steps.Select(s => s.Label));
        Assert.Equal(new[] { "Svc.Build", "Usage" }, r.Outline(main).Steps.Select(s => s.Label));
        // 顶级语句当目标：原来是 "$"
        Assert.Equal(new[] { "Program.Main", "FrontFlow.Build" }, r.Outline(caller).Steps.Select(s => s.Label));
    }

    [Fact]
    public void FindSymbols_AcceptsTypeDotMember_WithoutParametersOrGenerics()
    {
        using var r = new OutlineRepo();
        var cmd = r.Type("IndexCommand");
        var run = r.Method(cmd, "Run", parameters: "ParsedArgs, TextWriter, TextWriter");
        var runRoslyn = r.Method(cmd, "RunRoslyn", parameters: "IndexDatabase");
        var other = r.Type("GitHistory");
        var gitRun = r.Method(other, "Run", parameters: "Roots");
        var repo = r.Symbol(SymbolKind.Class, "Repo", "App.Repo<T>");
        var findInt = r.Symbol(SymbolKind.Method, "Find", "App.Repo<T>.Find(int)", parent: repo);
        var findStr = r.Symbol(SymbolKind.Method, "Find", "App.Repo<T>.Find(string)", parent: repo);
        var hook = r.Symbol(SymbolKind.Function, "useOrders", "web/hooks/useOrders.ts:useOrders", file: r.File("web/hooks/useOrders.ts"));
        long[] Find(string name) => OutlineQuery.FindSymbols(r.Db, name).Select(s => s.Id).ToArray();

        // 完整限定名、短名照旧
        Assert.Equal(new[] { run }, Find("App.IndexCommand.Run(ParsedArgs, TextWriter, TextWriter)"));
        Assert.Equal(new[] { gitRun, run }.OrderBy(i => i), Find("Run").OrderBy(i => i));
        // 类型.方法（原来"未找到符号"）：段对齐，Run 不匹配 RunRoslyn，Command.Run 不匹配 IndexCommand.Run
        Assert.Equal(new[] { run }, Find("IndexCommand.Run"));
        Assert.Equal(new[] { run }, Find("App.IndexCommand.Run"));
        Assert.Equal(new[] { runRoslyn }, Find("IndexCommand.RunRoslyn"));
        Assert.Empty(Find("Command.Run"));
        // 参数表：写了就按它挑重载（空白不计）；泛型参数两边都去掉
        Assert.Equal(new[] { run }, Find("IndexCommand.Run(ParsedArgs,TextWriter,TextWriter)"));
        Assert.Equal(new[] { findInt, findStr }, Find("Repo.Find"));
        Assert.Equal(new[] { findInt, findStr }, Find("Repo<T>.Find"));
        Assert.Equal(new[] { findStr }, Find("Repo.Find(string)"));
        // 脚本：路径前缀可写可不写
        Assert.Equal(new[] { hook }, Find("useOrders.ts:useOrders"));
        Assert.Equal(new[] { hook }, Find("hooks/useOrders.ts:useOrders"));
        Assert.Equal(new[] { hook }, Find("useOrders"));
        Assert.Empty(Find("other.ts:useOrders"));
        Assert.Empty(Find("Orders.ts:useOrders")); // 路径按目录段对齐
        Assert.Empty(Find("Nope.Run"));
    }
}
