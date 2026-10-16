using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程 · 纯 gRPC 服务（FrameworkFlowBuilder.GrpcOnly）：Go / Python / Java 与 Node 一样画「gRPC 调用 → 拦截器 → 服务方法 ×N」，
/// 以前 Go 只有一个空根（"阶段 0"）、Python / Java 画成"入口（常驻服务…）"。拦截器从源码文本认，没有就在说明里写。
/// </summary>
public class GrpcFrameworkFlowTests
{
    private static FrameworkInput Input(string subKind, string key, Dictionary<string, string> files, List<EntryPoint> entries)
    {
        var symbols = files.Keys.Select((p, i) => new MapSymbol(100 + i, SymbolKind.Module, p, p, null, 1, files[p].Split('\n').Length)).ToList();
        symbols.AddRange(entries.Select(e => new MapSymbol(e.SymbolId, SymbolKind.Method, $"{key}.{e.Route}", e.File, null, e.Line, e.Line + 2)));
        return new FrameworkInput(symbols, [], new ArchNode(key, ArchNodeKind.Container, subKind, key, key, ArchSource.Discovered, [], [key], []), entries, [],
            new FrameworkSources { Calls = _ => [], Signature = _ => null, ReadFile = p => files.GetValueOrDefault(p) });
    }

    private const string GoMain = """
        package main

        import "google.golang.org/grpc"

        func main() {
        	// grpc.NewServer(grpc.UnaryInterceptor(commented))
        	srv := grpc.NewServer(
        		grpc.ChainUnaryInterceptor(logging, auth.Unary()),
        		grpc.StatsHandler(otelgrpc.NewServerHandler()),
        	)
        	pb.RegisterCheckoutServiceServer(srv, svc)
        }
        """;

    [Fact]
    public void Go_NewServerOptions_InterceptorsInOrder_ThenServiceMethods()
    {
        var f = FrameworkFlowBuilder.Build(Input("go-service", "checkout", new() { ["checkout/main.go"] = GoMain },
        [
            new(1, "checkout", "go.grpc", "grpc", "CheckoutService/PlaceOrder", null, "checkout/main.go", 20),
            new(2, "checkout", "go.grpc", "grpc", "CheckoutService/Quote", null, "checkout/main.go", 30),
            new(3, "checkout", "go.main", "hosted", "checkout", null, "checkout/main.go", 5),
        ]));

        var root = f.Flow.Root;
        Assert.Equal(("gRPC 调用", "interceptor"), (root.Label, root.Actor));
        Assert.Equal(["logging", "auth.Unary()", "otelgrpc.NewServerHandler()", "服务方法"], root.Children.Select(c => c.Label));
        Assert.Equal("一元拦截器（grpc.ChainUnaryInterceptor，main.go:8）：包在服务方法外层，按注册顺序由外到内", root.Children[0].Note);
        Assert.StartsWith("StatsHandler（main.go:9）", root.Children[2].Note);
        Assert.Equal(("controllers", 2), (root.Children[3].Kind, root.Children[3].Count));
        Assert.Equal([("interceptor", 3), ("controller", 2)], f.Stages.Select(s => (s.Key, s.Symbols.Count)));
        Assert.Equal("认出 gRPC 服务（grpc.NewServer，main.go:7）：CheckoutService 2 个方法；没有 HTTP 框架，框架流程画 gRPC 这条链", f.Notes[0]);
        Assert.Contains("另有 1 个非 gRPC 入口（hosted 1），没画在这条链上", f.Notes);
    }

    [Fact]
    public void Python_GrpcServerInterceptorsList_AndJava_NoInterceptorExplained()
    {
        const string py = """
            import grpc
            # grpc.server(futures.ThreadPoolExecutor(), interceptors=[Commented()])
            def serve():
                server = grpc.server(futures.ThreadPoolExecutor(max_workers=10), interceptors=[TimingInterceptor(), AuthInterceptor()])
                demo_pb2_grpc.add_EmailServiceServicer_to_server(EmailService(), server)
            """;
        var p = FrameworkFlowBuilder.Build(Input("python-service", "email", new() { ["email/server.py"] = py },
            [new(1, "email", "python.grpc", "grpc", "EmailService/SendOrderConfirmation", null, "email/server.py", 10)]));
        Assert.Equal(["TimingInterceptor()", "AuthInterceptor()", "服务方法"], p.Flow.Root.Children.Select(c => c.Label));
        Assert.Equal("服务端拦截器（interceptors=，server.py:4）：按列表顺序由外到内", p.Flow.Root.Children[0].Note);

        const string java = """
            package hipstershop;
            public final class AdService {
              private void start() throws IOException {
                server = ServerBuilder.forPort(port).addService(new AdServiceImpl()).build().start();
              }
            }
            """;
        var j = FrameworkFlowBuilder.Build(Input("java-service", "ad", new() { ["ad/src/main/java/AdService.java"] = java },
            [new(1, "ad", "java.grpc", "grpc", "AdService/GetAds", null, "ad/src/main/java/AdService.java", 20)]));
        var root = j.Flow.Root;
        Assert.Equal(("gRPC 调用", "controller"), (root.Label, root.Actor));
        Assert.Equal(("服务方法", "controllers", 1), (Assert.Single(root.Children).Label, root.Children[0].Kind, root.Children[0].Count));
        Assert.Equal("认出 gRPC 服务（ServerBuilder.forPort，AdService.java:4）：AdService 1 个方法；没有 HTTP 框架，框架流程画 gRPC 这条链", j.Notes[0]);
        Assert.Equal("没有注册服务端拦截器（ServerBuilder 上没有 .intercept(…)，也没有 ServerInterceptors.intercept）：请求直接进服务方法", j.Notes[1]);
        Assert.DoesNotContain(j.Notes, n => n.Contains("不是 Web 服务"));
    }
}
