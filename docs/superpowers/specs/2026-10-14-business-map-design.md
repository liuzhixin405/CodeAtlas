# CodeAtlas 业务地图：AI 读代码整理业务 → Roslyn 出事实 → AI 选版式 设计文档

日期：2026-10-14
状态：**已搁置**（2026-10-14 当日）。靠 ccs-backend 的标记接口读业务不通用，换项目没有事实来源；方向改为 `2026-10-14-architecture-map-design.md`（C4 容器图 → DSM → 入口时序图 → 本地 AI 叙述）。本文保留作为"项目特有标记作为叠加层"的素材。
上游：`2026-10-13-codeatlas-design.md`（总体设计，§6 AI 集成、§4.7 间接调用链）、`2026-10-14-pipeline-map-design.md`（管道视图，本文把它降级为一种预设版式）。

## 0. 为什么要重做

管道视图把命名空间翻译成了泳道。用户的评价：

> 如果只是把代码翻译一下我感觉做出来的完全没有什么必要，应该要理解代码然后整理出业务，切合实际。

> MT4 / MT5 / Common / Approvals / PluginHub 这个列举非常不恰当，Approvals 不是业务，Common 是公共部分，它们只是业务的一部分。MT4 MT5 又没有细分。

取证后发现 ccs-backend 的业务其实已经写在代码里，只是地图没有读：

| 事实 | 证据 |
|---|---|
| 每条命令用标记接口声明业务属性：`IRequiresPermission`（`PermissionCategory.Group`、`ActionEnum.Create`、ServerId）、`IAuditable`（`AuditModule = Group`、`AuditVersion = MT5`、`AuditAction = Create`、`AuditTarget`） | `Application/Modules/MT5/Groups/Commands/BatchCreateMt5GroupsCommand.cs` |
| 标记接口共 21 个：`IApprovable`、`IApprovalLinkedJob`、`IAuditable`、`IBackgroundJobAuditable`、`IHasComparisonCacheKey`、`IServerIndependent`、`IPatchesEntity`、`IBatchRequestData`、`IOccupiesApprovalTargets`、`IFieldRiskAction`、`IReleasesEditLockOnSubmit`… | `Application/Abstractions/Markers/` |
| 管道里的 Behavior 就是按这些标记决定做什么：`ApprovalBehavior` 查 `IHasComparisonCacheKey` / `IOccupiesApprovalTargets` / `IBatchRequestData` / `IAuditable` / `IFieldRiskAction`；`AuditBehavior` 查 `IBackgroundJobAuditable` / `IServerIndependent` | `Application/Behaviors/*.cs` |
| 业务粒度在命名空间第三层以下：MT5.TradingAccounts 44 个消息、MT5.Symbols 43、MT4.Accounts 35、MT4.Groups 29…共 41 个候选单元；Approvals、Common 与它们并列但不是业务 | Roslyn 索引 `handles` 边统计（2026-10-14） |
| 本机没有在跑的本地模型（Ollama 11434 / LM Studio 1234 都连不上）；核显 Intel Arc 140T，内存 64GB | 本机探测 |

结论：**业务对象、平台、动作、治理要求这四个维度项目自己已经定义好了**，地图应该读出来，而不是猜；读不出来的部分再交给 AI，且 AI 的每句话都要能指回代码。

## 1. 用户定下的方向

1. 三段式：**AI 读代码分析出业务 → Roslyn 画地图（出事实）→ AI 分析地图、按业务调整呈现**。
2. AI 用**本地模型**。
3. 第三段 AI **只在预设版式里选并填参数**，不自由描述版式。

## 2. 范围

**做**：

1. 第一段（事实）：Roslyn 多提取一类"标签"事实——类型实现的标记接口、显式接口实现里的常量 / 枚举值、类型与方法上的特性。存索引库，可查询。
2. 第二段（业务卡片）：以"消息 + Handler + 调用链"为业务单元，本地模型按固定 JSON 契约产出卡片；每个字段带证据引用，服务端校验；按源码哈希缓存，可中断续跑。再做一遍仓库级归纳：卡片聚成业务域、起名、区分业务与支撑。
3. 第三段（视图方案）：服务端算好度量，本地模型从预设版式里选一种并填参数，输出 JSON 方案；页面确定性渲染；方案可锁定、可手改。
4. 本地模型接入：OpenAI 兼容端点（llama-server / Ollama / LM Studio），配置、超时、并发 1 起步、离线 CLI 子命令。
5. 页面：地图模式的第三个视图"业务"，没有方案时退回管道 / 命名空间视图。

**不做**：

- 不让 AI 自主翻仓库（不给工具、不做 agent 循环）；模型只看服务端喂给它的内容。
- 不用云端模型；不做自动回退到云端。
- 不改边的来源：AI 不产生任何边，不改任何 confidence。
- 不做通用绘图引擎：版式只有预设的几种。
- 自然语言搜索、embedding、MCP 不在本次。

## 3. 架构

```
索引库（唯一真相）
├─ symbols / edges / di_registrations           已有
├─ symbol_tags                                  第一段新增：标记接口、常量值、特性（Roslyn 产出）
├─ business_cards                               第二段：每个业务单元一张卡片（AI 产出，字段带证据，已校验）
├─ business_domains                             第二段：仓库级归纳（域、名称、业务 / 支撑、成员卡片）
└─ view_plans                                   第三段：视图方案（AI 选的版式 + 参数；用户可改、可锁定）

CodeAtlas.Ai（新项目）
├─ IChatProvider / OpenAiCompatibleProvider      总体设计 §6.1 的子集：只做非流式 JSON 补全 + 流式解释
├─ CardPrompt / DomainPrompt / PlanPrompt        三段提示词：输入裁剪、输出 JSON schema、校验
└─ BusinessPipeline                              队列：取待算单元 → 组上下文 → 调模型 → 校验 → 写库；可中断续跑

CodeAtlas.Server
├─ GET /api/business/status  POST /api/business/run|stop   进度与控制
├─ GET /api/business/map?domain=                             按视图方案组装好的渲染数据
└─ GET/PUT /api/business/plan                                查看 / 手改 / 锁定方案

CLI：codeatlas business <repo> [--cards] [--domains] [--plan] [--db] [--ai-url] [--model]
```

**红线**：事实层（符号、边、标签）只来自解析器；AI 产出的每个字段要么引用事实（符号 id + 行号），要么标 `inferred`。与事实矛盾的字段不采信、记录为矛盾。

## 4. 第一段：标签事实（Roslyn）

### 4.1 提取什么

| 标签 | 例子（ccs-backend） | 来源 |
|---|---|---|
| `implements:<接口限定名>` | `implements:…Markers.IApprovable` | `INamedTypeSymbol.AllInterfaces`（含经基类间接实现的） |
| `prop:<接口>.<成员>` = 常量值 | `prop:IRequiresPermission.PermissionCategory` = `PermissionCategory.Group`；`prop:IAuditable.AuditVersion` = `MTVersionEnum.MT5` | 显式接口实现的表达式体 / getter 单 return；`SemanticModel.GetConstantValue` 或枚举成员访问的符号显示名；非常量记 `<dynamic>` |
| `attr:<特性限定名>` = 构造参数字面量 | `attr:…AuthorizeAttribute` = `"Admin"` | `GetAttributes()`，只记字面量参数 |

只对仓库内类型提取；接口本身也是仓库内的才算（`IEquatable<T>` 这类框架接口不记，避免噪声）。

### 4.2 存储与版本

```
symbol_tags (id, symbol_id → symbols, tag TEXT, value TEXT NULL, line INTEGER)
索引 ix_symbol_tags_symbol(symbol_id), ix_symbol_tags_tag(tag)
```

Worker 新增记录 `TagRec(TypeQualifiedName, Tag, Value, Path, Line)`；`RoslynApplier` 按文件删旧标签再写新；索引版本 3 → 4（新表用 `CREATE TABLE IF NOT EXISTS`，旧库触发全量重建，和版本 3 时一样）。

### 4.3 对账目标（ccs-backend）

`IRequiresPermission` 的实现数、`IAuditable` 的实现数、`IApprovable` 的实现数，与 `grep -l` 源码的文件数一致（允许 partial 类型造成的差异，写明原因）。

## 5. 第二段：业务卡片与业务域（AI）

### 5.1 业务单元

一个单元 = 一个消息类型 + 它的全部 Handler + Handler 入口方法的调用链（深度 3，按管道视图的主干规则取前 N 个分支）。ccs-backend 约 500 个单元。没有 Handler 的类型不是单元。

### 5.2 喂给模型的内容（服务端组装，预算 6000 token）

1. 消息类型源码全文（通常几十行）。
2. 消息类型的标签（§4）——**明确告诉模型这些是事实**。
3. Handler 入口方法源码全文；超预算先裁掉日志 / 异常处理块。
4. 调用链里每个目标的签名 + 所属命名空间 + 是否 external；external 的给程序集名（`MT5 API client`、`StackExchange.Redis`、`Confluent.Kafka`…）。
5. 入口类型名（哪些控制器 / Job 分发它）。

不给模型工具，不让它要更多文件。

### 5.3 输出契约（JSON，服务端用 schema 校验，不合格重试一次后标失败）

```json
{
  "object":   { "value": "MT5 Group",            "evidence": ["tag:prop:IAuditable.AuditModule=Group", "tag:prop:IAuditable.AuditVersion=MT5"] },
  "action":   { "value": "批量创建",              "evidence": ["tag:prop:IAuditable.AuditAction=Create", "tag:implements:IBatchRequestData"] },
  "platform": { "value": "MT5",                   "evidence": ["tag:prop:IAuditable.AuditVersion=MT5"] },
  "governance": {
    "permission": { "value": true,  "evidence": ["tag:implements:IRequiresPermission"] },
    "approval":   { "value": false, "evidence": [] , "note": "没有实现 IApprovable" },
    "audit":      { "value": true,  "evidence": ["tag:implements:IAuditable"] },
    "job":        { "value": false, "evidence": [] }
  },
  "externalSystems": [ { "value": "mt5-event-publisher", "evidence": ["sym:48213"] } ],
  "summary": { "value": "把一批组配置下发到指定 MT5 服务器", "inferred": true },
  "risks":   [ { "value": "批量操作没有审批", "inferred": true } ]
}
```

证据格式：`tag:<tag>=<value>`（必须在该单元的标签里存在）、`sym:<id>`（必须在喂给它的调用链里）。校验规则：

- `evidence` 引用不存在 → 该字段改为 `inferred`，记一条矛盾。
- `governance.*.value = true` 但标签里没有对应标记接口 → 不采信，按标签改为 false，记矛盾；反之同理。也就是说**治理四项最终以标签为准**，AI 只负责解释和补充 `note`。
- `object` / `action` / `platform` 有标签证据时以标签为准；没有标签（项目没打标记）时接受 AI 的值并标 `inferred`。

### 5.4 缓存与失效

`business_cards (message_id, card JSON, source_hash, model, created_at, contradictions INTEGER)`。`source_hash` = 消息源码 + Handler 入口源码 + 标签集合 + 调用链目标限定名列表 的哈希。索引后比对哈希，只重算变了的。队列可随时停，下次续跑；进度在 `/api/business/status`。

### 5.5 仓库级归纳

输入：全部卡片的 `object / platform / action / governance / externalSystems`（不给源码，几百行表格）。输出：

```json
{
  "domains": [
    { "name": "MT5 交易账户", "members": ["MT5 TradingAccount", "MT5 Manager", "MT5 Client"], "kind": "business" },
    { "name": "审批流",       "members": ["ApprovalTask"], "kind": "support", "reason": "被 ApprovalBehavior 与多个域的命令触发" },
    { "name": "编辑锁 / 审计日志 / 服务器连通性", "members": ["EditLock", "AuditLog", "Server"], "kind": "support" }
  ]
}
```

校验：每个 `object` 恰好属于一个域；`kind=support` 必须给 `reason`。结果存 `business_domains`，用户可在页面里改名、拖动成员、改 business / support，改过的域打锁，下次归纳不覆盖。

### 5.6 规模与耗时（待实测）

每张卡片输入约 3–5k token、输出约 300 token。核显上本地模型若 15 tok/s，一张约 20–30 秒，500 张约 3–4 小时，是"开着电脑睡觉"的任务（总体设计 §6.4 的定位）。首次只跑卡片不跑解释；归纳一次约 1 分钟。

## 6. 第三段：视图方案（AI 选预设版式）

### 6.1 预设版式

| 版式 | 行 | 列 | 适合 |
|---|---|---|---|
| `capability-matrix` 业务能力矩阵 | 业务对象 × 平台 | 动作（新增 / 修改 / 删除 / 批量 / 迁移 / 导入 / 比对预览 / 查询） | 配置管理、CRUD 密集的系统（ccs-backend） |
| `pipeline-lanes` 管道泳道 | 业务域 | 入口 → 消息 → 行为链 → Handler → 下游（即现有管道视图） | 关心调用链形状 |
| `domain-graph` 域关系图 | 业务域为节点 | 嵌套分发 / 共享下游为边 | 关心域之间的耦合 |

每种版式有固定参数：行排序（按消息数 / 按名字 / 按风险）、支撑域的位置（底部单独一段 / 隐藏 / 并列）、高亮规则（无审批的写操作、无权限的写操作、绕过管道、孤儿消息）、折叠阈值。

### 6.2 AI 的输入与输出

输入：域列表与每域的度量（对象数、动作分布、治理覆盖率、跨域流、外部系统数、矛盾数），不给源码。输出：

```json
{ "layout": "capability-matrix", "rowOrder": "byMessages", "support": "bottom",
  "highlights": ["writeWithoutApproval", "writeWithoutPermission", "bypass"],
  "foldThreshold": 2,
  "rationale": "90% 的单元是对象 × 动作的配置操作，矩阵比泳道更能看出哪些动作缺审批" }
```

服务端 schema 校验；不合法就用默认方案（`capability-matrix`，按消息数排序，支撑在底部）。方案存 `view_plans`，页面显示 `rationale`；用户可改任何参数并锁定，锁定后不再问 AI。

### 6.3 渲染数据

`GET /api/business/map?domain=` 返回渲染好的数据（行、列、格子、徽标、高亮、流），页面不再做业务判断。格子点进去复用现有的调用图 / 管道视图：格子 → 该消息的管道行 → 调用图。

## 7. 本地模型接入

- 提供方：OpenAI 兼容 `POST {url}/v1/chat/completions`，`response_format: json_object`（不支持的模型退化为提示词约束 + 服务端抽取第一个 JSON 块）。
- 配置（VS Code 设置 / CLI 参数 / 环境变量，优先级从高到低）：`codeatlas.ai.url`（默认 `http://127.0.0.1:11434/v1`）、`codeatlas.ai.model`、`codeatlas.ai.concurrency`（默认 1）、`codeatlas.ai.timeoutSeconds`（默认 120）。没有 key 的概念；若端点要 key，走环境变量 `CODEATLAS_AI_KEY`。
- 推荐模型：总体设计 §6.1 的 Qwen3-Coder-30B-A3B（MoE，核显可跑）；候选 Qwen2.5-Coder-14B。**本机目前没装任何本地模型，见 §10。**
- 失败处理：端点连不上 → 业务视图置灰，悬停显示原因与启动命令；单张卡片失败 → 记录、跳过、继续；连续 5 张失败 → 停队列并提示。
- 隐私：本地端点不出机器；是否"本地"由 URL 是否回环地址判断，非回环地址时页面显示"源码将发送到 X"（总体设计 §6.5）。

## 8. 页面

- 地图模式三个视图：**业务 | 管道 | 命名空间**。有视图方案时默认"业务"；只有卡片没有方案 → 用默认方案；没有卡片 → "管道"，"业务"按钮显示"尚未分析：点此开始"，点了起队列并显示进度。
- 业务能力矩阵：行标签是业务对象 + 平台徽标，支撑域在底部单独一段（可折叠）；格子显示命令数与治理徽标（权限 / 审批 / 审计 / 任务），缺审批的写操作红框；单击格子底部显示卡片（summary、外部系统、风险、矛盾）；双击进管道行或调用图。
- 域归纳与方案可编辑：改名、拖成员、改 business / support、改版式参数、锁定。编辑走 `PUT`，写回库。
- 详情条始终显示：模型名、卡片覆盖率（已算 / 总数）、矛盾数、方案是 AI 选的还是用户锁定的。

## 9. 测试与验收

- 第一段：Roslyn fixture 加一个带标记接口与显式常量实现的命令；断言 `symbol_tags` 的 tag / value / line；ccs-backend 对账（§4.3）。
- 第二段：假端点（返回预置 JSON）测契约校验的每条规则：证据不存在 → inferred + 矛盾；治理与标签冲突 → 以标签为准；schema 不合格 → 重试一次后标失败；哈希不变不重算；中断后续跑。提示词构造做快照测试。不测模型输出质量。
- 第三段：假端点返回各版式方案 → 渲染数据断言；不合法方案 → 默认方案；锁定后不再调模型。
- 页面：vitest 测矩阵布局纯函数（行列、徽标、高亮）。
- ccs-backend 验收（真实本地模型）：抽 30 张卡片人工核对 object / action / platform 的准确率（目标 ≥ 90%，错误逐条记录原因）；矛盾数；全量耗时；归纳出的域与用户的认知对照（用户说了算）；**先装进 VS Code 用用户工作区验，再给用户**。

## 10. 待问项（动手前要答案）

| # | 问题 | 为什么要先问 |
|---|---|---|
| 1 | 本机装哪个本地推理端点与模型？候选：Ollama + `qwen3-coder:30b-a3b` / `qwen2.5-coder:14b`；或 llama-server。装好后告诉我 URL 和模型名 | 现在本机 11434 / 1234 端口都没有服务；核显 2GB 独显显存 + 64GB 内存，30B-A3B 的 MoE 能跑但速度要实测，决定首次全量是几小时还是一整夜 |
| 2 | 业务对象的命名用英文（`MT5 Group`）还是中文（`MT5 组`）？ | 影响卡片契约和界面；混用最难看 |
| 3 | 第三段方案由谁拍板：AI 选完直接用，还是每次给用户确认再生效？ | 前者省事，后者更可控；建议首次确认、之后锁定 |
| 4 | 第一次全量分析在哪跑：VS Code 后台（会占机器几小时）还是命令行 `codeatlas business` 夜里跑？ | 决定进度 UI 的优先级 |

## 11. 里程碑切分

- **5a 标签事实**（半天）：Roslyn 提取 + 存储 + 对账。不依赖模型，先做。
- **5b 卡片与归纳**（一天半 + 一次整夜运行）：`CodeAtlas.Ai`、队列、契约校验、CLI、状态接口。
- **5c 视图方案与业务矩阵页面**（一天）：预设版式、方案接口、页面、可编辑与锁定。
- 每个里程碑结束：装进 VS Code 用 ccs-backend 验收，用户看过再进下一个。

## 12. 诚实的边界

- 项目没打标记的地方（比如查询类命令没有 `IAuditable`），业务对象与动作就只能靠 AI 从名字和代码推断，字段标 `inferred`，准确率会低于有标记的部分。
- 本地模型的质量决定卡片 `summary` 与 `risks` 的可读性；治理四项不受模型影响，因为以标签为准。
- 域的划分本质上是人的判断，AI 给的是初稿；页面里的手改与锁定是正式机制，不是补丁。
