# CodeAtlas 架构地图：容器图 → DSM → 入口时序图 → 本地 AI 叙述 设计文档

日期：2026-10-14
状态：**草案，待用户审阅**。审阅通过后按 §9 的里程碑写计划，每个里程碑装进 VS Code 用真实项目验收。
上游：`2026-10-13-codeatlas-design.md`（总体设计）、`2026-10-14-reference-survey.md`（开源调研结论）、`2026-10-14-ccs-backend-c4-draft.md`（第一屏草稿，用户已确认"看起来没问题，5 的链路非常清晰"）。
取代：`2026-10-14-pipeline-map-design.md`（管道泳道，降级为入口列表的一种过滤）、`2026-10-14-business-map-design.md`（靠项目特有标记接口读业务，不通用，搁置）。

## 0. 这版和之前的根本区别

之前是从"索引里有哪些边"往上堆视图，于是顶层只能是命名空间或项目名。这版从**读者站在哪一层、要回答什么问题**往下设计（C4 的思路），每一层的内容都来自**框架约定**而不是某个项目的特殊写法，所以换项目效果一致；项目特有的东西（ccs-backend 的标记接口、NetArchTest 规则）作为可选叠加层。

| 层 | 回答的问题 | 内容来源 | 对应现有能力 |
|---|---|---|---|
| L1 容器图 | 系统由哪几个进程组成，各碰什么外部系统 | csproj / Dockerfile / Helm / appsettings / 客户端注册 | 新 |
| L2 组件 DSM | 一个容器里的组件怎么分层，有没有环和违规 | 现有 calls / references / implements / inherits 边 + 项目引用 | 命名空间地图的聚合逻辑 |
| L3 入口时序图 | 一个请求 / 事件 / 定时任务进来后走了哪些组件、到哪个外部系统为止 | 现有调用 / 分发 / 实现边 + 入口规则 | 调用图、管道视图 |
| L4 调用图 | 一个符号调了谁、被谁调 | 现有 | 现有 |
| 叠加 | 哪里改得多、哪些模块总一起改、哪些规则被违反、这块是干什么的 | git 历史、NetArchTest 规则、本地模型叙述 | 新 |

## 1. 范围

**做**：L1、L2、L3 三个视图及其事实层；git 行为叠加；本地 AI 叙述；规则包 v1 覆盖 ccs-backend、mt4proxy、mt5-event-publisher、ccs-ui 用到的框架；仓库级声明文件。
**不做**：云端模型；运行时 trace 接入；通用绘图引擎；自然语言搜索 / embedding；MCP。

## 2. 事实层：发现规则引擎

所有事实都带证据（文件 + 行，或配置键），存索引库，视图层只读。

### 2.1 容器发现

| 信号 | 规则 | 证据 |
|---|---|---|
| .NET 可执行项目 | `Sdk="Microsoft.NET.Sdk.Web"` / `Sdk.Worker`，或 `<OutputType>Exe</OutputType>` | csproj |
| 容器镜像 | 同目录或父目录有 `Dockerfile*`；`docker-compose*.yml` 的 service 名；Helm `Chart.yaml` + `templates/deployment.yaml` | 文件路径 |
| 前端应用 | `package.json` 含 `next` / `vite` / `react-scripts` / `@angular/core` 且有 `build` 脚本 | package.json |
| 容器名 | 优先 Helm chart 名或 compose service 名，其次项目名 | — |
| 容器成员 | .NET：可执行项目的传递项目引用；前端：package.json 所在目录 | csproj `ProjectReference` |

一个库被多个容器引用（ccs-backend 的 Application / Infrastructure 同时进 Api 与 Worker）是正常情况：组件归属按"在哪个容器里运行"记多条，DSM 按容器切片。

### 2.2 外部系统发现

| 信号 | 外部系统 | 证据 |
|---|---|---|
| `Npgsql.*` / `Microsoft.EntityFrameworkCore.SqlServer` / `MySql*` / `Dapper` + 连接字符串键 | 数据库 | 包引用 + appsettings `ConnectionStrings` |
| `StackExchange.Redis`、`IConnectionMultiplexer` | Redis | 包引用 + 注册 |
| `Confluent.Kafka` 的 `IProducer<>` / `IConsumer<>` | Kafka（区分生产 / 消费） | 代码 |
| `RabbitMQ.Client` | RabbitMQ | 包引用 |
| `Grpc.Net.Client` 的 `GrpcChannel.ForAddress(x)` | gRPC 服务（x 解析到配置键） | 代码 |
| `AddHttpClient("name", c => c.BaseAddress = …)` / `AddHttpClient<T>` | HTTP 服务（名字 + 配置键 + URL 字面量） | `DependencyInjectionSetup` 类代码 |
| `DllImport`、引用非 NuGet 的本地程序集（`<Reference Include>` / `HintPath`）、`using <厂商命名空间>` 集中出现 | 原生 / 厂商 SDK 边界（mt4proxy 的 P23、mt5-event-publisher 的 MetaQuotes） | csproj + 代码 |
| 前端 `fetch(` / `axios.*(` 的 URL 字面量与 `baseURL` 配置 | 后端 API | 代码 |
| appsettings 里形如 `*Config.Url / BaseUrl / ApiUrl` 的键 | 外部 HTTP 服务（兜底） | appsettings |

### 2.3 入口规则（规则包 v1）

| 规则 id | 入口 | 识别 | 元数据 |
|---|---|---|---|
| `aspnet.controller` | 控制器动作 | 类继承 `ControllerBase` / 带 `[ApiController]`；公开方法带 `[HttpGet]`… | 路由模板（类 `[Route]` + 方法模板，`[controller]` 展开） |
| `aspnet.minimal` | Minimal API | `app.MapGet/MapPost(...)` 的 lambda 或方法组 | 路由 |
| `aspnet.signalr` | Hub 方法 | 类继承 `Hub` | hub 路径 |
| `hosted.background` | 常驻服务 | 继承 `BackgroundService` / 实现 `IHostedService` | `ExecuteAsync` |
| `quartz.job` | 定时任务 | 实现 `Quartz.IJob` | 触发器注册里的 cron（能找到就记） |
| `kafka.consumer` | 消息消费 | `IConsumer<>` 的消费循环所在类型，或分发到的 `IEventHandler<>` | 主题配置键 |
| `rabbitmq.consumer` | 消息消费 | `EventingBasicConsumer` / `AsyncEventingBasicConsumer` 回调 | 队列名 |
| `dispatch.handler` | 分发处理器 | 现有 `handles` 边（MediatR / 自研）；它是**二级入口**，从一级入口可达 | 消息类型 |
| `frontend.route` | 页面 | React Router / Next.js `app/**/page.tsx` 与 `app/api/**/route.ts` | 路径 |
| `cli.command` | 命令行子命令 | `System.CommandLine` / 顶级语句的参数分派 | 名字 |

降级规则 `generic.root`：仓库内没有任何调用者的公开方法，标"按通用规则推断"。

### 2.4 跨容器边的匹配

出口到容器的匹配分三档，图上线的粗细 / 虚实按档位：

1. **按路由匹配（0.9）**：前端或 HttpClient 的 URL 字面量（含模板变量）与另一容器入口的路由模板规范化后匹配（`/api/v5/system/brands?mtVersion=4` ↔ `api/v5/system/[controller]/brands`）。
2. **按配置匹配（0.7）**：客户端的配置键解析出 URL（`MetaTraderManagerMT4Api:Url = http://localhost:5162/`），与另一容器声明的监听端口（`launchSettings.json`、`ASPNETCORE_URLS`、Dockerfile `EXPOSE`、Helm `containerPort` / ingress）对上。
3. **只知道客户端（0.5）**：对不上任何容器，就作为外部系统显示，名字取客户端名或配置键。

同一仓库里的多个 git 仓（mt4proxy 是独立仓库放在子目录）按目录都算在内。

### 2.5 规则文件

规则是数据不是代码：内置规则包放 `rules/*.yaml`（随 exe 发布），仓库级 `.codeatlas/rules/*.yaml` 可追加 / 覆盖。格式延续总体设计 4.7.2，增加 `entry` / `outbound` / `container` 三类。另有 `.codeatlas/architecture.yaml`（C4 风格）可**声明**容器、外部系统、关系与分层，用于：静态发现不到的（运行时配置的地址）、要改名的、要隐藏的。声明与发现冲突时图上两者都画并标"声明 / 发现不一致"。

### 2.6 存储

```
arch_nodes   (id, kind: container|external|person, key, name, path, source: discovered|declared, evidence)
arch_edges   (id, from_id, to_id, protocol, confidence, source, evidence_file, line, config_key)
entry_points (id, symbol_id, container_id, rule_id, route, kind, line)
outbound     (id, symbol_id, target_node_id, protocol, rule_id, line)
git_stats    (path, revisions, churn_added, churn_deleted, age_months, main_dev, fragmentation, last_commit)
git_coupling (path_a, path_b, shared, degree)
narratives   (scope_kind, scope_key, model, source_hash, json, contradictions, created_at)
```

索引版本 4 → 5；旧库自动全量重建（与版本 3 时相同）。

## 3. 视图层

### 3.1 L1 容器图（第一屏）

- 节点：人 / 前端在左，.NET 容器居中，外部系统在右（ELK layered，方向 RIGHT，层由 kind 固定）。节点上写名字、入口数、出口数。
- 边：协议标签（HTTP / gRPC / Kafka 生产 / Kafka 消费 / SQL / Redis / 原生），按匹配档位定粗细虚实；点边列出证据（客户端注册行、配置键、对端路由）。
- 双击容器 → L2；单击 → 右侧叙述卡（AI 叙述 + 事实计数）。
- 没有发现到任何容器（纯类库）→ 直接显示 L2，并说明原因。

### 3.2 L2 组件 DSM

- 组件 = 容器内的项目（第一层），钻入后 = 命名空间（深度可调，默认到项目名之下两段）；脚本仓库按目录。
- 单元格 = 行组件依赖列组件的边数（calls / references / implements / inherits，≥ 0.4），颜色按数量；对角线显示组件自身大小。
- 排序：Tarjan 找强连通分量，分量内合并成一块标红（环）；分量间按拓扑序排成下三角。排序确定（同序按名字）。
- 叠加：git 热点（行背景热度）、隐性耦合（有共变但没有静态边的单元格标虚框）、声明规则（NetArchTest 还原或 architecture.yaml 的分层）违规的单元格标红边框。
- 点单元格 → 该对组件之间的边列表 → 调用图。

### 3.3 L3 入口时序图

- 左栏：本容器的入口列表，按规则分组（HTTP 路由、定时、消息、常驻），可按路由前缀 / 消息类型过滤；原管道视图的"入口 → 消息 → 行为链 → Handler"信息折进每个入口的详情。
- 主区：选一个入口，沿 calls / dispatches / impl 边做深度 ≤ 12 的有序调用树，映射成时序图：
  - actor = 组件（容器内的项目或命名空间，深度与 L2 一致）→ 到外部系统为止（外部系统也是 actor）；跨容器时若对上了另一容器的入口（§2.4 第 1、2 档），时序继续进入那个容器。
  - 折叠：默认展开到深度 3（AppMap 的经验值），点开再展；相同子树按摘要折成"×N"。
  - 不确定：接口多实现的调用画全部候选、虚线标"可能"；分发边橙色；反射边界灰色节点。
  - 行为链（管道 Behavior）作为 actor 出现在入口与 Handler 之间，顺序来自 DI 注册；没有注册数据时按名字并标"顺序未知"。
- 叙述卡：这条流程"是干什么的"（AI），下面列事实：触达的外部系统、治理标记（有则显示）、绕过与孤儿等异常。

### 3.4 git 行为叠加

从 `git log --all --numstat --date=short --no-renames` 单遍计算：每文件修订数、增删行、年龄、主要作者与碎片度；逻辑耦合按提交 O(k²)，丢弃改动超过 30 个文件的提交，阈值最少 5 次修订、耦合度 ≥ 30%。聚合到组件按文件归属。子目录里的独立 git 仓各自跑。索引时顺带做，`--no-git` 可关。

### 3.5 本地 AI 叙述

- 对象：容器、组件、入口流程三种粒度，各一段话（≤ 120 字）+ 关键事实的引用。
- 输入：该对象的事实子图（节点、边、入口、出口、热点），**不给源码全文**；入口流程另给入口方法源码与调用树上每个目标的签名。预算 6000 token。
- 输出 JSON `{ summary, keyPoints: [{ text, evidence: ["node:12", "edge:88", "sym:4021"] }], risks: [...] }`；schema 校验，引用必须在输入子图里，否则该条标 `inferred`；失败重试一次。
- 缓存键 = 子图哈希 + 模型名；索引后只重算变了的对象；队列可停可续；端点连不上视图照常，叙述卡显示原因。
- 端点：OpenAI 兼容 `POST {url}/v1/chat/completions`，默认 `http://127.0.0.1:11434/v1`，并发 1，超时 120s；非回环地址时界面标"源码将发送到 X"。

## 4. 页面

- 地图模式的面包屑改为层级：`项目 › <容器> › <组件>`，三层视图随面包屑切换；调用图仍是独立模式。命名空间地图与管道视图两个标签撤掉，能力并入 L2 / L3。
- 右侧栏：叙述卡 + 事实列表 + 叠加开关（git 热点、耦合、声明规则）。
- 底部详情条保留，显示当前层级的计数与数据来源（发现 / 声明 / 推断）。

## 5. 服务接口

| 路径 | 返回 |
|---|---|
| `GET /api/arch/containers` | L1：节点、边、每容器入口 / 出口计数、声明与发现的冲突 |
| `GET /api/arch/dsm?container=&root=&depth=` | L2：组件列表（排好序）、矩阵、强连通分量、叠加数据 |
| `GET /api/arch/entries?container=&kind=&q=` | L3 左栏 |
| `GET /api/arch/flow?entry=&depth=` | L3 时序图数据（actors、有序调用树、折叠摘要、候选标记） |
| `GET /api/arch/narrative?kind=&key=` / `POST /api/arch/narrate` | 叙述卡 / 触发队列 |
| `GET /api/arch/git?container=` | 热点、耦合、年龄 |

## 6. 规则包 v1 覆盖范围（按四个验收项目反推）

ASP.NET Core 控制器与路由、SignalR Hub、`BackgroundService`、Quartz `IJob`、Kafka 生产 / 消费、RabbitMQ、`AddHttpClient` 命名 / 类型化客户端与配置键、`GrpcChannel.ForAddress`、EF Core / Npgsql、StackExchange.Redis、原生 / 厂商 SDK 边界（本地程序集引用 + 命名空间集中出现）、Next.js / React Router 路由、`fetch` / `axios` 调用、自研分发（现有 `handles` / `dispatch:T`）。

## 7. 测试与验收

- **规则**：每条规则一个 Roslyn / tree-sitter fixture，断言入口 / 出口 / 元数据与证据行；fixture 解决方案扩成"两个可执行项目 + 共享库 + 一个前端目录"，覆盖容器发现与跨容器匹配三档。
- **DSM**：排序确定性、Tarjan 环检测、违规叠加；手造 6 组件带一个环的数据钉到具体矩阵。
- **时序图**：调用树 → actors / 折叠 / 候选标记 的纯函数测试；深度 3 折叠；子树摘要折叠计数。
- **git**：用一个临时 git 仓造 10 个提交，钉修订数、耦合度、年龄。
- **AI**：假端点测 schema 校验、引用复核、缓存与续跑；不测模型质量。
- **验收（四个项目都要过，装进 VS Code 用用户工作区）**：
  - ccs-backend：L1 与 `2026-10-14-ccs-backend-c4-draft.md` 一致（5 容器、Kafka 回流、MT4 边 5162、MT5 边 5003）；L2 项目级下三角且标出 Api → Infrastructure；L3 `Mt5GroupController.BatchCreate` 走到 mt5-event-publisher 为止，行为链顺序正确。
  - mt4proxy：1 容器、1 外部边界（P23）、11 个控制器入口；一条流程 `Controller → Service → IMT4ManagerService → 原生边界`。
  - mt5-event-publisher：1 容器、入口三类（34 控制器、3 Job、2 常驻）、出口 MetaQuotes / Kafka 生产 / Redis；一条 Job 流程以 Kafka 收尾。
  - ccs-ui：1 前端容器、378 条路由为入口、487 处调用里按路由对上 Api 的比例（记录数字，不设及格线，第一版先看能对上多少）。
  - 每项记耗时；叙述卡用本地模型抽 10 条人工核对。

## 8. 诚实的边界

- 容器间的边静态只能到配置键；地址在部署环境时只能靠声明文件补。
- 时序图是"可能的调用"，不是一次真实执行：多实现画候选，循环与条件不表达，次数未知。
- 前端到后端的匹配靠 URL 字面量，拼接出来的 URL 对不上。
- git 叠加对 squash / 单一提交者的仓库信息很少。
- AI 叙述取决于本地模型质量，只影响文字。

## 9. 里程碑

| 里程碑 | 内容 | 验收 |
|---|---|---|
| **M5a 事实层 + L1 容器图**（约 2 天） | 规则引擎、容器 / 外部系统 / 出口发现、跨容器匹配、`architecture.yaml`、`/api/arch/containers`、容器图页面、索引版本 5 | 四个项目的第一屏 |
| **M5b L2 DSM + git 叠加**（约 1.5 天） | 组件切片、Tarjan 排序、矩阵页面、git 统计、热点 / 耦合叠加 | ccs-backend 项目级与命名空间级 DSM；mt4proxy 的小矩阵 |
| **M5c L3 入口时序图**（约 2 天） | 入口规则、调用树 → 时序图、折叠、候选、行为链 actor、跨容器续接、入口列表 | 四个项目各一条流程 |
| **M5d 本地 AI 叙述**（约 1.5 天 + 一次整夜运行） | provider、队列、校验、叙述卡 | 抽 10 条核对 |
| **M5e NetArchTest 规则还原**（可选，约 1 天） | 解析测试项目的 `Types.*` 链 → 禁止边 / 白名单叠加到 DSM | ccs-backend 的 `PipelineArchitectureTests` |

每个里程碑：先写计划（任务级 TDD 步骤）→ 实现 → 装进 VS Code 验收 → 用户看过 → 下一个。不再边改边发。

## 10. 待问项

1. 本地模型：本机还没装任何推理端点；M5d 之前需要你装好（Ollama + `qwen3-coder:30b-a3b` 或 llama-server），把 URL 与模型名给我。前三个里程碑不依赖它。
2. 命名空间地图与管道视图两个标签撤掉、能力并入 L2 / L3，可以吗？
3. ccs-ui 的匹配率第一版只记录不设及格线，可以吗？
