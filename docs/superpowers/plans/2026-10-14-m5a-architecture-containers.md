# CodeAtlas 里程碑 5a：架构事实层 + L1 容器图 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 打开任何多项目仓库，地图第一屏是 C4 容器图：可部署单元、外部系统、它们之间带协议与置信度的边，每条边能指到证据。在 ccs-backend、mt4proxy、mt5-event-publisher、ccs-ui 四个项目上验收。

**Architecture:** Core 新增 `CodeAtlas.Core.Architecture`：`ProjectInventory` 读文件系统（csproj / package.json / Dockerfile / Helm / launchSettings / appsettings），`ContainerDiscovery` 定容器与成员与监听端口，`ExternalDiscovery` 用一组 `IDiscoveryRule`（C# 实现，带 id 与说明）从包引用、csproj 本地程序集引用、源码文本模式（`AddHttpClient`、`GrpcChannel.ForAddress`、`IProducer<>` / `IConsumer<>`、`IConnectionMultiplexer`、`DllImport`、前端 `fetch` / `axios`）产出出口与外部系统，`EdgeMatcher` 把出口按三档匹配到容器或外部系统，`ArchitectureDeclarations` 读 `.codeatlas/architecture.yaml` 合并声明，`ArchitectureScanner` 串起来并写库（`arch_nodes` / `arch_edges` / `outbound`，索引版本 5）。索引结束后由 CLI 与 Server 各自调用一次。Server 暴露 `GET /api/arch/containers`；页面地图模式的第一屏换成容器图，双击容器钻到该容器的命名空间地图（L2 DSM 在 5b 替换）。

**Tech Stack:** 不变 + `YamlDotNet`（Core）。

**Spec:** `docs/superpowers/specs/2026-10-14-architecture-map-design.md` §2、§3.1、§5、§7。

## 裁定（与设计稿的偏差，先说清楚）

1. **规则 v1 用 C# 类实现，不做 YAML 规则 DSL**。设计稿 §2.5 说规则是 YAML 数据；为所有源码模式设计一门 DSL 比写规则本身贵得多，v1 先把规则做成有 id、有说明、可单测的 C# 类，仓库级扩展靠 `architecture.yaml` 声明。DSL 等规则稳定后再抽。
2. **源码模式用文本匹配（正则 + 同方法内回溯）而不是 Roslyn**。容器级事实几十处，正则够用且不依赖 Roslyn 阶段是否跑过；每条命中都带文件行作证据，匹配不到就降档，不猜。
3. **入口计数在 5c**。L1 节点上先显示成员项目数与出口数。
4. **双击容器暂时钻到命名空间地图**（root 取该容器成员项目的根命名空间）；5b 换成 DSM。

## Global Constraints

- 事实必须带证据：节点带发现它的文件，边带文件 + 行或配置键；没有证据的只能来自声明文件并标 `declared`。
- 排除目录沿用 `IndexOptions.DefaultIgnoredDirectories`，另排除测试项目（含 `Microsoft.NET.Test.Sdk` 包或项目名匹配测试模式）成为容器。
- 子目录里的独立 git 仓（mt4proxy、mt5-event-publisher、ccs-ui）照常纳入。
- 计数钉具体数字；每条规则有正例与反例。

## Review Focus

1. **一个库被多个容器引用**（Application 同时进 Api 与 Worker）：成员归属要记多条，外部系统的边要分别挂到两个容器。
2. **匹配档位不能虚高**：只有 URL 的 host:port 与另一容器的声明端口真对上才是 0.7；客户端名字像不算。
3. **appsettings 的多环境文件**：取 `appsettings.json` 为准，`appsettings.*.json` 的值作为备选记录，不覆盖。
4. **前端 URL 拼接**：`${baseUrl}/api/v5/...` 这种模板只取路径部分；纯变量 URL 不产出边。
5. **版本 5 的重建**：旧库打开 `NeedsRebuild = true`，扩展已有自动全量重建逻辑；新表用 `CREATE TABLE IF NOT EXISTS`。

## File Structure

```
src/CodeAtlas.Core/Architecture/ArchModel.cs             ArchNode / ArchEdge / Outbound / Evidence 记录与 kind 枚举
src/CodeAtlas.Core/Architecture/ProjectInventory.cs      文件系统读取：csproj、package.json、Dockerfile、Helm、launchSettings、appsettings 扁平化
src/CodeAtlas.Core/Architecture/ContainerDiscovery.cs    可执行项目 / 前端 → 容器；成员（传递项目引用）；监听端口
src/CodeAtlas.Core/Architecture/Rules/*.cs               IDiscoveryRule 与 v1 规则（包引用映射、HttpClient、gRPC、Kafka、Redis、数据库、原生 / 厂商 SDK、前端 HTTP）
src/CodeAtlas.Core/Architecture/EdgeMatcher.cs           出口 → 容器（0.7）/ 外部系统（0.5）；0.9 路由匹配留给 5c
src/CodeAtlas.Core/Architecture/ArchitectureDeclarations.cs  .codeatlas/architecture.yaml
src/CodeAtlas.Core/Architecture/ArchitectureScanner.cs   串联 + 写库 + 报告
src/CodeAtlas.Core/Storage/Schema.cs                     arch_nodes / arch_edges / outbound；CurrentIndexVersion = 5
src/CodeAtlas.Core/Storage/IndexReader.cs                GetArchNodes / GetArchEdges / GetOutbound
src/CodeAtlas.Cli/Commands/ArchCommand.cs                codeatlas arch <repo> [--db] [--json]：打印容器、外部系统、边与证据
src/CodeAtlas.Cli/Commands/IndexCommand.cs               索引后跑 scanner
src/CodeAtlas.Server/IndexService.cs                     索引后跑 scanner；Architecture() 缓存
src/CodeAtlas.Server/ServerHost.cs                       GET /api/arch/containers
src/CodeAtlas.Server/Dtos.cs                             ArchDto 等
extension/webview/archLayout.ts                          DTO → 分层元素（人 / 前端 | 容器 | 外部）纯函数
extension/webview/archRenderer.ts                        cytoscape + ELK，边样式按协议与档位
extension/webview/main.ts                                地图第一屏 = 容器图；双击钻到命名空间地图；详情条
tests/CodeAtlas.Core.Tests/Architecture/*.cs            fixture 仓库（临时目录生成）+ 每条规则的正反例 + 匹配 + 声明合并 + 写库读库
tests/CodeAtlas.Server.Tests/ApiTests.cs                /api/arch/containers 在纯 TS 仓库上的空结果
extension/test/archLayout.test.ts
docs/vscode-extension-guide.md                          §8 重写为"容器图"
docs/acceptance.md                                      四个项目的实测
```

### 实施记录（2026-10-14）
- Task 1–4 全部完成：`ArchModel` / `ArchStorage`（索引版本 4）、`ProjectInventory`、`ContainerDiscovery`、6 条规则（`package-reference`、`local-assembly`、`http-client`、`grpc-channel`、`infra-pattern`、`frontend-http`）、`EdgeMatcher`、`ArchitectureDeclarations`、`ArchitectureScanner`；Core 测试 63 个全绿，其中架构 12 个。
- Task 5 完成：CLI `arch` 子命令与 `index` 后自动扫描；Server `/api/arch/containers`（含 NamespaceRoots 与出口数）；页面"容器 | 管道 | 命名空间"三视图，默认容器图，双击容器钻到命名空间地图；vitest 59 个全绿。
- 真实数据逼出的修正：`.claude/worktrees` 等点目录要跳过（否则容器重复 6 份）；`var x = configuration` 跨行链式赋值要拼到分号再解析；`Options.DefaultName` 与小写变量名的 `AddHttpClient` 转发不是客户端；同名客户端"先占名再配地址"要合并；ccs 用 `ConsumerBuilder<>` 不是 `IConsumer<>`；前端脚本名是 `build:dev` 不是 `build`；MetaTrader 的 NuGet 包要进厂商 SDK 表；根命名空间取多数派前缀（漏写前缀的命名空间会把公共前缀拉成空串）。
- 偏差记录：规则是 C# 类不是 YAML（见"裁定"）；5a 不含入口与路由匹配（0.9 档）。

### Task 1: 模型、库表、读取（TDD）
- [x] `ArchModel`：`ArchNodeKind { Container, External, Person }`、`ArchSource { Discovered, Declared }`、`Evidence(File, Line?, Note)`；`ArchNode(Key, Kind, Name, Path, Source, Kind 细分 subkind: dotnet-web / dotnet-worker / frontend / database / cache / broker / http-service / grpc-service / native-sdk / other, Evidence[])`；`ArchEdge(FromKey, ToKey, Protocol, Confidence, Source, Evidence, ConfigKey?)`；`Outbound(ContainerKey, RuleId, TargetKey, Protocol, File, Line, ConfigKey?, Url?)`。
- [ ] Schema 新增三张表；`CurrentIndexVersion = 5`；`IndexDatabaseTests` 断言旧版本库 `NeedsRebuild`。
- [ ] `ArchWriter.Replace(model)`（整表替换，一个事务）与 `IndexReader.GetArch*`；内存库写读回环测试，钉数量与字段。

### Task 2: ProjectInventory 与容器发现（TDD）
- [ ] fixture 生成器 `ArchFixture.Create()`：临时目录里写 `Acme.sln`、`Acme.Api/Acme.Api.csproj`（`Sdk.Web`，引用 `Acme.App`，`Properties/launchSettings.json` 端口 5001，`Dockerfile` `EXPOSE 8080`，`appsettings.json` 含 `Billing:Url = http://localhost:5002/`、`ConnectionStrings:Db`、`Kafka:BootstrapServers`）、`Acme.Worker/…csproj`（`OutputType Exe`，引用 `Acme.App`）、`Acme.App/…csproj`（库，包引用 Npgsql、StackExchange.Redis、Confluent.Kafka）、`Acme.Tests/…csproj`（含 `Microsoft.NET.Test.Sdk`，引用 Api）、`billing/Billing.Api.csproj`（`Sdk.Web`，launchSettings 5002）、`vendor/Vendor.csproj`（`<Reference Include="MetaQuotes.X"><HintPath>…`）、`web/package.json`（next + build 脚本）与 `web/src/api.ts`（`fetch(\`${base}/api/v5/orders\`)`、`axios.get("/api/v5/users")`）。
- [ ] 测试：容器 = `Acme.Api`（dotnet-web，端口 5001 与 8080，Dockerfile 证据）、`Acme.Worker`（dotnet-worker）、`Billing.Api`、`web`（frontend）；`Acme.Tests` 不是容器；`Acme.Api` 与 `Acme.Worker` 的成员都含 `Acme.App`；`vendor` 不是容器（库）。
- [ ] 实现 `ProjectInventory`（csproj XML、package.json、Dockerfile EXPOSE、launchSettings applicationUrl、appsettings 扁平成 `A:B:C` 键，环境文件另存）与 `ContainerDiscovery`。

### Task 3: 规则 v1 与出口（TDD，每条规则一个测试类）
- [ ] `PackageReferenceRule`：Npgsql → database(postgresql)、`Microsoft.EntityFrameworkCore.SqlServer` → database(sqlserver)、StackExchange.Redis → cache(redis)、Confluent.Kafka → broker(kafka，方向待代码规则细化)、RabbitMQ.Client → broker(rabbitmq)、AWSSDK.S3 → storage(s3)；外部节点 key 按种类去重；边挂到引用该包的每个容器。
- [ ] `HttpClientRule`：`AddHttpClient(<名字或常量>` → 同方法内回溯找 `configuration["A:B"]` / `GetSection("A")` / `IOptions<T>` / `"A:B"` 字面量 → 配置键；`new Uri(x)` 的 x 若是字符串字面量直接记 URL；出口带客户端名、配置键、URL；无参 `AddHttpClient("Lark")` 也记（无 URL）。
- [ ] `GrpcRule`：`GrpcChannel.ForAddress(x)` 同上。
- [ ] `KafkaRule`：`IProducer<` → kafka 生产；`IConsumer<` / `.Subscribe(` → kafka 消费；`RabbitRule` 同理。
- [ ] `RedisRule`（`IConnectionMultiplexer`）、`DatabaseRule`（`: DbContext`、`UseNpgsql(`、`NpgsqlConnection`）。
- [ ] `NativeSdkRule`：csproj `<Reference Include="X"><HintPath>` → native-sdk 节点 "X"（去掉位数后缀）；`[DllImport("lib")]` → native-sdk "lib"。
- [ ] `FrontendHttpRule`：`.ts/.tsx/.js` 里 `fetch(` / `axios.<m>(` / `<client>.<m>(` 的第一个参数：字符串或模板字面量 → 取 `/api/...` 路径；纯变量 → 不产出；记录 `baseURL` / `getApiBaseUrl` 的配置。
- [ ] 每条规则：正例命中带正确行号与键；反例（注释里的、字符串里的、测试项目里的）不命中。

### Task 4: 匹配、声明、串联（TDD）
- [ ] `EdgeMatcher`：出口的 URL（字面量或配置键解析到 appsettings 值）→ `host:port`；与其他容器的端口集合对上 → 边 0.7 `discovered`；对不上 → 外部节点（名字 = 客户端名或配置键去掉 `Url`），边 0.5；fixture 里 `Billing:Url` 5002 → `Acme.Api → Billing.Api` 0.7；前端 `/api/v5/...` 在 5a 没有路由表，挂到"后端 API（待匹配）"外部节点 0.5，5c 升级。
- [ ] `ArchitectureDeclarations`：解析 `.codeatlas/architecture.yaml`（containers / externals / relationships，字段 key / name / kind / hide / from / to / protocol）；合并：同 key 覆盖名字与 kind、`hide` 去掉、新 key 新增 `declared`；声明边与发现边都保留，`conflicts` 列出"声明了但发现不到"与"隐藏了但发现到有边"。
- [ ] `ArchitectureScanner.Scan(root, db)`：inventory → containers → rules → matcher → declarations → 写库 → `ArchReport(containers, externals, edges, outboundUnmatched, elapsed)`。fixture 全量断言：节点 4 容器 + 外部 {postgresql, redis, kafka, MetaQuotes.X, 后端 API} 等，边数与档位逐条钉。

### Task 5: 接线与页面
- [ ] CLI：`IndexCommand` 索引后跑 scanner 并打印报告；新 `ArchCommand`（表格 / `--json`）。
- [ ] Server：`IndexService` 索引后跑 scanner；`Architecture()` 读库拼 `ArchDto { Nodes, Edges, Conflicts, Containers[].NamespaceRoots }`（根命名空间 = 成员项目目录下符号的公共命名空间前缀）；`GET /api/arch/containers`；`ApiTests` 纯 TS 仓库返回 0 容器与说明。
- [ ] 页面：`archLayout.ts`（kind → 层：person / frontend = 0，container = 1，external = 2；边样式类 = 协议 + 档位）+ 测试；`archRenderer.ts`（ELK layered RIGHT，层用 `elk.layered.layering.strategy` 固定或用 `partition`）；`main.ts` 地图默认第一屏 = 容器图，面包屑 `项目 › <容器>`，双击容器钻到命名空间地图（root 取 `namespaceRoots[0]`，多个时列出让选）；详情条：节点证据、边证据与档位说明；没有容器时直接显示命名空间地图并说明。
- [ ] `npm run typecheck && npm test && npm run build`；`dotnet test`。

### Task 6: 验收（四个项目，装进 VS Code）
- [ ] `scripts/publish.ps1` → `npm run package` → 安装 VSIX。
- [ ] ccs-backend：对照 `2026-10-14-ccs-backend-c4-draft.md`——5 容器（ccs-ui、Api、Worker、mt4proxy、mt5-event-publisher）；边 Api → mt4proxy 0.7（5162）、Api → mt5-event-publisher 0.7（5003）、Kafka 消费 / 生产方向、Lark / Postgres / Redis / RabbitMQ / MetaQuotes / P23；测试项目不出现。记录数字与耗时。
- [ ] mt4proxy 单独打开：1 容器 + P23 边界。mt5-event-publisher 单独打开：1 容器 + MetaQuotes / Kafka 生产 / Redis / RabbitMQ。ccs-ui 单独打开：1 前端容器 + "后端 API（待匹配）"，HTTP 调用数记录。
- [ ] 把结果与偏差写进 `docs/acceptance.md`；文档 §8 重写。
