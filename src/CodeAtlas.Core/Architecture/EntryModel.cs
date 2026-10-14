namespace CodeAtlas.Core.Architecture;

/// <param name="Kind">http / hosted / job / hub / handler / page / api-route。</param>
/// <param name="Route">HTTP 路由模板（/api/v5/system/brands）、页面路径、消息类型名或 null。</param>
/// <param name="HttpMethod">GET / POST / …；非 HTTP 入口为 null。</param>
public sealed record EntryPoint(long SymbolId, string ContainerKey, string RuleId, string Kind, string? Route, string? HttpMethod, string File, int Line);

/// <summary>一处出口路径对上了另一个容器的入口。</summary>
public sealed record RouteMatch(string FromContainerKey, string File, int Line, string Path, long EntrySymbolId, string ToContainerKey);
