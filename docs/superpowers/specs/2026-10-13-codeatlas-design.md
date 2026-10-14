# CodeAtlas 设计文档

日期：2026-10-13
状态：已批准（2026-10-13，用户选择跳过书面审阅，以实际项目验收为准）
作者：Victor Liu（与 Claude 协作整理）

---

## 0. 一句话

CodeAtlas 是一个通用的本地代码阅读器：点开一个符号，可视化地逐层展开它的调用、被调用、实现关系，旁边有本地 AI 解释，并通过 MCP 把同一份精确索引提供给 AI 编码助手。它要解决的问题是：AI 产出代码的速度已经超过人读懂它的速度，需要一个工具帮人重新建立对代码的心智模型。

## 1. 目标与非目标

### 1.1 目标

- **读懂陌生模块**：以任意符号为焦点，看它调谁、被谁调、实现了什么、被谁实现，逐层按需展开。这是核心交互，其他一切为它服务。
- **通用**：tree-sitter 作为多语言底座，第一版内置 C#、TypeScript、TSX、JavaScript、Python、Go、Java；SQL 不在 TreeSitter.DotNet 内置 grammar 中，需引入 TreeSitterLanguagePack 后补；加语言不改核心。
- **解耦代码也要看得见**：MediatR、事件总线、AOP、中间件管道、后台任务这类没有直接调用语句的链路，通过约定规则引擎产出带证据行的 `dispatches` 边（4.7）。
- **C# 精确**：Roslyn 增强器给 C# 提供语义级精度（重载、扩展方法、泛型、接口到实现）。
- **本地优先的 AI**：按需解释走本地 llama-server；可切换到公共模型；后台摘要和语义搜索可选。
- **可被 AI 助手消费**：MCP 服务暴露索引，让 Claude Code 等工具在改代码前先问清调用关系。
- **跳回编辑器**：界面上任何位置一键跳到 VS Code、Visual Studio、Rider 的对应行。
- **首个大样本**：ccs-backend（约 1.4 万 .cs 加 1500 TS/TSX 文件）必须能完整索引并流畅浏览。

### 1.2 非目标（第一版明确不做）

- 架构合规检查、分层违规检测、循环依赖报告。
- 影响分析报表、跨语言边（如 TS 的 HTTP 调用映射到 C# Controller）。
- 通用 AI 聊天、让模型自主翻仓库的 agent 循环。
- 运行时链路分析（trace、覆盖率）。
- 图的编辑、自定义布局保存、导出图片。
- Roslyn 级别的增量编译（第一版 Roslyn 只做全量）。
- VS Code 和 Visual Studio 插件本体（作为紧接着的独立子项目，见第 9 节）。

### 1.3 两条红线

1. **图上每一条线都要能指到一行真实代码作为证据。** 直接调用指向调用行；间接分发（4.7）指向触发它的那行代码（`Send()`、特性、注册语句）。DI 注册本身只做标注，不产生边。
2. **AI 不参与索引的正确性。** 调用边只来自解析器；AI 只做解释、摘要、自然语言搜索。

## 2. 整体架构

### 2.1 解决方案结构

.NET 10 解决方案，六个项目加一个前端目录：

```
CodeAtlas.sln
├─ CodeAtlas.Core                   领域模型 + SQLite 索引库读写接口 + IEdgeResolver 扩展点
├─ CodeAtlas.Indexing.TreeSitter    多语言通用解析器（结构 + 启发式引用解析）
├─ CodeAtlas.Indexing.Roslyn        C# 语义增强器（独立进程运行）
├─ CodeAtlas.Ai                     IChatProvider 抽象、提示词构造、按需解释、后台摘要与 embedding
├─ CodeAtlas.Server                 ASP.NET Core：REST API + SSE + MCP 端点 + 内嵌前端静态文件
└─ CodeAtlas.Cli                    入口 exe：index / serve / mcp 子命令
web/                                前端源码：TypeScript + Vite + Cytoscape.js + elkjs + CodeMirror 6
                                    构建产物复制进 Server 的 wwwroot，publish 时嵌入 exe
tests/
├─ CodeAtlas.Core.Tests
├─ CodeAtlas.Indexing.TreeSitter.Tests     每种语言一个 fixtures 目录，快照测试
├─ CodeAtlas.Indexing.Roslyn.Tests         内含一个小测试 .sln
├─ CodeAtlas.Ai.Tests
├─ CodeAtlas.Server.Tests                  WebApplicationFactory 集成测试
└─ web/ 内 Vitest
```

### 2.2 关键依赖

| 用途 | 包 | 说明 |
|---|---|---|
| 多语言解析 | TreeSitter.DotNet 1.3.x | MIT，自带 Windows/Linux/macOS 原生库和 28 种 grammar，支持 Query |
| 更多语言 | TreeSitterLanguagePack | 306 种预编译 grammar，按需引入，第一版不依赖 |
| C# 语义 | Microsoft.CodeAnalysis.CSharp.Workspaces + Microsoft.Build.Locator | MSBuildWorkspace 加载 .sln |
| 索引库 | Microsoft.Data.Sqlite | 启用 FTS5 |
| MCP | ModelContextProtocol、ModelContextProtocol.AspNetCore | 微软官方 C# SDK，stdio 与 HTTP |
| 图渲染 | cytoscape、elkjs、@codemirror/* | 前端 |

### 2.3 数据流

只有一条主线：

1. `codeatlas index <仓库路径>`：tree-sitter 扫全部支持语言的文件，产出符号和边写入该仓库的 SQLite；若发现 .sln 或 .csproj，启动 Roslyn 增强器子进程，用精确结果覆盖 .cs 文件的边（覆盖规则见 3.4）。
2. `codeatlas serve`：起本地 Web 服务。前端通过 REST 读符号、邻域、源码片段；通过 SSE 接收 AI 流式输出和索引进度。
3. `codeatlas mcp`：同一个索引库，以 stdio 暴露 MCP 工具；`serve` 同时挂 HTTP 传输的 MCP。

### 2.4 两个关键设计决定

- **索引库是唯一真相。** tree-sitter 和 Roslyn 都只是"边的生产者"，写同一张表，每条边带 `source` 和 `confidence`。前端和 AI 只读库。以后加任何语言增强器（TS 用 tsc、Java 用 JDT）都是加一个生产者。
- **AI 永远不参与索引的正确性。** 见 1.3。

## 3. 数据模型与索引库

### 3.1 存储位置

SQLite 单文件，每个仓库一个，默认 `<仓库>/.codeatlas/index.db`，可配置到别处。`.codeatlas/` 应加入仓库的 `.gitignore`，CLI 首次索引时提示。

### 3.2 表结构

```sql
files     (id, path, language, hash, size, indexed_at, status)
          -- status: ok | parse_failed | skipped

symbols   (id, file_id, kind, name, qualified_name, signature,
           start_line, start_col, end_line, end_col,
           parent_id, visibility, source)
          -- source: treesitter | roslyn

edges     (id, from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line)
          -- file_id/line 是边发生的位置，即那一行调用代码
          -- to_name 保存目标的 qualified_name 文本；to_id 为 NULL 时表示悬空边（见 3.5）
          -- via / evidence_line 仅 dispatches 边使用（见 4.7）

di_registrations (id, interface_id, impl_id, file_id, line, lifetime, conditional)
          -- 只做标注，不参与遍历

imports   (id, file_id, target, is_path, line)
          -- 原始 import / using 记录，供引用解析器确定 import 范围；is_path 区分相对路径（TS/JS）与命名空间（C#）

summaries (symbol_id, summary, source_hash, model, created_at, embedding BLOB)

symbols_fts  FTS5 虚表：name, qualified_name，支持前缀和模糊
```

`external` 是一个特殊的符号 kind，表示仓库外的目标（标准库、第三方包）。每个不同的名字一个 external 符号，不可展开。

### 3.3 统一的符号与边种类

**符号 kind**：`module / namespace / class / interface / struct / enum / function / method / property / field / variable / type_alias / external`。`module` 是每个文件的根符号（一个文件一个），承载文件级引用和 import 边，也是文件大纲的根。各语言的特殊构造映射到这个集合（Go 的 receiver method 是 method，TS 的箭头函数常量是 function，C# 的 record 是 class），不另开。

**边 kind**，第一版六种：

| kind | 含义 | tree-sitter | Roslyn |
|---|---|---|---|
| `contains` | 父子包含 | 精确 | 精确 |
| `calls` | 调用 | 启发式 | 精确，含重载解析 |
| `references` | 引用类型或字段 | 启发式 | 精确 |
| `inherits` | 继承 | 按名字 | 精确 |
| `implements` | 实现接口（类型级和方法级） | 按名字 | 精确 |
| `imports` | 文件级 import / using | 精确 | 精确 |
| `dispatches` | 间接调用（MediatR、事件、切面、中间件、任务框架，见 4.7） | 弱版本，按名字 | 精确，依赖规则引擎 |

**confidence** 为 0 到 1 的浮点。Roslyn 边恒为 1.0。tree-sitter 边按解析级别打分（见 4.2）。前端用它决定线的粗细和虚实；AI 解释时告知模型哪些边是猜测。

### 3.4 Roslyn 覆盖规则

Roslyn 完成某个 .cs 文件后：

1. 删除该文件 `source = treesitter` 的所有 `calls / references / inherits / implements` 边。
2. 保留 `contains / imports` 边。
3. 写入自己的边。
4. 符号按 `(file_id, start_line, start_col, kind)` 匹配合并：匹配上的，Roslyn 补充 `qualified_name` 和 `signature`，`source` 改为 roslyn；匹配不上的 Roslyn 符号新增；tree-sitter 独有的保留。

### 3.5 增量索引

- 按文件内容 hash 判断变更。
- 变更文件：删除其全部符号、从它出发的全部边、它的 di_registrations，然后重建。
- 指向它的边（别人调用它）：重建后按 `to_name` 重新挂接到新符号 id；挂不上的边标记 `to_id = NULL`，前端显示为悬空，不删除。
- 第一版只做文件级增量，Roslyn 不做增量；变更了 .cs 文件时，Roslyn 边保持上一次全量的结果，界面提示"C# 语义索引可能过期，建议重新全量"。

### 3.6 语义搜索的向量存储

embedding 直接存 `summaries.embedding` BLOB（float32 数组）。查询时进程内算余弦相似度。估算 ccs-backend 值得摘要的公开符号为数万级，暴力计算在毫秒级，不引入向量库。

## 4. 解析层

### 4.1 tree-sitter 通用底座

每种语言的支持由 **一对 .scm 查询文件 + 一个很薄的适配器类** 组成，不写逐语言的遍历代码。

- `definitions.scm`：capture 定义节点及名字，如 `@def.method @name`。适配器把 capture 名映射到统一 kind，并按包含链计算 `qualified_name`。
- `references.scm`：capture 调用表达式、类型引用、继承子句、import 语句。每个 capture 产出一条未解析引用 `(from_symbol, name_text, edge_kind, position)`。
- 第一版内置：C#（tree-sitter 版，作为 Roslyn 不可用时的兜底）、TypeScript、TSX、JavaScript、Python、Go、Java、SQL。
- 新增语言 = 新增一对 .scm、几十行适配器、一个 fixtures 目录。

**并行**：文件级 `Parallel.ForEachAsync`，每线程独立 Parser 实例（Parser 非线程安全，Language 可共享）。结果先在内存攒批，按几千行一个事务写 SQLite。

### 4.2 启发式引用解析（IEdgeResolver 的默认实现）

四级，命中即停：

| 级别 | 条件 | confidence |
|---|---|---|
| 1 | 同文件内有且仅有一个同名定义 | 0.9 |
| 2 | 该文件 import 的模块内有且仅有一个 | 0.7 |
| 3 | 全仓库同语言有且仅有一个同名符号 | 0.5 |
| 4 | 多个候选 | 每个候选一条边，各 0.3 |

解析不到的引用记成指向 `external` 符号的边，confidence 0.5，让图上能看到"调了 N 个外部函数"但不展开。

### 4.3 IEdgeResolver 扩展点

```csharp
public interface IEdgeResolver
{
    // 输入：当前所有未解析引用和 external 符号；可改写为新的边或新的符号。
    Task ResolveAsync(ResolveContext ctx, CancellationToken ct);
}
```

主解析完成后按注册顺序执行。4.2 的启发式解析是第一个也是第一版唯一的实现。预留给以后的：TS 的 `fetch('/api/orders')` 映射到 C# Controller、把 external 归到具体 NuGet 包等。

### 4.4 Roslyn 增强器

- 以独立子进程运行（`CodeAtlas.Cli index` 启动），通过标准输出以 JSON Lines 向主进程流式回传符号、边、di_registrations、进度和错误。隔离原因：MSBuildWorkspace 加载大解决方案可能占用数 GB 内存，崩溃不应影响 tree-sitter 结果。
- `MSBuildWorkspace` 打开 .sln；单个项目加载失败（缺 SDK、缺包）则跳过并记录，不中断整体。
- 每个 Document 取 `SemanticModel`，遍历语法树：
  - 定义：`ISymbol` 到符号表，`qualified_name` 用 `ToDisplayString`，`signature` 含参数类型。
  - `calls`：`InvocationExpression`、`ObjectCreationExpression` 用 `GetSymbolInfo` 取真实 `IMethodSymbol`。候选符号（`CandidateSymbols`，如重载歧义）各产一条边，confidence 0.8。
  - `inherits` / `implements`（类型级）：读 `INamedTypeSymbol.BaseType / Interfaces`。
  - `implements`（方法级）：对每个接口成员用 `SymbolFinder.FindImplementationsAsync` 找出所有实现，产出方法级边。这是点一个接口方法能跳到具体实现的关键。
- 只索引仓库内源码的符号；引用的程序集成员记为 external。

### 4.5 DI 注册：标注，不是边

- 扫描 `services.AddScoped<I, T>()`、`AddTransient`、`AddSingleton`、`TryAdd*` 以及它们的非泛型重载，写入 `di_registrations`。
- **不产生边、不替换边、不参与图遍历。** 接口到实现的边只来自 4.4 的真实代码分析，一个接口有几个实现就有几条边。
- 条件注册（位于 `if` 块、`#if` 块、工厂 lambda、Autofac 模块等）标记 `conditional = true`，不判断运行时是否生效。
- 前端在节点上显示小标记"在 Startup.cs:42 注册"，悬停显示位置和生命周期。
- 附带能力：列出"已注册但没有任何代码路径调用该接口"的注册，作为阅读线索。

### 4.6 跨语言边

第一版不做。见 1.2。

### 4.7 间接调用链（解耦代码的调用关系）

现代代码大量通过 MediatR、事件总线、AOP、中间件管道、后台任务框架解耦，没有直接调用语句。这是纯静态工具最弱的地方，也是 CodeAtlas 必须做完整而不是做简单的地方。

#### 4.7.1 分类与可解性

| 类别 | 例子 | 可解性 | 解法 |
|---|---|---|---|
| 按类型分发 | MediatR `Send(new Cmd())` → `IRequestHandler<Cmd>.Handle`；MassTransit / CAP / Kafka `Publish(new Evt())` → `IConsumer<Evt>.Consume`；`INotificationHandler<T>` | 精确 | Roslyn 取参数真实类型 T，找实现 `IHandler<T>` 的类及其入口方法 |
| 特性驱动 | `[Authorize]`、`[ServiceFilter(typeof(X))]`、`[TypeFilter]`、AspectCore / Castle 的 `[Intercept]`、自定义切面特性 | 精确 | 特性类型（或其 typeof 参数）→ 切面类 → 按框架约定的入口方法 |
| 注册式管道 | `app.UseMiddleware<X>()`、`app.UseXxx()`、MediatR `AddBehavior(typeof(B<,>))`、Autofac `InterceptedBy<T>()`、`Decorate<I,T>()` | 顺序精确 | 扫注册调用产出有序链；每个请求入口前挂该链 |
| 表达式树任务 | Hangfire `Enqueue<T>(x => x.Run(id))`、Quartz job 类型 | 精确 | 从 lambda 表达式树取方法符号 |
| 框架入口 | `BackgroundService.ExecuteAsync`、`IHostedService`、`IStartupFilter`、`[Fact]`、Minimal API `MapGet` lambda | 精确 | 标记为入口点节点，无调用者属正常 |
| 多态分发 | 调接口方法，运行时走哪个实现 | 候选集 | 显示全部实现为候选；DI 注册的做标注（4.5） |
| 纯反射 | `Type.GetType(str)`、`MethodInfo.Invoke`、`Activator.CreateInstance(name)` | 仅字符串常量可解 | 常量则解析；变量则把调用点标为"反射边界"节点 |

#### 4.7.2 约定规则引擎

前五类都是"某框架的某个约定"，统一用规则描述而不是逐个硬编码：

```yaml
# .codeatlas/rules/mediatr.yaml（内置规则包示例）
- id: mediatr.send
  via: mediatr
  trigger:
    call: { type: "MediatR.ISender", methods: [Send, SendAsync] }
  target:
    by_type_argument: { parameter: 0, handler_interface: "MediatR.IRequestHandler`2", entry_method: Handle }
- id: aspnet.filter_attribute
  via: attribute
  trigger:
    attribute_on: [method, class]
    attribute_base: "Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata"
  target:
    aspect_type: self_or_typeof_arg
    entry_methods: [OnActionExecuting, OnActionExecutionAsync, OnAuthorization, OnException]
- id: aspnet.middleware
  via: middleware
  trigger:
    call: { type: "Microsoft.AspNetCore.Builder.IApplicationBuilder", methods: [UseMiddleware], generic_arg: 0 }
  target:
    pipeline: { ordered: true, entry_methods: [Invoke, InvokeAsync] }
```

- **内置规则包**：MediatR、MassTransit、CAP、ASP.NET Core 管道与过滤器、AspectCore、Castle DynamicProxy、Hangfire、Quartz。
- **仓库级自定义规则**：`.codeatlas/rules/*.yaml`，覆盖自研的 pipeline、事件总线、切面。
- **AI 协助发现规则**：索引完成后，AI 扫描"接收对象参数但目标未知的分发方法"和"有多个实现却无直接调用者的接口"，提出候选规则，用户确认后生效。AI 只提议；边仍由静态分析产出。
- 规则引擎实现为一个 `IEdgeResolver`（4.3），依赖 Roslyn 的类型信息；tree-sitter 层只做按名字匹配的弱版本（confidence 0.4）。

#### 4.7.3 数据模型与呈现

- 新增边 kind `dispatches`。
- `edges` 增加列 `via`（mediatr / event / attribute / middleware / aop / expression / reflection / di / null）和 `evidence_line`（证据行：那条 `Send()`、那个 `[Authorize]`、那行 `UseMiddleware`）。
- 红线不变：每条 `dispatches` 边都指向一行真实代码作为证据。
- 图上 `dispatches` 边用点线加 `via` 小标签，可一键开关；找路径默认包含它们。
- "反射边界"是一种特殊节点，灰色带警示图标，表示静态分析到此为止。

#### 4.7.4 诚实的边界

纯反射按变量取类型、配置文件驱动的插件加载、运行时动态生成的代码，静态分析无法覆盖，显式标为边界节点而不是假装不存在。补齐这块的正确方式是接运行时 trace（OpenTelemetry span 或覆盖率数据），作为后续子项目。

## 5. Web 界面与图交互

### 5.1 布局

三栏，围绕"点一个符号，逐层展开"设计：

```
┌──────────────┬──────────────────────────────────┬──────────────────┐
│ 左：导航      │ 中：邻域图（Cytoscape + ELK）       │ 右：详情 + AI     │
│ 搜索框        │                                  │ 签名 / 文件位置   │
│ 文件树 / 符号树│   [被调用者] ← [焦点] → [调用者]     │ 源码片段          │
│ 最近访问      │   双击节点 = 展开一层               │ AI 解释（流式）    │
│              │   右键 = 设为焦点 / 折叠 / 隐藏 / 找路径 │ DI 标记、事实列表 │
└──────────────┴──────────────────────────────────┴──────────────────┘
```

### 5.2 图的行为规则

- **焦点模式**：始终有一个焦点符号居中，左侧为其出边目标（它调用的），右侧为入边来源（调用它的），ELK 分层布局从左到右。
- **按需展开**：初始只显示焦点加一层。双击任一节点展开其一层；节点右上角数字表示未展开数量。单次展开超过 30 个邻居时弹出列表供勾选，不直接铺满。
- **边的视觉编码**：confidence 1.0 实线；0.5 到 0.9 实线变细；0.3 虚线。kind 用颜色区分。多候选边默认折叠为一条带数字的虚线。
- **节点的视觉编码**：形状区分 kind（类矩形、方法圆角、接口菱形）；`external` 灰色不可展开；有 AI 摘要的节点可切换显示摘要而非名字。
- **找路径**：选中两个节点，右键"找路径"，后端 BFS 返回最短调用链，图上高亮。深度上限 12。
- **面包屑**：顶部记录焦点历史，可回退。

### 5.3 右栏

签名、定义位置、源码片段（CodeMirror 6 只读，语法高亮，行号）、AI 解释按钮与流式输出区、提供方下拉框与耗时显示、DI 标记、"被 N 处引用"等事实列表。

### 5.4 跳转编辑器

前端请求 `POST /api/editor/open {file, line, col}`，Server 按配置唤起编辑器：

| 编辑器 | 方式 |
|---|---|
| VS Code | `code -g <file>:<line>:<col>`，备选 `vscode://file/` 协议链接 |
| Visual Studio | 通过 COM 取运行中的 `VisualStudio.DTE` 实例，`ItemOperations.OpenFile` + `TextSelection.GotoLine`；多实例时优先选打开了该文件所在 .sln 的；无实例则 `devenv /Edit <file>` 启动 |
| Rider | `rider64 --line <line> <file>` |

交互：节点单击在右栏看代码；双击文件位置或按 Enter 跳编辑器。

反向查询 `GET /api/symbol/at?file&line&col` 返回该位置的符号，供编辑器插件使用（见第 9 节）。

### 5.5 前端技术

TypeScript + Vite，不用 React/Vue 等框架，三个面板用原生 DOM 加轻量事件总线。Cytoscape.js 画图，elkjs 在 Web Worker 中布局，CodeMirror 6 看代码。构建产物进 `wwwroot`，`dotnet publish` 时嵌入 exe。

## 6. AI 集成

### 6.1 提供方抽象与配置

```csharp
public interface IChatProvider
{
    IAsyncEnumerable<ChatChunk> ChatStreamAsync(ChatRequest req, CancellationToken ct);
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
```

两个实现：OpenAI 兼容（覆盖 llama-server、DeepSeek、Gemini 的 OpenAI 端点、Ollama、LM Studio 等）和 Anthropic Messages。

**复用 cove 的 profile**：配置项 `ai.import_cove_profiles`（默认 true）启动时读取 `~/.cove/config.json`，每个 profile 自动成为一个 provider，名字沿用。CodeAtlas 自己的配置可追加或覆盖 provider，但不重复存 key。key 来源优先级：环境变量 > cove 配置文件。CodeAtlas 自己的配置文件中不允许明文 key。

```yaml
ai:
  import_cove_profiles: true
  providers:                      # 追加或覆盖
    claude: { type: anthropic, model: claude-sonnet-5-5, api_key_env: ANTHROPIC_API_KEY }
  default: local
  fallback:
    enabled: true
    chain: [local, deepseek, gemini]
    first_token_timeout_s: 20
    min_tokens_per_s: 5
    low_throughput_window_s: 10
  explain_context_budget_tokens: 6000
  embedding:
    provider: local-embed         # 另一个 llama-server 实例跑 bge-m3，--embedding 模式
    url: http://127.0.0.1:8081/v1
    model: bge-m3
```

`model` 用于按需解释，`model_fast` 用于后台摘要。embedding 未配置时禁用语义搜索，其他功能不受影响。

默认本地模型推荐 Qwen3-Coder-30B-A3B（MoE，激活 3.3B，核显上生成速度明显快于 27B 稠密模型）。

### 6.2 按需解释（主路径）

后端组装上下文，SSE 流式返回：

1. 焦点符号完整源码。
2. 其直接调用的每个符号：签名加一行已有摘要（若有），不给源码。
3. 其直接调用者：签名，最多 10 个。
4. 图的事实：实现了什么接口、有几个实现、哪些边是启发式猜测。

提示词要求模型回答三件事：**它做什么、为什么被这些地方调用、读它时要注意什么**（副作用、异常、隐式约定）。上下文预算默认 6000 token，超出时先裁调用者，再裁调用目标的签名。结果写入 `summaries`，`source_hash` 不变则不重算。

模型只看到喂给它的内容，不给它工具自主翻仓库。

### 6.3 手动切换与自动回退

- 右栏下拉框随时切换 provider，下一次解释生效。界面始终显示当前 provider 和本次耗时。
- 自动回退只针对按需解释，依据是首 token 延迟和吞吐，不是请求失败（请求失败直接报错并建议切换）。按 `fallback.chain` 顺序逐级尝试，每级独立计时。触发时界面提示"本地模型过慢，已切到 X"，本地已输出的部分保留显示。
- 后台摘要永远只用本地 provider，不走回退。

### 6.4 后台摘要与语义搜索（可选）

- `codeatlas index --summarize` 或界面开关启动低优先级队列：按被引用次数降序给公开符号生成一句话摘要（不超过 40 字）加 embedding。随时可停，下次续跑。
- 搜索：输入自然语言，FTS5 名字匹配与 embedding 相似度两路合并去重，结果点击即设为焦点。
- 规模预期：ccs-backend 数万公开符号，每个约 3 秒，需整夜运行。它是"开着电脑睡觉"的任务，不是前置条件。

### 6.5 隐私边界

- 本地 provider 的调用不出机器。
- `allow_source` 按 provider 配置：本地默认 true；公共 provider 默认 true 但界面有明显标识"源码将发送到 X"。关闭后该 provider 只收到签名和图关系，不收到源码。
- MCP 的 `mcp.allow_source` 同理，关闭后 MCP 工具不返回源码。
- API key 只从环境变量或 cove 配置读取。

## 7. MCP 服务

`codeatlas mcp` 以 stdio 启动；`serve` 同时挂 HTTP 传输。工具刻意少而精：

| 工具 | 作用 |
|---|---|
| `search_symbols(query, limit)` | 名字搜索加语义搜索 |
| `get_symbol(id_or_qualified_name)` | 签名、位置、源码（受 allow_source 控制）、摘要 |
| `get_neighbors(id, direction, kinds, depth=1)` | 调用者 / 被调用者 / 实现，带 confidence |
| `find_path(from, to, max_depth=12)` | 最短调用链 |
| `get_file_outline(path)` | 文件内符号列表 |

意义：让云端编码助手在改 ccs-backend 这类仓库前先问清调用关系，比自行 grep 准确。本地模型若支持函数调用可接同一套。

## 8. 错误处理、性能目标、测试

### 8.1 错误处理原则

索引永远产出结果，只是质量有高低。

- 单文件解析失败：记日志，`files.status = parse_failed`，继续；界面该文件标红。
- Roslyn 整体不可用：tree-sitter 结果照常可用；界面顶部黄色横幅说明原因，不弹窗。
- llama-server 未启动：AI 按钮置灰，悬停显示原因和启动命令；图和导航不受影响。
- 索引过程中仓库被修改：下次增量处理，不中途重扫。
- Roslyn 子进程崩溃：主进程记录 stderr，保留已回传的部分结果，界面提示。

### 8.2 性能目标（ccs-backend 基准，开发机 Core Ultra 9 285H / 64GB）

| 操作 | 目标 |
|---|---|
| tree-sitter 全量索引 | 2 分钟内 |
| Roslyn 增强全量 | 15 分钟内，峰值内存 8GB 以下 |
| 单文件增量（tree-sitter） | 1 秒内 |
| 邻域查询（一层） | 50 ms 内 |
| 找路径 | 500 ms 内 |
| 界面首屏 | 1 秒内 |

### 8.3 测试策略

- **Core**：SQLite 读写、增量合并、覆盖规则，内存库单元测试。
- **tree-sitter 适配器**：每种语言 fixtures 目录，小源文件加期望符号/边 JSON，快照测试。
- **启发式解析**：四级 confidence 分配、多候选折叠、external 归并。
- **Roslyn 增强器**：测试 .sln 覆盖接口多实现、重载、扩展方法、泛型、DI 注册、条件注册；断言精确边与 di_registrations。
- **AI 层**：假 HTTP 服务测流式解析、首 token 超时回退、低吞吐回退、上下文裁剪顺序；提示词构造快照测试；cove profile 导入与 key 优先级。不测模型输出质量。
- **API 与 MCP**：WebApplicationFactory 集成测试覆盖所有端点和工具，含 allow_source 开关。
- **前端**：Vitest 测图状态机（展开、折叠、焦点切换、面包屑、30 阈值）；渲染层手工验证。
- **端到端验收**：ccs-backend 全量索引，人工核对 10 个已知调用关系（清单在实施时确定并写入 `docs/acceptance.md`），作为每个里程碑的回归。

## 9. 里程碑与后续子项目

1. Core + tree-sitter + CLI `index`：能把一个 TS 项目索引进 SQLite。
2. Server API + 前端图：能点开展开、找路径、跳编辑器。
3. Roslyn 增强器：ccs-backend 上跑通并达到 8.2 的目标。
3b. 间接调用链：约定规则引擎 + 内置规则包（MediatR、ASP.NET 管道与过滤器、AspectCore/Castle、MassTransit/CAP、Hangfire/Quartz）+ 仓库自定义规则，在 ccs-backend 上验证真实的 pipeline / AOP 链路。
4. AI 按需解释 + 多 provider + 自动回退 + cove profile 导入。
5. MCP + 后台摘要 + 语义搜索。

**后续独立子项目**（各自一份 spec）：

- **VS Code 插件**：Webview 内嵌同一套界面；命令：光标处 Show in CodeAtlas（调 `symbol/at`）、Explain this symbol、Index this workspace；节点点击通过 Webview 消息回传，用 VS Code API 打开文件。
- **Visual Studio 插件**：VSIX，Tool Window 内 WebView2 加载同一套界面；命令绑定编辑器右键菜单；用 DTE / VS SDK 打开文件。

两个插件都是薄壳，不重复实现任何逻辑；Web 界面与 Server 的接口在第一版即定型。

## 10. 配置文件一览

`<仓库>/.codeatlas/config.yaml`（仓库级）与 `~/.codeatlas/config.yaml`（用户级）合并，仓库级优先。

```yaml
index:
  db_path: .codeatlas/index.db
  languages: [csharp, typescript, tsx, javascript, python, go, java, sql]
  exclude: [node_modules, bin, obj, .git, dist]
  roslyn:
    enabled: auto          # auto | on | off
    solution: null         # null 则自动发现
server:
  host: 127.0.0.1
  port: 7420
editor:
  default: vscode          # vscode | visualstudio | rider
ai:                        # 见 6.1
mcp:
  allow_source: true
```
