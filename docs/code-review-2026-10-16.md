# 代码审查与优化建议（2026-10-16）

## 修复进展（同日）

用户授权修复后，本轮已修改源码并增加回归。下文的问题描述及定位保留为修复前审查证据，行号可能已随修改移动；当前状态以本节为准。

| 项目 | 当前状态 |
| --- | --- |
| R01 | 已修复：扫描解析后以单一事务提交删除、文件、符号、原始引用和绑定边；解析异常及多写入块之间取消均回滚。 |
| R02 | 已修复：持久化原始引用，增量重算非精确覆盖的绑定；新增目标及同名转导出变更覆盖未修改调用方。新绑定成功会替换旧悬空边，真实删除仍保留缺失目标提示。 |
| R03 | 已修复：目标文件重建使关联精确调用方失效；未变化调用方可恢复启发式绑定，仍有效的精确边不重复生成；自动 Roslyn 检测过期 C# 文件，显式禁用仍生效。 |
| R04 | 已修复：重启/恢复使旧启动失效，旧 Promise 只能清理自身；等待凭据时停止不会继续启动进程。 |
| R05 | 已修复：所有扩展索引请求进入同一队列，全量优先；等待包含后续排队运行；服务繁忙结束后重新提交实际需求。 |
| R06 | 已修复：搜索与 DSM 校验请求代次、连接及查询状态，清空搜索或选择结果后旧响应失效。 |
| R07 | 已修复：索引完成时刷新当前 DSM 和命名空间视图，保留钻取状态。隐藏视图重新进入时沿现有加载路径取数。 |
| R08 | 已修复：停止接收新任务，取消索引及 Roslyn，等待后台任务和已有查询退出后再释放资源；同步/异步释放共享任务。 |
| R09 | 0.1.1 发布准备已修复：恢复 Microsoft.Data.Sqlite 10.0.13，以及 Pkcs / Xml 10.0.13 显式引用；最终依赖审计与测试结果见下方发布验证。 |

另修复两类直接影响调用图准确性的行为：

- **假分派**：不再仅凭方法内 `new Message` 或相邻行推导 Handler 调用。C# 语法树记录直接消息实参对应的分派调用偏移，解析器仅将该构造和该分派调用标为 conduit。单行及跨行真实分派保留；单纯创建消息、相邻发送无关参数不生成 Handler 分支。分派匹配改为按调用方及偏移查找，移除逐条扫描全部边的内循环；没有做大仓库性能基准。
- **旧链路混入新图**：网状焦点、自动/手动展开、索引刷新、光标/宿主焦点和模式/历史导航校验请求归属；旧跨模式追链不再恢复后发起旧焦点请求。时序刷新也不能覆盖等待期间的新焦点。

修复代码及回归入口：[索引器](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs)、[引用存储](../src/CodeAtlas.Core/Storage/IndexWriter.cs)、[引用读取](../src/CodeAtlas.Core/Storage/IndexReader.cs)、[分派解析](../src/CodeAtlas.Core/Resolution/HeuristicEdgeResolver.cs)、[索引服务](../src/CodeAtlas.Server/IndexService.cs)、[Webview](../extension/webview/main.ts)、[时序控制器](../extension/webview/callView.ts)、[索引回归](../tests/CodeAtlas.Indexing.TreeSitter.Tests/TreeSitterIndexerTests.cs)、[生命周期回归](../tests/CodeAtlas.Server.Tests/StartupRebuildTests.cs)、[Webview 并发回归](../extension/test/webviewConcurrency.test.ts)。

### 修复后验证

- `dotnet test CodeAtlas.slnx --verbosity minimal`：1094 通过，0 失败，0 跳过；测试摘要 39.4 秒，生成 44.2 秒，53 条告警，包含重复的依赖漏洞告警及既有编译/测试分析告警。
- `npm --prefix extension test`：31 个测试文件、418 项全部通过；保留现有 Vite 配置加载提示。
- `npm --prefix extension run typecheck`：通过。
- `npm --prefix extension run build`：通过，生成扩展及 Webview 开发 bundle；未重新发布各平台服务包或 VSIX。
- 已先复现再修复：新增目标缺边、解析失败后错误跳过、消息未发送却分派、图焦点/展开乱序、跨模式旧追链、搜索/DSM 旧响应、刷新遗漏、服务启动中重启、全量请求丢失、C# 精确入边失效、关闭后继续接收任务。另验证多写入块取消回滚、同名转导出重绑定，以及受控阻塞解析期间释放必须等待退出。

### 生效与剩余限制

- 索引格式从 11 升为 **12**。新服务启动会对旧格式索引自动全量重建；不能仅替换前端 bundle 却继续运行旧后端。已有发布包和已安装扩展不会因源码构建自动升级。
- 持久化原始引用避免重做未变化文件的语法解析，但绑定阶段会重算非精确覆盖的引用；本轮没有测量大型仓库的延迟及内存变化。C# 保存后的精确补算可能增加延迟，未实现按文档粒度的 Roslyn worker 更新。
- tree-sitter 分派兜底目前要求直接消息构造实参；先赋局部变量再发送、工厂返回消息等形式仍依赖 Roslyn 精确语义。没有扩展为全程序数据流推断。
- 重命名转导出（例如 `export { moved as target }`）仍存在解析限制，本轮不宣称已支持。各语言的动态调用、反射及框架运行时路由也不是静态调用图的完整覆盖范围。
- 初次审查时 NuGet 修复工具没有给出稳定的联合升级方案；后续 0.1.1 发布准备恢复此前已验证的稳定依赖：Microsoft.Data.Sqlite 10.0.13 和 Pkcs / Xml 10.0.13，不使用预发布依赖，也不关闭漏洞审计。
- C# 混合精度缺边回归模拟 Roslyn 已写入的符号及边状态；未在真实大型解决方案中执行“精确索引、编辑、自动补算”的 UI 验收。未进行进程强杀故障注入、真实 VS Code Webview 点击验收或全部平台发布测试。
- API 依据：SQLite 事务隔离/回滚参考官方 [Transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)（L1，本地 `SqliteTransaction` 未命中）；取消语义参考本地官方 TAP 文档 `dotnet-navigate-advanced-programming#0004` 的 Cancellation 节（L0）。其余项目绑定约定沿用现有模型与测试。

## 范围与结论边界

### 0.1.1 发布准备补充验证

- 扩展类型检查与生产构建通过；32 个测试文件、421 项测试通过。
- .NET 各测试项目通过：Core 200、TreeSitter 335、Roslyn 110、CLI 25、Server 501，共 **1171** 项，0 失败、0 跳过。初次全套运行因旧管道测试引用已删除的 `MapDi` 而编译失败，清理过期模块后服务端 501 项重新通过。
- 清理已被框架流程替代的旧管道布局 / 渲染器及其旧 API 测试，保留共用测试路径判定的回归。
- CLI 与 Roslyn worker 的传递依赖审计均未发现已知漏洞；仍存在少量可空性、过时 API 等编译告警，未为本次发布额外修改这些无关行为。
- 上述为自动化测试和构建结果，不代替所有系统上的 VS Code UI 验收，也不代表完整安全审计。

本轮针对当前工作区文件，而不是仅针对已提交的 HEAD。工作区存在大量原有未提交改动；审查阶段未修改业务源码，后续授权修复见上节，原有工作区改动予以保留。

已逐段读取索引器、文件扫描、数据库读写与事务、引用解析主流程、Roslyn 写回与进程管理、索引服务、HTTP 路由、扩展服务管理与索引命令、Webview 搜索及刷新流程、AI 队列与缓存、Git 统计，以及相关测试。部分大型文件只读取与上述路径相关的区段。

**这不是全仓所有文件的逐行审计完成证明。** 各语言适配器、各框架识别规则、布局算法、打包脚本及全部测试正文尚未完整逐行覆盖。此前业务准确性审计见 [已有审计](audit-2026-10-16.md)，本报告没有把其中标记已修的问题重新计入。

证据分级：实测 = 执行当前源码或构建产物复现；静态 = 从读写和控制流确认，尚未做对应故障注入；依赖告警 = 工具报告，不等于已证明当前应用可被利用。

## 问题清单

### R01 / P1：解析失败或进程中断后，增量索引可能永久缺边

- 位置：[批次提交](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L103)、[引用解析](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L115)、[增量跳过条件](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L65)。证据：静态。
- 文件的新符号、内容哈希及 `Ok` 状态先按批次提交；引用仅保存在内存 `pending` 中，随后在另一事务中解析。如果解析抛异常、取消或进程在两阶段之间退出，已经提交的文件没有完整出边。
- 下一次普通增量仍满足“哈希相同且状态为 Ok”，跳过这些文件，内存引用又已丢失，所以不能自愈。不能把一次成功的重试等同于数据完整恢复。
- 建议：持久化待解析状态或引用，并在出边完成后才标记文件可跳过；或使用索引代次，在完整生成后原子发布。不要仅给解析阶段加重试。
- 应补测试：解析器在写入后抛错，重开数据库并执行普通增量，最终调用边必须与全量结果一致；同时覆盖取消和多批次中断。

### R02 / P1：新增定义后，未修改调用方不会更新外部边

- 位置：[未变化文件跳过](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L65)、[悬空边重挂](../src/CodeAtlas.Core/Storage/IndexWriter.cs#L117)。证据：实测。
- 复现：先只有调用方 `app.ts`，其中导入尚不存在的 `./target` 并调用 `target()`；首次索引后加入导出该函数的目标文件，仅运行增量。
- 增量实际结果：`external:./target.target (0.50)`；全量结果：`target.ts:target (0.95)`。增量报告为 `parsed: 1, unchanged: 1`。
- 根因：只为重解析文件生成引用；旧外部边仍指向外部符号，既不是 `to_id IS NULL`，名字也不是目标的限定名，重挂不能修复。配置别名或导出关系变化也应纳入失效策略，但本轮未分别实测。
- 建议：保留原始绑定引用和反向文件依赖，目标或模块配置变化时重解析受影响调用方；最低限度先覆盖从外部目标转为仓库内目标的情形。
- 应补测试：新增目标、目标导出改变、别名配置改变后，增量结果与全量结果等价。

### R03 / P1：C# 保存后降级为 tree-sitter，原 Roslyn 入边可能无法重挂

- 位置：[精确限定名及边名升级](../src/CodeAtlas.Core/Storage/RoslynApplier.cs#L182)、[删除旧符号](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L94)、[精确名重挂](../src/CodeAtlas.Core/Storage/IndexWriter.cs#L117)、[自动 Roslyn 条件](../src/CodeAtlas.Server/IndexService.cs#L104)。证据：静态。
- 例如旧精确目标为 `Ns.C.M(int)`，入边的 `to_name` 也被升级成这个名字。修改目标文件后，tree-sitter 删除旧符号并写入不带参数类型的 `Ns.C.M`，旧入边成为悬空边。
- 其他文件仍有 `roslyn_at` 时，普通保存不再运行 Roslyn。重挂要求限定名完全相等，带参数的旧名匹配不到新符号，未修改调用方又不会重新解析。聚合图只加载有目标 id 的边，因此相关调用可能直接消失。
- 建议：为符号引入跨解析来源的稳定身份映射，或对变更 C# 文件及受影响调用方执行精确语义更新；降级时显式处理旧精确边，不能仅按字符串重挂。
- 应补测试：先精确索引两个互相调用的 C# 文件，只修改被调方法的方法体，再执行服务端普通增量，入边不得消失。

### R04 / P1：服务启动过程中重启，会返回已停止进程的地址

- 位置：[复用启动任务](../extension/src/serverManager.ts#L359)、[重启](../extension/src/serverManager.ts#L382)。证据：实测。
- `restart()` 停止并删除服务实例，却没有使 `starting` 中的旧启动任务失效；紧接着 `ensure()` 返回旧 Promise，不会创建新进程。`recover()` 同样存在这个窗口。
- 可控探针结果：`createdProcesses=1`、`restartReturned=http://old`、`returnedProcessAlive=false`、`managerHasServer=false`。
- 建议：按仓库维护生命周期代次，显式取消或等待旧启动；旧 Promise 的 `finally` 只能删除属于自己的映射。单独删除 `starting` 不足以防止旧任务完成后污染新状态。
- 应补测试：启动中重启、启动中恢复、停止后立即重新启动、多个并发恢复请求。

### R05 / P2：增量运行时点击全量重建，全量请求会被丢弃

- 位置：[全量绕过调度器](../extension/src/extension.ts#L564)、[繁忙处理](../extension/src/extension.ts#L573)。证据：实测实际函数，服务边界使用可控客户端。
- 全量直接进入 `doIndex(true)`；服务返回 `busy` 时，逻辑仅等待当前索引结束，之后按成功刷新，没有再次提交全量请求。
- 探针结果：`indexRequests=[true]`、`requeuedAfterBusy=false`。已有任务可以只是普通增量，不能满足用户请求的全量重建。
- 建议：统一调度所有索引请求，合并需求时全量优先于增量；繁忙时等待后重新提交，直到此次请求的需求被实际执行。
- 应补测试：后台增量期间发起全量，最终必须执行一次全量，并让全量调用等待该次任务完成。

### R06 / P2：搜索和 DSM 加载存在过期响应覆盖

- 位置：[搜索响应写回](../extension/webview/main.ts#L708)、[DSM 加载](../extension/webview/main.ts#L1214)。证据：搜索实测，DSM 静态。
- 防抖只取消未触发的定时器，不取消已经发出的请求，也没有请求序号校验。搜索旧词后再搜索新词，新响应先返回、旧响应后返回，会将列表覆盖成旧结果。
- 从实际源码提取的搜索逻辑探针结果：`input=new`、`showsOldResult=true`、`showsNewResult=false`。
- DSM 在等待期间切换容器或钻取路径，同样没有校验捕获的 API、容器键及 root 是否仍为当前选择；旧数据还可能搭配新容器标题。
- 建议：沿用已有模块关系视图的请求序号模式，校验连接代次和完整查询键；取消仅是节省资源，写回前仍要验证身份。
- 应补测试：请求反序返回、输入清空后旧请求返回、切换容器或服务后旧请求返回。

### R07 / P2：索引结束后，当前 DSM 不刷新

- 位置：[索引后刷新分支](../extension/webview/main.ts#L1735)、[索引消息入口](../extension/webview/main.ts#L1903)。证据：实测实际刷新函数。
- `doRefreshMap()` 只处理 `arch`、`flow`、`framework`，没有 `dsm`，也没有命名空间地图 `ns` 的分支。两种索引完成通知最终均进入这条刷新路径。
- 探针结果：当前为 `dsm` 时，`refreshRequests=0`。服务端即使清除了缓存，页面仍保留旧依赖、环和统计，直到用户手动触发重新加载。
- 建议：补齐当前视图刷新；隐藏视图标记为过期，回到视图时重取，同时保留仍然有效的钻取路径和选中状态。
- 应补测试：当前 DSM 下更新依赖并索引，矩阵同步变化；覆盖命名空间地图。

### R08 / P2：关闭索引服务超时后，后台任务仍可能使用已释放资源

- 位置：[后台索引任务](../src/CodeAtlas.Server/IndexService.cs#L67)、[Roslyn 不可取消](../src/CodeAtlas.Server/IndexService.cs#L127)、[释放](../src/CodeAtlas.Server/IndexService.cs#L479)。证据：静态。
- 服务没有生命周期取消令牌，`Dispose()` 最多同步等待 30 秒，忽略等待是否真正完成，随后释放数据库和适配器。大仓库或慢 worker 超过这个时限时，后台任务仍然活动。
- 可能结果是释放后访问连接或解析器、索引中断，以及进程退出前的异常；并非每次关闭都会触发。
- 建议：生命周期令牌贯穿扫描、解析和 worker；先停止接收新任务，再取消并异步等待退出，最后释放资源。超时应有明确的隔离及进程清理策略。
- 应补测试：关闭时仍在解析、仍在 worker 中、任务超过释放等待时限。

### R09 / P1：当前依赖解析包含已知高危告警

- 位置：[SQLite 入口依赖](../src/CodeAtlas.Core/CodeAtlas.Core.csproj#L11)、[MSBuild 相关依赖](../src/CodeAtlas.Indexing.Roslyn/CodeAtlas.Indexing.Roslyn.csproj#L13)。证据：NuGet 还原输出及编辑器诊断。
- 当前解析出的 `SQLitePCLRaw.lib.e_sqlite3 2.1.11`、`System.Security.Cryptography.Pkcs 8.0.0`、`System.Security.Cryptography.Xml 8.0.0` 均有已知漏洞告警。
- SQLite 示例公告：[GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)。同一个传递依赖会在多个项目重复告警，不能把告警行数当作独立漏洞数量。
- 建议：核对依赖树、修复版本和实际分发资产，优先升级拥有这些传递依赖的直接包；在 CI 增加漏洞门禁并重做跨平台发布验证。`ExcludeAssets="runtime"` 的依赖需核对实际部署影响，不应直接断言所有告警都可利用。
- 本轮没有升级依赖，也没有验证这些公告在当前应用中的具体利用条件。

## 优化方向

以下为代码结构支持的候选优化，未做 CPU、分配量或真实大仓库前后基准，不宣称固定提速倍数。

1. **分派推导避免反复扫整张边表。** [分派循环](../src/CodeAtlas.Core/Resolution/HeuristicEdgeResolver.cs#L172)对每个命中 Handler 的构造调用再次遍历全部边，并逐项查询符号。若总边数为 E、这类调用有 D 个，内层比较约为 D×E。先按调用方和行号建立索引，并批量准备类型及 Handler 信息；保持现有前后相邻一行的判定语义。测量 resolve 耗时、SQL 次数和分配量。
2. **保存文件时不要无条件重算所有 Git 历史。** [索引后处理](../src/CodeAtlas.Server/IndexService.cs#L84)每次运行 [git log --all](../src/CodeAtlas.Core/Architecture/GitHistory.cs#L28)。工作树普通保存通常不改变提交历史。按各仓库全部相关引用的指纹缓存统计，仅在引用、统计配置或仓库集合变化时更新；只比较 HEAD 不足以覆盖其他分支变化。测量保存到索引完成的 P50/P95。
3. **限定解析流水线的在途结果，降低全量索引峰值内存。** [解析结果收集](../src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs#L51)先保留所有变更文件的解析产物，再形成待解析引用及多份列表。可使用有界流水线或代次暂存，分阶段持久化；必须与 R01 的一致性修复一起设计，不能仅为降低内存提前宣布文件完成。测量峰值工作集、GC 和吞吐。
4. **减少查询串行等待和不必要的整库物化。** [同步读锁](../src/CodeAtlas.Server/IndexService.cs#L470)覆盖整个查询及部分文件读取，首个复杂视图加载整库时会阻塞轻量请求。优先把耗时文件读取移出锁、批量取数并建立一致的读快照；再用压测决定是否引入读连接池。现有 Outline/Impact 按需取数模式可以复用，不建议直接去锁共享连接。

## 性能模式检查记录

检查范围固定为索引服务、tree-sitter 索引器、启发式解析器三个主流程文件；数字为语法命中，不直接等于缺陷数量。

| 检查项 | 命中数 | 判断 |
| --- | ---: | --- |
| 字符串字面量 IndexOf 未指定比较方式 | 0 | 未发现该模式 |
| Substring | 0 | 未发现该模式 |
| StartsWith/EndsWith/Contains 总调用 | 21 | 含集合操作，不能直接判字符串问题 |
| 上述字符串字面量单参数调用 | 0 | 未发现该模式 |
| ToLower/ToUpper | 0 | 未发现该模式 |
| Replace | 5 | 按调用位置判断，不直接要求替换 |
| params | 0 | 未发现该模式 |
| Select/Where/OrderBy/GroupBy | 52 | 不将 LINQ 命中本身定为缺陷 |
| Any/All | 2 | 不据此要求手写循环 |
| 显式 new Dictionary/List | 13 | 不包含目标类型 new，非所有分配总数 |
| static readonly Dictionary | 0 | 未发现该模式 |
| RegexOptions.Compiled | 1 | 非本轮优先优化项 |
| 显式 new Regex / GeneratedRegex | 0 / 0 | 存在目标类型 new，不能误称没有正则 |
| sealed 类 / 类声明总数 | 3 / 4 | 其余为 static 类，无未封闭实例类问题 |
| IEquatable | 0 | 不适用，不能据此要求新增接口 |
| 同步 Wait | 2 | 一处读锁，一处释放等待；见 R08 |
| Task.Result / ValueTask / stackalloc | 0 / 0 / 0 | 未发现这些危险用法 |
| new byte[] / string.Format | 0 / 0 | 未发现该模式 |
| new JsonSerializerOptions / new HttpClient | 0 / 0 | 未发现该模式 |
| ToCharArray | 0 | 未发现该模式 |

## 验证与后续门槛

- `dotnet test CodeAtlas.slnx --verbosity minimal`：1081 通过，0 失败，0 跳过，测试摘要耗时 43.5 秒；生成成功，包含依赖漏洞警告。
- `npm --prefix extension test`：30 个测试文件，403 个测试通过；另有 Vite 配置加载兼容性提示。
- `npm --prefix extension run typecheck`：通过。
- 编辑器诊断：本轮返回的是上述依赖漏洞告警，没有发现新的编译或 TypeScript 错误。
- 已执行 5 类定向探针：新增目标的增量/全量对照、启动中重启、搜索响应乱序、全量请求遇到 busy、DSM 刷新分支。涉及 TypeScript 的探针执行实际源码提取/转译后的逻辑；边界使用可控依赖，不等价于真实 VS Code UI 集成测试。
- 临时 TypeScript 样例和数据库位于系统临时目录，不在工作区，不影响项目索引。没有写入 C# 探针代码。

建议先处理 R01-R04 的索引及生命周期一致性，再处理 R05-R08 的请求与界面状态；R09 在发布前独立设门禁。每项先补能在当前实现上失败的回归，再修复并运行相同验证。

| 分类 | 数量 | 优先关注 |
| --- | ---: | --- |
| P1 缺陷及依赖风险 | 5 | 索引缺边、进程竞态、依赖告警 |
| P2 缺陷及生命周期风险 | 4 | 请求丢失、异步覆盖、刷新遗漏、资源释放 |
| 性能候选方向 | 4 | 分派推导、Git 重算、峰值内存、查询排队 |

本报告由 AI 辅助审查生成，静态结论仍需对应故障注入测试及人工复核；性能建议需基准验证。本轮未完成全仓逐行覆盖，也未验证各平台发布包和真实 Webview 交互。