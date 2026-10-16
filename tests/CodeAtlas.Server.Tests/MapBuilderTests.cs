using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>符号短名：取限定名里最后一个成员段（括号 / 尖括号里的点不算分隔符），顶级语句的显示名。</summary>
public class MapBuilderTests
{
    [Theory]
    // C# 局部函数：原来从第一个 '(' 截断得到外层方法名 Build
    [InlineData("Acme.Shop.FlowBuilder.Build(IReadOnlyList<MapSymbol>, int).Reach(MapSymbol, ArchNode)", "Reach")]
    [InlineData("Acme.Shop.Svc.Build(int).Reach(Node).Walk()", "Walk")]
    [InlineData("Acme.Shop.Svc.Build(int).Local<T>(T)", "Local")]
    // 顶级语句与它里面的局部函数：原来 <Main>$ 被 StripGenericArgs 剥成 "$"
    [InlineData("Program.<Main>$(string[])", "Main")]
    [InlineData("Program.<Main>$(string[]).Usage()", "Usage")]
    // 参数表里的点、泛型参数在中间、泛型方法、显式接口实现、运算符
    [InlineData("Acme.Shop.Repo.Find(System.String, Acme.Shop.Options)", "Find")]
    [InlineData("Acme.Shop.Repo<T>.Find(int)", "Find")]
    [InlineData("System.Linq.Enumerable.ToDictionary<TSource, TKey>(IEnumerable<TSource>, Func<TSource, TKey>)", "ToDictionary")]
    [InlineData("Acme.Shop.Repo.Acme.Shop.IRepo.Save()", "Save")]
    [InlineData("Acme.Shop.Money.operator +(Money, Money)", "operator +")]
    [InlineData("Acme.Shop.Money.operator <(Money, Money)", "operator <")]
    [InlineData("Dictionary<string, List<int>>", "Dictionary")]
    // 没有括号的（tree-sitter、Go 包目录、脚本里的成员）与原来一样取最后一段
    [InlineData("Acme.Shop.OrderService.Place", "Place")]
    [InlineData("src/pkg/v1.2.Handler.Serve", "Serve")]
    [InlineData("OrderTable.columns", "columns")]
    [InlineData("Run", "Run")]
    public void MemberName_IsTheLastMemberSegment(string qualifiedName, string expected) =>
        Assert.Equal(expected, MapBuilder.MemberName(qualifiedName));

    [Fact]
    public void MapSymbolName_UsesTheLastMemberSegment_AfterExternalAndScriptPathPrefixes()
    {
        Assert.Equal("Reach", new MapSymbol(1, SymbolKind.Function, "Acme.Svc.Build(int, string).Reach(Node)", "a/Svc.cs", 2).Name);
        Assert.Equal("Main", new MapSymbol(1, SymbolKind.Function, "Program.<Main>$(string[])", "a/Program.cs", 2).Name);
        Assert.Equal("Contains", new MapSymbol(1, SymbolKind.External, "external:System.Linq.Enumerable.Contains<TSource>(IEnumerable<TSource>, TSource)", "<external>", null).Name);
        Assert.Equal("useOrders", new MapSymbol(1, SymbolKind.Function, "web/(shop)/hooks.v2.ts:useOrders", "web/(shop)/hooks.v2.ts", null).Name);
        Assert.Equal("page.tsx", new MapSymbol(1, SymbolKind.Module, "web/app/[id]/page.tsx", "web/app/[id]/page.tsx", null).Name);
    }

    [Theory]
    [InlineData("Acme.Shop.FrontFlow.Build(Input)", "FrontFlow")]
    [InlineData("Acme.Shop.Repo<T>.Find(System.Int32)", "Repo")]
    [InlineData("Acme.Shop.Svc.Build(int).Reach(Node)", null)] // 前一段是外层方法：局部函数不补类型名
    [InlineData("Run()", null)]
    public void DeclaringTypeSegment_IsTheSegmentBeforeTheMember(string qualifiedName, string? expected) =>
        Assert.Equal(expected, MapBuilder.DeclaringTypeSegment(qualifiedName));

    [Theory]
    [InlineData("Program.<Main>$(string[])", "Program.Main")]
    [InlineData("Acme.Tool.Program.<Main>$(string[])", "Program.Main")]
    [InlineData("Program.<Main>$(string[]).Usage()", null)] // 顶级语句里的局部函数：显示自己的名字
    [InlineData("Acme.Shop.Program.Main(string[])", null)]
    public void TopLevelLabel_ForCompilerGeneratedMain(string qualifiedName, string? expected) =>
        Assert.Equal(expected, MapBuilder.TopLevelLabel(qualifiedName));
}
