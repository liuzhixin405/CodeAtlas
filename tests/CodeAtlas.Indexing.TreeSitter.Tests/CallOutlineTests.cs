using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 调用大纲的两项原料：每条调用的 Context（最内层控制结构）与 Pos（调用名的 UTF-16 偏移）。
/// 夹具开头都有中文注释和 emoji：若 Pos 误用 UTF-8 字节偏移，这里的 IndexOf 对不上。
/// </summary>
public class CallOutlineTests
{
    private static string Src(string name) => File.ReadAllText(TypeScriptAdapterTests.Fixture(Path.Combine("outline", name)));

    private static ParsedReference Call(ParsedFile f, string name) =>
        Assert.Single(f.References, r => r.Kind == EdgeKind.Calls && r.Name == name);

    /// <summary>名字在源码里的唯一出现位置（marker 是名字加上紧跟的字符，用来唯一定位），再加上名字在 marker 里的偏移。</summary>
    private static int At(string source, string marker, int offsetInMarker = 0)
    {
        var i = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"夹具里没有 {marker}");
        Assert.Equal(i, source.LastIndexOf(marker, StringComparison.Ordinal));
        return i + offsetInMarker;
    }

    // ---------------- C# ----------------

    private static (ParsedFile File, string Source) ParseCs()
    {
        var src = Src("Outline.cs");
        using var adapter = new CSharpAdapter();
        return (adapter.Parse("Outline.cs", src), src);
    }

    [Theory]
    // 成员边界：字段初始化器里的 lambda、属性访问器、构造函数
    [InlineData("FieldInit", CallContext.Callback)]
    [InlineData("PropCond", null)]
    [InlineData("PropThen", CallContext.Cond)]
    [InlineData("CtorCall", null)]
    [InlineData("Start", null)]
    // 循环头不算，只有体算
    [InlineData("ForInit", null)]
    [InlineData("ForCond", null)]
    [InlineData("ForStep", null)]
    [InlineData("ForBody", CallContext.Loop)]
    [InlineData("ForeachColl", null)]
    [InlineData("ForeachBody", CallContext.Loop)]
    [InlineData("WhileCond", null)]
    [InlineData("WhileBody", CallContext.Loop)]
    [InlineData("DoBody", CallContext.Loop)]
    [InlineData("DoCond", null)]
    // 条件头不算，分支算；else if 的条件本身在 else 分支里
    [InlineData("IfCond", null)]
    [InlineData("IfThen", CallContext.Cond)]
    [InlineData("ElifCond", CallContext.Cond)]
    [InlineData("ElifThen", CallContext.Cond)]
    [InlineData("ElseBody", CallContext.Cond)]
    [InlineData("SwitchValue", null)]
    [InlineData("CaseBody", CallContext.Cond)]
    [InlineData("DefaultBody", CallContext.Cond)]
    [InlineData("TernCond", null)]
    [InlineData("TernA", CallContext.Cond)]
    [InlineData("TernB", CallContext.Cond)]
    [InlineData("ArmOne", CallContext.Cond)]
    [InlineData("ArmOther", CallContext.Cond)]
    [InlineData("CoalLeft", null)]
    [InlineData("CoalRight", CallContext.Cond)]
    [InlineData("SafeFirst", CallContext.Cond)]
    [InlineData("SafeThen", CallContext.Cond)]
    [InlineData("TryBody", CallContext.Try)]
    [InlineData("CatchFilter", null)] // when 过滤不算 catch（与 Roslyn 侧一致）
    [InlineData("AndRight", null)] // && / || 右边不算 cond
    [InlineData("CatchBody", CallContext.Catch)]
    [InlineData("FinallyBody", CallContext.Try)]
    [InlineData("LambdaBody", CallContext.Callback)]
    [InlineData("AnonBody", CallContext.Callback)]
    // 嵌套取最内层
    [InlineData("NestedIfInLoop", CallContext.Cond)]
    [InlineData("NestedLoopBody", CallContext.Loop)]
    [InlineData("LambdaInLoop", CallContext.Callback)]
    // 局部函数自己是符号：体里的调用归它，按它的体算
    [InlineData("LocalBody", null)]
    [InlineData("Baz", null)]
    [InlineData("Chain", null)]
    [InlineData("Mid", null)]
    [InlineData("Tail", null)]
    [InlineData("End", null)]
    public void CSharp_Context(string name, string? expected) => Assert.Equal(expected, Call(ParseCs().File, name).Context);

    [Fact]
    public void CSharp_RunInsideLoop_IsLoop_OthersNot()
    {
        var runs = ParseCs().File.References.Where(r => r.Kind == EdgeKind.Calls && r.Name == "Run").OrderBy(r => r.Pos).ToList();
        Assert.Equal([null, null, CallContext.Loop], runs.Select(r => r.Context));
    }

    [Fact]
    public void CSharp_LocalFunctionBody_BelongsToLocalFunction()
    {
        var (f, _) = ParseCs();
        Assert.Equal("Outline.Handler.Handle.Local", f.Symbols[Call(f, "LocalBody").FromIndex].QualifiedName);
    }

    [Theory]
    [InlineData("FieldInit")]
    [InlineData("PropThen")]
    [InlineData("Start")]
    [InlineData("ForInit")]
    [InlineData("ForBody")]
    [InlineData("IfThen")]
    [InlineData("SafeFirst")]
    [InlineData("SafeThen")]
    [InlineData("CatchBody")]
    [InlineData("LambdaInLoop")]
    [InlineData("LocalBody")]
    [InlineData("Chain")]
    [InlineData("Mid")]
    [InlineData("Tail")]
    [InlineData("End")]
    public void CSharp_Pos_IsUtf16OffsetOfName(string name)
    {
        var (f, src) = ParseCs();
        Assert.Equal(At(src, name + "("), Call(f, name).Pos);
    }

    [Fact]
    public void CSharp_Pos_QualifiedNew_PointsAtTypeName_AndChainIsOrdered()
    {
        var (f, src) = ParseCs();
        Assert.Equal(At(src, "Baz("), Call(f, "Baz").Pos);
        // 多行链式调用：行号记链开头那一行（对齐 Roslyn），Pos 仍是名字的真实位置
        var chain = Call(f, "Chain");
        var mid = Call(f, "Mid");
        var tail = Call(f, "Tail");
        Assert.Equal(chain.Line, tail.Line);
        Assert.True(chain.Pos < mid.Pos && mid.Pos < tail.Pos);
        Assert.Equal('\n', src[tail.Pos - "            ".Length - 1]);
    }

    [Fact]
    public void CSharp_Pos_SortsCallsInSourceOrder()
    {
        var (f, _) = ParseCs();
        var inHandle = f.References.Where(r => r.Kind == EdgeKind.Calls && f.Symbols[r.FromIndex].Name == "Handle").OrderBy(r => r.Pos).Select(r => r.Name).ToList();
        Assert.Equal("Start", inHandle[0]);
        Assert.Equal("End", inHandle[^1]);
        Assert.True(inHandle.IndexOf("ForBody") < inHandle.IndexOf("ForeachColl"));
        Assert.All(f.References.Where(r => r.Kind == EdgeKind.Calls), r => Assert.True(r.Pos >= 0, r.Name));
    }

    // ---------------- TypeScript ----------------

    private static (ParsedFile File, string Source) ParseTs()
    {
        var src = Src("outline.ts");
        using var adapter = new TypeScriptAdapter();
        return (adapter.Parse("src/outline.ts", src), src);
    }

    [Theory]
    [InlineData("start", null)]
    [InlineData("forInit", null)]
    [InlineData("forCond", null)]
    [InlineData("forStep", null)]
    [InlineData("forBody", CallContext.Loop)]
    [InlineData("forOfColl", null)]
    [InlineData("forOfBody", CallContext.Loop)]
    [InlineData("forInObj", null)]
    [InlineData("forInBody", CallContext.Loop)]
    [InlineData("whileCond", null)]
    [InlineData("whileBody", CallContext.Loop)]
    [InlineData("doBody", CallContext.Loop)]
    [InlineData("doCond", null)]
    [InlineData("ifCond", null)]
    [InlineData("ifThen", CallContext.Cond)]
    [InlineData("elifCond", CallContext.Cond)]
    [InlineData("elifThen", CallContext.Cond)]
    [InlineData("elseBody", CallContext.Cond)]
    [InlineData("switchValue", null)]
    [InlineData("caseBody", CallContext.Cond)]
    [InlineData("defaultBody", CallContext.Cond)]
    [InlineData("ternCond", null)]
    [InlineData("ternA", CallContext.Cond)]
    [InlineData("ternB", CallContext.Cond)]
    [InlineData("coalLeft", null)]
    [InlineData("coalRight", CallContext.Cond)]
    [InlineData("safeFirst", CallContext.Cond)]
    [InlineData("safeThen", CallContext.Cond)]
    [InlineData("tryBody", CallContext.Try)]
    [InlineData("catchBody", CallContext.Catch)]
    [InlineData("finallyBody", CallContext.Try)]
    [InlineData("arrowBody", CallContext.Callback)]
    [InlineData("fnExprBody", CallContext.Callback)]
    [InlineData("nestedIfInLoop", CallContext.Cond)]
    [InlineData("nestedLoopBody", CallContext.Loop)]
    [InlineData("forEach", CallContext.Loop)]
    [InlineData("arrowInLoop", CallContext.Callback)]
    [InlineData("chain", null)]
    [InlineData("mid", null)]
    [InlineData("tail", null)]
    [InlineData("end", null)]
    // const f = async () => {…}、类字段箭头函数自己是符号：按它的体算，不是回调
    [InlineData("arrowFnTop", null)]
    [InlineData("arrowFnCond", null)]
    [InlineData("arrowFnThen", CallContext.Cond)]
    [InlineData("fieldArrowTop", null)]
    [InlineData("methodTop", null)]
    [InlineData("map", null)]
    [InlineData("methodCallback", CallContext.Callback)]
    public void TypeScript_Context(string name, string? expected) => Assert.Equal(expected, Call(ParseTs().File, name).Context);

    [Theory]
    [InlineData("start")]
    [InlineData("forBody")]
    [InlineData("safeFirst")]
    [InlineData("safeThen")]
    [InlineData("arrowInLoop")]
    [InlineData("chain")]
    [InlineData("mid")]
    [InlineData("tail")]
    [InlineData("methodCallback")]
    public void TypeScript_Pos_IsUtf16OffsetOfName(string name)
    {
        var (f, src) = ParseTs();
        Assert.Equal(At(src, name + "("), Call(f, name).Pos);
    }

    [Fact]
    public void TypeScript_Pos_ChainIsOrdered()
    {
        var (f, _) = ParseTs();
        Assert.True(Call(f, "chain").Pos < Call(f, "mid").Pos && Call(f, "mid").Pos < Call(f, "tail").Pos);
        var inHandle = f.References.Where(r => r.Kind == EdgeKind.Calls && f.Symbols[r.FromIndex].Name == "handle").OrderBy(r => r.Pos).Select(r => r.Name).ToList();
        Assert.Equal("start", inHandle[0]);
        Assert.Equal("end", inHandle[^1]);
    }

    // ---------------- TSX / JSX ----------------

    public static TheoryData<string> JsxAdapters => ["tsx", "javascript"];

    private static (ParsedFile File, string Source) ParseJsx(string adapterId)
    {
        var src = Src("Page.tsx");
        using LanguageAdapterBase adapter = adapterId == "tsx" ? new TsxAdapter() : new JavaScriptAdapter();
        return (adapter.Parse(adapterId == "tsx" ? "src/Page.tsx" : "src/Page.jsx", src), src);
    }

    [Theory]
    [MemberData(nameof(JsxAdapters))]
    public void Jsx_Context(string adapterId)
    {
        var f = ParseJsx(adapterId).File;
        // memo(() => …) 包装的组件体、组件里 const onSave = () => … 都是符号的体
        Assert.Null(Call(f, "useData").Context);
        Assert.Null(Call(f, "useEffect").Context);
        Assert.Equal(CallContext.Callback, Call(f, "loadInEffect").Context);
        Assert.Null(Call(f, "saveTop").Context);
        Assert.Null(Call(f, "Panel").Context);
        // onClick={() => …} 里的调用、onClose={handleClose} 传出去的处理函数 = 回调
        Assert.Equal(CallContext.Callback, Call(f, "clickBody").Context);
        Assert.Equal(CallContext.Callback, Call(f, "handleClose").Context);
        Assert.Equal(CallContext.Callback, Call(f, "pressBody").Context);
        // 条件渲染与 map 里的元素
        Assert.Equal(CallContext.Cond, Call(f, "Ok").Context);
        Assert.Equal(CallContext.Cond, Call(f, "Fail").Context);
        Assert.Null(Call(f, "map").Context);
        Assert.Equal(CallContext.Callback, Call(f, "Row").Context);
        Assert.Null(Call(f, "renderTop").Context);
        Assert.Null(Call(f, "Btn").Context);
    }

    [Theory]
    [MemberData(nameof(JsxAdapters))]
    public void Jsx_Pos(string adapterId)
    {
        var (f, src) = ParseJsx(adapterId);
        Assert.Equal(At(src, "clickBody("), Call(f, "clickBody").Pos);
        Assert.Equal(At(src, "{handleClose}", 1), Call(f, "handleClose").Pos);
        Assert.Equal(At(src, "<Panel", 1), Call(f, "Panel").Pos);
        Assert.Equal(At(src, "<Row", 1), Call(f, "Row").Pos);
        Assert.Equal(At(src, "map("), Call(f, "map").Pos);
    }
}
