using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// 共享库里的出口只归真正用到它的容器。夹具照 eShop 的事件总线写：Shop.EventBus 被四个宿主引用，
/// Orders 发 + 订阅、Basket 只订阅、Grace 只发（起了总线没订阅）、Silent 引用了却什么都不用；
/// Shop.Jobs 里的处理器经分派调用（谁都找不到调用）→ 保留但降置信度。
/// </summary>
public sealed class SharedLibraryAttributionTests : IDisposable
{
    private readonly ArchFixtureRoot _fx = new();
    public void Dispose() => _fx.Dispose();

    private sealed class ArchFixtureRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-shared-" + Guid.NewGuid().ToString("N"));
        public void Write(string rel, string content)
        {
            var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private static string Host(string name, params string[] refs) =>
        $"""<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup>{string.Concat(refs.Select(r => $"<ProjectReference Include=\"..\\{r}\\{r}.csproj\" />"))}</ItemGroup></Project>""";

    private ArchModel Scan()
    {
        _fx.Write("Shop.EventBus/Shop.EventBus.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="RabbitMQ.Client" Version="7.0.0" /></ItemGroup>
            </Project>
            """);
        _fx.Write("Shop.EventBus/RabbitBus.cs", """
            namespace Shop.EventBus;
            public interface IEventBus { Task PublishAsync(ShopEvent e); }
            public sealed class RabbitBus(IChannel channel) : IEventBus, IHostedService
            {
                public async Task PublishAsync(ShopEvent e)
                {
                    await channel.BasicPublishAsync(exchange: "shop", routingKey: e.Name, body: e.Body);
                }

                public async Task StartAsync(CancellationToken ct)
                {
                    await channel.BasicConsumeAsync(queue: "shop", autoAck: false, consumer: null);
                }
            }
            """);
        _fx.Write("Shop.EventBus/BusExtensions.cs", """
            namespace Shop.EventBus;
            public static class BusExtensions
            {
                public static IBusBuilder AddRabbitBus(this IHostApplicationBuilder builder)
                {
                    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RabbitBus>());
                    return new BusBuilder(builder.Services);
                }

                public static IBusBuilder AddSubscription<T, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TH>(this IBusBuilder bus)
                {
                    return bus;
                }
            }
            """);
        _fx.Write("Shop.Orders/Shop.Orders.csproj", Host("Shop.Orders", "Shop.EventBus"));
        _fx.Write("Shop.Orders/Program.cs", """
            builder.AddRabbitBus()
                   .AddSubscription<PaidEvent, PaidHandler>();
            app.MapPost("/orders", async (IEventBus bus) => await bus.PublishAsync(new OrderPlaced()));
            """);
        _fx.Write("Shop.Basket/Shop.Basket.csproj", Host("Shop.Basket", "Shop.EventBus"));
        _fx.Write("Shop.Basket/Program.cs", "builder.AddRabbitBus().AddSubscription<OrderPlaced, ClearBasket>();\n");
        _fx.Write("Shop.Grace/Shop.Grace.csproj", Host("Shop.Grace", "Shop.EventBus"));
        _fx.Write("Shop.Grace/Program.cs", "builder.AddRabbitBus();\nawait bus.PublishAsync(new GraceOver());\n");
        _fx.Write("Shop.Silent/Shop.Silent.csproj", Host("Shop.Silent", "Shop.EventBus", "Shop.Jobs"));
        _fx.Write("Shop.Silent/Program.cs", "var app = builder.Build();\napp.Run();\n");
        // 经分派调用的处理器：类名、方法名在宿主里都找不到
        _fx.Write("Shop.Jobs/Shop.Jobs.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Confluent.Kafka" Version="2.6.0" /></ItemGroup></Project>""");
        _fx.Write("Shop.Jobs/PingHandler.cs", """
            namespace Shop.Jobs;
            public sealed class PingHandler(IProducer<string, string> producer) : IRequestHandler<Ping>
            {
                public Task Handle(Ping request, CancellationToken ct) => producer.ProduceAsync("ping", new());
            }
            """);
        _fx.Write("Shop.Orders2/Shop.Orders2.csproj", Host("Shop.Orders2", "Shop.Jobs"));
        _fx.Write("Shop.Orders2/Program.cs", "builder.Services.AddMediatR();\n");
        return ArchitectureScanner.Scan(_fx.Root);
    }

    [Fact]
    public void LibraryCapabilities_CountOnlyForContainersThatUseThem()
    {
        var model = Scan();
        var rabbit = model.Edges.Where(e => e.FromKey == "external:broker:rabbitmq" || e.ToKey == "external:broker:rabbitmq")
            .Select(e => (e.FromKey, e.ToKey, e.Protocol)).OrderBy(x => x.FromKey, StringComparer.Ordinal).ThenBy(x => x.ToKey, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            ("Shop.Grace", "external:broker:rabbitmq", "amqp-produce"),
            ("Shop.Orders", "external:broker:rabbitmq", "amqp-produce"),
            ("external:broker:rabbitmq", "Shop.Basket", "amqp-consume"),
            ("external:broker:rabbitmq", "Shop.Orders", "amqp-consume"),
        }, rabbit);

        // 证据：先列本容器自己用到库的那一行，再列库里的出口并注明来自共享库
        var produce = model.Edges.Single(e => e.FromKey == "Shop.Orders" && e.Protocol == "amqp-produce");
        Assert.Equal(("Shop.Orders/Program.cs", 3), (produce.Evidence[0].File, produce.Evidence[0].Line));
        Assert.StartsWith("经共享库 Shop.EventBus 的 PublishAsync：", produce.Evidence[0].Note);
        Assert.Equal(("Shop.EventBus/RabbitBus.cs", 7), (produce.Evidence[1].File, produce.Evidence[1].Line));
        Assert.EndsWith("（来自共享库 Shop.EventBus）", produce.Evidence[1].Note);
        Assert.Equal(0.5, produce.Confidence);

        // 消费经托管服务（StartAsync → RabbitBus → AddRabbitBus）连上，Basket 的证据是它调 AddRabbitBus 的那行
        var consume = model.Edges.Single(e => e.ToKey == "Shop.Basket");
        Assert.Equal(("Shop.Basket/Program.cs", 1), (consume.Evidence[0].File, consume.Evidence[0].Line));

        // Silent 不用事件总线：连包引用的 amqp 边也没有（RabbitMQ.Client 在共享库的 csproj 里）
        Assert.DoesNotContain(model.Edges, e => (e.FromKey == "Shop.Silent" || e.ToKey == "Shop.Silent") && e.Protocol.StartsWith("amqp", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryCodeNobodyCallsDirectly_KeepsEdgeWithLowerConfidence()
    {
        var model = Scan();
        // PingHandler 经分派调用：Orders2 与 Silent 都找不到调用，也没有别的容器找到 → 都保留，置信度 0.3，证据写来自共享库
        var kafka = model.Edges.Where(e => e.Protocol == "kafka-produce").OrderBy(e => e.FromKey, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Shop.Orders2", "Shop.Silent" }, kafka.Select(e => e.FromKey));
        Assert.All(kafka, e =>
        {
            Assert.Equal(SharedLibraryAttribution.Unsure, e.Confidence);
            var ev = Assert.Single(e.Evidence);
            Assert.Equal(("Shop.Jobs/PingHandler.cs", 2), (ev.File, ev.Line));   // IProducer< 在主构造函数里
            Assert.Contains("来自共享库 Shop.Jobs，本容器里没找到调用", ev.Note);
        });
    }
}
