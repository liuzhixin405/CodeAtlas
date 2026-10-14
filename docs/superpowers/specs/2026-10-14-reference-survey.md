# 开源代码地图参考调研（2026-10-14）

目的：在重做 CodeAtlas 的"整个项目"视图之前，先看市面上的现成做法，决定借什么、避什么。全部仓库浅克隆在 `D:\github\reference\`（不进 CodeAtlas 仓库）。三份详细报告由调研代理逐文件读出，下面是结论；文件行号见各报告原文（本会话）。

| 仓库 | 许可证 | 能不能抄代码 |
|---|---|---|
| AsyncFuncAI/deepwiki-open | MIT | 可以 |
| sourcegraph/scip-dotnet | Apache-2.0 | 可以 |
| getappmap/appmap-js（sequence-diagram、components） | MIT + Commons Clause（禁止"销售"） | **只借概念** |
| swark-io/swark | AGPL-3.0 | **只借概念** |
| adamtornhill/code-maat | GPL-3.0（README 声明，无 LICENSE 文件） | **只借概念**，算法用 C# 重写 |
| glato/emerge | MIT | 可以 |
| BenMorris/NetArchTest | MIT | 可以（ccs-backend 已在用） |
| structurizr/dotnet（官方 2023-12 归档，克隆的是 d4span fork） | Apache-2.0 | 可以；只借 C4 模型的数据结构 |

## 1. DeepWiki-Open：AI 写仓库 wiki

**流水线**：浅克隆 → 按扩展名过滤文件 → 整文件切 350 词块、embedding 入 pickle →（一次 LLM）决定 wiki 结构（XML：sections / pages / relevant_files）→（每页一次 LLM）生成页面，要求大量 Mermaid 与 `Sources: [path:start-end]()` 引用 → 后处理把引用改成链接 → JSON 缓存。本地模型走 Ollama（embedder `nomic-embed-text`，generator 默认 `qwen3:1.7b`，`num_ctx` 32000），小模型加 `/no_think`、剥 `<think>`。

**致命弱点（我们不能犯）**：
- 页面生成时**文件内容根本没喂进去**，只给了路径列表；代码靠 RAG 取 top-20 块，而检索的查询是整段提示词，检索到的是"像提示词的块"而不是主题相关的代码。
- 引用从不校验：路径不存在也照样生成链接；Mermaid 不校验；文件树以 Python list 的 repr 喂给模型；参数顺序 bug 把排除当成包含；API 错误文本被当成页面内容缓存下来。
- 固定大纲（"Model Integration"）硬套到任何仓库；强制至少引用 5 个文件，逼模型凑数。
- 缓存键不含 commit / 模型 / 过滤条件，只能手动失效。

**值得抄的是它的 codemap 子功能**：只允许引用出现在上下文里的文件；片段必须逐字复制；输出 JSON 用 Pydantic 校验、最多重试 3 次；行号在事后拿片段到真实文件里反查修正。

→ CodeAtlas 的 AI 叙述层按 codemap 的纪律做：上下文直接来自索引（真实的调用 / 分发子图，不是 embedding 检索），输出 JSON schema 校验，每条引用（符号 id / 行号）事后对索引复核，缓存键含源码哈希与模型名。

## 2. AppMap：按入口的时序图（概念）

**模型**：`{actors, rootActions}`；节点类型 Loop / Function / ServerRPC / ClientRPC / Query，节点带 `children`、`digest`、`subtreeDigest`、`returnValue`。
**从事件流构图**：actor = 代码对象树上最近的"被包含"的祖先（默认 package / database / http / external-service 是 actor，可 expand 到类、exclude 掉某个、require 只保留经过某对象的栈）；列顺序按优先级（HTTP 固定最左、数据库与外部服务最右，组内按首次出现）。
**重复折叠**：子树 SHA-256 摘要，滑窗 1–5 找连续相同子树合成 `Loop{count}`。
**渲染**：PlantUML 导出；交互 UI 用 CSS grid（行 = 调用 / 子调用 / 返回，列 = actor），**默认折叠到深度 3**，选中节点所在路径自动展开，只经折叠节点可达的 actor 隐藏。

→ CodeAtlas 从静态调用链出时序图时需要：每个入口一棵有序调用树（callee、caller、children）、每个符号的 actor 键（类 / 命名空间 / 项目）与顺序、可选的返回类型。会丢的：真实分派（只能画全部候选实现并标"可能"）、真实次序与次数、耗时与异常。深度 3 折叠、actor 隐藏、子树摘要折叠这三条直接借。

## 3. scip-dotnet：另一个 Roslyn 索引器

**借**：多 TFM 取 `net{当前 runtime}.0` 否则取第一个；`FindImplementationForInterfaceMember` 扫全部接口找实现（隐式 / 显式都覆盖）；`OverriddenMethod` 链；`#line` 用 `GetMappedLineSpan`。
**我们已经比它好或要比它好的**：它的重载 id 是"同名成员里的位置"（成员重排 id 就变），泛型不编 arity（`Foo` 与 `Foo<T>` 撞），`GenericNameSyntax` 不产生引用（`List<IGeneric>` 里的 `List` 漏掉），运算符 / 索引器 / 访问器没有定义，源生成文档不索引，完全串行。CodeAtlas 的签名式限定名、`AllInterfaces`、并行都已经在前面。

## 4. emerge：多语言依赖图 + d3

**模型**：networkx 有向图，六种图（文件依赖、实体依赖、继承、合并、文件系统、变更耦合）；指标挂在节点上（fan-in/out、SLOC、方法数、空白复杂度、TF-IDF 关键词、git 变更 / 作者 / 共变）。"模块" = Louvain 社区（跑 5 次取最后一次，**非确定**）。输出 `node_link_data` JSON 再内联成 JS。
**UI**：一张平铺的 d3 力导向图，按指标定半径、按社区上色加凹包、热力图、搜索、git 时间过滤、变更耦合弦图。
**大仓库的问题**：没有聚合、没有层级、没有边捆绑，每帧重画全部；外部 import 也成节点，README 自己承认结果是"大泥球"，解法是手写忽略列表。

→ 正好印证：节点连线不适合做"整个项目"的第一屏。可借的是"指标挂在节点上、按指标渲染"的思路和变更耦合弦图。

## 5. code-maat：git 历史的行为分析（概念）

输入 `git log --all --numstat --date=short --pretty=format:'--%h--%ad--%aN' --no-renames`。分析与输出列：revisions（entity, n-revs）、coupling（entity, coupled, degree, average-revs；degree = 共同修订 ÷ 两者平均修订）、soc、age（entity, age-months）、entity-churn、authors、main-dev / ownership、fragmentation（1 − Σ 作者份额²）、communication 等。`-g` 分组文件把结果聚合到架构组件。耦合按提交 O(k²)，默认丢弃改动超过 30 个文件的提交；其余都是单遍扫描，内存里完成。

→ 给代码地图的是静态拿不到的三样东西：**热点**（修订次数 × 复杂度）、**隐性耦合**（常一起改却没有静态边的两个模块）、**年龄 / 所有权风险**。用 C# 直接从 `git log --numstat` 重写，几十行。

## 6. NetArchTest：项目自己声明的分层

`Types.InAssembly(..).That().ResideInNamespace(..).ShouldNot().HaveDependencyOn(..).GetResult()`；依赖用 Mono.Cecil 读 IL（基类、特性、接口、字段、方法签名、局部变量、IL 操作数、编译器生成的嵌套类型），`ResideInNamespace` 实际是不区分大小写的前缀匹配。结果只有失败类型列表，没有"哪条引用违规"。

→ **ccs-backend 已经用它写了 `PipelineArchitectureTests`**，这就是现成的"人声明的架构规则"。地图可以用 Roslyn 解析测试项目里的 `Types.*` 调用链，把 `ShouldNot().HaveDependencyOn(B)`（作用域 A）还原成禁止边 A→B、`OnlyHaveDependenciesOn` 还原成白名单，叠在 DSM 上显示"声明 vs 实际"。要注意前缀匹配语义、`Or` 组合、循环里构造的规则。

## 7. swark：LLM 直接画架构图（概念）

按扩展名找文件，按 `findFiles` 返回的顺序贪心塞进 token 预算，塞不下的静默丢弃；一段提示词要 Mermaid-only 的 subgraph 图；后处理只用正则抽代码块、修一个"子图与节点同名"的问题；结构错了的建议是"多生成几次"。

→ 反面教材：按文件顺序而不是重要性截断、没有任何事实校验、不可导航回代码、输出不确定。CodeAtlas 的 AI 只能在事实层之上做叙述，每条提出的边都要能在索引里找到。

## 8. 对 CodeAtlas 的决定

1. **第一屏 = C4 容器图**，容器与外部系统从 csproj / Dockerfile / Helm / appsettings / `AddHttpClient` / 消息客户端注册里静态发现（见 `2026-10-14-ccs-backend-c4-draft.md`）；仓库可用一份 C4 风格声明文件覆盖。Structurizr 的 `SoftwareSystem / Container / Component / Relationship` 数据结构直接借。
2. **容器内 = DSM**，不再画模块连线；叠加 NetArchTest 还原出的声明规则，环依赖与违规标红。
3. **流程 = 按入口的时序图**，借 AppMap 的 actor 推导、深度 3 折叠、子树摘要折叠；分派不确定时画候选并标"可能"。现有管道视图里有价值的部分（入口、分发、行为链、绕过）收进这里。
4. **git 行为信号**按 code-maat 的定义重写：热点、隐性耦合、年龄，作为 DSM 与容器图上的叠加层。
5. **AI 叙述**按 DeepWiki codemap 的纪律：上下文来自索引子图，JSON schema 校验，引用事后复核，缓存键含哈希与模型；本地模型。
6. 索引器补课（来自 scip-dotnet 对比）：`GenericNameSyntax` 引用、运算符 / 索引器定义、源生成文档——按需排期。
