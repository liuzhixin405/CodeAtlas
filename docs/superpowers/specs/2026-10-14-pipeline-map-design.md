# CodeAtlas 项目地图：管道视图 设计文档

日期：2026-10-14
状态：已实现（服务端 `PipelineBuilder`、`/api/pipeline`、页面泳道视图），但作为"整个项目"的视图被用户否定（"只是把代码翻译一下"）。后续按 `2026-10-14-architecture-map-design.md` 降级为 L3 入口列表的一种过滤与入口详情，不再是独立标签。
上游：`2026-10-13-codeatlas-design.md`（总体设计 4.7 间接调用链）、`2026-10-13-vscode-extension-design.md`（面板）。本文件只写管道视图新增的部分。

## 0. 为什么要再加一个视图

现有"项目地图"按命名空间 / 目录聚合。它忠实，但对按管道模式组织的项目信息量很低：ccs-backend 顶层一个命名空间装了 54695 / 55k 个符号，钻进去看到的是 Application / Infrastructure / Tests 这类类库分布，而项目真正的结构是**入口 → 消息 → 行为链 → Handler → 下游**这一条管道。用户的原话："从项目设计上来看就是一个管道模式，所有业务都通过管道来串起来的"。

取证（直接查 ccs-backend 的索引库 `.codeatlas/index.db`，tree-sitter 索引，2026-10-14）：

| 事实 | 数字 | 证据 |
|---|---|---|
| `dispatches` 边 | 4807 条：`handles` 524、`dispatch:*` 4275、无 via 8 | `SELECT via, count(*) FROM edges WHERE kind='dispatches'` |
| 消息类型（`handles` 的目标） | 519 个：Command 232、Query 230、Request 43、Event 11 | 按名字后缀统计 |
| Handler 类型（`handles` 的起点） | 468 个；实现的接口：IQueryHandler 230、ICommandHandler 214、IBackgroundJobHandler 64、IEventHandler 8 | implements 边 to_name |
| 分发调用点里测试代码的占比 | 4275 条 `dispatch:*` 里 3700+ 来自 `CentralizedConfigurationSystem.Tests`；非测试的只有 556 条 / 156 个类型 | 按 enclosing type 聚合 |
| 行为链 | 5 个类实现 `IPipelineBehavior<,>`（Validation / Permission / Approval / Audit / ComparePreviewCaching），注册顺序写在 `Bootstrap/PipelineRegistration.cs:29-37`；测试里另有 5 个 | implements 边 + 源码 |
| 消息的命名空间分布 | `…Application.Modules.{MT5,MT4,Common,Approvals}.<子模块>.{Commands,Queries,…}` 512 个，`…Application.Features.Approvals.Events` 7 个 | 消息限定名 |
| Handler 嵌套分发（Handler 里再 Send 另一个消息） | 67 条 | from 在 Handler 类型内的 `dispatch:*` 边 |
| 入口直接调 Handler 方法（绕过管道） | 0 条 | Controllers 方法 → Handler 方法的 calls 边 |
| 没有任何非测试入口的消息 | 41 个 | 消息名不在非测试 `dispatch:*` 的 via 里 |
| 该库的 `di_registrations` | 0 行（索引是旧版本、没跑过 Roslyn，schema 还没有 `files.roslyn_at`） | `SELECT count(*) FROM di_registrations` |

结论：管道视图所需的全部角色都能从现有的边推出来，不需要新的分析器；需要的只是 **按角色而不是按命名空间聚合** 的一个新 Builder，以及页面上的泳道布局。

## 1. 范围

**做**：

1. 服务新增 `GET /api/pipeline?root=`：把符号按管道角色分类（入口 / 消息 / 行为 / Handler / 下游），按消息所在命名空间分成泳道（行），返回每行每列的计数与代表项、跨行的流（嵌套分发、绕过）、行为链及其顺序。
2. 面板"项目地图"模式内增加 **管道 / 命名空间** 两个视图的切换；仓库里有分发数据时默认进管道视图，没有时停留在命名空间视图并禁用切换。
3. 泳道可钻取：双击一行进入该命名空间下一层；到叶子时每个消息一行；双击叶子上的消息 / Handler / 入口切回调用图并聚焦。
4. 行为链按 DI 注册顺序排列（`di_registrations`，Roslyn 产出）；没有注册数据时按名字排列并标明"顺序未知"。
5. 测试代码的入口默认排除（否则 ccs-backend 的入口列几乎全是测试类）。

**不做**：

- 不新增边、不改索引器、不改索引版本。
- 不判定"入口直接调领域 / 基础设施"这种广义绕过：哪些命名空间算"领域"没有可靠依据，v1 只报"入口直接调用 Handler 方法"。
- 不区分某个 Behavior 只对某类消息生效（闭合泛型 `IPipelineBehavior<X, R>`）：implements 边记录的是原始定义，分不出来；全部按"对所有消息生效"显示。
- 不做事件总线（`IEventPublisher`）与命令管道的区分：事件也是 `handles` 边的目标，按"事件"类别标出即可，不单独画一条总线。
- 不做命名空间视图的改进（>90% 自动下钻、全局命名空间桶、双向边标红）；那是另一张票。

## 2. 角色判定（服务端，纯函数）

输入与命名空间地图相同：全库 `MapSymbol`、全库边（`MapEdge` 增加 `Via`）、再加 `di_registrations`。所有规则只用已经存在的数据。

| 角色 | 规则 | 备注 |
|---|---|---|
| **Handler 类型** | `dispatches` 且 `via = handles` 的起点（类型） | tree-sitter 与 Roslyn 都是类型 → 消息 |
| **消息类型** | 上述边的目标 | 类别：Handler 实现的接口名含 `Query` 或消息名以 `Query` 结尾 → 查询；接口名匹配 `Event|Notification|Consumer` 或消息名以 `Event` 结尾 → 事件；其余 → 命令 |
| **行为类型** | 有 `implements` 边指向名字（去掉泛型参数）以 `Behavior` 结尾的接口的类型 | 顺序：`di_registrations` 里 impl 为该类型的记录按（文件路径，行）排序；没有则按名字，`ordered = false` |
| **入口类型** | `dispatches` 且 `via` 以 `dispatch:` 开头的边的起点所在的最外层类型，且该类型 **不是 Handler**、**不是测试代码** | Handler 发出的分发另算"嵌套分发" |
| **下游分组** | Handler 类型内的方法发出的 `calls` 边（confidence ≥ 0.4）的目标所在类型，按命名空间截到**与自动 root 同样的段数**聚合（ccs-backend：自动 root 是 `…Application.Modules` 3 段，下游就显示 `…Application.Dtos`、`…Infrastructure.PluginHub` 这一层）；目标是 external 的单独计数为"框架调用" | 排除目标在 Handler 类型内部的调用。实测发现消息里混有全局命名空间的类型时公共前缀是空串，所以不能按"公共前缀 + 1"截 |
| **测试代码** | 文件路径匹配 `(^|[/.])(tests?|__tests__|spec|specs)([/.]|$)`、`\.(test|spec)\.[tj]sx?$`、`Tests?\.cs$`，或限定名含 `.Tests.` / `.Test.` | 与页面 `isTestSymbol` 同一条规则，移植到 C# |

测试代码整体不进管道：入口、消息、Handler、行为都排除（实测时只排除入口，顶层冒出一条 `Tests` 泳道，全是夹具消息，用户看不懂）。排除的数量返回给页面（`testEntriesSkipped`、`testMessagesSkipped`），不悄悄吞掉。

## 3. 泳道（行）的推导与钻取

- 行的依据是 **消息类型的命名空间**（C#）或目录（脚本）。
- `root` 为空表示"自动"：取全部消息命名空间的最长公共前缀；然后只要某一个子行占了 ≥ 90% 的消息且它还有下一层，就继续下钻。返回实际使用的 `root` 与自动层 `defaultRoot`。**面包屑以 `defaultRoot` 为底**（`项目 › Modules › MT5`）：它上面的层级是项目 / 类库名（`Application`、`Tests`），对管道没有意义，不提供。ccs-backend 预期：公共前缀 `CentralizedConfigurationSystem.Application`，`Modules` 占 98.6% → 自动落到 `…Application.Modules`，行是 MT4 / MT5 / Common / Approvals。
- `root` 非空：行 = 消息命名空间在 root 下一层的键（复用 `MapBuilder.ChildKey`）。消息命名空间恰好等于 root 的，归入一行 `（本级）`。
- **叶子**：root 下没有任何消息处在更深一层时，每个消息一行（`symbolId` 非空），行内：入口类型、这个消息、它的 Handler、Handler 的下游。
- root 之外的消息数量返回为 `messagesOutsideRoot`，页面在详情条提示。
- `root` 不存在 → 空行列表（与命名空间地图一致）。

## 4. 每行的列与标记

| 列 | 内容 | 标记 |
|---|---|---|
| 入口 | 向本行消息分发过的入口类型数；代表项按分发次数排序，最多 20 个 | 行里有消息一个非测试入口都没有 → `orphans` 计数（"孤儿消息"） |
| 消息 | 总数及命令 / 查询 / 事件分项 | — |
| 行为链 | 全局同一条链，所有行相同；显示个数、首尾名字 | `ordered = false` 时标"顺序未知" |
| Handler | 处理本行消息的 Handler 类型数；代表项 | 本行 Handler 发出的嵌套分发条数 `nested` |
| 下游 | 按权重排序的下游分组（前 5 个）+ 框架调用数 | — |

**跨行的流**（`flows`）：

| kind | 从 → 到 | 含义 | 画法 |
|---|---|---|---|
| `nested` | 行 A 的 Handler → 行 B 的消息 | A 的 Handler 里再分发了 B 的消息 | 橙色虚线 |
| `bypass` | 行 A 的入口 → 行 B 的 Handler | 入口直接调用 Handler 方法，没有经过管道 | 红色实线 |

同一行内部的嵌套分发不画流，计入 `nested`。

## 5. 服务接口

```
GET /api/pipeline?root=            root 省略或为空 = 自动
→ {
  root, leaf, behaviorsOrdered, testEntriesSkipped, messagesOutsideRoot,
  behaviors: [{ name, qualifiedName, symbolId, order }],
  rows: [{
    key, label, hasChildren, symbolId,
    entries:  { count, items: [{ name, qualifiedName, symbolId, weight }] },
    messages: { count, commands, queries, events, orphans },
    handlers: { count, items: [...] },
    nested, externalCalls,
    downstream: [{ key, label, weight }]
  }],
  flows: [{ from, to, kind, weight }]
}
```

- 没有任何 `handles` 边时 `rows` 为空、`root` 为空串；页面据此回退到命名空间视图。
- 结果按 root 缓存，索引结束时与 `_mapCache` 一起清空。
- Core 改动只有两处读取：`IndexReader.GetEdgeTriples()` 多返回 `via`；新增 `IndexReader.GetDiRegistrations()`。

## 6. 页面

- 地图模式的面包屑栏右侧增加切换：`管道 | 命名空间`。首次进入地图模式先请求 `/api/pipeline`；`rows` 非空则默认管道视图，否则命名空间视图并把"管道"按钮禁用，title 说明"没有分发数据"。
- 管道视图用独立的 cytoscape 实例、**preset 布局**（位置由代码算出，不跑 ELK）：列 = 行标签 / 入口 / 消息 / 行为链 / Handler / 下游，行 = 泳道；顶部一行列标题。行内五个格子之间用灰色直线相连表示管道走向；跨行的流按 §4 画。
- 格子标签：`入口 12`、`消息 68\n命令 40 · 查询 28`、`行为链 ×5\nValidation … Caching`、`Handler 68`、`下游\nDtos.MT5 168 · Infrastructure.PluginHub 129 · …`。孤儿消息 > 0 的消息格子、顺序未知的行为链格子用警示色边框。
- 单击格子：底部详情条列出代表项（限定名可点，点了切到调用图聚焦）。单击流：显示两端与条数。
- 双击：非叶子行的任意格子 → 钻入该行；叶子行的消息 / Handler / 入口格子 → 切到调用图聚焦（多个代表项时聚焦第一个，其余在详情条里点）。
- 面包屑与命名空间视图分开各一套（两个 `MapState`）；`MapState` 新增 `goToPath(root)`，按 `.` / `/` 拆成逐级路径。
- 详情条默认文案：`管道 Modules：4 条泳道，519 个消息，468 个 Handler，156 个入口，行为链 5（按注册顺序 / 顺序未知），嵌套分发 67，已排除测试入口 3700`。

## 7. 测试

- `tests/CodeAtlas.Server.Tests/PipelineTests.cs`：手造符号与边——两个业务模块（`Acme.App.Orders`、`Acme.App.Users`）、控制器入口、测试项目里的入口、三个消息（命令 / 查询 / 事件）、Handler、两个 Behavior 与 DI 注册顺序（与名字顺序相反，证明顺序来自 DI）、一条 Handler 嵌套分发到另一模块、一条控制器直接调 Handler 方法、Handler 调仓库内下游与 external。断言：自动 root 落到 `Acme.App`；行键与计数钉到具体数字；测试入口被排除且 `testEntriesSkipped = 1`；`flows` 含一条 `nested` 与一条 `bypass`；没有 DI 时 `behaviorsOrdered = false` 且按名字排；叶子行 `symbolId` 非空；`root` 不存在返回空。
- `ApiTests`：纯 TS 仓库 `/api/pipeline` 返回空 rows、root 为空串、200。
- vitest：`MapState.goToPath`；`pipelineLayout.ts`（把 DTO 变成带坐标的 cytoscape 元素的纯函数）——列 / 行坐标、行内连线数、流的样式类。
- 证伪：每条规则至少有一个用例在去掉实现后变红（计划里逐条标注）。

## 8. 诚实的边界

- 只有 tree-sitter 索引时，`dispatch:*` 边靠"同方法里 `new T()`"推导（0.4），控制器里经 `request.ToCommand()` 之类映射再 Send 的看不见；ccs-backend 148 个控制器只有 23 个被识别为入口。Roslyn 索引后会补全。页面不掩盖这一点：入口数就是索引里有的数。
- Handler 经基类间接实现 `IQueryHandler<,>` 的（ccs-backend 的 PluginHub 模块），tree-sitter 认不出 `handles` 边，这些模块在管道视图里没有行；Roslyn 用 `AllInterfaces` 能认出。
- 行为链是否"真的"对某条消息生效取决于运行时 DI，静态上只能给出注册链。

## 9. 文档

- `docs/vscode-extension-guide.md` §8 增加"管道视图"小节（切换、泳道、格子、流、钻取、默认规则、排除测试入口）。
- `docs/acceptance.md` 增加本轮在 ccs-backend 上的实测记录。
