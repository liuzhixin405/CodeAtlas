using CodeAtlas.Core.Model;
using CodeAtlas.Core.Roslyn;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Indexing.Roslyn.Tests;

/// <summary>调用大纲：Roslyn 边的 Pos（调用名偏移）与 Ctx（所在控制结构），夹具 Sample.Core/Outline.cs。</summary>
public partial class AnalyzerTests
{
    private const string OutlineRun = "Sample.Core.Outline.Run(Sink)";

    private string OutlineSource => File.ReadAllText(Path.Combine(fx.Root, "Sample.Core", "Outline.cs"));

    private EdgeRec OutlineCall(string name) =>
        Assert.Single(fx.EdgesFrom(OutlineRun, "calls"), e => e.ToQualifiedName.StartsWith("Sample.Core.Sink." + name + "(", StringComparison.Ordinal)
                                                          || e.ToQualifiedName.StartsWith("Sample.Core.Sink." + name + "<", StringComparison.Ordinal));

    [Theory]
    [InlineData("Plain", null)]
    [InlineData("Head", null)]            // if 条件不算
    [InlineData("InIf", CallContext.Cond)]
    [InlineData("ElseIfHead", CallContext.Cond)] // else if 的条件在 else 分支里
    [InlineData("InElse", CallContext.Cond)]
    [InlineData("ForInit", null)]         // 循环头不算
    [InlineData("ForCond", null)]
    [InlineData("ForStep", null)]
    [InlineData("ForBody", CallContext.Loop)]
    [InlineData("EachBody", CallContext.Loop)]
    [InlineData("WhileHead", null)]
    [InlineData("WhileBody", CallContext.Loop)]
    [InlineData("DoBody", CallContext.Loop)]
    [InlineData("DoHead", null)]
    [InlineData("SwitchOn", null)]        // 被 switch 的表达式不算
    [InlineData("InCase", CallContext.Cond)]
    [InlineData("ArmValue", CallContext.Cond)]
    [InlineData("WhenYes", CallContext.Cond)]
    [InlineData("WhenNo", CallContext.Cond)]
    [InlineData("MaybeNull", null)]
    [InlineData("Fallback", CallContext.Cond)]   // ?? 右侧
    [InlineData("Next", null)]
    [InlineData("AfterNullCheck", CallContext.Cond)] // ?. 之后
    [InlineData("InTry", CallContext.Try)]
    [InlineData("InCatch", CallContext.Catch)]
    [InlineData("InFilter", null)]        // catch filter 不算
    [InlineData("InFinally", CallContext.Try)]
    [InlineData("Nested", CallContext.Cond)]     // foreach 里的 if：取最内层
    [InlineData("InLambda", CallContext.Callback)]
    [InlineData("InAnonymous", CallContext.Callback)]
    [InlineData("Chain", null)]
    [InlineData("Tail", null)]
    [InlineData("Generic", null)]
    public void Outline_CallEdge_HasNamePosAndContext(string name, string? ctx)
    {
        var edge = OutlineCall(name);
        var src = OutlineSource;
        var expected = src.IndexOf("." + name + "(", StringComparison.Ordinal);
        if (expected < 0) expected = src.IndexOf("." + name + "<", StringComparison.Ordinal);
        Assert.True(expected > 0, name);
        Assert.Equal(expected + 1, edge.Pos);   // 名字 token 的开头（跳过点号）
        Assert.Equal(src.Substring(edge.Pos!.Value, name.Length), name);
        Assert.Equal(ctx, edge.Ctx);
    }

    [Fact]
    public void Outline_EveryCallHasADistinctPos_PointingAtTheCalleeName_ChainTailAfterHead()
    {
        var src = OutlineSource;
        var calls = fx.EdgesFrom(OutlineRun, "calls").ToList();
        Assert.True(calls.Count >= 35, $"calls={calls.Count}");
        Assert.All(calls, e => Assert.NotNull(e.Pos));
        Assert.Equal(calls.Count, calls.Select(e => e.Pos).Distinct().Count()); // Items / Pick 各调两次，也是两个位置
        foreach (var e in calls)
        {
            // 限定名最后一段的名字（去掉参数表与泛型实参）就是 pos 处的源码
            var q = e.ToQualifiedName;
            var head = q.EndsWith(')') ? q[..q.LastIndexOf('(')] : q;      // 局部函数 Run(Sink).Local()：只去掉最后一个参数表
            var simple = head[(head.LastIndexOf('.') + 1)..].Split('<')[0];
            Assert.True(string.CompareOrdinal(src, e.Pos!.Value, simple, 0, simple.Length) == 0, $"{e.ToQualifiedName} @ {e.Pos}");
        }
        var chain = OutlineCall("Chain");
        var tail = OutlineCall("Tail");
        Assert.Equal(chain.Line, tail.Line);   // 多行链式调用的行号都记在链开头，只有 pos 能排对先后
        Assert.True(tail.Pos > chain.Pos);
    }

    [Fact]
    public void Outline_ObjectCreation_PosIsTypeName()
    {
        var src = OutlineSource;
        var created = Assert.Single(fx.EdgesFrom(OutlineRun, "calls"), e => e.ToQualifiedName.StartsWith("System.Collections.Generic.List<T>", StringComparison.Ordinal));
        Assert.Equal(src.IndexOf("List<int>()", StringComparison.Ordinal), created.Pos);
        Assert.Null(created.Ctx);
    }

    [Fact]
    public void Outline_LocalFunction_IsItsOwnSymbol_BodyCallsAreNotCallbackForIt_AndCallingItIsPlain()
    {
        var src = OutlineSource;
        var inLocal = Assert.Single(fx.Edges, e => e.Kind == "calls" && e.ToQualifiedName == "Sample.Core.Sink.InLocal()");
        Assert.NotEqual(OutlineRun, inLocal.FromQualifiedName);  // 挂在局部函数自己身上
        Assert.Null(inLocal.Ctx);                                  // 在它自己的大纲里是顺序调用
        Assert.Equal(src.IndexOf(".InLocal(", StringComparison.Ordinal) + 1, inLocal.Pos);

        var callLocal = Assert.Single(fx.EdgesFrom(OutlineRun, "calls"), e => e.ToQualifiedName == inLocal.FromQualifiedName);
        Assert.Equal(src.IndexOf("        Local();", StringComparison.Ordinal) + 8, callLocal.Pos);
        Assert.Null(callLocal.Ctx);
    }

    [Fact]
    public void Dispatch_EdgeCarriesTheDispatcherCallSite()
    {
        var src = File.ReadAllText(Path.Combine(fx.Root, "Sample.App", "Controller.cs"));
        var dispatch = fx.EdgesFrom("Sample.App.Controller.List(int, CancellationToken)", "dispatches").Single();
        Assert.Equal(src.IndexOf("QueryAsync<", StringComparison.Ordinal), dispatch.Pos);
        // 声明级的 handles 边没有调用点
        Assert.Null(fx.EdgesFrom("Sample.Core.ListQueryHandler", "dispatches").Single(e => e.Via == "handles").Pos);
    }

    [Fact]
    public void RecordJson_RoundTripsPosAndCtx()
    {
        var rec = new EdgeRec("A.M()", "B.N()", false, "calls", 1.0, "A.cs", 3, Pos: 42, Ctx: CallContext.Loop);
        var json = RecordJson.Serialize(rec);
        Assert.Contains("\"pos\":42", json);
        Assert.Contains("\"ctx\":\"loop\"", json);
        Assert.Equal(rec, RecordJson.Deserialize(json));
    }
}

/// <summary>CallSyntax 的纯语法用例：夹具里不好放的写法。</summary>
public class CallSyntaxTests
{
    private static (InvocationExpressionSyntax Call, string Source) Find(string body, string name)
    {
        var src = "class C { void M() { " + body + " } }";
        var tree = CSharpSyntaxTree.ParseText(src);
        var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString().Contains(name, StringComparison.Ordinal));
        return (call, src);
    }

    [Theory]
    [InlineData("a?.B()?.Target();", "Target", CallContext.Cond)]
    [InlineData("a?.B.Target();", "Target", CallContext.Cond)]
    [InlineData("x ??= Target();", "Target", CallContext.Cond)]
    [InlineData("var q = from x in xs where Target(x) select x;", "Target", CallContext.Callback)]
    [InlineData("var q = from x in Target() select x;", "Target", null)]   // 第一个 from 的集合立即求值
    [InlineData("switch (k) { case 1 when Target(): break; }", "Target", CallContext.Cond)]
    [InlineData("using (Target()) { }", "Target", null)]
    [InlineData("lock (o) { Target(); }", "Target", null)]
    [InlineData("foreach (var x in xs) { try { Target(); } catch { } }", "Target", CallContext.Try)]  // 最内层
    [InlineData("items.ForEach(i => { if (i) Target(); });", "Target", CallContext.Cond)]           // lambda 里的 if：最内层是 cond
    [InlineData("while (true) { Run(() => Target()); }", "Target", CallContext.Callback)]
    [InlineData("int L() { for (;;) Target(); }", "Target", CallContext.Loop)]                        // 局部函数内照常判断
    [InlineData("for (;;) { int L() => Target(); }", "Target", null)]                                // 局部函数是边界，外层的循环不算
    public void ContextOf_Cases(string body, string name, string? expected)
    {
        var (call, _) = Find(body, name);
        Assert.Equal(expected, CallSyntax.ContextOf(call));
    }

    [Theory]
    [InlineData("a.b.Target();")]
    [InlineData("a?.Target();")]
    [InlineData("a.Target<int>();")]
    [InlineData("Target<int, string>();")]
    [InlineData("this.Target();")]
    [InlineData("a.Target!();")]
    [InlineData("a.B().C().Target();")]
    [InlineData("a.B()\n   .Target();")]
    public void NamePos_PointsAtTheCalleeName(string body)
    {
        var (call, src) = Find(body, "Target");
        Assert.Equal(src.IndexOf("Target", StringComparison.Ordinal), CallSyntax.NamePos(call));
    }

    [Theory]
    [InlineData("var x = new Target();", "Target")]
    [InlineData("var x = new Ns.Inner.Target(1);", "Target")]
    [InlineData("var x = new global::Target();", "Target")]
    [InlineData("var x = new Target<int>();", "Target")]
    [InlineData("List<int> x = new();", "new")]
    public void NamePos_ObjectCreation_PointsAtTheTypeName(string body, string token)
    {
        var src = "class C { void M() { " + body + " } }";
        var oc = CSharpSyntaxTree.ParseText(src).GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().Single();
        var expected = token == "new" ? src.IndexOf("new()", StringComparison.Ordinal) : src.LastIndexOf(token, StringComparison.Ordinal);
        Assert.Equal(expected, CallSyntax.NamePos(oc));
    }
}
