using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>C# 控制台 / 宿主程序的启动入口（csharp.main，kind hosted，route = 容器名）：static Main 与顶级语句；一个容器只取一个（顶级语句优先，与编译器一致）；
/// 只看容器自己的项目目录（引用的类库里的 Main 不算），测试项目排除，只给 dotnet-exe / dotnet-worker / dotnet-web 容器。</summary>
public partial class EntryDiscoveryTests
{
    private static readonly Dictionary<string, string[]> MainFiles = new()
    {
        ["Tool/Program.cs"] =
        [
            "namespace Tool;",                                  // 1
            "public static class Program",                      // 2
            "{",                                                // 3
            "    public static int Main(string[] args)",        // 4
            "    {",                                            // 5
            "        return Run(args);",                        // 6
            "    }",                                            // 7
            "    public static int Run(string[] args) => 0;",   // 8
            "}",                                                // 9
        ],
        ["Tool/Helper.cs"] =
        [
            "namespace Tool;",                                  // 1
            "public class Helper { public void Main() { } }",   // 2：实例方法，不是入口
        ],
        ["Lib/Gen.cs"] =
        [
            "namespace Lib;",                                              // 1
            "static class Gen { static void Main(string[] args) { } }",    // 2：被 Tool 引用的类库里的 Main → 不算 Tool 的入口
        ],
        ["Web/Program.cs"] =
        [
            "var builder = WebApplication.CreateBuilder(args);", // 1
            "var app = builder.Build();",                        // 2
            "app.Run();",                                        // 3
        ],
        ["Both/Program.cs"] =
        [
            "Console.WriteLine(1);",                             // 1
        ],
        ["Both/Legacy.cs"] =
        [
            "class Legacy {",                                    // 1
            "    static void Main(string[] args) { }",           // 2：和顶级语句同在一个项目 → 只取顶级语句
            "}",                                                 // 3
        ],
        ["Worker/Program.cs"] =
        [
            "namespace Worker;",                                 // 1
            "internal static class Program",                     // 2
            "{",                                                 // 3
            "    private static async Task Main(string[] args)", // 4
            "    { await Host.RunAsync(); }",                    // 5
            "}",                                                 // 6
        ],
        ["Tool.Tests/ProgramTests.cs"] =
        [
            "public static class Entry { public static void Main(string[] args) { } }", // 1：测试项目
        ],
    };

    private static readonly List<MapSymbol> MainSymbols =
    [
        Sym(2000, SymbolKind.Class, "Tool.Program", "Tool/Program.cs", null, 2, 9, "public"),
        Sym(2001, SymbolKind.Method, "Tool.Program.Main(string[])", "Tool/Program.cs", 2000, 4, 7, "public"),
        Sym(2002, SymbolKind.Method, "Tool.Program.Run(string[])", "Tool/Program.cs", 2000, 8, 8, "public"),
        Sym(2003, SymbolKind.Class, "Tool.Helper", "Tool/Helper.cs", null, 2, 2, "public"),
        Sym(2004, SymbolKind.Method, "Tool.Helper.Main()", "Tool/Helper.cs", 2003, 2, 2, "public"),
        Sym(2005, SymbolKind.Class, "Lib.Gen", "Lib/Gen.cs", null, 2, 2, "internal"),
        Sym(2006, SymbolKind.Method, "Lib.Gen.Main(string[])", "Lib/Gen.cs", 2005, 2, 2, "private"),
        Sym(2009, SymbolKind.Module, "Web/Program.cs", "Web/Program.cs", null, 1, 3),
        Sym(2010, SymbolKind.Function, "Program.<Main>$(string[])", "Web/Program.cs", 2009, 1, 3, "internal"), // Roslyn 给顶级语句的限定名；没有父符号时入库挂到文件模块上
        Sym(2011, SymbolKind.Function, "Program.<Main>$(string[]).Usage()", "Web/Program.cs", 2010, 3, 3, "internal"), // 顶级语句里的局部函数：不是入口
        Sym(2020, SymbolKind.Function, "<top-level>", "Both/Program.cs", null, 1, 1, "internal"),
        Sym(2021, SymbolKind.Class, "Legacy", "Both/Legacy.cs", null, 1, 3, "internal"),
        Sym(2022, SymbolKind.Method, "Legacy.Main(string[])", "Both/Legacy.cs", 2021, 2, 2, "private"),
        Sym(2030, SymbolKind.Class, "Worker.Program", "Worker/Program.cs", null, 2, 6, "internal"),
        Sym(2031, SymbolKind.Method, "Worker.Program.Main(string[])", "Worker/Program.cs", 2030, 4, 5, "private"),
        Sym(2040, SymbolKind.Class, "Entry", "Tool.Tests/ProgramTests.cs", null, 1, 1, "public"),
        Sym(2041, SymbolKind.Method, "Entry.Main(string[])", "Tool.Tests/ProgramTests.cs", 2040, 1, 1, "public"),
    ];

    private static readonly List<ArchNode> MainContainers =
    [
        Container("Tool", "dotnet-exe", "Tool", "Lib"),
        Container("Web", "dotnet-web", "Web"),
        Container("Both", "dotnet-exe", "Both"),
        Container("Worker", "dotnet-worker", "Worker"),
        Container("Tool.Tests", "dotnet-exe", "Tool.Tests", "Tool"),
        Container("Lib", "frontend", "Lib"), // 不是 .NET 宿主容器：不给 Main 入口
    ];

    [Fact]
    public void CSharpMain_StaticMainOrTopLevelStatements_OnePerHostContainer()
    {
        var entries = EntryDiscovery.Discover(MainSymbols, [], MainContainers, p => MainFiles.TryGetValue(p, out var l) ? l : [])
            .Where(e => e.RuleId == "csharp.main").ToList();
        Assert.Equal(new[]
        {
            ("Both", "Both", 2020L, "Both/Program.cs", 1),       // 顶级语句优先于同项目的 Main
            ("Tool", "Tool", 2001L, "Tool/Program.cs", 4),       // static int Main(string[])；实例方法 Main() 与类库 Lib 里的 Main 不算
            ("Web", "Web", 2010L, "Web/Program.cs", 1),          // Web 项目的 Program.cs 顶级语句：启动流程
            ("Worker", "Worker", 2031L, "Worker/Program.cs", 4), // static async Task Main
        }, entries.Select(e => (e.ContainerKey, e.Route!, e.SymbolId, e.File, e.Line)));
        Assert.All(entries, e => Assert.Equal(("hosted", (string?)null), (e.Kind, e.HttpMethod)));
        Assert.DoesNotContain(entries, e => e.ContainerKey == "Tool.Tests" || e.SymbolId is 2004 or 2006 or 2022 or 2041);
    }
}
