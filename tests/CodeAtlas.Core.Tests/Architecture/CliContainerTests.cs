using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>命令行工具也是可部署单元：package.json 有 bin 的 Node 包（node-cli，不要求 Dockerfile）、声明了 console_scripts 的 Python 项目。</summary>
public sealed class CliContainerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void NodeBinPackages_AreNodeCliContainers_ServicesWithDockerfileStayNodeService()
    {
        Write("tools/gen/package.json", """{ "name": "gen", "bin": { "gen": "bin/gen.js" }, "dependencies": { "commander": "12.0.0" } }""");
        Write("tools/gen/bin/gen.js", "const { program } = require('commander');\n");
        Write("tools/single/package.json", """{ "name": "single", "bin": "cli.js" }"""); // 字符串形式的 bin，没有 dependencies
        Write("svc/api/package.json", """{ "name": "api", "bin": { "api": "server.js" }, "dependencies": { "express": "4.19.0" } }""");
        Write("svc/api/Dockerfile", "FROM node:22\nEXPOSE 3000\n");                               // 有 Dockerfile 的服务：仍是 node-service
        Write("libs/util/package.json", """{ "name": "util", "dependencies": { "express": "4.19.0" } }"""); // 没有 bin 也没有 Dockerfile：不是容器

        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_root));
        Assert.Equal(new[] { ("svc/api", "node-service"), ("tools/gen", "node-cli"), ("tools/single", "node-cli") },
            containers.Select(c => (c.Key, c.SubKind)));
        var gen = containers.Single(c => c.Key == "tools/gen");
        Assert.Equal("gen", gen.Name);
        Assert.Equal(["tools/gen"], gen.Members);
        Assert.Contains(gen.Evidence, e => e.File == "tools/gen/package.json" && e.Note == "bin（命令行）");
    }

    [Fact]
    public void PythonConsoleScripts_MakeTheProjectDeployable()
    {
        Write("tool/pyproject.toml", "[project]\nname = \"tool\"\ndependencies = [\"click>=8\"]\n\n[project.scripts]\ntool = \"tool.cli:main\"\n");
        Write("tool/tool/cli.py", "import click\n\n@click.group()\ndef main():\n    pass\n");
        Write("legacy/setup.py", "from setuptools import setup\nsetup(\n    name='legacy',\n    entry_points={'console_scripts': ['legacy=legacy.main:run']},\n)\n");
        Write("legacy/legacy/main.py", "def run():\n    pass\n");
        Write("lib/pyproject.toml", "[project]\nname = \"lib\"\n");                 // 纯库：没有脚本、没有 __main__
        Write("lib/lib/core.py", "def f():\n    pass\n");

        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_root));
        Assert.Equal(new[] { "legacy", "tool" }, containers.Select(c => c.Key));
        Assert.All(containers, c => Assert.Equal("python-service", c.SubKind));
        Assert.Contains(containers.Single(c => c.Key == "tool").Evidence, e => e.File == "tool/pyproject.toml" && e.Line == 6 && e.Note == "console_scripts");
        Assert.Contains(containers.Single(c => c.Key == "legacy").Evidence, e => e.File == "legacy/setup.py" && e.Line == 4 && e.Note == "console_scripts");
    }
}
