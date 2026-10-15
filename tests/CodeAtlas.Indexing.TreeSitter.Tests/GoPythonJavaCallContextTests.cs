using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 调用大纲：Go / Python / Java 调用所在的控制结构（ParsedReference.Context）。夹具 Fixtures/callctx 里每个调用名唯一，
/// 逐条断言（按源码先后）：条件头 / 循环头不算、嵌套取最内层、lambda / 函数字面量 / 匿名类 = callback、Go defer / go = callback、Python 推导式 = loop。
/// </summary>
public class GoPythonJavaCallContextTests
{
    private const string? None = null;
    private const string Loop = CallContext.Loop, Cond = CallContext.Cond, Try = CallContext.Try, Catch = CallContext.Catch, Callback = CallContext.Callback;

    private static string Src(string file) => File.ReadAllText(TypeScriptAdapterTests.Fixture(Path.Combine("callctx", file)));

    /// <summary>
    /// 某个符号直接发出的调用，按 Pos 排：(名字, Context)。顺带断言 Pos：等于源码里该名字（整词）第 k 次出现的偏移
    /// （k = 前面同名调用的个数；夹具里调用名不在别处出现，开头的中文注释顺带验证 UTF-16 偏移），且按 Pos 排与按（行，列）排一致。
    /// </summary>
    private static List<(string Name, string? Context)> Calls(ILanguageAdapter adapter, string file, string member)
    {
        var source = Src(file);
        var f = adapter.Parse(file, source);
        var from = f.Symbols.ToList().FindIndex(s => s.Name == member);
        Assert.True(from > 0, $"没找到符号 {member}");
        var calls = f.References.Where(r => r.FromIndex == from && r.Kind == EdgeKind.Calls).OrderBy(r => r.Pos).ToList();
        Assert.Equal(calls.OrderBy(r => r.Line).ThenBy(r => r.Col).Select(r => r.Name), calls.Select(r => r.Name));
        var seen = new Dictionary<string, int>();
        foreach (var r in calls)
        {
            var k = seen[r.Name] = seen.GetValueOrDefault(r.Name) + 1;
            var expected = Regex.Matches(source, $@"\b{Regex.Escape(r.Name)}\b")[k - 1].Index;
            Assert.True(expected == r.Pos, $"{r.Name} 第 {k} 处：Pos {r.Pos}，源码偏移 {expected}");
        }
        return calls.Select(r => (r.Name, r.Context)).ToList();
    }

    [Fact]
    public void Go_ContextOfEachCall()
    {
        using var adapter = new GoAdapter();
        Assert.Equal(new (string, string?)[]
        {
            ("setup", None),
            ("forInit", None), ("forCond", None), ("forStep", None),   // 三段式 for 的头不算
            ("inFor", Loop),
            ("rangeSrc", None), ("inRange", Loop),                     // range 的被遍历对象不算
            ("whileCond", None), ("inWhile", Loop),
            ("inForever", Loop),
            ("ifInit", None), ("ifCond", None),                        // if 的初始化语句与条件不算
            ("inIf", Cond),
            ("elseIfCond", Cond),                                      // else if 的条件在外层 if 的 else 里
            ("inElseIf", Cond), ("inElse", Cond),
            ("switchVal", None), ("caseVal", Cond), ("inCase", Cond), ("inDefault", Cond),
            ("typeSwitchVal", None), ("inTypeCase", Cond),
            ("recvChan", None), ("inSelect", Cond),                    // select 的通信操作进 select 就求值
            ("sendVal", None), ("inSend", Cond), ("inSelectDefault", Cond),
            ("deferred", Callback), ("deferArg", None),                // defer 延后的是调用本身，实参当场求值
            ("spawned", Callback),
            ("inClosure", Callback),
            ("nestedCond", Loop), ("inLoopIf", Cond),                  // 嵌套取最内层
            ("inGoClosure", Callback),
            ("after", None),
            ("makeClient", None), ("chainA", None), ("chainB", None),   // 跨行链式调用：Pos 区分先后
            ("andLeft", None), ("andRight", None), ("orRight", None),   // && / || 的右侧不算 cond
        }, Calls(adapter, "flow.go", "Run"));
    }

    [Fact]
    public void Python_ContextOfEachCall()
    {
        using var adapter = new PythonAdapter();
        Assert.Equal(new (string, string?)[]
        {
            ("setup", None),
            ("for_src", None), ("in_for", Loop), ("for_else", Cond),   // for 头不算；for-else 没 break 才执行
            ("while_cond", None), ("in_while", Loop),
            ("if_cond", None), ("in_if", Cond), ("elif_cond", Cond), ("in_elif", Cond), ("in_else", Cond),
            ("tern_a", Cond), ("tern_cond", None), ("tern_b", Cond),   // 三元的条件不算
            ("list_body", Loop), ("list_src", None), ("list_filter", Loop),   // 推导式：可迭代对象只求值一次
            ("dict_key", Loop), ("dict_val", Loop), ("dict_src", None),
            ("set_body", Loop), ("set_src", None),
            ("gen_sum", None), ("gen_body", Loop), ("gen_src", None),
            ("in_try", Try), ("in_except", Catch), ("in_try_else", Try), ("in_finally", Try),
            ("match_subject", None), ("in_case", Cond), ("case_guard", Cond), ("in_guard_case", Cond),
            ("in_lambda", Callback),
            ("loop_if_cond", Loop), ("in_loop_if", Cond), ("in_loop_try", Try), ("in_loop_except", Catch),
            ("after", None),
            ("chain_a", None), ("chain_b", None),
            ("and_left", None), ("and_right", None), ("or_right", None),   // and / or 的右侧不算 cond
        }, Calls(adapter, "flow.py", "run"));
        // 嵌套 def 是独立符号：里面的调用归它，对它自己而言不在任何结构里
        Assert.Equal(new (string, string?)[] { ("in_nested", None) }, Calls(adapter, "flow.py", "nested"));
    }

    [Fact]
    public void Java_ContextOfEachCall()
    {
        using var adapter = new JavaAdapter();
        Assert.Equal(new (string, string?)[]
        {
            ("setup", None),
            ("forInit", None), ("forCond", None), ("forStep", None), ("inFor", Loop),
            ("forEachSrc", None), ("inForEach", Loop),
            ("whileCond", None), ("inWhile", Loop),
            ("inDo", Loop), ("doCond", None),
            ("ifCond", None), ("inIf", Cond), ("elseIfCond", Cond), ("inElseIf", Cond), ("inElse", Cond),
            ("switchVal", None), ("inCase", Cond), ("inDefault", Cond),
            ("switchExpr", None), ("inArrow", Cond), ("inYield", Cond),
            ("ternCond", None), ("ternA", Cond), ("ternB", Cond),
            ("openRes", Try), ("inTry", Try), ("inCatch", Catch), ("inFinally", Try),
            ("stream", None), ("map", None), ("inLambda", Callback), ("forEach", None), ("methodRef", Callback),   // Stream 的 lambda 是 callback 不是 loop
            ("Thread", None), ("threadArg", None), ("inAnon", Callback),                                        // 匿名类的方法体
            ("loopIfCond", Loop), ("inLoopIf", Cond), ("forEach", Loop), ("inLoopLambda", Callback),
            ("after", None),
            ("makeBuilder", None), ("chainA", None), ("chainB", None),
            ("andLeft", None), ("andRight", None), ("orRight", None),   // && / || 的右侧不算 cond
        }, Calls(adapter, "Flow.java", "run"));
        // 局部类是独立符号：它的方法是边界，里面的调用归它、不是 callback（匿名类的方法不是符号，才记 callback）
        Assert.Equal(new (string, string?)[] { ("inLocal", None) }, Calls(adapter, "Flow.java", "localMethod"));
    }
}
