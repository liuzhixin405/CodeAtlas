using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Server.Tests;

/// <summary>纯类库仓库（没有任何可部署容器）的整库 DSM：伪容器 "*" 的成员 = 非测试的项目目录；一个项目清单都没有时是整个仓库。</summary>
public partial class DsmTests
{
    [Fact]
    public void RepoContainer_MembersAreNonTestProjects_DsmShowsProjectDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeatlas-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string rel, string content)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            Write("Lib.Core/Lib.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("Lib.Client/Lib.Client.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Lib.Core\\Lib.Core.csproj\" /></ItemGroup></Project>");
            Write("Lib.Core.Tests/Lib.Core.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>");

            var repo = DsmBuilder.RepoContainer(ProjectRoots.Single(root));
            Assert.Equal((DsmBuilder.RepoKey, ArchNodeKind.Container, "repo"), (repo.Key, repo.Kind, repo.SubKind));
            Assert.Equal(["Lib.Client", "Lib.Core"], repo.Members); // 测试项目不算
            Assert.Equal(Path.GetFileName(root), repo.Name);

            List<MapSymbol> symbols =
            [
                new(1, SymbolKind.Namespace, "Lib.Core", "Lib.Core/Model.cs", null),
                new(2, SymbolKind.Class, "Lib.Core.Model", "Lib.Core/Model.cs", 1),
                new(3, SymbolKind.Namespace, "Lib.Client", "Lib.Client/Api.cs", null),
                new(4, SymbolKind.Class, "Lib.Client.Api", "Lib.Client/Api.cs", 3),
                new(5, SymbolKind.Class, "Lib.Core.Tests.ModelTests", "Lib.Core.Tests/ModelTests.cs", null),
            ];
            List<MapEdge> edges = [new(4, 2, EdgeKind.References, 0.9, null), new(5, 2, EdgeKind.Calls, 0.9, null)];
            var d = DsmBuilder.Build(symbols, edges, repo, "", null);
            Assert.Equal([("Lib.Core", "Core"), ("Lib.Client", "Client")], d.Components.Select(c => (c.Key, c.Label))); // 组件 = 项目目录，被依赖的在前；测试项目不在矩阵里
            Assert.Equal((1, 0, 1), (d.Cells.Single().Row, d.Cells.Single().Col, d.Cells.Single().Count));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RepoContainer_NoProjectManifests_IsTheWholeRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeatlas-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal([""], DsmBuilder.RepoContainer(ProjectRoots.Single(root)).Members);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
