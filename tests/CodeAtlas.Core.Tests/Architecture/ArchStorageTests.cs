using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests.Architecture;

public class ArchStorageTests
{
    [Fact]
    public void Replace_ThenRead_RoundTripsEveryField_AndSecondReplaceOverwrites()
    {
        using var db = IndexDatabase.OpenInMemory();
        var model = new ArchModel(
            [
                new ArchNode("Acme.Api", ArchNodeKind.Container, "dotnet-web", "Acme.Api", "Acme.Api", ArchSource.Discovered,
                    [new Evidence("Acme.Api/Acme.Api.csproj", null, "Sdk.Web"), new Evidence("Acme.Api/Dockerfile", 12, "EXPOSE 8080")],
                    ["Acme.Api", "Acme.App"], [5001, 8080]),
                new ArchNode("external:database:postgresql", ArchNodeKind.External, "database", "PostgreSQL", null, ArchSource.Discovered,
                    [new Evidence("Acme.App/Acme.App.csproj", 9, "Npgsql.EntityFrameworkCore.PostgreSQL")], [], []),
                new ArchNode("person:ops", ArchNodeKind.Person, "person", "运维", null, ArchSource.Declared, [new Evidence(".codeatlas/architecture.yaml")], [], []),
            ],
            [
                new ArchEdge("Acme.Api", "external:database:postgresql", "sql", 0.5, ArchSource.Discovered, [new Evidence("Acme.App/Acme.App.csproj", 9)]),
                new ArchEdge("Acme.Api", "Billing.Api", "http", 0.7, ArchSource.Discovered, [new Evidence("Acme.Api/Setup.cs", 40)], "Billing:Url", "http://localhost:5002/"),
            ],
            [new Outbound("Acme.Api", "http-client", "http", "Billing", "Billing:Url", "http://localhost:5002/", "Acme.Api/Setup.cs", 40, "Billing.Api")],
            [new ArchConflict("declared-not-found", "声明了 Payments 但没有发现")]);

        ArchStorage.Replace(db, model);
        var read = ArchStorage.Read(db);

        Assert.Equal(3, read.Nodes.Count);
        var api = read.Nodes.Single(n => n.Key == "Acme.Api");
        Assert.Equal((ArchNodeKind.Container, "dotnet-web", "Acme.Api", ArchSource.Discovered), (api.Kind, api.SubKind, api.Path, api.Source));
        Assert.Equal(new[] { "Acme.Api", "Acme.App" }, api.Members);
        Assert.Equal(new[] { 5001, 8080 }, api.Ports);
        Assert.Equal(2, api.Evidence.Count);
        Assert.Equal(("Acme.Api/Dockerfile", 12, "EXPOSE 8080"), (api.Evidence[1].File, api.Evidence[1].Line, api.Evidence[1].Note));
        Assert.Equal(ArchSource.Declared, read.Nodes.Single(n => n.Key == "person:ops").Source);

        Assert.Equal(2, read.Edges.Count);
        var http = read.Edges.Single(e => e.Protocol == "http");
        Assert.Equal(("Acme.Api", "Billing.Api", 0.7, "Billing:Url", "http://localhost:5002/"), (http.FromKey, http.ToKey, http.Confidence, http.ConfigKey, http.Url));
        Assert.Equal(40, http.Evidence[0].Line);

        var o = Assert.Single(read.Outbound);
        Assert.Equal(("Acme.Api", "http-client", "Billing", "Billing.Api", 40), (o.ContainerKey, o.RuleId, o.ClientName, o.TargetKey, o.Line));
        Assert.Equal("declared-not-found", Assert.Single(read.Conflicts).Kind);

        // 再写一次更小的模型：旧内容必须全部消失
        ArchStorage.Replace(db, new ArchModel([read.Nodes[0]], [], [], []));
        var again = ArchStorage.Read(db);
        Assert.Single(again.Nodes);
        Assert.Empty(again.Edges);
        Assert.Empty(again.Outbound);
        Assert.Empty(again.Conflicts);
    }

    [Fact]
    public void Read_OnFreshDb_IsEmpty()
    {
        using var db = IndexDatabase.OpenInMemory();
        var m = ArchStorage.Read(db);
        Assert.Empty(m.Nodes);
        Assert.Empty(m.Edges);
    }
}
