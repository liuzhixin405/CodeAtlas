// 调用大纲夹具：中文注释放在最前面，验证 Pos 是 UTF-16 字符偏移而不是 UTF-8 字节偏移 😀
using System;

namespace Outline;

/// <summary>订单处理（中文）</summary>
public class Handler
{
    private readonly Lazy<int> _lazy = new(() => FieldInit());

    public int Prop
    {
        get { if (PropCond()) return PropThen(); return 0; }
    }

    public Handler() { CtorCall(); }

    public void Handle(Item[] items, Item? a)
    {
        Start();
        for (var i = ForInit(); ForCond(i); ForStep()) { ForBody(); }
        foreach (var x in ForeachColl()) ForeachBody();
        while (WhileCond()) { WhileBody(); }
        do { DoBody(); } while (DoCond());
        if (IfCond()) IfThen(); else if (ElifCond()) ElifThen(); else ElseBody();
        switch (SwitchValue()) { case 1: CaseBody(); break; default: DefaultBody(); break; }
        var t = TernCond() ? TernA() : TernB();
        var s = t switch { 1 => ArmOne(), _ => ArmOther() };
        var n = CoalLeft() ?? CoalRight();
        var both = AndLeft() && AndRight();
        a?.SafeFirst().SafeThen();
        try { TryBody(); } catch (Exception) when (CatchFilter()) { CatchBody(); } finally { FinallyBody(); }
        Run(() => LambdaBody());
        Run(delegate { AnonBody(); });
        foreach (var item in items)
        {
            if (item.Valid) { NestedIfInLoop(); }
            NestedLoopBody();
            Run(() => LambdaInLoop());
        }
        int Local() => LocalBody();
        new Foo.Bar.Baz();
        Chain().Mid().
            Tail();
        End();
    }
}
