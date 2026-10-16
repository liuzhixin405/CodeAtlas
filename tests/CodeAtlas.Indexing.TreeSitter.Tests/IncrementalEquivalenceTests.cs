using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 增量索引（只重解析受影响文件的引用）与全量索引的等价性。每个场景：全量 → 改动 → 增量，再和两个参照比边集合：
/// 1. 旧行为（增量写入、重解析全部引用 ResolveAllReferences）——必须逐条一致；
/// 2. 对改后内容新建库全量索引——除悬空边外一致（删掉的文件被别处引用时，增量保留悬空边等文件回来再挂上，
///    全量没有这些边、改记 external；这是 ReconcileResolvedReferences 的既有取舍，与本次增量无关）。
/// </summary>
public class IncrementalEquivalenceTests
{
    private sealed record Scenario(Dictionary<string, string> Before, Dictionary<string, string?> Changes);

    private static readonly Dictionary<string, Dictionary<string, string>> Repos = new()
    {
        ["ts"] = new()
        {
            ["src/a.ts"] = "export function helper(x: number): number { return x + 1; }\nexport function other(): number { return 2; }\n",
            ["src/b.ts"] = "import { helper } from './a';\nimport { Thing } from './lib';\nexport function main(): number { return helper(1) + shared() + new Thing().go(); }\n",
            ["src/c.ts"] = "export function shared(): number { return 3; }\n",
            ["src/lib/index.ts"] = "export class Thing { go(): number { return 1; } }\n",
            ["src/d.ts"] = "import { later } from './later';\nexport function useLater(): number { return later(); }\n",
        },
        ["tsx"] = new()
        {
            ["src/Button.tsx"] = "export function Button() { return <button>ok</button>; }\n",
            ["src/App.tsx"] = "import { Button } from './Button';\nimport { Card } from './widgets';\nexport function App() { return <div><Button /><Card />{format()}</div>; }\n",
            ["src/format.tsx"] = "export function format() { return 'x'; }\n",
            ["src/widgets/index.tsx"] = "export function Card() { return <div />; }\n",
            ["src/Page.tsx"] = "import { Later } from './Later';\nexport function Page() { return <Later />; }\n",
        },
        ["js"] = new()
        {
            ["src/a.js"] = "export function helper(x) { return x + 1; }\n",
            ["src/b.js"] = "import { helper } from './a';\nimport { Thing } from './lib';\nexport function main() { return helper(1) + shared() + new Thing().go(); }\n",
            ["src/c.js"] = "export function shared() { return 3; }\n",
            ["src/lib/index.js"] = "export class Thing { go() { return 1; } }\n",
            ["src/d.js"] = "const { later } = require('./later');\nfunction useLater() { return later(); }\nmodule.exports = { useLater };\n",
        },
        ["go"] = new()
        {
            ["go.mod"] = "module example.com/m\n\ngo 1.21\n",
            ["util/util.go"] = "package util\n\nfunc Helper(x int) int { return x + 1 }\n\ntype Inner struct{}\n\nfunc (i *Inner) Work() int { return 1 }\n",
            ["model/outer.go"] = "package model\n\nimport \"example.com/m/util\"\n\ntype Outer struct {\n\tutil.Inner\n}\n",
            ["main.go"] = """
                package main

                import (
                	"example.com/m/later"
                	"example.com/m/model"
                	"example.com/m/util"
                )

                type A struct{}

                func (a *A) Do() int { return 1 }

                func getAny() interface{} { return nil }

                func run(o *model.Outer) int {
                	return util.Helper(1) + o.Work() + later.Later() + getAny().Do()
                }

                """,
        },
        ["python"] = new()
        {
            ["pkg/__init__.py"] = "from .util import helper\n",
            ["pkg/util.py"] = "def helper(x):\n    return x + 1\n",
            ["base.py"] = "class Base:\n    def run(self):\n        return 1\n",
            ["child.py"] = "from base import Base\n\n\nclass Child(Base):\n    pass\n",
            ["main.py"] = """
                from pkg import helper
                from child import Child
                from later import later_fn


                class A:
                    def process(self):
                        return 1


                def main(obj):
                    return helper(1) + Child().run() + later_fn() + obj.process()

                """,
        },
        ["java"] = new()
        {
            ["com/x/Util.java"] = "package com.x;\n\npublic class Util {\n    public static int helper(int x) { return x + 1; }\n}\n",
            ["com/x/Base.java"] = "package com.x;\n\npublic class Base {\n    public int run() { return 1; }\n}\n",
            ["com/x/Child.java"] = "package com.x;\n\npublic class Child extends Base {\n}\n",
            ["com/y/Shared.java"] = "package com.y;\n\npublic class Shared {\n    public int shared() { return 1; }\n}\n",
            ["com/y/Main.java"] = """
                package com.y;

                import com.x.Util;
                import com.x.Child;
                import com.x.Later;

                public class Main {
                    public int main(Object obj) {
                        return Util.helper(1) + new Child().run() + new Later().go() + unknown.shared();
                    }
                }

                """,
        },
        ["csharp"] = new()
        {
            ["src/Util.cs"] = "namespace Acme.Core;\npublic static class Util { public static int Helper(int x) => x + 1; }\n",
            ["src/Base.cs"] = "namespace Acme.Core;\npublic class Base { public int Run() => 1; }\n",
            ["src/Child.cs"] = "namespace Acme.Core;\npublic class Child : Base { }\n",
            ["src/Shared.cs"] = "namespace Acme.Core;\npublic class SharedOne { public int Shared() => 1; }\n",
            ["src/Main.cs"] = """
                using Acme.Core;
                using Acme.Later;
                namespace Acme.App;
                public class Query {}
                public interface IRequestHandler<T> {}
                public class Main {
                    public int Run(dynamic obj) {
                        Send(new Query());
                        return Util.Helper(1) + new Child().Run() + new LaterThing().Go() + obj.Shared();
                    }
                    public void Send(object message) {}
                }

                """,
        },
    };

    private static readonly Dictionary<string, Dictionary<string, string?>> Changes = new()
    {
        // TypeScript
        ["ts/body"] = new() { ["src/a.ts"] = "export function helper(x: number): number { return x + 100; }\nexport function other(): number { return 2; }\n" },
        ["ts/rename"] = new() { ["src/a.ts"] = "export function helperRenamed(x: number): number { return x + 1; }\nexport function other(): number { return 2; }\n" },
        ["ts/caller"] = new() { ["src/b.ts"] = "import { helper } from './a';\nimport { Thing } from './lib';\nexport function main(): number { return helper(2) + shared() + new Thing().go() + other(); }\n" },
        ["ts/same-name"] = new() { ["src/e.ts"] = "export function shared(): number { return 4; }\n" },
        ["ts/add-imported"] = new() { ["src/later.ts"] = "export function later(): number { return 5; }\n" },
        ["ts/delete-imported"] = new() { ["src/a.ts"] = null },
        ["ts/delete-index"] = new() { ["src/lib/index.ts"] = null },
        // TSX
        ["tsx/body"] = new() { ["src/Button.tsx"] = "export function Button() { return <button>okay then</button>; }\n" },
        ["tsx/rename"] = new() { ["src/Button.tsx"] = "export function PrimaryButton() { return <button>ok</button>; }\n" },
        ["tsx/caller"] = new() { ["src/App.tsx"] = "import { Button } from './Button';\nimport { Card } from './widgets';\nexport function App() { return <section><Button /><Card />{format()}</section>; }\n" },
        ["tsx/same-name"] = new() { ["src/format2.tsx"] = "export function format() { return 'y'; }\n" },
        ["tsx/add-imported"] = new() { ["src/Later.tsx"] = "export function Later() { return <span />; }\n" },
        ["tsx/delete-imported"] = new() { ["src/Button.tsx"] = null },
        ["tsx/delete-index"] = new() { ["src/widgets/index.tsx"] = null },
        // JavaScript
        ["js/body"] = new() { ["src/a.js"] = "export function helper(x) { return x + 100; }\n" },
        ["js/rename"] = new() { ["src/a.js"] = "export function helperRenamed(x) { return x + 1; }\n" },
        ["js/caller"] = new() { ["src/b.js"] = "import { helper } from './a';\nimport { Thing } from './lib';\nexport function main() { return helper(2) + shared() + new Thing().go(); }\n" },
        ["js/same-name"] = new() { ["src/e.js"] = "export function shared() { return 4; }\n" },
        ["js/add-imported"] = new() { ["src/later.js"] = "function later() { return 5; }\nmodule.exports = { later };\n" },
        ["js/delete-imported"] = new() { ["src/a.js"] = null },
        ["js/delete-index"] = new() { ["src/lib/index.js"] = null },
        // Go
        ["go/body"] = new() { ["util/util.go"] = "package util\n\nfunc Helper(x int) int { return x + 100 }\n\ntype Inner struct{}\n\nfunc (i *Inner) Work() int { return 1 }\n" },
        ["go/rename"] = new() { ["util/util.go"] = "package util\n\nfunc Helper2(x int) int { return x + 1 }\n\ntype Inner struct{}\n\nfunc (i *Inner) Work() int { return 1 }\n" },
        ["go/caller"] = new() { ["main.go"] = Repos["go"]["main.go"] + "\nfunc extra(o *model.Outer) int { return o.Work() }\n" },
        ["go/same-name"] = new() { ["b.go"] = "package main\n\ntype B struct{}\n\nfunc (b *B) Do() int { return 2 }\n" },
        ["go/add-imported"] = new() { ["later/later.go"] = "package later\n\nfunc Later() int { return 5 }\n" },
        ["go/delete-imported"] = new() { ["util/util.go"] = null },
        ["go/embed"] = new() { ["model/outer.go"] = "package model\n\nimport \"example.com/m/util\"\n\ntype Outer struct {\n\t*util.Inner\n\tName string\n}\n" },
        // Python
        ["python/body"] = new() { ["pkg/util.py"] = "def helper(x):\n    return x + 100\n" },
        ["python/rename"] = new() { ["pkg/util.py"] = "def helper2(x):\n    return x + 1\n" },
        ["python/caller"] = new() { ["main.py"] = Repos["python"]["main.py"] + "\ndef extra():\n    return Child().run()\n" },
        ["python/same-name"] = new() { ["other.py"] = "class B:\n    def process(self):\n        return 2\n" },
        ["python/add-imported"] = new() { ["later.py"] = "def later_fn():\n    return 5\n" },
        ["python/delete-imported"] = new() { ["pkg/util.py"] = null },
        ["python/reexport"] = new() { ["pkg/__init__.py"] = "from .util import helper as helper\nfrom base import Base\n" },
        ["python/base"] = new() { ["child.py"] = "from base import Base\n\n\nclass Child:\n    def run(self):\n        return 3\n" },
        // Java
        ["java/body"] = new() { ["com/x/Util.java"] = "package com.x;\n\npublic class Util {\n    public static int helper(int x) { return x + 100; }\n}\n" },
        ["java/rename"] = new() { ["com/x/Util.java"] = "package com.x;\n\npublic class Util {\n    public static int helper2(int x) { return x + 1; }\n}\n" },
        ["java/caller"] = new() { ["com/y/Main.java"] = Repos["java"]["com/y/Main.java"].Replace("Util.helper(1)", "Util.helper(2) + new Child().run()") },
        ["java/same-name"] = new() { ["com/z/Other.java"] = "package com.z;\n\npublic class Other {\n    public int shared() { return 2; }\n}\n" },
        ["java/add-imported"] = new() { ["com/x/Later.java"] = "package com.x;\n\npublic class Later {\n    public int go() { return 5; }\n}\n" },
        ["java/delete-imported"] = new() { ["com/x/Util.java"] = null },
        ["java/base"] = new() { ["com/x/Child.java"] = "package com.x;\n\npublic class Child {\n    public int run() { return 3; }\n}\n" },
        // C#
        ["csharp/body"] = new() { ["src/Util.cs"] = "namespace Acme.Core;\npublic static class Util { public static int Helper(int x) => x + 100; }\n" },
        ["csharp/rename"] = new() { ["src/Util.cs"] = "namespace Acme.Core;\npublic static class Util { public static int Helper2(int x) => x + 1; }\n" },
        ["csharp/caller"] = new() { ["src/Main.cs"] = Repos["csharp"]["src/Main.cs"].Replace("Util.Helper(1)", "Util.Helper(2) + new Child().Run()") },
        ["csharp/same-name"] = new() { ["src/Other.cs"] = "namespace Acme.Other;\npublic class SharedTwo { public int Shared() => 2; }\n" },
        ["csharp/add-imported"] = new() { ["src/Later.cs"] = "namespace Acme.Later;\npublic class LaterThing { public int Go() => 5; }\n" },
        ["csharp/delete-imported"] = new() { ["src/Util.cs"] = null },
        ["csharp/handler"] = new() { ["src/Handler.cs"] = "namespace Acme.App;\npublic class Handler : IRequestHandler<Query> { public void Handle(Query q) {} }\n" },
        ["csharp/base"] = new() { ["src/Child.cs"] = "namespace Acme.Core;\npublic class Child { public int Run() => 3; }\n" },
        ["csharp/overload"] = new() { ["src/Util.cs"] = "namespace Acme.Core;\npublic static class Util { public static int Helper(int x, int y = 0) => x + y; }\n" },
    };

    public static TheoryData<string> ScenarioNames => new(Changes.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Incremental_MatchesFullReindex(string name)
    {
        using var repo = new TempRepo(Repos[name[..name.IndexOf('/')]]);
        using var incremental = IndexDatabase.OpenInMemory();
        using var resolveAll = IndexDatabase.OpenInMemory();
        await Indexer(incremental).IndexAsync(repo.Root);
        await Indexer(resolveAll).IndexAsync(repo.Root);

        repo.Apply(Changes[name]);
        await Indexer(incremental).IndexAsync(repo.Root);
        await Indexer(resolveAll, new IndexOptions { ResolveAllReferences = true }).IndexAsync(repo.Root);
        using var fresh = IndexDatabase.OpenInMemory();
        await Indexer(fresh).IndexAsync(repo.Root);

        Assert.Equal(Symbols(fresh), Symbols(incremental));
        Assert.Equal(Edges(resolveAll), Edges(incremental));
        var dangling = DanglingKeys(incremental);
        Assert.Equal(Edges(fresh, dangling), Edges(incremental, dangling));
    }

    [Fact]
    public async Task Incremental_BodyChange_ResolvesOnlyAffectedReferences()
    {
        using var repo = new TempRepo(Repos["ts"]);
        using var db = IndexDatabase.OpenInMemory();
        var full = await Indexer(db).IndexAsync(repo.Root);
        repo.Apply(Changes["ts/body"]);

        var report = await Indexer(db).IndexAsync(repo.Root);

        Assert.Equal(1, report.FilesParsed);
        // a.ts 自己没有引用，只有 b.ts（调用 helper）受影响：比全量少
        Assert.True(PendingCount(report) < PendingCount(full), $"{PendingCount(report)} vs {PendingCount(full)}");
        Assert.True(PendingCount(report) > 0);
    }

    [Fact]
    public async Task Incremental_NothingChanged_ResolvesNothing()
    {
        using var repo = new TempRepo(Repos["csharp"]);
        using var db = IndexDatabase.OpenInMemory();
        await Indexer(db).IndexAsync(repo.Root);
        var before = Edges(db);

        var report = await Indexer(db).IndexAsync(repo.Root);

        Assert.Equal(0, report.FilesParsed);
        Assert.Equal(0, PendingCount(report));
        Assert.Equal(before, Edges(db));
    }

    /// <summary>
    /// 取舍：大小与 mtime 都和库里一致就认为没变，不读文件。内容改了却保持大小、又把 mtime 回拨（工具保留时间戳、手工 touch -d）
    /// 的情况增量看不到，要 --full 才会重读——和 git / make / MSBuild 的快速检查同一假设，换来每次不读、不哈希全部文件。
    /// </summary>
    [Fact]
    public async Task Incremental_SameSizeAndMtime_SkipsReading_FullPicksItUp()
    {
        using var repo = new TempRepo(Repos["ts"]);
        using var db = IndexDatabase.OpenInMemory();
        await Indexer(db).IndexAsync(repo.Root);
        var path = Path.Combine(repo.Root, "src", "a.ts");
        var mtime = File.GetLastWriteTimeUtc(path);
        var content = File.ReadAllText(path);
        File.WriteAllText(path, content.Replace("helper", "helpex")); // 同样长度
        File.SetLastWriteTimeUtc(path, mtime);

        var skipped = await Indexer(db).IndexAsync(repo.Root);
        Assert.Equal(0, skipped.FilesParsed);
        Assert.NotEmpty(new IndexReader(db).FindByQualifiedName("src/a.ts:helper"));

        // mtime 变了（大小不变）就重读
        File.SetLastWriteTimeUtc(path, mtime.AddSeconds(2));
        var reread = await Indexer(db).IndexAsync(repo.Root);
        Assert.Equal(1, reread.FilesParsed);
        Assert.Empty(new IndexReader(db).FindByQualifiedName("src/a.ts:helper"));
        Assert.NotEmpty(new IndexReader(db).FindByQualifiedName("src/a.ts:helpex"));

        // --full 无条件重读
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, mtime.AddSeconds(2));
        var full = await Indexer(db, new IndexOptions { Full = true }).IndexAsync(repo.Root);
        Assert.Equal(5, full.FilesParsed);
        Assert.NotEmpty(new IndexReader(db).FindByQualifiedName("src/a.ts:helper"));
    }

    [Fact]
    public async Task Incremental_NullMtime_HashesOnceAndBackfills()
    {
        using var repo = new TempRepo(Repos["ts"]);
        using var db = IndexDatabase.OpenInMemory();
        await Indexer(db).IndexAsync(repo.Root);
        Assert.All(new IndexReader(db).GetAllFiles(), f => Assert.NotNull(f.Mtime));
        db.Execute("UPDATE files SET mtime = NULL"); // 旧库：没有 mtime

        var report = await Indexer(db).IndexAsync(repo.Root);

        Assert.Equal(0, report.FilesParsed);
        Assert.Equal(5, report.FilesUnchanged);
        Assert.All(new IndexReader(db).GetAllFiles(), f => Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(repo.Root, f.Path)).Ticks, f.Mtime));
    }

    /// <summary>tsconfig 的 paths 别名变了而源码没变：配置比上次写库新就退回全部重解析。</summary>
    [Fact]
    public async Task Incremental_TsConfigChanged_ReresolvesEverything()
    {
        var files = new Dictionary<string, string>
        {
            ["tsconfig.json"] = """{ "compilerOptions": { "baseUrl": ".", "paths": { "@lib/*": ["src/one/*"] } } }""",
            ["src/one/thing.ts"] = "export function thing(): number { return 1; }\n",
            ["src/two/thing.ts"] = "export function thing(): number { return 2; }\n",
            ["src/app.ts"] = "import { thing } from '@lib/thing';\nexport function app(): number { return thing(); }\n",
        };
        using var repo = new TempRepo(files);
        var dbPath = Path.Combine(repo.Root, ".codeatlas", "index.db");
        using (var db = IndexDatabase.Open(dbPath))
            await Indexer(db).IndexAsync(repo.Root);
        var config = Path.Combine(repo.Root, "tsconfig.json");
        File.WriteAllText(config, files["tsconfig.json"].Replace("src/one", "src/two"));
        File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddSeconds(5));

        using var incremental = IndexDatabase.Open(dbPath);
        await Indexer(incremental).IndexAsync(repo.Root);
        using var fresh = IndexDatabase.OpenInMemory();
        await Indexer(fresh).IndexAsync(repo.Root);

        Assert.Equal(Edges(fresh), Edges(incremental));
        Assert.Contains(Edges(incremental), e => e.Contains("src/two/thing.ts:thing", StringComparison.Ordinal));
    }

    private static TreeSitterIndexer Indexer(IndexDatabase db, IndexOptions? options = null) =>
        new(db, LanguageAdapters.Create(), new HeuristicEdgeResolver(), options);

    private static int PendingCount(IndexReport report)
    {
        var label = report.Timings!.Single(t => t.Phase.StartsWith("ts.resolve.load(", StringComparison.Ordinal)).Phase;
        return int.Parse(label["ts.resolve.load(".Length..^1]);
    }

    private static List<string> Symbols(IndexDatabase db) =>
        db.Query<string>("SELECT qualified_name || '|' || kind || '|' || start_line || '|' || COALESCE(signature, '') FROM symbols WHERE kind <> 'external' ORDER BY 1"); // external 符号增量时不回收，不比

    /// <summary>边按 (from 限定名, to 限定名 / to_name, kind, via, line, pos, 置信度, 来源, ctx) 比较，忽略 id；skip 里的位置（悬空边所在调用处）不比。</summary>
    private static List<string> Edges(IndexDatabase db, HashSet<string>? skip = null) =>
        db.Query<string>("""
            SELECT f.qualified_name || '|' || e.kind || '|' || e.line || '|' || COALESCE(e.pos, -1) || '|' || COALESCE(t.qualified_name, '~' || e.to_name)
                || '|' || COALESCE(e.via, '') || '|' || e.confidence || '|' || e.source || '|' || COALESCE(e.ctx, '') || '|' || COALESCE(e.evidence_line, -1)
            FROM edges e JOIN symbols f ON f.id = e.from_id LEFT JOIN symbols t ON t.id = e.to_id
            ORDER BY 1
            """).Where(e => skip is null || !skip.Contains(Key(e))).ToList();

    private static HashSet<string> DanglingKeys(IndexDatabase db) =>
        db.Query<string>("""
            SELECT f.qualified_name || '|' || e.kind || '|' || e.line || '|' || COALESCE(e.pos, -1)
            FROM edges e JOIN symbols f ON f.id = e.from_id WHERE e.to_id IS NULL
            """).ToHashSet(StringComparer.Ordinal);

    private static string Key(string edge) => string.Join('|', edge.Split('|').Take(4));

    private sealed class TempRepo : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-incr-" + Guid.NewGuid().ToString("N"));

        public TempRepo(Dictionary<string, string> files)
        {
            foreach (var (path, content) in files) Write(path, content);
        }

        /// <summary>写入改动并把 mtime 往后拨：同一时钟刻度内写完、大小又没变时也一定会被看成改过。</summary>
        public void Apply(Dictionary<string, string?> changes)
        {
            foreach (var (path, content) in changes)
            {
                var full = Path.Combine(Root, path);
                if (content is null) { File.Delete(full); continue; }
                Write(path, content);
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(2));
            }
        }

        private void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content.ReplaceLineEndings("\n"));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
