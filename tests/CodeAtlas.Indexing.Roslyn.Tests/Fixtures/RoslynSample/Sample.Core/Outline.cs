namespace Sample.Core;

// 调用大纲夹具：每个被调方法名全文件只出现一次（声明在 Sink 里，调用写成 s.名字），测试用 源码.IndexOf("." + 名字 + "(") 定位。
// 中文注释行：偏移按 UTF-16 字符计，这里的汉字每个算 1，不能按 UTF-8 字节数算错。
public class Sink
{
    public bool Head() => true;
    public bool InIf() => true;
    public bool InElse() => true;
    public bool ElseIfHead() => true;
    public int ForInit() => 0;
    public bool ForCond(int i) => i < 1;
    public int ForStep(int i) => i + 1;
    public void ForBody() { }
    public int[] Items() => [];
    public void EachBody(int x) { }
    public bool WhileHead() => false;
    public void WhileBody() { }
    public void DoBody() { }
    public bool DoHead() => false;
    public int SwitchOn() => 0;
    public void InCase() { }
    public int ArmValue() => 0;
    public int Pick() => 0;
    public int WhenYes() => 1;
    public int WhenNo() => 2;
    public string? MaybeNull() => null;
    public string Fallback() => "";
    public Sink? Next() => null;
    public void AfterNullCheck() { }
    public void InTry() { }
    public void InCatch() { }
    public bool InFilter() => true;
    public void InFinally() { }
    public void Nested() { }
    public int InLambda(int x) => x;
    public void InAnonymous() { }
    public void InLocal() { }
    public void Plain() { }
    public Sink Chain() => this;
    public void Tail() { }
    public T Generic<T>() => default!;
}

public class Outline
{
    public void Run(Sink s)
    {
        s.Plain();
        if (s.Head()) s.InIf();
        else if (s.ElseIfHead()) s.InElse();
        for (var i = s.ForInit(); s.ForCond(i); i = s.ForStep(i)) s.ForBody();
        foreach (var x in s.Items()) s.EachBody(x);
        while (s.WhileHead()) s.WhileBody();
        do { s.DoBody(); } while (s.DoHead());
        switch (s.SwitchOn())
        {
            case 1: s.InCase(); break;
        }
        var arm = s.Pick() switch { 1 => s.ArmValue(), _ => 0 };
        var t = s.Pick() > 0 ? s.WhenYes() : s.WhenNo();
        var u = s.MaybeNull() ?? s.Fallback();
        s.Next()?.AfterNullCheck();
        try { s.InTry(); }
        catch (Exception) when (s.InFilter()) { s.InCatch(); }
        finally { s.InFinally(); }
        foreach (var y in s.Items())
        {
            if (y > 0) s.Nested();
        }
        Func<int, int> f = v => s.InLambda(v);
        Action g = delegate { s.InAnonymous(); };
        void Local() { s.InLocal(); }
        Local();
        s.Chain()
            .Tail();
        var gen = s.Generic<int>();
        var created = new List<int>();
    }
}
