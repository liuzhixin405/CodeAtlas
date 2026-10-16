using System.Net;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Server.Narration;

namespace CodeAtlas.Server.Tests;

public class NarrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ValidJson_BecomesReady_WithEvidenceValidated_AndCached()
    {
        var handler = new FakeChatHandler().Reply(FakeChat.ValidJson(["node:k", "edge:a→k:http"]));
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", force: false, CancellationToken.None);

        Assert.Equal("ready", r.Dto.Status);
        Assert.Equal("K 是一个 Web 容器。", r.Dto.Summary);
        Assert.Equal("fake-model", r.Dto.Model);
        Assert.NotNull(r.Dto.GeneratedAt);
        Assert.Equal(64, r.Dto.Hash!.Length);
        Assert.Equal(2, r.Dto.KeyPoints.Count);
        Assert.All(r.Dto.KeyPoints, p => Assert.False(p.Inferred));
        Assert.Equal(["node:k", "edge:a→k:http"], r.Dto.KeyPoints[0].Evidence);
        Assert.Single(r.Dto.Risks);
        Assert.Equal("columns", r.Dto.Layout);
        Assert.Equal(1, r.Attempts);
        Assert.Equal(7, r.Chat!.CompletionTokens);
        Assert.Equal(1, handler.Calls);
        // 请求体：模型名、system + user 两条消息、事实文本在 user 里
        Assert.Equal("POST http://127.0.0.1:1/v1/chat/completions", handler.RequestUris[0]);
        Assert.Contains("\"model\":\"fake-model\"", handler.RequestBodies[0]);
        Assert.Contains("[node:k]", handler.RequestBodies[0].Replace("\\u5bb9", "容"));

        // 落库了：Get 直接命中，不再调端点
        var got = svc.Get("container", "k");
        Assert.Equal("ready", got.Status);
        Assert.Equal(r.Dto.Hash, got.Hash);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, svc.Status().Queued); // 同步路径不经队列
    }

    [Fact]
    public async Task FencedJson_IsParsed()
    {
        var handler = new FakeChatHandler().Reply("好的，结果如下：\n```json\n" + FakeChat.ValidJson(["node:k"]) + "\n```\n");
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task UnknownEvidence_MarksPointInferred_ButOthersNot()
    {
        var handler = new FakeChatHandler().Reply(FakeChat.ValidJson(["node:k", "sym:999"]));
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.True(r.Dto.KeyPoints[0].Inferred);   // sym:999 不在事实集合里
        Assert.False(r.Dto.KeyPoints[1].Inferred);  // entry:12 在
        Assert.Equal(["node:k", "sym:999"], r.Dto.KeyPoints[0].Evidence); // 引用原样保留，页面自己标
    }

    [Fact]
    public async Task InvalidLayout_BecomesNull()
    {
        var handler = new FakeChatHandler().Reply(FakeChat.ValidJson(["node:k"], layout: "freeform-canvas"));
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.Null(r.Dto.Layout);
    }

    [Fact]
    public async Task GarbageThenValid_RetriesOnce()
    {
        var handler = new FakeChatHandler().Reply("我觉得这个容器挺好的。").Reply(FakeChat.ValidJson(["node:k"]));
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GarbageTwice_OrHttpErrorTwice_Failed_WithChineseError()
    {
        using var svc = FakeChat.Service(new FakeChatHandler().Reply("不是 JSON").Reply("还不是"));
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("failed", r.Dto.Status);
        Assert.Contains("JSON", r.Dto.Error);
        Assert.Contains("已重试", r.Dto.Error);

        using var svc2 = FakeChat.Service(new FakeChatHandler().ReplyStatus(HttpStatusCode.InternalServerError).ReplyStatus(HttpStatusCode.BadGateway));
        var r2 = await svc2.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("failed", r2.Dto.Status);
        Assert.Contains("HTTP 502", r2.Dto.Error);
        Assert.True(svc2.Status().Reachable); // 连得上，只是答错

        // failed 也落库：Get 返回 failed 而不是重新排队
        Assert.Equal("failed", svc2.Get("container", "k").Status);
    }

    [Fact]
    public async Task EndpointUnreachable_Unavailable_WithChineseReason_NoRetry()
    {
        var handler = new FakeChatHandler().Refuse().Refuse();
        using var svc = FakeChat.Service(handler);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("unavailable", r.Dto.Status);
        Assert.Contains("连不上 AI 端点", r.Dto.Error);
        Assert.Contains("127.0.0.1:1", r.Dto.Error);
        Assert.Equal(1, handler.Calls); // 连不上不重试
        Assert.False(svc.Status().Reachable);
    }

    [Fact]
    public async Task Disabled_ReturnsUnavailable_WithoutCallingEndpoint()
    {
        var handler = new FakeChatHandler();
        using var svc = FakeChat.Service(handler, options: new NarrationOptions("http://127.0.0.1:1/v1", "m", Enabled: false));
        Assert.Equal("unavailable", svc.Get("container", "k").Status);
        Assert.Contains("--no-ai", svc.Get("container", "k").Error);
        Assert.Equal("unavailable", svc.Enqueue("container", "k", true).Status);
        Assert.Equal("unavailable", (await svc.GenerateAsync("container", "k", true, CancellationToken.None)).Dto.Status);
        Assert.Equal(0, handler.Calls);
        Assert.False(svc.Status().Enabled);
    }

    [Fact]
    public async Task SameFacts_SecondRequestDoesNotCallEndpoint_ForceDoes()
    {
        var handler = new FakeChatHandler();
        using var svc = FakeChat.Service(handler);
        svc.Enqueue("container", "k", false);
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, svc.Status().Done);

        // 事实没变：Enqueue 直接返回 ready，不入队不调端点
        var hit = svc.Enqueue("container", "k", false);
        Assert.Equal(("ready", false), (hit.Status, hit.Stale));
        await svc.WaitIdleAsync(Wait);
        Assert.Equal("ready", svc.Get("container", "k").Status);
        Assert.Equal(1, handler.Calls);

        // force：重算；重算期间 Get 带着旧内容报 pending / running
        svc.Stop();
        var updating = svc.Enqueue("container", "k", true);
        Assert.Equal(("pending", "K 是一个 Web 容器。"), (updating.Status, updating.Summary));
        svc.Resume();
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(2, handler.Calls);
    }

    // ---------- 缓存：过期判断、稳定键、换模型、失败不覆盖 ----------

    private static NarrationService Svc(Core.Storage.NarrativeCache cache, FakeChatHandler handler, string model = "fake-model", params Facts[] facts)
    {
        var o = new NarrationOptions("http://127.0.0.1:1/v1", model, TimeoutSeconds: 5);
        return new NarrationService(o, new OpenAiCompatibleProvider(o, handler), new StubFacts(facts), cache, ownsCache: false);
    }

    private static Facts WithSources(Facts f, params (string Path, string Hash)[] sources) =>
        f with { Sources = sources.ToDictionary(s => s.Path, s => s.Hash) };

    [Fact]
    public async Task FactsChanged_OldContentStillShown_MarkedStale_NotAutoRegenerated_UpdateOnForce()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        var handler = new FakeChatHandler();
        using (var first = Svc(cache, handler, facts: FakeChat.ContainerFacts()))
            Assert.Equal("ready", (await first.GenerateAsync("container", "k", false, CancellationToken.None)).Dto.Status);

        // 下一次打开时架构事实变了（多了一条边）
        var changed = FakeChat.ContainerFacts() with { Text = FakeChat.ContainerFacts().Text + "[edge:k→db:sql] K 读写 DB\n", FingerprintText = FakeChat.ContainerFacts().Text + "[edge:k→db:sql] K 读写 DB\n" };
        using var svc = Svc(cache, handler, facts: changed);
        var got = svc.Get("container", "k");
        Assert.Equal(("ready", true, "架构事实有变化", 0), (got.Status, got.Stale, got.StaleReason, got.ChangedFiles));
        Assert.Equal("K 是一个 Web 容器。", got.Summary);  // 旧内容照样给
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);                    // 不自动重算
        Assert.Equal("pending", svc.Enqueue("container", "k", force: false).Status); // 用户点"更新"：过期的就排队（不需要 force）

        await svc.WaitIdleAsync(Wait);
        Assert.Equal(2, handler.Calls);
        Assert.False(svc.Get("container", "k").Stale);
    }

    [Fact]
    public async Task SourceFilesChanged_CountsAddedRemovedAndModified()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        var handler = new FakeChatHandler();
        var v1 = WithSources(FakeChat.ContainerFacts(), ("src/A.cs", "h1"), ("src/B.cs", "h2"), ("src/C.cs", "h3"));
        using (var first = Svc(cache, handler, facts: v1))
            await first.GenerateAsync("container", "k", false, CancellationToken.None);

        // B 改了、C 删了、D 新增；事实文本没变
        var v2 = WithSources(FakeChat.ContainerFacts(), ("src/A.cs", "h1"), ("src/B.cs", "h2-new"), ("src/D.cs", "h4"));
        using var svc = Svc(cache, handler, facts: v2);
        var got = svc.Get("container", "k");
        Assert.Equal((true, 3, "生成后 3 个相关文件有改动"), (got.Stale, got.ChangedFiles, got.StaleReason));

        // 原样的文件：不过期
        using var same = Svc(cache, handler, facts: v1);
        Assert.False(same.Get("container", "k").Stale);
    }

    [Fact]
    public async Task StableKey_SurvivesSymbolIdChange_AfterReindex_EvidenceMappedToNewIds()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        // 第一次索引：入口 id 10、Save 的 id 11
        var before = new Facts("flow", "10", "/api/orders", "[entry:10] 入口 Create\n[sym:11] Save\n", new HashSet<string> { "entry:10", "sym:11" }, 0)
        {
            StableKey = "Api|Acme.Ctl.Create()",
            StableIds = new Dictionary<string, string> { ["entry:10"] = "entry@Acme.Ctl.Create()", ["sym:11"] = "sym@Acme.Repo.Save()" },
        };
        var handler = new FakeChatHandler().Reply(FakeChat.ValidJson(["sym:11", "entry:10"], layout: "first-seen"));
        using (var first = Svc(cache, handler, facts: before))
            Assert.Equal("ready", (await first.GenerateAsync("flow", "10", false, CancellationToken.None)).Dto.Status);

        // 全量重建后：同样的代码，id 变成 20 / 21
        var after = new Facts("flow", "20", "/api/orders", "[entry:20] 入口 Create\n[sym:21] Save\n", new HashSet<string> { "entry:20", "sym:21" }, 0)
        {
            StableKey = "Api|Acme.Ctl.Create()",
            StableIds = new Dictionary<string, string> { ["entry:20"] = "entry@Acme.Ctl.Create()", ["sym:21"] = "sym@Acme.Repo.Save()" },
        };
        Assert.Equal(before.Fingerprint, after.Fingerprint); // id 换成稳定形式后指纹相同
        using var svc = Svc(cache, handler, facts: after);
        var got = svc.Get("flow", "20");
        Assert.Equal(("ready", false, "20"), (got.Status, got.Stale, got.Key));
        Assert.Equal(["sym:21", "entry:20"], got.KeyPoints[0].Evidence); // 证据换回当前 id，页面点得动
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SwitchingModel_DoesNotInvalidate_OldResultShownWithItsModel()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        var handler = new FakeChatHandler();
        using (var local = Svc(cache, handler, "qwen3.6-27b", FakeChat.ContainerFacts()))
            await local.GenerateAsync("container", "k", false, CancellationToken.None);

        using var online = Svc(cache, handler, "deepseek-chat", FakeChat.ContainerFacts());
        var got = online.Get("container", "k");
        Assert.Equal(("ready", "qwen3.6-27b", false), (got.Status, got.Model, got.Stale));
        await online.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FailedRegeneration_DoesNotOverwriteAReadyResult()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        var handler = new FakeChatHandler().Reply(FakeChat.ValidJson(["node:k"])).Reply("坏").Reply("还坏");
        using var svc = Svc(cache, handler, facts: FakeChat.ContainerFacts());
        await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        var retry = await svc.GenerateAsync("container", "k", force: true, CancellationToken.None);
        Assert.Equal("failed", retry.Dto.Status);
        var got = svc.Get("container", "k");
        Assert.Equal(("ready", "K 是一个 Web 容器。"), (got.Status, got.Summary)); // 本地辛苦算的那条还在
    }

    [Fact]
    public async Task Unreachable_IsForgottenAfterTtl_SoOpeningTheCardTriesAgain()
    {
        using var cache = Core.Storage.NarrativeCache.OpenInMemory();
        var handler = new FakeChatHandler().Refuse();
        var o = new NarrationOptions("http://127.0.0.1:1/v1", "fake-model", TimeoutSeconds: 5);
        using var svc = new NarrationService(o, new OpenAiCompatibleProvider(o, handler), new StubFacts(FakeChat.ContainerFacts()), cache, ownsCache: false) { UnavailableTtl = TimeSpan.Zero };
        svc.Get("container", "k");
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);
        await Task.Delay(20);
        svc.Get("container", "k");                     // 过了 TTL：重新排队（这次端点好了）
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("ready", svc.Get("container", "k").Status);
    }

    [Fact]
    public void CacheFile_SurvivesReopen_AndLivesBesideTheIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codeatlas-narr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Core.Storage.NarrativeCache.PathBeside(Path.Combine(dir, "index.db"));
            Assert.Equal(Path.Combine(dir, "narratives.db"), path);
            using (var c = Core.Storage.NarrativeCache.Open(path))
                c.Upsert(new Core.Storage.NarrativeRow("container", "k", "m", "ready", "{}", null, "2026-10-14T00:00:00Z", "fp", "{}", "abc1234"));
            using var again = Core.Storage.NarrativeCache.Open(path);
            Assert.Equal(("fp", "abc1234"), (again.Get("container", "k")!.Fingerprint, again.Get("container", "k")!.GitCommit));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { /* 临时目录 */ } }
    }

    [Fact]
    public async Task Stop_HoldsQueue_Resume_Runs()
    {
        var handler = new FakeChatHandler();
        using var svc = FakeChat.Service(handler);
        var s = svc.Stop();
        Assert.True(s.Paused);
        var dto = svc.Enqueue("container", "k", false);
        Assert.Equal("pending", dto.Status);
        Assert.Contains("暂停", dto.Error);
        await Task.Delay(200);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(1, svc.Status().Queued);
        Assert.Equal("pending", svc.Get("container", "k").Status);

        var resumed = svc.Resume();
        Assert.False(resumed.Paused);
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, svc.Status().Queued);
        Assert.Equal("ready", svc.Get("container", "k").Status);
    }

    [Fact]
    public async Task AutoQueuedByGet_KeepsOnlyTheLatest_ExplicitEnqueueSurvives()
    {
        var handler = new FakeChatHandler();
        var options = new NarrationOptions("http://127.0.0.1:1/v1", "fake-model", TimeoutSeconds: 5);
        using var svc = new NarrationService(options, new OpenAiCompatibleProvider(options, handler),
            new StubFacts(FakeChat.ContainerFacts("a"), FakeChat.ContainerFacts("b"), FakeChat.ContainerFacts("c"), FakeChat.ContainerFacts("d")),
            Core.Storage.NarrativeCache.OpenInMemory(), ownsCache: true);
        svc.Stop(); // 暂停，队列里看得清

        Assert.Equal("pending", svc.Get("container", "a").Status); // 打开 a 的叙述卡：自动排队
        Assert.Equal("pending", svc.Get("container", "b").Status); // 换到 b：a 还没开始，被撤掉
        Assert.Equal(1, svc.Status().Queued);
        svc.Enqueue("container", "c", force: false);                // 用户点"生成"：显式项
        svc.Get("container", "d");                                  // 再换到 d：撤掉 b，c 留着
        Assert.Equal(2, svc.Status().Queued);

        svc.Resume();
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(2, handler.Calls);                             // 只生成了 c 和 d
        Assert.Equal("ready", svc.Get("container", "c").Status);
        Assert.Equal("ready", svc.Get("container", "d").Status);
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(2, handler.Calls);                             // 读已就绪的不再排队
    }

    [Fact]
    public async Task UnknownObject_Failed_NotQueued()
    {
        var handler = new FakeChatHandler();
        using var svc = FakeChat.Service(handler);
        var got = svc.Get("container", "nope");
        Assert.Equal("failed", got.Status);
        Assert.Contains("没有这个对象", got.Error);
        Assert.Equal("failed", svc.Enqueue("container", "nope", false).Status);
        await svc.WaitIdleAsync(Wait);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Schema_ExtractsJson_FromThinkAndFences_AndAcceptsStringPoints()
    {
        Assert.Equal("{\"a\":1}", NarrativeSchema.ExtractJson("<think>想想…{}</think>\n```json\n{\"a\":1}\n```"));
        Assert.Null(NarrativeSchema.ExtractJson("没有对象"));
        var body = NarrativeSchema.Parse("{\"summary\":\"s\",\"keyPoints\":[\"纯字符串要点\"],\"risks\":[{\"text\":\"r\",\"evidence\":\"node:k\"}],\"layout\":\"topological\"}", "container", new HashSet<string> { "node:k" });
        Assert.True(body.KeyPoints[0].Inferred);  // 没有引用 = 推断
        Assert.False(body.Risks[0].Inferred);     // 单个字符串 evidence 也认
        Assert.Equal("topological", body.Layout);
        Assert.Throws<NarrativeParseException>(() => NarrativeSchema.Parse("{\"keyPoints\":[]}", "container", new HashSet<string>()));
    }

    [Fact]
    public void RemoteWarning_OnlyForNonLoopback()
    {
        Assert.Null(new NarrationOptions("http://127.0.0.1:8080/v1", "m").RemoteWarning);
        Assert.Null(new NarrationOptions("http://localhost:11434/v1", "m").RemoteWarning);
        // IncludeSource 默认 true：事实子图和入口源码都会发出去
        Assert.Equal("事实子图与入口源码将发送到 gpu-box.lan", new NarrationOptions("http://gpu-box.lan:8080/v1", "m").RemoteWarning);
    }

    [Fact]
    public void FactsBuilder_Container_HasIdsForNodeEdgesEntries_AndBudgetTruncates()
    {
        var api = new ArchNodeDto("src/Api", "container", "dotnet-web", "Api", "src/Api", "discovered", [new Evidence("src/Api/Api.csproj")], ["src/Api", "src/App"], [8080], ["Acme"], 2);
        var db = new ArchNodeDto("database:pg", "external", "database", "PostgreSQL", null, "discovered", [new Evidence("src/App/Db.cs", 10)], [], [], [], 0);
        var web = new ArchNodeDto("web", "container", "frontend", "Web", "web", "discovered", [], ["web"], [], [], 1);
        var arch = new ArchDto([api, db, web], [new ArchEdgeDto("src/Api", "database:pg", "sql", 0.7, "discovered", [new Evidence("src/App/Db.cs", 10)], "ConnectionStrings:Main", null), new ArchEdgeDto("web", "src/Api", "http", 0.9, "discovered", [], null, "http://api")], [], null);
        var entries = Enumerable.Range(1, 25).Select(i => new EntryDto(i, "http", "aspnet.controller", $"/api/x{i}", "GET", $"Get{i}", $"Acme.Api.XController.Get{i}", "src/Api/X.cs", i)).ToList();

        var f = FactsBuilder.Container(arch, api, entries);
        Assert.Equal(("container", "src/Api", "Api"), (f.Kind, f.Key, f.Title));
        Assert.Contains("node:src/Api", f.Ids);
        Assert.Contains("node:database:pg", f.Ids);
        Assert.Contains("node:web", f.Ids);
        Assert.Contains("edge:src/Api→database:pg:sql", f.Ids);
        Assert.Contains("edge:web→src/Api:http", f.Ids);
        Assert.Contains("entry:1", f.Ids);
        Assert.Contains("entry:20", f.Ids);
        Assert.DoesNotContain("entry:21", f.Ids); // 只给前 20 条
        Assert.Contains("其余 5 个入口略", f.Text);
        Assert.Contains("ConnectionStrings:Main", f.Text);
        Assert.Equal(0, f.Omitted);
        Assert.Equal(f.Fingerprint, FactsBuilder.Container(arch, api, entries).Fingerprint); // 同样的事实，同样的指纹（与模型无关）

        // 预算：事实很多时低优先级的被省掉并注明
        var huge = new ArchDto([api, db], Enumerable.Range(0, 2000).Select(i => new ArchEdgeDto("src/Api", "database:pg", "sql" + i, 0.5, "discovered", [new Evidence(new string('x', 60))], null, null)).ToList(), [], null);
        var big = FactsBuilder.Container(huge, api, []);
        Assert.True(big.Omitted > 0);
        Assert.True(big.Text.Length <= FactsBuilder.BudgetChars + 200, $"len={big.Text.Length}");
        Assert.Contains("已按重要性省略", big.Text);
        Assert.Contains("node:src/Api", big.Ids); // 高优先级的留着
    }

    [Fact]
    public void FactsBuilder_Component_AndFlow_Ids()
    {
        var comps = new List<DsmComponentDto>
        {
            new("Acme.App", "App", 12, 5, true, 0, new DsmHotDto(21, 140, 27, 1, "2026-09-01")),
            new("Acme.Infra", "Infra", 3, 1, false, 0, null),
            new("Acme.Api", "Api", 3, 1, false, 1, null),
        };
        var dsm = new DsmDto("Acme.Api", "", comps, [new DsmCellDto(0, 1, 1, new() { ["calls"] = 1 }), new DsmCellDto(1, 0, 1, new() { ["calls"] = 1 }), new DsmCellDto(2, 0, 2, new() { ["calls"] = 2 })],
            [["Acme.App", "Acme.Infra"]], [new DsmCouplingDto(0, 2, 7, 0.6)], true, null);
        var f = FactsBuilder.Component(dsm, comps[0], "Acme.Api|Acme.App", "Api");
        Assert.Contains("comp:Acme.App", f.Ids);
        Assert.Contains("cell:Acme.App→Acme.Infra", f.Ids);
        Assert.Contains("cell:Acme.Infra→Acme.App", f.Ids);
        Assert.Contains("cell:Acme.Api→Acme.App", f.Ids);
        Assert.Contains("comp:Acme.Api", f.Ids);   // 隐性耦合对端
        Assert.Contains("在环里", f.Text);
        Assert.Contains("反向", f.Text);          // App → Infra 依赖了排在后面的
        Assert.Contains("文件修订合计 21", f.Text); // 夹具 Revisions=21、没有提交号：各文件修订之和，照实写（同一提交改几个文件算几次）
        Assert.Equal("App", f.Title);

        var leaf = new FlowNodeDto(21, "Save", "Acme.Infra.OrderRepo.Save(Order)", "call", "Acme.Api|Acme.Infra", "impl", 0.9, 1, 2, [], 0, null);
        var deep = new FlowNodeDto(99, "Deep", "X.Deep", "call", "Acme.Api|Acme.Infra", null, 1.0, 1, 5, [], 0, null);
        var handler = new FlowNodeDto(11, "HandleAsync", "Acme.App.H.HandleAsync", "dispatch", "Acme.Api|Acme.App", "dispatch:CreateOrder", 1.0, 1, 1, [leaf, new FlowNodeDto(null, "2 个框架调用", "ToList, Log", "external-group", "ext|System", null, 1.0, 1, 2, [], 0, null), deep with { Depth = 2, Children = [deep] }], 0, null);
        var root = new FlowNodeDto(3, "Create", "Acme.Api.Ctl.Create(Order)", "entry", "Acme.Api|Acme.Api", null, 1.0, 1, 0, [handler], 0, null);
        var flow = new FlowDto(3, "/api/orders", "/api/orders", "Acme.Api", [new("Acme.Api|Acme.Api", "Api", "project"), new("Acme.Api|Acme.App", "App", "project"), new("Acme.Api|Acme.Infra", "Infra", "project"), new("ext|System", "System", "external")], root, ["AuditBehavior"], 5, false, null);
        var entry = new EntryDto(3, "http", "aspnet.controller", "/api/orders", "POST", "Create", "Acme.Api.Ctl.Create(Order)", "src/Api/Ctl.cs", 5);
        var src = Enumerable.Range(1, 100).Select(i => $"line {i}").ToList();
        var ff = FactsBuilder.Flow(flow, entry, "Api", src, 5);
        Assert.Equal(("flow", "3", "/api/orders"), (ff.Kind, ff.Key, ff.Title));
        Assert.Contains("entry:3", ff.Ids);
        Assert.Contains("sym:11", ff.Ids);
        Assert.Contains("sym:21", ff.Ids);
        Assert.DoesNotContain("sym:99", ff.Ids.Where(i => i == "sym:99").Skip(1)); // 同一符号只给一次 id
        Assert.Contains("AuditBehavior", ff.Text);
        Assert.Contains("dispatch:CreateOrder", ff.Text);
        Assert.Contains("line 80", ff.Text);
        Assert.DoesNotContain("line 81", ff.Text);   // 源码最多 80 行
        Assert.Contains("只给前 80 行", ff.Text);
        Assert.Contains("深度超过 4", ff.Text);
    }
}
