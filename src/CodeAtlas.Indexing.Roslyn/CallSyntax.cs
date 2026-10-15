using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Indexing.Roslyn;

/// <summary>调用大纲用的纯语法判断：调用名的位置（edges.pos）与调用所在控制结构（edges.ctx）。不依赖语义模型，便于单测。</summary>
public static class CallSyntax
{
    /// <summary>
    /// 被调名字 token 的 SpanStart（UTF-16 偏移，0 起）：a.B().C() 取 C；obj?.M() 取 M；M&lt;T&gt;() 取 M；new Ns.T() 取 T；
    /// new() 没有类型名，取 new 关键字；认不出的被调表达式（委托调用 f()() 等）取表达式开头。
    /// </summary>
    public static int NamePos(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax inv => CalleePos(inv.Expression),
        ObjectCreationExpressionSyntax oc => TypeNamePos(oc.Type),
        ImplicitObjectCreationExpressionSyntax ioc => ioc.NewKeyword.SpanStart,
        _ => call.SpanStart,
    };

    private static int CalleePos(ExpressionSyntax e) => e switch
    {
        SimpleNameSyntax n => n.Identifier.SpanStart,                    // M() / M<T>()
        MemberAccessExpressionSyntax m => m.Name.Identifier.SpanStart,  // a.M() / a.B().M() / p->M()
        MemberBindingExpressionSyntax b => b.Name.Identifier.SpanStart, // x?.M() 里的 .M
        PostfixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.SuppressNullableWarningExpression) => CalleePos(p.Operand), // a.M!()
        ParenthesizedExpressionSyntax p => CalleePos(p.Expression),
        _ => e.SpanStart,
    };

    private static int TypeNamePos(TypeSyntax t) => t switch
    {
        SimpleNameSyntax n => n.Identifier.SpanStart,          // T / T<U>
        QualifiedNameSyntax q => q.Right.Identifier.SpanStart, // Ns.T
        AliasQualifiedNameSyntax a => a.Name.Identifier.SpanStart, // global::T
        _ => t.SpanStart,                                      // object / int? 等
    };

    /// <summary>
    /// 调用所在的最内层控制结构（<see cref="CallContext"/>），不在任何结构里为 null。从调用节点往上走，
    /// 到所属成员（类型成员、访问器、局部函数、顶级语句）为止；局部函数是独立符号，它体内的调用挂在它自己身上，所以是边界不是 callback。
    /// 规则（child 是路径上该祖先的直接子节点）：
    /// <list type="bullet">
    /// <item>loop：for / foreach / while / do 的循环体；循环头（初始化、条件、递增、集合表达式）不算；</item>
    /// <item>cond：if 的分支体与 else、switch section（含 case 标签与 when）、switch 表达式分支、?: 的两支、?? 与 ??= 的右侧、x?.M() 的 WhenNotNull；条件 / 被 switch 的表达式本身不算；</item>
    /// <item>try：try 块与 finally 块；catch：catch 块（filter 不算，按外层算）；</item>
    /// <item>callback：lambda、匿名方法、LINQ 查询体（第一个 from 的集合表达式之外的子句）。</item>
    /// </list>
    /// </summary>
    public static string? ContextOf(SyntaxNode call)
    {
        var child = call;
        for (var p = call.Parent; p is not null; child = p, p = p.Parent)
        {
            switch (p)
            {
                case MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or CompilationUnitSyntax:
                    return null;
                case AnonymousFunctionExpressionSyntax or QueryBodySyntax:
                    return CallContext.Callback;
                case ForStatementSyntax f when child == f.Statement:
                case CommonForEachStatementSyntax fe when child == fe.Statement:
                case WhileStatementSyntax w when child == w.Statement:
                case DoStatementSyntax d when child == d.Statement:
                    return CallContext.Loop;
                case IfStatementSyntax i when child == i.Statement || child == i.Else:
                case SwitchSectionSyntax or SwitchExpressionArmSyntax:
                case ConditionalExpressionSyntax c when child == c.WhenTrue || child == c.WhenFalse:
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) && child == b.Right:
                case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.CoalesceAssignmentExpression) && child == a.Right:
                case ConditionalAccessExpressionSyntax ca when child == ca.WhenNotNull:
                    return CallContext.Cond;
                case TryStatementSyntax t when child == t.Block || child == t.Finally:
                    return CallContext.Try;
                case CatchClauseSyntax cc when child == cc.Block:
                    return CallContext.Catch;
            }
        }
        return null;
    }
}
