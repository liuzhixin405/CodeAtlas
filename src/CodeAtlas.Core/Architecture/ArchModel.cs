namespace CodeAtlas.Core.Architecture;

/// <summary>容器 = 可部署单元（进程 / 前端应用）；External = 容器碰到的外部系统；Person = 使用者（只来自声明）。</summary>
public enum ArchNodeKind { Container, External, Person }

/// <summary>Discovered 来自文件与代码的规则；Declared 来自 .codeatlas/architecture.yaml。</summary>
public enum ArchSource { Discovered, Declared }

/// <summary>一条事实的出处：文件 + 行；Line 为 null 表示整个文件（比如 csproj、Dockerfile）。</summary>
public sealed record Evidence(string File, int? Line = null, string? Note = null);

/// <param name="Key">稳定键：容器用项目 / 目录相对路径，外部系统用 "kind:name"。</param>
/// <param name="SubKind">container：dotnet-web / dotnet-worker / dotnet-exe / frontend / go-service / python-service / node-service / node-cli / vscode-extension / java-service；
/// external：database / cache / broker / http-service / grpc-service / native-sdk / storage / observability / email / identity / other。</param>
/// <param name="Members">容器的成员项目目录（相对仓库根，'/' 分隔），含自身；外部系统为空。</param>
/// <param name="Ports">容器声明的监听端口（launchSettings / Dockerfile EXPOSE / ASPNETCORE_URLS / Helm）。</param>
public sealed record ArchNode(
    string Key, ArchNodeKind Kind, string SubKind, string Name, string? Path, ArchSource Source,
    IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> Members, IReadOnlyList<int> Ports);

/// <param name="Protocol">http / grpc / sql / redis / kafka-produce / kafka-consume / amqp / native / unknown。</param>
/// <param name="Confidence">0.9 按路由对上；0.7 按配置地址对上；0.5 只知道客户端；声明的边 1.0。</param>
public sealed record ArchEdge(
    string FromKey, string ToKey, string Protocol, double Confidence, ArchSource Source,
    IReadOnlyList<Evidence> Evidence, string? ConfigKey = null, string? Url = null);

/// <summary>一处出口：某容器的代码里对外部的一次调用登记（HttpClient 注册、gRPC 通道、Kafka 生产者…）。TargetKey 为匹配到的节点。</summary>
public sealed record Outbound(
    string ContainerKey, string RuleId, string Protocol, string? ClientName, string? ConfigKey, string? Url,
    string File, int Line, string? TargetKey);

/// <param name="Kind">declared-not-found（声明了但发现不到）、hidden-but-used（声明隐藏但有边）、declared-overrides（声明改了发现的名字 / 种类）。</param>
public sealed record ArchConflict(string Kind, string Message);

public sealed record ArchModel(
    IReadOnlyList<ArchNode> Nodes, IReadOnlyList<ArchEdge> Edges, IReadOnlyList<Outbound> Outbound, IReadOnlyList<ArchConflict> Conflicts)
{
    public static readonly ArchModel Empty = new([], [], [], []);
}
