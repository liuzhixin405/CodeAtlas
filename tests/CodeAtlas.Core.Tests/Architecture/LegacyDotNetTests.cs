using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// .NET Framework 旧式项目：csproj 带 MSBuild 2003 命名空间、没有 Sdk 属性；包在同目录 packages.config 里，
/// &lt;Reference&gt; 里混着框架程序集（System / System.Core）和 packages\ 下的 NuGet 程序集，只有真正的本地程序集才是厂商 SDK。
/// </summary>
public sealed class LegacyDotNetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyDotNetTests()
    {
        // 行号：原始字符串去掉公共缩进后，第 1 行是 <?xml …?>
        Write("legacy/Old.Service/Old.Service.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="System.Core" />
                <Reference Include="Npgsql, Version=4.1.0.0, Culture=neutral, PublicKeyToken=5d8b90d52f46fda7, processorArchitecture=MSIL">
                  <HintPath>..\packages\Npgsql.4.1.3\lib\net45\Npgsql.dll</HintPath>
                </Reference>
                <Reference Include="Vendor.Sdk64, Version=1.0.0.0, Culture=neutral, processorArchitecture=AMD64">
                  <HintPath>..\libs\Vendor.Sdk64.dll</HintPath>
                </Reference>
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Old.Data\Old.Data.csproj">
                  <Project>{B58D16AD-9987-4BB8-8BB2-2A0CE8BC9829}</Project>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);
        Write("legacy/Old.Service/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Npgsql" version="4.1.3" targetFramework="net45" />
              <package id="StackExchange.Redis" version="1.2.6" targetFramework="net45" />
              <package id="Newtonsoft.Json" version="12.0.3" targetFramework="net45" />
            </packages>
            """);
        Write("legacy/Old.Data/Old.Data.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);
        Write("legacy/Old.Data/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="RabbitMQ.Client" version="5.1.2" targetFramework="net472" />
            </packages>
            """);
        // 旧式 ASP.NET Web 应用：OutputType 是 Library，靠 ProjectTypeGuids 里的 Web Application 类型认出来
        Write("legacy/Old.Web/Old.Web.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids>
                <OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.6.1</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Inventory_LegacyCsproj_ReadsNamespacedElements_PackagesConfig_AndFiltersFrameworkAndNuGetReferences()
    {
        var inv = ProjectInventory.Load(_root);
        var svc = inv.Projects.Single(p => p.Dir == "legacy/Old.Service");
        Assert.Equal((null, "Exe", "v4.5", false), (svc.Sdk, svc.OutputType, svc.TargetFramework, svc.IsTest));
        Assert.Equal(["legacy/Old.Data"], svc.ProjectRefs);
        // packages.config：行号与文件都指向 packages.config
        Assert.Equal(new (string, int, string?)[]
        {
            ("Npgsql", 3, "legacy/Old.Service/packages.config"),
            ("StackExchange.Redis", 4, "legacy/Old.Service/packages.config"),
            ("Newtonsoft.Json", 5, "legacy/Old.Service/packages.config"),
        }, svc.Packages.Select(p => (p.Name, p.Line, p.File)));
        // System / System.Core 是框架程序集，packages\ 下的 Npgsql 是 NuGet 包：都不是本地 SDK；强名称只取逗号前的程序集名
        var local = Assert.Single(svc.LocalReferences);
        Assert.Equal(("Vendor.Sdk64", @"..\libs\Vendor.Sdk64.dll", 13), (local.Name, local.HintPath, local.Line));

        Assert.Equal("v4.7.2", inv.Projects.Single(p => p.Dir == "legacy/Old.Data").TargetFramework);
    }

    [Fact]
    public void Containers_LegacyExeAndLegacyWeb_EvidenceShowsFrameworkVersion()
    {
        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_root));
        Assert.Equal(["legacy/Old.Service", "legacy/Old.Web"], containers.Select(c => c.Key));
        var svc = containers[0];
        Assert.Equal(("dotnet-exe", "Old.Service"), (svc.SubKind, svc.Name));
        Assert.Equal(["legacy/Old.Data", "legacy/Old.Service"], svc.Members);
        Assert.Equal(new Evidence("legacy/Old.Service/Old.Service.csproj", null, "OutputType=Exe，.NET Framework v4.5"), svc.Evidence[0]);
        var web = containers[1];
        Assert.Equal("dotnet-web", web.SubKind);
        Assert.Equal(new Evidence("legacy/Old.Web/Old.Web.csproj", null, "ProjectTypeGuids=Web Application，.NET Framework v4.6.1"), web.Evidence[0]);
    }

    [Fact]
    public void Scan_PackagesConfig_MapsToExternals_ThroughMemberClosure()
    {
        var model = ArchitectureScanner.Scan(_root);
        var edges = model.Edges.Select(e => $"{e.FromKey} → {e.ToKey} {e.Protocol} {string.Join(",", e.Evidence.Select(x => $"{x.File}:{x.Line}"))}").ToList();
        Assert.Equal(
        [
            "legacy/Old.Service → external:broker:rabbitmq amqp legacy/Old.Data/packages.config:3", // 库项目的 packages.config 经项目引用并进来
            "legacy/Old.Service → external:cache:redis redis legacy/Old.Service/packages.config:4",
            "legacy/Old.Service → external:database:postgresql sql legacy/Old.Service/packages.config:3",
            "legacy/Old.Service → external:native-sdk:vendor-sdk native legacy/Old.Service/Old.Service.csproj:13",
        ], edges);
        Assert.Equal("Vendor.Sdk", model.Nodes.Single(n => n.Key == "external:native-sdk:vendor-sdk").Name); // 64 后缀照旧去掉
    }
}

/// <summary>同一目录放了多个 csproj（Bing.NetCore 的 framework/tests/Bing.AspNetCore.Mvc.Tests 就是这样）：不能崩，一个目录只出一个容器。</summary>
public sealed class SameDirectoryProjectsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-samedir-" + Guid.NewGuid().ToString("N"));

    public SameDirectoryProjectsTests()
    {
        Write("dup/A.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.3" /><ProjectReference Include="..\tlib\TLib.csproj" /></ItemGroup></Project>""");
        Write("dup/B.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><ProjectReference Include="..\lib\Lib.csproj" /></ItemGroup></Project>""");
        Write("dup/C.Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\lib2\Lib2.csproj" /></ItemGroup></Project>""");
        Write("lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("lib2/Lib2.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("tlib/TLib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        // 测试包写在共享 props 里的 Web 测试项目（Bing.NetCore 的写法）：不是容器
        Write("tests/X.Mvc.Tests/X.Mvc.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><Import Project="..\..\framework.tests.props" /></Project>""");
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Containers_TwoExecutablesInOneDirectory_OneContainer_DeployableFirstThenByName_MembersMerged()
    {
        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_root));
        var c = Assert.Single(containers);
        // 可部署的 B.Web 与 C.Tool 里按名字取 B.Web；成员合并同目录非测试项目的引用（A.Tests 的 tlib 不算）
        Assert.Equal(("dup", "B.Web", "dotnet-web"), (c.Key, c.Name, c.SubKind));
        Assert.Equal(["dup", "lib", "lib2"], c.Members);
        Assert.Equal("dup/B.Web.csproj", c.Evidence[0].File);
    }
}
