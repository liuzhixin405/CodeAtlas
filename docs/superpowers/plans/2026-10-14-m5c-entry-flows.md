# CodeAtlas 里程碑 5c：入口 + 按入口的时序图 + 跨容器续接 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 容器内除了组件矩阵，还能看"入口流程"：左栏列出这个容器的全部入口（HTTP 路由、常驻服务、定时任务、分发处理器、前端页面），选一个就画出它从入口到外部系统为止的时序图，跨容器的调用（前端 → Api、Api → Publisher）接着画进对方容器。前端的 fetch 路径对上控制器路由后，容器图上那条边升到 0.9。

**Architecture:** Server 新增 `EntryDiscovery`（从索引的继承 / 实现边加源码特性文本认出入口，带路由）、`RouteLinker`（出口路径 ↔ 入口路由匹配，升级容器图的边）、`FlowBuilder`（从入口沿 calls / impl / dispatch 边做有序调用树，按子树摘要折循环，主干优先限宽，跨容器续接，映射成 actor）。Core 新增 `EntryStorage`（`entry_points` / `route_matches`，索引版本 6）。索引结束时与容器图、git 一起算。接口 `GET /api/arch/entries`、`GET /api/arch/flow`。页面 L2 加子标签"组件矩阵 | 入口流程"，时序图用 CSS Grid 画（列 = actor，行 = 调用），默认折叠到深度 3。

**Spec:** `2026-10-14-architecture-map-design.md` §2.3、§2.4、§3.3。

## 裁定
1. 入口规则 v1 用索引事实 + 源码文本：控制器靠继承链到 `ControllerBase` / `Controller`，路由靠类与方法声明上方的 `[Route]` / `[HttpX]` 文本；常驻服务靠继承 `BackgroundService` / 实现 `IHostedService`；定时任务靠实现 `IJob`；SignalR 靠继承 `Hub`；Minimal API 靠文件里的 `MapGet/MapPost(...)` 字面量；分发处理器直接用 `handles` 边；前端页面按 Next.js / React Router 的文件路径约定。`generic.root`（无调用者的公开方法）不做，列表里没有的就是没有。
2. 流程里走的是**运行时去向**：calls（含 via=impl 的接口 → 实现派生边）与 dispatch 边；不走 references / inherits / implements，也不走 conduit。这和 DSM 的"编译期依赖"相反，各自正确。
3. 跨容器续接只做两种：前端 `fetch` / `axios` 的路径字面量对上控制器路由；C# 方法里出现路径字面量且同方法调用了 HttpClient 方法，对上另一容器的路由。对不上就停在"HTTP 调用 → 外部"。
4. 行为链不作为 actor 画进时序图（分派边已经直接到 Handler），在流程头部列出注册顺序作提示。
5. 页面按 AppMap 的经验：默认展开到深度 3，相同子树折成 ×N，每个节点最多展开 6 个分支，其余折成"还有 N 个"。

## Review Focus
1. 控制器继承链：`Mt5CommonController : BaseController : ControllerBase` 这种中间基类要认出来（ccs-backend 大量如此）。
2. 路由模板拼接：类 `[Route("api/v5/[controller]")]` + 方法 `[HttpGet("brands")]` → `/api/v5/system/brands`，`[controller]` 去掉 `Controller` 后缀；方法没有模板时就是类路由。
3. 前端路径匹配要容忍模板变量：`/api/v5/accounts/{id}` 与 `/api/v5/accounts/123`、`${id}` 都要对上；`/api/proxy-gateway/api/v5/...` 这种代理前缀对不上就是对不上，不猜。
4. 流程不能爆：深度 12、每节点 6 分支、总节点 300；环（A 调 B 调 A）用祖先集合剪掉。
5. 子仓库符号未索引时，续接到 Publisher 停在"HTTP → Publisher（容器，无符号）"，说明原因。

## File Structure
```
src/CodeAtlas.Core/Architecture/EntryModel.cs        EntryPoint / RouteMatch 记录
src/CodeAtlas.Core/Storage/EntryStorage.cs           entry_points / route_matches；版本 6
src/CodeAtlas.Server/MapBuilder.cs                   MapSymbol 增加 StartLine / EndLine / Visibility（可选参数）
src/CodeAtlas.Server/EntryDiscovery.cs               入口规则
src/CodeAtlas.Server/RouteLinker.cs                  路径 ↔ 路由匹配；升级容器图边到 0.9
src/CodeAtlas.Server/FlowBuilder.cs                  调用树 → 时序图 DTO
src/CodeAtlas.Server/IndexService.cs                 索引后算入口与匹配；Entries / Flow
src/CodeAtlas.Server/ServerHost.cs                   /api/arch/entries、/api/arch/flow
src/CodeAtlas.Cli/Commands/ArchCommand.cs            --entries <容器> / --flow <符号 id>
extension/webview/flowLayout.ts                      FlowDto → actor 列、行、折叠（纯函数）
extension/webview/flowView.ts                        入口列表 + 时序图 DOM
extension/webview/main.ts                            L2 子标签；mapView "flow"
tests/CodeAtlas.Server.Tests/EntryDiscoveryTests.cs / RouteLinkerTests.cs / FlowBuilderTests.cs
extension/test/flowLayout.test.ts
```

### Task 1: 模型与存储
- [x] `EntryPoint(SymbolId, ContainerKey, RuleId, Kind, Route, HttpMethod, Line)`、`RouteMatch(FromContainerKey, File, Line, Path, EntrySymbolId, ToContainerKey)`；两张表；版本 6；写读测试。
- [x] `MapSymbol` 加 `StartLine`、`EndLine`、`Visibility`；`MapData.Load` 填充。

### Task 2: EntryDiscovery（TDD）
- [x] fixture：控制器（经中间基类）两个动作带路由；BackgroundService；IJob；Hub；Minimal API 文件；Handler（handles 边）；前端 `app/orders/page.tsx`、`app/api/chat/route.ts`、`app/(auth)/login/page.tsx`；测试项目里的控制器要排除。
- [x] 断言每个入口的 kind / route / method / line；继承链三层；`[controller]` 展开。
- [x] eShop 逼出来的补充：Minimal API 的 `MapGroup` 前缀（变量链与同一行链式）拼进路由；`MapGet("/x", Handler)` 方法组时入口落到 Handler 方法；Roslyn 符号起始行在特性行时向下扫特性。

### Task 3: RouteLinker（TDD）
- [x] 模板匹配：`{id}` 与具体值、`${x}`；查询串去掉；方法前缀匹配不要求（路径对上即可，方法有就比）。
- [x] 升级容器图边：前端容器 → Api 容器 http 0.9，证据是 fetch 行；对应的"API /api/v5"外部节点在全部路径都对上时移除，部分对上保留并标数量。
- [x] C# 方法内路径字面量 + HttpClient 调用 → 另一容器入口。
- [x] eShop 逼出来的补充：路径前缀放在 `string` 字段里、方法里 `$"{field}items/{id}"` 插值或直接传字段名的写法也能拼出路径；同路径 GET / POST 两个入口按调用方式挑。

### Task 4: FlowBuilder（TDD）
- [x] 调用树：从入口方法出发，calls（含 impl）与 dispatch:*，排除 conduit / references；祖先剪环；深度 12；每节点 6 分支，排序：分派 > 容器内调用 > 外部；外部合并成一个"N 个框架调用"节点。
- [x] actor：容器内按成员项目；外部按名字首段；跨容器续接节点带容器名；子仓库无符号时停住并说明。
- [x] 子树摘要折叠：相邻相同子树合成 ×N。
- [x] DTO：`FlowDto(Entry, Actors[], Root, Behaviors[], Truncated, Note)`。
- [x] 行为链按 implements 边先建索引（原来是 类 × 边 的扫描，ccs-backend 上 `--flow` 6.9s → 1.1s；配合 IndexService 整库符号 / 边只加载一次）。

### Task 5: 接线与 CLI
- [x] `IndexService`：索引后 EntryDiscovery + RouteLinker（更新 arch_edges）→ 存库；`Entries(container, kind, q)`、`Flow(symbolId, depth)`；路由；ApiTests。
- [x] CLI `arch --entries <容器>`、`arch --flow <symbolId>` 打印（验收用）。

### Task 6: 页面
- [x] `flowLayout.ts`：actor 列顺序（入口所在 actor 第一，外部最后，其余按首次出现）、行序（先序遍历）、折叠状态（深度 3）、箭头方向与 grid 列范围；vitest 7 个。
- [x] `flowView.ts`：左栏入口列表（按 kind 分组、搜索框），右侧时序图（CSS Grid，列头 actor，行 = 调用，点节点展开 / 折叠，点标签聚焦到调用图）。
- [x] `main.ts`：L2 子标签"组件矩阵 | 入口流程"，面包屑 `项目 › 容器 › 入口流程 › <入口>`。

### Task 7: 验收
- [x] ccs-backend：Api 容器 http 489 / hosted 12 / hub 8 / handler 547 / job 0；路由匹配 4 处 → ccs-ui → Api 0.9；`BatchCreateGroups` 流程走到 `SendAndReadDataAsync`（MT5 管理 API）；Kafka 消费流程 17 节点。数字与流程在 `docs/acceptance.md` 5c 节。
- [x] eShop 对照（换项目验证通用性）：孤岛 → 容器间 0.7 / 0.9 边，路由匹配 0 → 28；补的七条都是通用规则。
- [x] `docs/acceptance.md` 5c 节；指南 §8.0.2；浏览器模式验证；VSIX 打包（`extension/codeatlas-0.1.0.vsix`）。
- [ ] 装进 VS Code 验收（等用户）。
