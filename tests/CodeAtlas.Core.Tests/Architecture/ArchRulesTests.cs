using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests.Architecture;

public class ArchRulesTests
{
    /// <summary>ccs-backend PipelineArchitectureTests 的几种链写法（行号对齐：第 1 行是 using）。</summary>
    private static readonly string[] CcsLike =
    """
    using CentralizedConfigurationSystem.Application.Abstractions;
    using NetArchTest.Rules;

    namespace CentralizedConfigurationSystem.ArchTests;

    public class PipelineArchitectureTests
    {
        private const string ApplicationNamespace = "CentralizedConfigurationSystem.Application";
        private const string InfrastructureNamespace = "CentralizedConfigurationSystem.Infrastructure";
        private const string ApiNamespace = "CentralizedConfigurationSystem.Api";

        [Fact]
        public void Commands_ShouldResideInModulesNamespace()
        {
            var result = Types.InAssembly(typeof(ICommand<>).Assembly)
                .That()
                .ImplementInterface(typeof(ICommand<>))
                .And()
                .DoNotResideInNamespaceContaining("Abstractions")
                .Should()
                .ResideInNamespaceContaining("Modules")
                .GetResult();
            Assert.True(result.IsSuccessful, $"x {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }

        [Fact]
        public void Handlers_ShouldNotDependOnInfrastructure()
        {
            var result = Types.InAssembly(typeof(ICommandHandler<,>).Assembly)
                .That()
                .ImplementInterface(typeof(ICommandHandler<,>))
                .Or()
                .ImplementInterface(typeof(IQueryHandler<,>))
                .ShouldNot()
                .HaveDependencyOn(InfrastructureNamespace)
                .GetResult();
        }

        [Fact]
        public void Behaviors_ShouldResideInApplicationBehaviors()
        {
            var result = Types.InAssembly(typeof(IPipelineBehavior<,>).Assembly)
                .That().ImplementInterface(typeof(IPipelineBehavior<,>))
                .Should().ResideInNamespaceStartingWith($"{ApplicationNamespace}.Behaviors")
                .GetResult();
        }

        [Fact]
        public void ApprovableCommands_ShouldHaveTaskActionProperty()
        {
            var approvableTypes = Types.InAssembly(typeof(IApprovable).Assembly)
                .That()
                .ImplementInterface(typeof(IApprovable))
                .GetTypes();
        }

        [Fact]
        public void Application_ShouldNotDependOnApiOrInfrastructure()
        {
            // Types.InAssembly(typeof(ICommand<>).Assembly).ShouldNot().HaveDependencyOn("Commented").GetResult();
            var result = Types.InAssembly(typeof(ICommand<>).Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(ApiNamespace, InfrastructureNamespace)
                .GetResult();
        }

        [Fact]
        public void Domain_ShouldNotDependOnApplication_AndBeSealed()
        {
            var a = Types.InNamespace("CentralizedConfigurationSystem.Domain").Should().NotHaveDependencyOn(ApplicationNamespace).GetResult();
            var b = Types.InNamespace("CentralizedConfigurationSystem.Domain").That().HaveNameEndingWith("Entity").Should().BeSealed().GetResult();
            var c = Types.InNamespace("CentralizedConfigurationSystem.Api").Should().HaveDependencyOn(ApplicationNamespace).GetResult();
        }
    }
    """.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private sealed class FakeResolver : IArchTypeResolver
    {
        public string? NamespaceOf(string typeName) => typeName is "ICommand" or "ICommandHandler" or "IQueryHandler" or "IPipelineBehavior" or "IApprovable" ? "CentralizedConfigurationSystem.Application.Abstractions" : null;
        public string? AssemblyRootOf(string typeName) => NamespaceOf(typeName) is null ? null : "CentralizedConfigurationSystem.Application";
    }

    [Fact]
    public void Parse_CcsChains_WithResolver()
    {
        var r = ArchRuleParser.Parse("Tests/Arch/PipelineArchitectureTests.cs", CcsLike, new FakeResolver());
        var rules = r.Rules;

        // 第 1 条链：ImplementInterface + Should().ResideInNamespaceContaining → placement，from = 接口名
        var placement = rules.Single(x => x.TestName == "Commands_ShouldResideInModulesNamespace");
        Assert.Equal((ArchRuleKind.Placement, "ICommand", ArchRuleMatch.Exact, "Modules", ArchRuleMatch.Contains, 15), (placement.Kind, placement.FromPattern, placement.FromMatch, placement.ToPattern, placement.ToMatch, placement.Line));
        Assert.Contains("排除命名空间 Abstractions", placement.Source);

        // 跨多行 + Or() 两个接口 + 常量替换 → 一条 forbid，主语 = 程序集根命名空间，Source 注明接口限定
        var handlers = rules.Single(x => x.TestName == "Handlers_ShouldNotDependOnInfrastructure");
        Assert.Equal((ArchRuleKind.ForbidDependency, "CentralizedConfigurationSystem.Application", ArchRuleMatch.Prefix, "CentralizedConfigurationSystem.Infrastructure", ArchRuleMatch.Prefix), (handlers.Kind, handlers.FromPattern, handlers.FromMatch, handlers.ToPattern, handlers.ToMatch));
        Assert.Equal(29, handlers.Line);
        Assert.StartsWith("netarchtest", handlers.Source);
        Assert.Contains("ICommandHandler / IQueryHandler", handlers.Source);

        // 插值常量 $"{ApplicationNamespace}.Behaviors" → StartingWith 是 prefix
        var behaviors = rules.Single(x => x.TestName == "Behaviors_ShouldResideInApplicationBehaviors");
        Assert.Equal((ArchRuleKind.Placement, "IPipelineBehavior", "CentralizedConfigurationSystem.Application.Behaviors", ArchRuleMatch.Prefix), (behaviors.Kind, behaviors.FromPattern, behaviors.ToPattern, behaviors.ToMatch));

        // GetTypes() 的链不是规则
        Assert.DoesNotContain(rules, x => x.TestName == "ApprovableCommands_ShouldHaveTaskActionProperty");

        // HaveDependencyOnAny → 每个目标一条；注释里的链不算
        var any = rules.Where(x => x.TestName == "Application_ShouldNotDependOnApiOrInfrastructure").ToList();
        Assert.Equal(new[] { "CentralizedConfigurationSystem.Api", "CentralizedConfigurationSystem.Infrastructure" }, any.Select(x => x.ToPattern));
        Assert.All(any, x => Assert.Equal((ArchRuleKind.ForbidDependency, "CentralizedConfigurationSystem.Application", "netarchtest", 61), (x.Kind, x.FromPattern, x.Source, x.Line)));

        // 同一方法里三条链：Should().NotHaveDependencyOn → forbid；BeSealed 跳过计数；Should().HaveDependencyOn → require
        var domain = rules.Where(x => x.TestName == "Domain_ShouldNotDependOnApplication_AndBeSealed").ToList();
        Assert.Equal(2, domain.Count);
        Assert.Equal((ArchRuleKind.ForbidDependency, "CentralizedConfigurationSystem.Domain", "CentralizedConfigurationSystem.Application", 70), (domain[0].Kind, domain[0].FromPattern, domain[0].ToPattern, domain[0].Line));
        Assert.Equal((ArchRuleKind.RequireDependency, "CentralizedConfigurationSystem.Api", "CentralizedConfigurationSystem.Application", 72), (domain[1].Kind, domain[1].FromPattern, domain[1].ToPattern, domain[1].Line));
        Assert.Equal(1, r.SkippedPredicates);
        Assert.Equal(7, rules.Count);
    }

    [Fact]
    public void Parse_WithoutResolver_FallsBackToUniqueConstantMatchingUsings_OrAny()
    {
        // 文件里只有 ApplicationNamespace 是 using 的前缀 → InAssembly 猜成它
        var r = ArchRuleParser.Parse("t.cs", CcsLike);
        var handlers = r.Rules.Single(x => x.TestName == "Handlers_ShouldNotDependOnInfrastructure");
        Assert.Equal("CentralizedConfigurationSystem.Application", handlers.FromPattern);

        // 没有 using、没有常量 → * 并在 Source 里注明
        string[] bare =
        [
            "public class T {",
            "  public void A() {",
            "    var r = Types.InAssembly(typeof(Foo).Assembly).ShouldNot().HaveDependencyOn(\"X.Infra\").GetResult();",
            "  }",
            "}",
        ];
        var rule = Assert.Single(ArchRuleParser.Parse("t.cs", bare).Rules);
        Assert.Equal((ArchRule.Any, ArchRuleMatch.Prefix, "X.Infra", "A", 3), (rule.FromPattern, rule.FromMatch, rule.ToPattern, rule.TestName, rule.Line));
        Assert.Contains("无法解析", rule.Source);
        Assert.True(ArchRule.Matches("Anything.At.All", rule.FromPattern, rule.FromMatch));
    }

    [Fact]
    public void Matches_PrefixIsSegmentAware_AndCoarserComponentsAreNotJudged()
    {
        Assert.True(ArchRule.Matches("A.App", "A.App", ArchRuleMatch.Prefix));
        Assert.True(ArchRule.Matches("A.App.Modules", "A.App", ArchRuleMatch.Prefix));
        Assert.False(ArchRule.Matches("A.AppX", "A.App", ArchRuleMatch.Prefix));
        Assert.False(ArchRule.Matches("A.App", "A.App.Modules", ArchRuleMatch.Prefix));
        Assert.True(ArchRule.IsCoarserThan("A.App", "A.App.Modules", ArchRuleMatch.Prefix));
        Assert.False(ArchRule.IsCoarserThan("A.App.Modules", "A.App", ArchRuleMatch.Prefix));
        Assert.True(ArchRule.Matches("A.App.Modules.Orders", "Modules", ArchRuleMatch.Contains));
        Assert.False(ArchRule.Matches("A.App.Modules", "A.App", ArchRuleMatch.Exact));
    }

    [Fact]
    public void Yaml_LayersAndRules_BecomeForbidAndRequire()
    {
        var decl = ArchitectureDeclarations.Parse("""
            layers:
              - name: Api
                namespace: Acme.Api
              - name: Application
                namespace: Acme.App
              - name: Domain
                namespace: Acme.Domain
            rules:
              - forbid: "Acme.App -> Acme.Infra"
              - require: "Acme.Api -> Acme.App"
              - forbid: "no arrow here"
            """);
        var rules = decl.DependencyRules();
        Assert.Equal(5, rules.Count);
        Assert.Equal((ArchRuleKind.ForbidDependency, "Acme.App", "Acme.Infra", "yaml", ".codeatlas/architecture.yaml", 0), (rules[0].Kind, rules[0].FromPattern, rules[0].ToPattern, rules[0].Source, rules[0].File, rules[0].Line));
        Assert.Equal((ArchRuleKind.RequireDependency, "Acme.Api", "Acme.App"), (rules[1].Kind, rules[1].FromPattern, rules[1].ToPattern));
        // 下层不得依赖上层：App→Api，Domain→Api，Domain→App
        Assert.Equal(new[] { ("Acme.App", "Acme.Api"), ("Acme.Domain", "Acme.Api"), ("Acme.Domain", "Acme.App") }, rules.Skip(2).Select(r => (r.FromPattern, r.ToPattern!)));
        Assert.All(rules.Skip(2), r => Assert.Equal(ArchRuleKind.ForbidDependency, r.Kind));
    }

    [Fact]
    public void Scanner_OnlyReadsTestProjects_AndAppendsYaml()
    {
        using var fx = new ArchFixture();
        fx.Write("Acme.Tests/ArchTests.cs", "using NetArchTest.Rules;\npublic class A { public void T() { var r = Types.InNamespace(\"Acme.App\").ShouldNot().HaveDependencyOn(\"Acme.Api\").GetResult(); } }\n");
        fx.Write("Acme.Api/NotATest.cs", "public class B { public void T() { var r = Types.InNamespace(\"Acme.Api\").ShouldNot().HaveDependencyOn(\"Acme.Worker\").GetResult(); } }\n");
        fx.Write(".codeatlas/architecture.yaml", "rules:\n  - forbid: \"Acme.App -> Acme.Infra\"\n");
        var inv = ProjectInventory.Load(fx.Root);
        var report = ArchRuleScanner.Scan(inv, null, rel => File.ReadAllLines(Path.Combine(fx.Root, rel)), ArchitectureDeclarations.Load(fx.Root));
        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(new[] { ("Acme.App", "Acme.Api", "netarchtest"), ("Acme.App", "Acme.Infra", "yaml") }, report.Rules.Select(r => (r.FromPattern, r.ToPattern!, r.Source)));
        Assert.Equal("Acme.Tests/ArchTests.cs", report.Rules[0].File);
    }

    [Fact]
    public void Storage_RoundTrips_AndReplaceOverwrites()
    {
        using var db = IndexDatabase.OpenInMemory();
        var rules = new List<ArchRule>
        {
            new(ArchRuleKind.ForbidDependency, "A.App", ArchRuleMatch.Prefix, "A.Infra", ArchRuleMatch.Prefix, "T/Arch.cs", 12, "App_NotInfra", "netarchtest"),
            new(ArchRuleKind.Placement, "ICommand", ArchRuleMatch.Exact, "Modules", ArchRuleMatch.Contains, "T/Arch.cs", 30, "Cmd_Modules", "netarchtest（排除命名空间 Abstractions）"),
            new(ArchRuleKind.RequireDependency, "*", ArchRuleMatch.Prefix, null, null, ".codeatlas/architecture.yaml", 0, null, "yaml"),
        };
        ArchRuleStorage.Replace(db, rules);
        Assert.Equal(rules, ArchRuleStorage.Read(db));
        Assert.Equal(rules, ArchRuleStorage.Load(db));

        ArchRuleStorage.Replace(db, [rules[1]]);
        Assert.Equal([rules[1]], ArchRuleStorage.Read(db));
        ArchRuleStorage.Replace(db, []);
        Assert.Empty(ArchRuleStorage.Read(db));
    }
}
