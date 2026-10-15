namespace CodeAtlas.Core.Model;

/// <summary>解析器产出的符号，尚无数据库 id；ParentIndex 指向同一 ParsedFile.Symbols 的下标。</summary>
/// <param name="Visibility">C#：public / private / protected / internal；TS/JS：accessibility 修饰符，或 "export"、"export default"（默认导出，入口识别与 import 解析靠它找组件）。</param>
public sealed record ParsedSymbol(
    SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, int? ParentIndex, string? Visibility);

/// <summary>
/// 未解析的引用。FromIndex 是包含该引用的符号下标（文件级为 0，即 Module）。
/// Via 标记间接关系：
/// "handles"（Dispatches：该类型实现了 IXxxHandler&lt;Name, …&gt;，即它处理 Name）；
/// "render"（Calls：JSX 里用到组件 &lt;Name /&gt;，含成员式 &lt;X.Name /&gt; 与作为 prop 传进去的元素；
/// 以及 const X = memo(Y) / forwardRef(Y) 这类别名从 X 指向 Y）；
/// "event"（Calls：处理函数作为 JSX 的 on 开头属性传出去——onClick={handleX}、onClick={this.handleX}、
/// onSubmit={form.handleSubmit(onSubmit)} 里的实参标识符；只在解析到本仓库的函数 / 方法时建边，0.9，解析不到（多半是 prop）不建）；
/// "impl"（解析阶段派生：调用接口 / 抽象类成员时，仓库里唯一的实现类上的同名成员，见 ImplementationLinker 与脚本的构造注入）。
/// Script 是 TS/JS 的绑定信息（名字来自哪条 import、接收者是什么）；为 null 时解析器按名字启发式解析。
/// </summary>
/// <param name="Context">调用所在的最内层控制结构（<see cref="CallContext"/> 里的取值），不在任何结构里为 null。只对 calls 有意义。</param>
/// <param name="Pos">调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致）；-1 = 未知。调用大纲按它排先后：多行链式调用的行号都记成链开头那一行，只按（行，列）会排错。</param>
public sealed record ParsedReference(int FromIndex, string Name, EdgeKind Kind, int Line, int Col, string? Via = null, ScriptBinding? Script = null, string? Context = null,
    int Pos = -1);

/// <summary>
/// 调用所在的控制结构（edges.ctx），调用大纲在行尾标"循环内 / 条件内"用。取从调用节点往上、到所属成员为止遇到的最内层一个。
/// </summary>
public static class CallContext
{
    /// <summary>for / foreach / while / do，Python 推导式，Go 的 for。</summary>
    public const string Loop = "loop";
    /// <summary>if / else、switch / case、三元表达式、switch 表达式、模式匹配分支、Go 的 select。</summary>
    public const string Cond = "cond";
    /// <summary>try 块（含 finally）。</summary>
    public const string Try = "try";
    /// <summary>catch / except 块。</summary>
    public const string Catch = "catch";
    /// <summary>lambda、匿名函数、箭头函数、局部函数体里的调用：传出去当回调，执行时机不在当前顺序里。</summary>
    public const string Callback = "callback";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Loop, Cond, Try, Catch, Callback };
}

/// <summary>脚本引用的接收者形态。</summary>
public enum ScriptReceiver
{
    /// <summary>裸标识符：X()、new X()、&lt;X /&gt;、类型名 X。</summary>
    None,
    /// <summary>this.x() / super.x()。</summary>
    This,
    /// <summary>接收者是 import 进来的名字：NS.x()、&lt;NS.X /&gt;。</summary>
    Import,
    /// <summary>其他表达式：局部变量、参数、调用结果……类型未知。</summary>
    Other,
    /// <summary>
    /// 接收者是本类带类型的成员：this.svc.x()，svc 是构造参数属性（constructor(private svc: T)）、带类型标注的字段，
    /// 或构造函数里 this.svc = 带类型的参数。类型见 <see cref="ScriptBinding"/>。
    /// </summary>
    Typed,
}

/// <summary>
/// TS/JS 引用的绑定信息，由适配器按本文件的 import 语句与 CommonJS 的 require 算出
/// （const x = require('m') 记作 m 的 "default"；const { f, g: h } = require('m') 记作具名 f / g；require('m').f 同理）。
/// </summary>
/// <param name="ImportSource">
/// 名字（Receiver=None）或接收者（Receiver=Import）所绑定 import 的模块说明符，如 "@/hooks/x"、"./A"、"react"；没有绑定为 null。
/// Receiver=Typed 时是接收者类型的 import；Receiver=This 时是本类父类的 import（父类在本文件或没有父类时为 null）。
/// </param>
/// <param name="ImportedName">
/// 导出名："default"、"*"（命名空间导入），或具名导出的原名（import { A as B } 里是 A）。
/// Receiver=Typed / This 时是类型 / 父类的导出名；ImportSource 为 null 时是本文件里的类型名（This 没有父类时为 null）。
/// </param>
public sealed record ScriptBinding(ScriptReceiver Receiver, string? ImportSource = null, string? ImportedName = null);

/// <summary>import / using 记录。IsPath 为 true 表示相对路径（TS/JS），否则是命名空间（C#）或包名 / 路径别名（TS/JS 的 "react"、"@/x"）。</summary>
public sealed record ParsedImport(string Target, bool IsPath, int Line);

/// <summary>一个文件的解析结果。约定 Symbols[0] 是该文件的 Module 符号。</summary>
public sealed record ParsedFile(
    string RelativePath, string Language,
    IReadOnlyList<ParsedSymbol> Symbols,
    IReadOnlyList<ParsedReference> References,
    IReadOnlyList<ParsedImport> Imports);
