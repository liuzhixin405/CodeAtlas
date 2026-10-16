# 验收记录

## 0.1.1 发布验收 — 2026-10-16

以下为当前源码验证；后续里程碑章节保留历史结果，不代表当前版本的功能缺失。

- 市场身份保持 `164910441.codetrail-graph`，显示名 CodeTrail Graph，版本升为 **0.1.1**；五平台服务与 Webview 一起重新构建。
- 文档与当前代码同步：默认调用时序图、框架流程、跨模式导航、增量 Roslyn、索引格式 **12** 自动重建，以及远程 AI 数据发送说明。
- 扩展：类型检查和生产构建通过，32 个测试文件、**421** 项通过。
- .NET：Core 200、TreeSitter 335、Roslyn 110、CLI 25、Server 501，合计 **1171** 项通过，0 失败、0 跳过。框架流程替换后遗留的旧管道布局 / DTO 测试已清理，测试路径判定的工具函数回归保留。
- 依赖：恢复 Microsoft.Data.Sqlite 10.0.13、Pkcs / Xml 10.0.13；CLI 与 Roslyn worker 的传递依赖报告未发现已知漏洞。
- Windows 正式产物实测：3 个夹具文件全量解析成功（134 符号、136 边）；第二次增量 parsed 0、unchanged 3，架构及入口后处理按指纹跳过；worker 成功探测 .NET 10.0.402。
- 五个平台的 VSIX 均检查包内 publisher / name / version、平台标记、图标、扩展 bundle、自带运行时、原生 SQLite 和 Roslyn worker；未混入其他 VSIX、源代码目录或 node_modules。
- Marketplace 已接收并通过服务端版本记录确认五个平台的 `0.1.1`，沿用 `164910441.codetrail-graph`；上传完成时仍在等待市场验证扫描，上传成功不等同于扫描通过。
- 边界：Linux / macOS 仅完成交叉构建和包内容验证，未在对应系统执行 UI 验收；无大型真实仓库的新增性能基准，不把夹具耗时当作全仓性能保证。保留现有可空性、过时 API 等编译告警。

## 里程碑 1（tree-sitter + CLI）— 2026-10-13

机器：Core Ultra 9 285H / 64GB，Release 构建，SQLite 索引库。

### ccs-ui（TypeScript / TSX）

| 指标 | 全量 | 第二次（增量） |
|---|---|---|
| files | 1557 | 1557（unchanged 1557，parsed 0） |
| symbols | 26758 | 26758 |
| edges | 117507 | 117507 |
| elapsed | 6.2s | 0.9s |
| failed | 0 | 0 |

抽查（符号 / 预期 / 实际 / 结论）：

1. `LoginPageNew/index.tsx:LoginPageNew` / 调用 `getAppConfig`、`useState`、`useEffect`，引用 `LoginPageNewProps` / 全部命中：`getAppConfig` 经 `./appConfigs` import 解析为 0.7，`useState` / `useEffect` 归为 external 0.5，`LoginPageNewProps` 同文件 0.9 / **正确**。JSX 里的 `<Box>`、`<ThemeProvider>` 组件引用不在图中（见"发现的问题" 3）。
2. `utils/searchQuery.ts:loginOutOfRange` / 调用同文件 `quoted`，被同文件 `resolveSearchQuery` 调用 / 两条边都是 0.9，行号与源码一致 / **正确**。
3. 搜索 `login` / 返回 LoginFormZoom、LoginPageNew、LoginButton、LoginField、useStore 的 `login` 属性等 / 结果合理，修复前混入的 `external:` 条目已排除 / **正确**。

### ccs-backend（C#，tree-sitter 兜底）

| 指标 | 修复前（含 worktree 副本） | 修复后 |
|---|---|---|
| files | 16165 | 4372（csharp + typescript/tsx） |
| symbols | 217041 | 59129 |
| edges | 4626167 | 846108 |
| elapsed（目标 ≤ 120s） | 88.7s | **17.1s** |
| failed | 0 | 0 |
| 悬空边 | — | 0 |

边分布（修复后）：calls 0.3 共 347763 条、imports 1.0 共 234958 条、calls 0.5 共 115342 条、contains 53319 条、calls 0.7 共 36976 条、calls 0.9 共 30269 条、implements 0.7 共 2615 条。

**评审修复后再跑一次**（多候选改为取最窄非空层级）：edges 846108 → 730965，其中 calls 0.3 从 347763 降到 232534（减少 33%），elapsed 21.3s。问题 1 的噪声减少了三分之一，剩余部分是真正全仓库范围内歧义的名字，要靠 Roslyn。

抽查：

1. **Controller action** `ApprovalTaskController.ApproveAsync`（第 74 行）/ 预期：`new ApproveTaskCommand(...)`、`_dispatcher.SendAsync(...)`、`Ok(result)` / 实际：`ApproveTaskCommand` 0.7、`IDispatcher.SendAsync` 0.7 都对；`Ok` 没有 external 候选可选，被解析成 20 条 0.3 边指向各测试类里恰好叫 `Ok` 的方法 / **主路径正确，`Ok` 为噪声**（见问题 1）。
2. **Service 方法** `EditLockService.ReleaseAsync`（第 33 行）/ 预期：`EditLockCaller.RequireEmail`、`EditLockModules.For`、`repository.ReleaseAsync`、`repository.GetAsync` / 实际：前两条 0.7 正确；`repository.ReleaseAsync` 被解析为**自身**（同文件唯一同名，0.9），实际应是 `IEditLockRepository.ReleaseAsync`；`repository.GetAsync` 展开为 20 条 0.3 / **发现一处精度错误**（见问题 2）。
3. **Repository 方法** `ApprovalTaskRepository.GetAsync` 的调用者 / 预期：被各 Handler 调用 / 实际：13 条 0.3 入边，候选中确有真实调用者（如 `GetEditLockHandler.HandleAsync`），但因 `GetAsync` 全仓库到处都有，无法区分 / **符合 spec 对多候选的定义，可读性差**。
4. **接口实现** `IQueryHandler` 的实现者 / 预期：所有 `*Handler : IQueryHandler<...>` / 实际：226 条 implements 边，抽样 9 条全部为真实 Handler，confidence 0.7（同命名空间或 using 范围）/ **正确**。
5. **高频工具方法** `For`（3 个同名定义，5747 处调用）/ 预期：多候选折叠为 0.3 / 实际：按名字查询给出 3 个候选的限定名列表；每个候选的入边均为 0.3，唯一一条 0.7 是同命名空间内的真实调用 / **符合预期**。

### 发现的问题

1. **多候选噪声**：`Ok`、`GetAsync`、`List`、`For` 这类名字在大仓库里有几十到上千个定义，每个调用点产生 20 条 0.3 边，`calls 0.3` 占了全部边的 41%。tree-sitter 层不知道接收者类型，这是启发式的固有上限。建议：里程碑 2 的图界面按 spec 把多候选折叠成一条带数字的虚线；里程碑 3 由 Roslyn 产出精确边后覆盖掉这些 0.3 边。可选的中间改进：当候选超过 5 个且无一在文件/import 范围内时，只记一条指向 `ambiguous:<name>` 伪符号的边，而不是 20 条。
2. **同文件同名优先导致误判**：`repository.ReleaseAsync(...)` 被解析成所在类自己的 `ReleaseAsync`。原因是"同文件唯一"级别不看接收者。可选改进：引用携带"是否有非 this 接收者"的标记，有接收者时跳过同文件级别。Roslyn 会彻底修正。
3. **TSX 的 JSX 组件引用未入图**：`<Box>`、`<ThemeProvider>` 没有 references 边。需要在 tsx 查询里加 `jsx_opening_element` / `jsx_self_closing_element` 的 name 捕获。
4. **TS 路径别名**：`@/components/...` 这类 tsconfig `paths` 别名被当成外部包。需要读取 tsconfig.json 的 `baseUrl` / `paths`。
5. **C# `using` 边数量大**：`using X.Y;` 会连到每个声明了命名空间 `X.Y` 的文件（上限 20），imports 1.0 有 23 万条。不影响正确性，但占空间；可考虑命名空间符号按限定名去重为一个逻辑节点。
6. **已修复**：`.claude/worktrees/` 下的 git worktree 副本被索引（符号三倍膨胀）→ 扫描器现在跳过含 `.git` 的子目录；搜索结果混入 external 符号 → 已排除。

### 结论

里程碑 1 验收通过：性能远优于目标（17.1s 对 120s），增量路径有效，直接调用、import、接口实现在 TS 和 C# 上都能正确呈现。启发式解析的精度上限在真实仓库上清晰可见（问题 1、2），这正是里程碑 3 引入 Roslyn 的理由，不在本里程碑内修。

## 里程碑 2a（本地服务）— 2026-10-13

`codeatlas serve D:\gitlab\RC_SZ\ccs-backend --db C:\tmp\ccs-backend-index.db`，stdout 首行 `{"port":62448}`。

| 接口 | 结果 |
|---|---|
| `GET /api/health` | files 4373，symbols 57705（不含 external），indexedAt 正确 |
| `GET /api/search?q=ApprovalTaskController` | 返回构造函数与测试类，带 path 和 signature |
| `GET /api/symbols/at?path=D:\...\ApprovalTaskController.cs&line=85&col=20` | 绝对 Windows 路径正确归一化，命中 `ApproveAsync`（74–87 行） |
| `GET /api/symbols/37/neighbors?direction=out&kinds=calls,references` | 13 条；`ApproveTaskCommand`、`IDispatcher.SendAsync` 0.7；`Ok` 的 20 条 0.3 都带 `ambiguousGroup = "calls:86"`，页面可按它折叠 |
| 不存在的文件 | 404 |
| kill 进程 | 立即退出，无残留 |

发现：C# 方法带特性时 `signature` 取到的是第一个特性行（`[HttpPost("{id:int}/approve")]`）而不是方法签名。待 2b 修：签名应跳过特性行。

## 里程碑 2b（面板页面，浏览器模式）— 2026-10-13

`codeatlas serve ccs-backend` + 静态托管 `extension/webview/index.html?api=...`，Chromium 1280×800。

| 步骤 | 结果 |
|---|---|
| 搜索 `ApproveAsync` → 回车 | 焦点居中；左侧 `ApproveTaskCommand`、`SendAsync`（0.7 粗线）、`ApproveDto`、`CancellationToken`（external 虚框）与"9 个可能目标"胶囊；右侧两个测试方法调用者 |
| 双击胶囊 | 清单弹层列出 9 个同名 `Ok` 候选（0.3 · L86） |
| 勾选一个 → 加入 | `Ok` 变为实节点带 0.3 虚线，胶囊变为"8 个可能目标" |
| 右键 `SendAsync` → 设为焦点 | 面包屑 `ApproveAsync › SendAsync`，详情条显示签名 |
| 焦点 `IDispatcher.SendAsync`（504 个调用者） | 直接显示 30 个（confidence 高优先），其余折成"还有 474 个调用者"，多候选折成"222 个可能调用者"；29 节点 33 边，布局 fit 正常 |

验收中发现并修复：
1. 入边的多候选按行号分组会得到 20 个"1 个可能目标"胶囊 → 入边改为按 kind 分组为"N 个可能调用者"。
2. 底部详情条长文本把 grid 撑宽到 1409px → `minmax(0,1fr)` 加 `overflow:hidden`。
3. cytoscape-elk 的 `fit` 在标签尺寸测量前计算 → 布局结束后等一帧渲染再用 `getFitViewport` 动画过去。
4. 焦点层没有阈值保护，`SendAsync` 一次画 408 个节点不可读 → 新增溢出胶囊（每侧每种边最多 30 个），同时取代了原设计里"展开超过 30 个先弹清单"（裁定：溢出胶囊对焦点和展开统一生效，用户先看到最重要的 30 个，再按需拉剩下的，比先弹清单更顺）。
5. Cytoscape 不接受 `var()` 字体值 → 用 `getComputedStyle` 解析后传入。

未修（2c 或之后）：C# 带特性的方法 `signature` 取到特性行；`width: label` 在 Cytoscape 3.34 标记为弃用。

## 里程碑 2c（VS Code 扩展）— 2026-10-13

- `npm test`：23 个扩展侧测试通过（端口行解析、等待超时/流结束、服务查找顺序、保存防抖 10 次合并为 1、运行中变更结束后重跑、runNow、错误回调）。
- `npm run package` → `codeatlas-0.1.0.vsix`（8.66 MB，内含 `server/` 70.7 MB 的 framework-dependent 服务，依赖本机 .NET 10 运行时）。
- `code --install-extension codeatlas-0.1.0.vsix --force` 安装到本机 VS Code 1.141。

手工端到端清单（在 VS Code 里打开 D:\gitlab\RC_SZ\ccs-backend 后由使用者完成，结果回填）：

| 步骤 | 预期 | 结果 |
|---|---|---|
| 打开任一 .cs 文件，光标放在方法名上，右键"在 CodeAtlas 中查看" | 首次：提示"还没有索引"→ 点"建立索引"，状态栏显示进度，约 20 秒后面板出现并聚焦该方法 | |
| 再次右键其他方法 / `Ctrl+Alt+G` | 面板直接切焦点，面包屑追加 | |
| 双击图中节点 | 未展开的展开一层；已展开的在编辑器中打开定位到行 | |
| 双击"还有 N 个 / N 个可能目标"胶囊 | 弹清单勾选加入 | |
| 右键节点 → 在编辑器中打开 | 左侧编辑器跳到定义行 | |
| 修改任一 .cs 文件并保存 | 1.5 秒后状态栏"索引中…"，结束后面板刷新 | |
| Reload Window | 任务管理器里没有残留 codeatlas.exe（看门狗 --parent-pid） | |

已知限制：首次索引前面板不可用；多根工作区按文件所属文件夹各起一个服务。

## 里程碑 2 复审修复 + 调用链（第二轮，用户试用反馈后）— 2026-10-13

用户在 VS Code 里试用后反馈：一跳邻域图"看不懂"，要的是从一个方法**一路向下到最后的调用、从底层一路向上到源头**，以及整个项目的地图。本轮做了前两项。

### 评审修复（独立评审 1 Critical / 6 Important 全部修复）
- 服务加 `X-CodeAtlas-Token` 鉴权（扩展每次启动生成，`--token` 传给服务，`init` 消息传给页面）；预检放行。
- 保存文件只对本会话已拉起服务的文件夹做增量，不再静默拉起服务往仓库写索引。
- 增量后符号 id 变化：按 id → 原位置 → 限定名 依次找回焦点。
- 首次建索引后直接定位，不用再右键一次。
- 覆盖层只在真正看到过"进行中"后才关闭；init 不再误弹"索引完成"。
- 并发 `ensure()` 共享启动 Promise；请求失败自动重启一次，再失败等用户手动重启。

### 调用链
- **分发边（spec 4.7 的 tree-sitter 弱版本）**：`class X : IQueryHandler<T, …>`（接口名以 Handler / Consumer 结尾）产生 `X → T` 的 handles 边；任何方法里 `new T(...)` 都推导出 `方法 → X.HandleAsync` 的 dispatches 边（0.4，证据行 = new T 那一行）。
- **接口与实现同名时优先实现**：`repo.ListAsync()` 不再在 `IRepo.ListAsync` 与 `Repo.ListAsync` 之间变成多候选。
- **LINQ/BCL 常见名字**（ToList、OrderBy、FirstOrDefault、Add、Format、Log… 共 130 余个）不按"全仓库唯一"解析，归为外部。
- **页面**：聚焦后自动沿 calls / dispatches 向下展开到叶子（深度 ≤ 12，单节点分支 > 6 处停下并提示）；"↑ 到源头"按钮向上追；类型引用默认不画；测试代码默认不画；布局改为调用者在左、被调用者在右。

### 在 ccs-backend 上的实测（浏览器模式，索引 4538 文件 / 78 万边 / 27 秒）
| 操作 | 结果 |
|---|---|
| 聚焦 `Mt4DataFeedController.ListDataFeedModules` | 自动展开出完整链路：`ListDataFeedModules` →(dispatch)→ `ListMt4DataFeedModulesQueryHandler.HandleAsync` → `ListDataFeedModulesAsync` → `GetServerInfoWithConnection` / `GetDataFeedModulesAsync` / `Create` → `Adapt` / `FirstOrDefault`…，24 节点 |
| 聚焦 `GetServerInfoWithConnection` → ↑ 到源头（含测试） | 106 节点，一半以上是单元测试方法 → 促成"测试代码"开关 |
| 同上（默认不含测试） | 37 节点；追到的源头：`ListDataFeedModules`、`MovePlugin`、`PatchPlugin`、`SyncPluginPriority`、`PatchBatchPluginsAsync`、`CreateMt4DataFeedCommandValidator`，全是 Controller action / Validator，即真正的入口 |

### 本轮裁定（偏离原 spec 之处）
1. 图方向改为调用者在左、被调用者在右（原 spec 相反）。读调用链时左→右更符合直觉。代价若错：一个常量。
2. 溢出胶囊取代"展开超过 30 个先弹清单"，并同样保护焦点层。评审者认可。
3. 测试代码默认不参与自动展开（可开）。追源头时测试是最大噪声。代价若错：测试驱动的调用关系要手动勾选。
4. BCL 常见名字黑名单是启发式；仓库里真有同名自定义方法且只在 import 范围外被调用时会被误归外部。Roslyn（里程碑 3）替代。

### 待办
- 整个项目的地图（命名空间 / 模块级聚合视图）——下一步。
- 评审 Minor 清单（14 条）见评审记录，均未处理。

### 第三轮（用户反馈：点 QueryAsync 炸出几百个调用者）— 2026-10-13
- 同一行里"对分发方法的调用 + new 消息对象"两条边标为 `conduit`，页面默认不画（"分发细节"开关可开）；dispatch 边 via 改为 `dispatch:消息类型`，边上显示 `new ListMt4DataFeedModulesQuery`。
- 索引库加 `user_version`（当前 2）；旧库打开时 `needsRebuild=true`，扩展自动全量重建。
- 服务启动超时 5s → 20s（首次启动被杀毒扫描拖慢；正常 1.2s）。
- 通用入口名（HandleAsync / Execute / Consume …）显示为 `类名.方法名`。
- 实测：聚焦 `ListDataFeedModules` 时唯一出边是 `new ListMt4DataFeedModulesQuery → ListMt4DataFeedModulesQueryHandler.HandleAsync`，链路向下完整；全量索引 4538 文件 / 74.5 万边 / 38s。

## 项目地图 — 2026-10-13
- 服务 `GET /api/map?root=`：C# 按命名空间、脚本按目录分组，`.` 与 `/` 都当分隔符（C# 项目目录里的前端目录归到该命名空间组）；root 下直接的类型作为类型节点；边按 calls / dispatches / implements / inherits / references 计数，排除 0.3 多候选边。整库聚合首次 1.7s，按 root 缓存，索引后失效。
- 面板"项目地图"模式：节点大小按符号数，边粗细按依赖数，含分发的边橙色；双击模块钻入，面包屑回退，双击类型切回调用图并聚焦。
- ccs-backend 实测：顶层 1 个大模块 + 若干全局命名空间类型；钻入后 12 个模块、42 条依赖，Application 21476 / Infrastructure 5525 / Tests 24822。
- 已知：Tests 模块的边占了大头，地图里还没有"隐藏测试"开关；8 个 .NET 测试 + 4 个 mapState 测试覆盖聚合规则与钻取路径。

## 主干优先的调用链（用户反馈"乱糟糟一坨"后）— 2026-10-13
- 每个节点只自动展开排名前 N 个分支（工具栏"主线 / 2 分支 / 3 分支 / 5 分支 / 全部"，默认 2），其余折成"还有 N 个目标"。排名：分发边 > 仓库内调用 > 实现/继承；被 40 处以上调用的工具方法、Validate/Format/Is/Has/Try 之类名字、测试代码降权；与焦点同模块加分。
- 一个节点调的全部外部库方法折成一个"N 个框架调用"胶囊。
- 自动向下结束后，从焦点逐步选分最高的孩子得到主干，主干边加粗、其余节点与边变淡。
- 连续聚焦同一符号不再重复面包屑；节点与边按 id 排序，同样的数据永远摆成同样的图。
- ccs-backend 实测 `ListDataFeeds`：16 节点 15 边（此前约 45 节点），主干 `ListDataFeeds → ListMt4DataFeedsHandler.HandleAsync → ListDataFeedsAsync → ToServerName → GetServerNameById`，分支胶囊 6 个。

## 里程碑 3：Roslyn 精确语义 — 2026-10-13
- 结构：`codeatlas-roslyn.exe`（独立进程，MSBuildWorkspace 打开 .sln，两遍：先收集 `Handler<T>`，再逐文档输出 JSON Lines）→ `RoslynRunner`（拉起子进程、流式反序列化、10 分钟无输出判挂死）→ `RoslynApplier`（写回索引：按文件删 tree-sitter 的 calls/references/inherits/implements/dispatches 边，符号按位置合并并把限定名改成带参数类型的形式，其他文件指向它的边 to_name 同步改写）→ `ImplementationLinker`（接口调用补一条到实现的派生边：唯一实现 0.9，2..20 个候选各 0.5，via=`impl`）。
- 接线：`codeatlas index --roslyn auto|on|off [--sln]`；服务在全量索引或首次索引后串行跑 Roslyn，`/api/index/status` 的 phase=`roslyn`，`/api/health` 加 `roslynAt` / `roslynStaleFiles`；保存触发的增量只跑 tree-sitter，改过的文件退回启发式直到下次全量。索引版本升到 3，`files.roslyn_at` 列用 ALTER TABLE 迁移。
- 发布：`scripts/publish.ps1` 把两个 exe 一起放进 `dist/`，扩展打包脚本整目录拷进 VSIX。

### ccs-backend 实测（8 项目 / 4496 文档）
| 项 | 数值 |
|---|---|
| tree-sitter 全量 | 4539 文件 / 74.5 万边 / 34s |
| Roslyn worker 单独 | 70s，输出 43.8 万条记录（190MB JSON Lines），峰值 1.0–1.1GB |
| 写回（第一版） | 438s —— `DELETE FROM edges WHERE file_id` 没有索引，每个文件全表扫描；逐条 CreateCommand |
| 写回（加 `ix_edges_file`、单事务、预编译语句复用） | 13.7s |
| 端到端 `index --full --roslyn on` | 86s（修完下面的分析器漏洞后：Roslyn 阶段 53s，obj/ 文件 0 个被跳过，35.6 万条边） |
| 结果 | 合并 53968 符号、新增 52、35.6 万条精确边（全部 1.0）、512 条 DI 注册、13524 条接口→实现派生边、0 错误 |

### 抽查（里程碑 1 验收里的问题点）
| 抽查 | tree-sitter 时 | Roslyn 后 |
|---|---|---|
| `EditLockService.ReleaseAsync` 里的 `repository.ReleaseAsync` | 指向自身 | `IEditLockRepository.ReleaseAsync(EditLockModule, string, string, CancellationToken)` 1.0 + `EditLockRepository.ReleaseAsync(...)` 0.9（唯一实现） |
| `ApproveAsync` | 一个名字混在一起 | 7 个重载各自独立符号（Controller / 接口 3 个 / 实现 3 个） |
| `GetAsync` 调用者 | 13 条 0.3 多候选 | 22 个 `GetAsync` 各自独立，调用边精确到重载，无 0.3 |
| `ListDataFeedModules` 链路 | 完整 | 完整且更深：`ListDataFeedModules →(dispatch:ListMt4DataFeedModulesQuery)→ ListMt4DataFeedModulesHandler.HandleAsync → IMt4DataFeedApiClient.ListDataFeedModulesAsync →(impl 0.9)→ Mt4DataFeedApiClient.ListDataFeedModulesAsync → GetServerInfoWithConnection / IMt4X86ServiceClientFactory.Create →(impl)→ Mt4X86ServiceClientFactory.Create`；gRPC 生成代码（obj/ 下）归为外部 |

### 回放记录发现并修掉的分析器漏洞（1502 条边起点找不到）
- 1097 条：record 合成的 `Equals(T)` 等成员作为 implements 起点 → 跳过 `IsImplicitlyDeclared`。
- 208 条：属性初始化器 `public X Y { get; } = new X()` 里的调用归到了 `<Y>k__BackingField` → 归到属性。
- 187 条：顶级语句的调用归到编译器生成的 `Program.<Main>$` 而它没有符号 → 补一个 `function` 符号 `<top-level>`。
- 7 条：索引器 `this[int]` 没当成声明 → 加 `IndexerDeclarationSyntax`。

### 页面
- 唯一实现（0.9）时，同一行上指向接口方法的调用边被实现边替代（边标签"calls（接口→唯一实现）"），链路直接走到实现；多候选或同一行多条调用时不折叠；入边不折叠。
- 状态栏在 Roslyn 阶段显示"Roslyn 编译中"→"Roslyn 123/4496"，输出面板打印 Roslyn 结果或未运行原因。

### 裁定
1. 记录定义（`RoslynRecord` 等）从 `CodeAtlas.Indexing.Roslyn` 挪到 `CodeAtlas.Core.Roslyn`：CLI/Server 只需要应用记录，不该拖进 Roslyn 程序集。
2. Roslyn 只在全量 / 首次索引后跑，保存触发的增量不跑：整解决方案分析 1 分钟，不适合每次保存。代价：改过的文件退回启发式，`health.roslynStaleFiles` 可见。
3. 接口 → 实现的派生边按 implements 关系推导、不看 DI 注册；多实现时给候选而非挑一个。符合"以实际代码为准"的要求。
4. obj/ bin/ 下的生成代码按仓库外处理（gRPC stub、AssemblyInfo）。
5. 基类虚方法 → 重写 的派生边还没做（只处理接口）。

## 真实项目审计后的并行修复 — 2026-10-16（晚）
- **起因**：用户在 CodeAtlas 自己的仓库里看到测试夹具被当成"shop / svc Go 服务"，还推出一个假 PostgreSQL，说"测试写了一堆，但是拿实际项目一试，一堆问题"。当时回归 55 项全绿：检查全是"该有的有没有"，挡不住假入口、错连、假边。
- **审计**：四个只读代理对照源码逐视图挑错，覆盖 CodeAtlas 自身、ccs-backend、ccs-ui（单独 / 合并）和 9 个公开项目，归并成 19 类问题，见 `docs/audit-2026-10-16.md`。
- **并行修复**：用户要求"多开一起修复，以最快的时间修复"。9 个代理按文件归属并行修，共同规则写在 brief 里，构建经全局锁排队。主线负责索引版本 10→11、跨组配合、面板显示、回归反例和收尾。
- **修前 → 修后**（都在真实项目上核对过源码）：
  - **前端续接**：ccs-ui 错连 22 → 0，`${x}` 不再匹配字面量段，HTTP 方法不同的不连。
  - **读写颠倒**：MT4 列表 GET 不再续接到 bulk-update，CfgUpdateSymbol 9 → 0 次。
  - **后端续接**：Api→Publisher 0 → 162；Api→MT4Proxy 18（12 条错）→ 68。
  - **前端出口**：372 → 409 处识别；`api.post` 能连到 `ApiClient.post`（423 / 425 处），ApiClient.post 的影响范围 0 → 33 个页面。
  - **常驻服务 / Hub 按宿主真实注册归属**：ccs Api 去掉 6 个 Worker 服务，Worker 去掉 4 个服务和 8 个 Hub；Kafka→Worker 假边去掉。
  - **eShop**：RabbitMQ 发布边 7 → 4 个容器；共享库的假 GET / 只留在 3 个真调用的容器。
  - **lambda 路由**：CodeAtlas 的 23 条路由 1 → 23 个符号，影响范围不再把 23 个入口全算上。
  - **异步命令**：19 个分派到 EnqueueJobHandler（只入队），不再连到后台作业 Handler；Handler 清单 557 → 473，去掉 79 个假的。
  - **请求流程里的管道**：补上 Publish 领域事件、审批回放的分派、行为 `next()` 之后的提交，以及 7 个漏掉的验证器和 Fetcher；不经分派器的入口不再挂"管道行为"。
  - **时序图**：同档分支按"分量"排（子树规模、I/O、公共工具降权、名字相关度、取数据的 hook 优先），主调用不再被字母序折掉。flow-coverage 召回 97.2% → 98.7%；同一符号一张图只展开一次，ccs-ui 平均节点 108 → 60。
  - **显示**：局部函数显示真名（原来 83 行都叫 `Build`）；外部调用按类别成行（`JpaRepository.save` 单独一行，不再藏在 [Logger] 下）。
  - **TS 同名错连**：Set.has、Map.get 这类 12 条错边清零；以回调传出的函数补上边，fastapi 的 /login 能一路走到 `login_access_token`。
  - **入口**：Express 8 → 20 条且带 /api 前缀；补上 DRF 继承方法、GraphQL 29 条、MVC 约定路由、健康检查；测试客户端不再算入口。
  - **容器图**：
    - 测试字符串里的规则 7 → 0，整库 DSM 的夹具 / 生成代码组件去掉；
    - VS Code 扩展算成一个容器，连到 CLI；
    - 补上 AWS SSM、Keycloak、MT4 x86 gRPC、Redis、AI 端点；
    - Lark / SendGrid 按服务命名，Keycloak 合成一个。
  - **框架流程**：
    - CLI 容器按 Web 画；ccs Worker 改成常驻服务驱动分派器；
    - Send / Query 合并，部分 Handler 自己提交（25 直接 / 63 经服务），阶段计数与树一致；
    - gin 的说明按注册函数点名，ccs-ui 补上代理一跳和 6 个局部布局守卫，gRPC 服务统一画法。
  - **编辑器与面板**：右键收进「CodeAtlas」子菜单，CodeLens 加 `ctg:` 标识，「调用 N 个」能直接跳到经分派的 Handler；面板有了后退 / 前进（← →、Alt+← / Alt+→、鼠标侧键）。
- **主线收尾时发现并修掉**：
  - 回归第一轮 6 项不通过，3 项是真问题：MT5 分组表的主数据 hook `useMT5GroupByServerId` 被导入模板 hook 挤掉，因为名字相关度 2/5 < 2/4 → 加了"取数据的 hook 优先"。
  - 另外 3 项是脚本问题：`[roots]` 提示行让 Get-Json 解析失败、合并模式要存库才去重、Django 第一条入口变成 `/admin/`。
  - 我自己改文案时引入了两处变量重名，造成编译错误，已修。
- **测试**：.NET 1081 个（Server 488 / Core 197 / TreeSitter 269 / Roslyn 102 / Cli 25），vitest 403 个。每个代理都把关键规则逐条还原，确认测试会变红（共 100 多处）。
- **回归**：`scripts/regression.ps1` 17 个场景 75 项，全部通过。新增 20 项"反例："检查，即"不得出现"类：常驻服务不串容器、假边 / 假外部系统、异步命令不画成同步、列表查询不续接到批量写入、lambda 路由不共用符号、测试夹具不是容器等。
- **面板实测**（新索引）：ccs-backend `RestartDataFeeds` 画成 验证（RestartMt4DataFeedsCommandValidator）→ 权限 → 审计 → RestartMt4DataFeedsHandler → MT4Proxy HTTP，并标出跳过 Approval。
- **未做**（详见 audit 文档）：
  - hook 只画页面用到的返回成员；
  - Blazor .razor 进索引；
  - `@/auth` 再导出；
  - 分部类父符号；
  - DSM 提交号入库；
  - .NET 生成 gRPC 客户端续接；
  - 默认扇出仍为 6。

## 框架流程覆盖五种语言 + 请求流程里的管道 + SPA 页面入口 — 2026-10-16（下午）
- 起因：
  - 用户问"其它语言的也改过来了吗"，选了"一起做吧"。
  - 做的过程中又指出（ccs-backend）："控制器调用 handler 之前有一道 validation 验证……没有任何地方体现出来"，"还有其他的可能是通过反射来的，不是直接引用"。
- **分工**：主线先抽共用工具，在 FrameworkFlowBuilder 里加了 internal 的 `CallNode` / `Assemble` / `Empty` 等，以及新的阶段键；按容器 SubKind 分派；页面的入口占位和清单标题改成用服务端给的名字。然后五个代理并行，各写一个 `*FrameworkFlow.cs`，构建经全局锁脚本排队。
- **各语言**（实测摘要）：
  - **Spring**：
    - spring-boot-realworld：Security 过滤器链里的 `JwtTokenFilter.doFilterInternal`（`addFilterBefore`）→ DispatcherServlet → 控制器 ×19；收尾有 `@Transactional` 和 `CustomizeExceptionHandler` 的 3 个 `@ExceptionHandler`。
    - petclinic：`LocaleChangeInterceptor`。
    - 微服务网关：CircuitBreaker → Retry → 各路由过滤器（读 yml）。
  - **Go**：
    - gin：`gin.Default()` 自带的 Logger / Recovery，以及只作用于 `/api` 组的 `AuthMiddleware`；Note 写清 `Use` 只对之后注册的路由生效。
    - microservices-demo 前端：手工包装链按执行顺序 otelhttp → ensureSessionID → logHandler，`defer` 进收尾。
  - **Python**：
    - FastAPI：CORSMiddleware；依赖链 get_db（22 个路由）、get_current_user（17 个）、get_current_active_superuser（6 个，与源码里 6 处 `Depends` 一致）；yield 依赖的收尾。
    - Django：MIDDLEWARE 8 项按列表顺序；DRF 的 JWTAuthentication；EXCEPTION_HANDLER 进收尾"出错时"。
  - **Node**：
    - Express：cors / bodyParser / 路由挂载树 / 四参数错误中间件。
    - NestJS：3 个模块的 `AuthMiddleware`，带 forRoutes 摘要。
    - 纯 gRPC 服务只给说明。
  - **前端**：
    - fastapi 模板（React + TanStack）：挂载前 `client.setConfig` → StrictMode → ThemeProvider → QueryClientProvider → `/_layout` 的登录守卫 → 页面 ×8 → 生成的客户端（axios）→ 收尾 `QueryCache.onError`（401 / 403 跳登录）。
    - Next.js App Router：middleware、layout 链、error.tsx。
- **请求流程里的管道**（`MessagePipeline.cs`；服务的入口流程、任意方法时序、CLI `arch --flow` / `query --flow` 都走它）：
  - 每个 dispatch:M 前面插入这条消息生效的行为，按注册顺序（取自框架流程）。泛型约束和 Handle 开头的 `if (request is not IX) return await next()` 守卫（含代码块写法）不满足就跳过，原因写进标题下的 Note。
  - 行为里 `IValidator<TRequest>` / `GetService(typeof(IRequestEnricher<TRequest>))` 一类依赖，按消息找声明为 `: AbstractValidator<M>` / `: IRequestEnricher<M>` 的类，挂在对应调用下面。
  - 索引里验证器的继承边是 `AbstractValidator<T>`，类型实参丢了，必须读声明头文本。
  - 事件 Handler 前面不插。
  - ccs-backend `POST /api/v5/audit-logs/client-export`：
    - 原来是"控制器 → Handler"两步，行为只在标题里按字母序列一行；
    - 现在是 ValidationBehavior（`IValidator.ValidateAsync` 下挂 `LogClientExportCommandValidator`）→ Permission → Audit → ComparePreviewCaching → Handler；
    - 标题下写"管道里跳过：ApprovalBehavior（LogClientExportCommand 没实现 IApprovable）"，对照源码：该命令只实现 ICommand / IRequiresPermission / IAuditable。
  - `PATCH /api/v5/mt4/accounts/patch`：Approval 生效，`UpdateMt4AccountEnricher`（通过 GetService 反射拿到）挂在 ApprovalBehavior 下。
- **顺带修掉**：
  - SPA 页面入口：TanStack 文件路由、React Router 的 JSX（按嵌套拼路径）和对象写法，见 `EntryDiscovery.Spa.cs`。fastapi 模板前端从 0 个页面变成 8 个，`__root` / `_layout` 不算。
    - 写的时候私有方法和 Go 的 `JoinRoute(params string[])` 重名，抢走了 Java / Node 的两参数调用，入口测试红了一片；已加 Spa 前缀。
  - JSX 布局路由关闭时会把前面的同级页面一起作废；已改成每层只作废自己那一条。
  - NestJS 示例没有 Dockerfile，不被当成容器；改成依赖服务端框架、且 package.json 有 start 脚本也算 node-service。
  - 入口和容器在每次建索引后都会重算，不升索引版本。
- **样本**：用户同意后克隆了 node-express-realworld、nestjs-realworld、spring-boot-realworld 三个公开仓库到 `D:\github\reference`，只解析不执行。
- **测试**：
  - .NET 922 个（Server 362 / Core 175 / TreeSitter 262 / Roslyn 98 / Cli 25），vitest 392 个。
  - 新增：Spring 11（14 个用例）、Go 9、Python 11、Node 9、前端 8、MessagePipeline 3、SPA 入口 2（6 个用例）、Node 容器正反例。
  - 关键规则逐条还原都会变红：各代理共 80 条（Go 11、Python 12、前端 9、Spring 14、Node 34），主线 MessagePipeline 4 条、SPA 2 条、start 脚本 1 条。
- **回归**：`scripts/regression.ps1` 17 个场景 55 项，全部通过。
  - 新增：各语言框架流程 10 项；场景 1 请求流程带管道；场景 8 SPA 页面入口与前端框架流程；场景 15–17 三个新样本。
- **已知**：
  - Go / Node 的路由级中间件（`r.GET(path, mw, h)`、`router.get('/x', auth, h)`）只在 Note 里计数。
  - Django 的 `process_view` 画在各自中间件下，实际是全部中间件跑完才统一调用。
  - FastAPI 只认创建 app 的那个文件里的注册。
  - Next.js Pages Router 规则对任何 `pages/` 目录都生效，SPA 的 `src/pages/X.tsx` 会多出按文件名猜的页面（原有行为，没改）。
  - 请求流程里管道行的列取 Handler 所在那一列。
  - `D:\github\reference` 根目录有 6 个 10-15 留下的 0 字节 .cs 文件（EntryDiscovery.cs 等），没动。

## 调用图改成时序 + 「管道」改成「框架流程」 — 2026-10-16
- 起因：
  - 用户的原话是："调用图……没有使用它的欲望"，以及"调用大纲看起来还可以，为什么调用图就这么拉跨"。
  - 取证结果：旧网状图默认每层只挑 2 个分支，在抽样端点里有 65% 把业务调用藏掉了。
  - 原因有三条：调用方的扣分；`new` 一个 DTO 和真正的调用同分；默认画出类型关系。
  - 用户选了方案 A。
- **调用图（方案 A）**：
  - 新增 `GET /api/symbols/{id}/flow`：任意方法都能出时序图，和入口流程共用 `seqDiagram.ts`。
  - 子节点按分数挑，但按源码位置排。接口只有一个实现时合成一步。
  - 面板加了「时序 | 网状」切换。网状图的排序也改了：分派 +100、调用 +30、构造 −20、继承 / 实现 −25，去掉了调用方扣分；默认只画 calls 和 dispatches。
  - 覆盖率指标：`arch --flow-coverage` 和 `scripts/flow-metric.py`。在 ccs-backend 上：
    - 以 Handler 为根抽样 30 个，前 2 层业务调用全部命中（100%，旧网状图约 35%），平均召回 97.2%；
    - 以下一层方法为根抽样 21 个，100%。
- **框架流程**（替换原来的「管道」统计格子）：
  - 用户的定义："从框架层面展示调用流程和从实际的请求调用时不一样"，范围是"从 HTTP 进来到收尾"。
  - 新增 `GET /api/arch/framework?container=`、CLI `arch --framework`，页面是项目地图里的「容器 | 框架流程 | 命名空间」。
  - 各阶段的取法：
    - 中间件：`app.UseXxx` 的书写顺序；
    - 控制器：占位行，点开是清单；
    - 分派器：conduit 目标，自研分派器会展开实现；
    - 行为链：按 DI / `AddOpenBehavior` 的注册顺序，`next()` 前是前置、后是后置；
    - Handler：占位行，按模块列清单；
    - 收尾：`next()` 之后的调用、工作单元提交、事件发布。
  - 非 Web 容器把控制器占位换成"入口（常驻服务 / 任务 / 消息消费）"。
  - 旧的 `PipelineBuilder` 统计、`/api/pipeline` 和 `PipelineTests` 已删，只留下共用的 `IsTest` 和 `CommonPrefix`。
  - 第一屏不再恢复"上次点的视图"，按原规则走：只有一个容器时直接进入口流程，否则显示容器图。
- **整合时发现并修掉**：
  - eShop 的 `IdentifiedCommandHandler<T, R>` 有 8 个子类。它们自己不写 `Handle`，都落到基类方法上，Handler 清单里出现 8 行一模一样的"IdentifiedCommand"，展开 key 也撞了。
    - 现在从子类声明里读基类的泛型实参代进去，显示成 `IdentifiedCommand<CancelOrderCommand, bool>`；读不到时显示"消息名（子类名）"。
    - 补了测试，还原实现后确认会变红。
  - `extension/` 下一个 296 MB 的 `.7z` 被打进了 VSIX（357 MB）。`.vscodeignore` 加了 `*.vsix / *.7z / *.zip`，VSIX 回到 62 MB。
- **实测**：
  - eShop Ordering.API：控制器 8 → 分派器 1（MediatR `ISender.Send`）→ 行为链 3（Logging → Validator → Transaction）→ Handler 28 → 收尾 4，收尾里有 `CommitTransactionAsync` 和 `PublishEventsThroughEventBusAsync`。浏览器里用真实服务端数据确认了占位展开和 Handler 清单。
  - ccs-backend：中间件 → 控制器 → 自研分派器 → 行为链 5 个（顺序已知）→ Handler → 收尾。
- **测试**：.NET 862 个（Server 302 / Core 175 / TreeSitter 262 / Roslyn 98 / Cli 25），vitest 392 个。
- **回归**：`scripts/regression.ps1` 共 14 个场景 43 项，全部通过。新增的检查有：两项覆盖率、ccs 框架流程识别出分派器与行为链、eShop 三个行为按注册顺序且收尾有提交、时序图里接口与唯一实现合成一步。
- **交付**：只打了 `extension/codeatlas-win32-x64-0.1.0.vsix`（62 MB）。
- **已知**：框架流程只认 .NET 容器，其他语言只给一句说明。

## 影响范围 + 模块关系 + 快速跳转 — 2026-10-16
- 起因：用户要的四个视角里，"一步步做了什么"已经用调用大纲做完，这次把另外三个也做了。三条要求依次是："继续把影响范围的反向树也做了"、"把模块关系那个视图也一起做了"、"快速跳转那个也一起做了"。
- 主线先定数据格式（`ImpactDto`、`RelationsDto`、`FileLensDto`），然后六个代理并行：每个视角一个服务端、一个扩展。共享文件约定只加接线、逻辑放新文件。主线最后负责整合：
  - 跨服务调用方 Kind=hop；
  - 面板接线：`showRelations`、聚焦后 `chain:"in"`、`open` 消息带 pos；
  - 编辑器右键与侧边栏按钮"在模块关系中查看"；
  - 使用指南第 12 节。
- **影响范围**（侧边栏视图 + `GET /api/symbols/{id}/impact` + `query --impact`）：
  - 直接调用方分 call / impl / dispatch / new / hop 五种。hop 是别的容器经路由匹配调到这里，比如前端 fetch。
  - 根请求附带往上能走到的全部入口（BFS，深度 15、节点 5000 封顶，0.5 以下的调用边不走）。
  - 分派边例外：只用 tree-sitter 时分派边固定 0.4，按原规则 Handler 往上到不了 HTTP 入口。这是 Linux 复测时发现的，已改成分派边照走，并补了测试。
- **模块关系**（面板第二种模式 + `GET /api/symbols/{id}/relations` + `query --relations`）：
  - 以类为单位汇总调用、构造、分派、继承、实现、引用，按组件分框。
  - 组件按依赖顺序从左到右，排序复用 DSM 抽出来的 `DsmBuilder.DependencyOrder`。编译期依赖定不了先后时，用运行时去向补。
  - 反向依赖标红；深度 2、40 个节点封顶。
- **快速跳转**：
  - 方法上方显示 CodeLens"被 N 处调用 · 调用 M 个 · 影响范围"，数据来自 `GET /api/files/lens`，一次给整个文件。
  - 三个命令：转到调用方 / 被调方 / 受影响的入口，快捷键 `Ctrl+Alt+H / J / R`（macOS `Ctrl+Cmd+…`）。
    - 原想用 `Ctrl+Alt+↑/↓`，它和多光标冲突；`Shift+Alt+H` 被 VS Code 自带的调用层次占了；`Ctrl+Alt+E` 在 AltGr 布局下会打出 €。
  - 跳转改用 `showTextDocument({ selection })`，能进导航历史，`Alt+←` 可以退回。
  - 面板调用图加了"跟随光标"开关。
- **三个视角的计数一致性**：CodeLens 的调用方数和影响范围、被调方数和调用大纲，都复用同一套规则代码。在 6 个产品文件上逐个核对，没有不一致。
- **测试**：.NET 848 个（Server 290 / Core 175 / TreeSitter 262 / Roslyn 98 / Cli 23），vitest 338 个。各代理都把关键规则还原过，确认测试会变红。
- **实测**（eShop）：
  - 影响范围：`Order.AddOrderItem` 开 Roslyn 时找到 5 个入口，`POST /api/orders` 在深度 3；只用 tree-sitter 时在深度 2。
  - 模块关系：`CreateOrderCommandHandler` 的组件顺序是 Ordering.Domain → Ordering.Infrastructure → Ordering.API → 外部。浏览器里用真实服务端数据确认了分框、反向依赖和外部框。
  - Linux 上大纲、影响范围、模块关系三个命令的输出与 Windows 逐行一致（57 行，0 差异）。
- **性能**（大库 10.6 万符号、88 万边）：

  | 请求 | 耗时 |
  |---|---|
  | 影响范围 entries=true | 中位 5 ms，最慢 140–170 ms（热缓存）；冷缓存的第一次出现过 290 ms |
  | 模块关系 depth=2 | 最慢 128 ms |
  | CodeLens，200 个方法的文件 | 约 17 ms |
- **回归**：场景 4 新增"影响范围追到 POST /api/orders""模块关系组件按依赖序"两项，共 38 项全部通过。场景 4 另外用 `-NoRoslyn` 跑了一遍，5 项全部通过。
- **已知**：
  - 模块关系图里线多时线上的标签会压住组件框的标题——已修（同日）：标题改成单独的节点，骑在框的上边线上，带不透明底色，层级在所有线与线上文字之上；框本身不再画字。
  - 只用 tree-sitter 时，模块关系里会多出按名字猜错的组件（eShop 里多了 Basket.API）。
  - 抽象类没法区分：签名里没有 abstract 修饰符。
  - Blazor 的 `.razor` 文件不在索引里，WebApp 的页面入口追不到。
- **只能在真实 VS Code 里验证的**：
  - CodeLens 的位置，以及编辑后隐藏、保存后恢复；
  - 三个快捷键在 macOS 上的实际效果，`Alt+←` 退回；
  - 两个视图共用光标跟随的手感；
  - 面板"跟随光标"与模块关系的跳转。

## 调用大纲（侧边栏）— 2026-10-16
- **起因**：用户说"复杂一点的项目，调用图不太直观"。
- **追问后的结论**：
  - 四个用途都要：一步步做了什么、改它会影响谁、快速跳转、模块关系。最想先解决的是"这个方法一步步做了什么"。
  - 只列调用，按源码顺序，行尾标"循环内 / 条件内"。
  - 做成 VS Code 原生的侧边栏树视图，跟随光标。
  - 用户要求不写设计稿、直接写代码、拆成几个任务并行。
- **现状问题**：在 eShop 的 `OrdersApi.CreateOrderAsync` 上看：
  - 方法和类型（继承链）混在一张图里；
  - 看不出先后顺序；
  - 节点名字不带类名；
  - "N 个框架调用"之类的占位节点多；
  - 布局交叉。
- **T0（主线）**：
  - edges 新增 `pos`（调用名的 UTF-16 字符偏移）和 `ctx`（loop / cond / try / catch / callback）。
  - 用字符偏移而不是列号，是因为多行链式调用的行号都记在链开头那一行，按（行，列）排会错。
  - 解析器统一给每个引用产生的边盖上这两项，各语言分支不用各自处理。
  - 适配器基类加了 `ContextOf` / `IsContextBoundary` / `ReferencePos` 三个钩子。
  - 定好 `OutlineDto` 数据格式。索引版本升到 10。
- **并行的五个任务**：
  - T1：tree-sitter C# / TS / JS 的结构判断，以及所有语言共用的字符偏移。实测 TreeSitter.DotNet 的 `StartIndex` 本来就是 UTF-16 偏移，不用换算。
  - T2：Go / Python / Java 的结构判断。
  - T3：Roslyn `DocumentWalker` 给出偏移和结构。
  - T4：`OutlineBuilder`、`GET /api/symbols/{id}/outline`、命令行 `query --outline`。
  - T5：侧边栏「调用大纲」树视图，带跟随 / 固定、框架调用开关、递归检测。
- **共同约定**：
  - 结构只算"体"：条件头、循环头里的调用不算。
  - 取最内层的那一个结构。
  - 本身是独立符号的嵌套函数当边界，包括 C# 局部函数、TS 的 `const f = () =>`、Python 嵌套 def；它们体内的调用归它们自己，不标"回调"。
  - `&&` / `||` 右侧不算条件。
- **测试**：.NET 794 个（Server 238 / Core 175 / TreeSitter 262 / Roslyn 98 / Cli 21），vitest 231 个。每个代理都把关键规则还原过，确认测试会变红。
- **实测**（eShop `CreateOrderCommandHandler.Handle`）：两种索引都按源码顺序列出 8 步，接口调用下挂唯一实现，`Order.AddOrderItem` 标 `[loop]`。
  - 开 Roslyn：第 1 步是 `new OrderStartedIntegrationEvent`。
  - 只用 tree-sitter：第 1 步被启发式当成分派到 `OrderStartedIntegrationEventHandler`。
  - Linux 上的结果与 Windows 逐行一致。
- **性能**：ccs-backend 真实库（10.6 万符号、88 万边），单次大纲请求中位 0.29 ms，95 分位 8 ms。ccs-ui 的索引耗时没有可测出的变化。
- **回归**：场景 4 新增"调用大纲按源码顺序、标出循环内"一项。
- **只能在真实 VS Code 里验证的**（侧边栏是原生控件，没法截图）：
  - 活动栏图标的观感；
  - 框架调用的文字会不会变灰；
  - 单击可展开的条目会不会顺带展开；
  - 欢迎内容会不会按状态切换；
  - 固定 / 跟随按钮会不会正确切换；
  - 跟随的手感；
  - 跳转是否落在调用名上；
  - 重启窗口后视图的状态；
  - 多根工作区里的跳转。

## 不再要求 .NET + 跨平台（Windows / Linux / macOS）— 2026-10-15（深夜）
- 起因：用户问"假如用户只需要前端的代码地图，为什么还需要安装 .NET 10"，"其它语言的地图强制 .NET 10 肯定不合理"。
- 做法：
  - **服务自带运行时发布**：`publish.ps1 -Rid …` 用 `--self-contained true`，只保留用到的 7 个 tree-sitter 语法库。原来带 30 个，用不上的 23 个约 58 MB。
  - **Roslyn worker 单独放**：放到 `roslyn/` 子目录，仍依赖本机 .NET 10。和服务同目录时会误用服务自带的 hostfxr。
  - **跑 Roslyn 前先探测**：用 `codeatlas-roslyn --probe` 检查本机环境，缺运行时、缺 SDK 时给一句原因，C# 退回 tree-sitter，扩展只提示一次。仓库 `global.json` 要求的 SDK 本机没有时，也给一句原因，不打堆栈。
  - **Linux / macOS 补可执行位**：在 Windows 上打的 VSIX 里没有 Unix 权限。扩展启动服务前、服务启动 worker 前都会自动补上。
  - **每个平台一个 VSIX**：`package-all.ps1` 打出 win32-x64、linux-x64、linux-arm64、darwin-x64、darwin-arm64 五个包，用 `vsce --target`。
- 跨平台排查（代理）：
  - Roslyn 文档去重、仓库根判断：原来写死忽略大小写，改为按平台比较。
  - 清单遍历：原来会跟进符号链接，Linux 上指回上级目录会无限递归，改为跳过。
  - Dockerfile / appsettings：在 Linux 上找文件时不区分大小写，并按名字排序，与 NTFS 一致。
  - `backendDetect`：同级目录判断按平台处理大小写。
- 主线：`.SLN` 不分大小写也能找到，符号链接目录跳过。
- **顺带发现、与平台无关的 bug**：在 Linux 上测 Roslyn 时发现，Bing 跑完 Roslyn 后 HTTP 入口从 121 个变成 0 个，Windows 上一样。上一轮回归的场景 14 只查了"不崩"，没查入口数，现已加上检查。查下来是三个原因叠在一起：
  1. **继承边被删**：包没还原时，Roslyn 认不出 `ControllerBase`，不给 inherits 边，写回时又删了 tree-sitter 的继承边。修法：Roslyn 对某个类型的某种边（inherits / implements）一条都没给时，保留启发式边。修后 0 → 89。
  2. **继承链只找 4 层**：Roslyn 会把只差泛型参数个数的同名基类分开，链就变深了（`TreesControllerBase` 到 `ControllerBase` 要走 5 层以上）。修法：改成最多找 8 层，并防止"自己继承自己"造成的环。
  3. **泛型类成员的短名取错**：`Ns.Repo<T>.Find` 原来从第一个 `<` 截断，得到的是类名 `Repo`，入口识别就把这个方法当成构造函数滤掉了。修法：去掉所有成对的 `<…>`。同样的写法还在管道视图短名、命名空间推断里，一并改了。时序图里泛型类成员的标签以前也显示成类名。
  - 修后：Bing 开 Roslyn 有 123 个 HTTP 入口，只用 tree-sitter 时是 121 个。每条修复都补了单测，并确认还原后测试变红。
  - 另修：入口识别里 `AttributesAbove` 在文件比索引短时会下标越界（索引之后文件被改短就会触发）。

### 实测：没装 .NET 的 Ubuntu 24.04（WSL，`wsl --import` 官方 cloud-images 镜像）
- 部署方式模拟 VSIX：全部文件去掉可执行位，只给主程序加回；仓库复制进 Linux 自己的文件系统（区分大小写）。
- 9 个仓库的 tree-sitter 结果与 Windows **逐项相同**：

| 仓库 | 文件 / 符号 / 边 | 容器 / 外部 / 容器边 | 入口 / 跨服务匹配 |
|---|---|---|---|
| ccs-ui | 1577 / 27343 / 129635 | 1 / 3 / 3 | 60 / 0 |
| ccs-backend | 7027 / 101838 / 770643 | 5 / 16 / 32 | 1880 / 412 |
| microservices-demo | 59 / 3760 / 10129 | 12 / 9 / 34 | 44 / 20 |
| petclinic-microservices | 84 / 915 / 1779 | 8 / 5 / 38 | 25 / 15 |
| fastapi | 152 / 1880 / 5791 | 2 / 4 / 4 | 24 / 23 |
| gin | 19 / 448 / 2484 | 1 / 1 / 1 | 21 / 0 |
| django | 44 / 293 / 651 | 1 / 1 / 1 | 18 / 0 |
| eShop | 567 / 5614 / 15111 | 12 / 8 / 38 | 111 / 28 |
| Bing | 1976 / 19511 / 88120 | 5 / 1 / 2 | 132 / 0 |

- 含 `.sln` 的仓库给出提示："没有找到 .NET 10 运行时，C# 精确语义（Roslyn）需要它（装 .NET 10 SDK 即可）。C# 改用 tree-sitter 解析（精度略低），其他语言不受影响。"
- worker 的权限从 `-rw-r--r--` 被自动补成 `-rwxr-xr-x`。
- `serve` 按扩展的参数启动：打出 `{"port":N}`，带令牌请求 `/api/health` 正常。
- 看门狗：父进程退出后 12 秒内服务自动退出。
- 大小写和符号链接：
  - 只差大小写的 `Program.cs` / `program.cs` 都进了索引，两者之间的调用也连上了；
  - 全大写的 `App.SLN` 能找到；
  - 指回上级目录的符号链接没有挂死。
- 之后在同一发行版里装了 Ubuntu 源的 `dotnet-sdk-10.0`（10.0.112）：
  - Bing 的 Roslyn 能跑：1968 个文件写回，边 15985 条，错误 44 个。
  - eShop 的 `global.json` 锁定 SDK 10.0.302，本机 10.0.112 不兼容，Roslyn 跳过，原因给成一句话，其余结果照常。

### 最终复测（三个修复之后）
- **Windows**：`scripts/regression.ps1` 14 个场景 **35 项全部通过**。场景 14 新增的"控制器入口不因 Roslyn 丢失"：123 个。
- **单测**：.NET 575 个（Core 172 / Server 219 / TreeSitter 130 / Cli 20 / Roslyn 34），vitest 187 个。
- **Linux**（重新部署，去掉可执行位）：9 个仓库的 tree-sitter 结果与 Windows 逐行相同。
  - ccs-backend 在 Windows 上已经从 7027 个文件变成 7043 个（仓库本身有更新），所以重新复制后再对照。
  - 开 Roslyn 时 Bing 有 123 个 HTTP 入口，与 Windows 一致；worker 的可执行位被自动补上。
- **已知小问题**：在旧库上全量重建，会残留不再被引用的外部符号（ccs-backend 多了 16 个），不影响地图。两个平台都是这样。

### 没有实测的
- macOS 两个包是在 Windows 上交叉发布的，没在 Mac 上跑过。Apple 芯片要求二进制签名：.NET SDK 会给 apphost 做 ad-hoc 签名，NuGet 带的 tree-sitter / SQLite 原生库有没有签名没核实。
- linux-arm64 没实测。Alpine 等 musl 发行版没有打包。
- 没装 .NET 的 **Windows** 机器没实测。runtimeconfig 已确认是自带运行时（`includedFrameworks`），Linux 上的实测走的是同一套发布方式。

## Java 支持 + 全部语言的缺口 + 控制台 / 旧版 .NET — 2026-10-15（夜）
- 起因：用户问"C# 只是控制台项目，项目地图还有用吗？3.1 / 5 / 6 … 不同版本有效吗"，随后"有缺口的一起修了吧，所有已实现语言"。
- 两波共九个代理并行：
  - Java 三件套：索引（`JavaAdapter` + 按 package 限定名解析，`via=impl`）；架构层（Maven / Gradle 容器、Spring 依赖、Eureka / Config 服务端、网关路由、Feign / RestTemplate / WebClient 出口、Java gRPC）；入口与地图（Spring MVC / WebFlux / JAX-RS / gRPC / `@Scheduled` / 监听器 / main，网关穿透匹配，DSM 按 package）。
  - TS/JS：CommonJS `require` / `exports`、`memo` / `forwardRef`、`onX={handler}` 记 `via=event`、`this.字段` 按类型、NestJS 构造注入。
  - Go/Python：字段、链式、range、泛型、包名 ≠ 目录名、函数值；实例属性、局部变量、`import *`、pb2，外部调用带包名。
  - C# tree-sitter：按接收者类型解析，重载按实参个数挑，`nameof` 不算调用，链式行号与 Roslyn 一致。
  - CLI 框架入口（新 kind「命令行命令」）、Node 后端入口、纯类库仓库首屏改为 DSM。
- 主线：
  - `RoslynApplier` 增加兜底：Roslyn 某行一条调用都没绑上时，保留 ≥0.7 的启发式边，置信度封顶 0.7。
  - 时序图排序：下一层才续接的调用（`chooseAd → getAd → gRPC`）排在普通调用前。
  - 详情条的置信度文字按现行分档改写，显示两位小数。
  - `.java` 纳入保存时增量。
  - 索引版本升到 9。
  - 回归脚本扩到 14 个场景。
- 测试：.NET 552（Server 213 / Core 165 / TreeSitter 130 / Cli 20 / Roslyn 24），vitest 185，`tsc --noEmit` 0 错误。每个代理挑了修复，把实现还原后跑过测试，确认会变红。TS/JS 代理当时没能做这一步，由主线补做：还原构造注入解析和 `require` 绑定后，3 个测试变红。

### 版本与项目形态（回答用户的问题，实测）
| 项目 | 形态 | 结果 |
|---|---|---|
| CodeAtlas 自身 | net10 控制台（手写 switch 分派） | `src/CodeAtlas.Cli` 容器有 `Program.Main` 入口，流程走到各子命令的 `Run`。只用 tree-sitter 时，流程从修前停在 Main 一个节点，变为 38 个节点 |
| Bing.NetCore | netcoreapp3.1 / netstandard2.x，110 个项目，同目录多 csproj，包未还原 | 修前容器扫描崩溃（重复键），修后 5 个容器。Roslyn 有 206 个错误，绑不上的调用保留启发式边 2097 条 |
| Audit.NET | net462 → net10 多目标 | 4 个容器、7 个入口；Roslyn 错误 347，其余照常写回 |
| WebTrader.Server.MT5 迁移备份 | .NET Framework 4.7.2 旧式 csproj + packages.config | 修前 27 个垃圾"原生 SDK"节点（System.* 与强名称全串），修后 3 个真实外部系统 |

C# tree-sitter 精度，以 Roslyn 为答案，按（文件, 行）对齐 calls 边：

| 仓库 | 修前 | 修后 |
|---|---|---|
| CodeAtlas | 67.2% | 99.4% |
| ccs-backend | 34.6% | 97.8% |
| Bing | 35.8% | 94.9% |

仓库内边数下降，主要是去掉了 0.3 的多候选误连；与 Roslyn 一致的边数上升。

### 回归（`scripts/regression.ps1`，Roslyn auto，34 项全部通过）
| 场景 | 检查 | 实测 |
|---|---|---|
| 1 ccs-backend | 索引 / 批量建组 / mt4proxy 入口 / 组管理页 / 续接进后端 | 7027 文件 186s；73 节点到 SendAndReadDataAsync；76 个入口；343 节点；9 个 hop 到 Mt5GroupController |
| 2 mt4proxy | 控制器 → P23 | 54 节点 |
| 3 mt5-event-publisher | Job / Kafka | 3 个 job，常驻服务流程到 Kafka 14 处 |
| 4 eShop | 路由匹配 / 分派 | 28；只分派到 CreateOrderIdentifiedCommandHandler |
| 5 ccs-ui 单独 | 出口 / 组管理页 / 接口行 | 389；189 节点；GET /api/v5/mt5/groups |
| 6 ccs-ui + 后端 | 5 个容器 / 组管理页 / 续接 | 5；343 节点；9 个 hop |
| 7 Online Boutique | 容器 / gRPC / 商品目录续接 / **Java 广告服务续接** / **Node gRPC 入口** | 12 个（含 adservice）；0.9 的 grpc 边 14 条；54 节点；AdServiceImpl 2 个节点；PaymentService/Charge |
| 8 FastAPI | 路由匹配 / 前缀 | 23；23 个 `/api/v1/*` |
| 9 gin | 入口 / 流程 | 20；38 节点 |
| 10 Django | 入口 / 流程 | 17；7 节点 |
| 11 spring-petclinic | Spring MVC 入口 / 控制器 → Repository | 17；`GET /owners/{ownerId}` 到 `findById` |
| 12 petclinic-microservices | 容器与匹配 / 网关续接 | 8 个容器、route-matches 15；`/api/gateway/owners/{ownerId}` 22 节点，2 个 hop 到 OwnerResource |
| 13 CodeAtlas 控制台 | Main → 子命令 | 到 `IndexCommand.Run` |
| 14 Bing（3.1） | 容器扫描 / Roslyn 兜底 | 5 个容器；保留 2097 条 |

第一次跑有 2 项不通过，都是新加的检查：
- "首页续接进 Java 广告服务"：查明是时序图排序问题。`chooseAd` 自己不续接，被扇出上限 6 折进了"还有 8 个"。改了排序并补了单测，还原排序后单测变红。
- "Node gRPC 入口"：是检查脚本的问题。PowerShell 5.1 的 `if` 表达式会把单元素数组拆开，单个 PSCustomObject 没有 `.Count`。工具输出本身正确。

### 已知
- Java：
  - 不支持 Gradle 版本目录、Kotlin、Lombok 生成的方法；重载只按个数挑。
  - 接口上写映射注解的控制器不认。
  - 静态前端没有单独成容器，所以 petclinic 的边是"网关 → 服务"一段。
- TS/JS：不支持改名导出、动态 `import()`、`withRouter` / `connect` 这类 HOC；`props.onX` 不标 event。
- Go：只推第一个返回值，不推 type switch 变量。
- Python：不推别的文件里赋值的实例属性和 `@property` 返回类型，不支持 `.pyi`。
- C#：元组解构、lambda 参数、泛型返回 `T`、`global using` 别名推不出来。外部调用名改成了 `external:<类型>.方法`，时序图里外部参与者按类型分列。
- CLI 框架只有 System.CommandLine（scip-dotnet）和 yargs（appmap-js）做过真实项目验证，其余是夹具验证。
- 时序图节点数：回归里出现了 343 / 462 节点的流程，超过默认封顶 300。原因未核实，推测是脚本遍历把"还有 N 个"、框架调用组这类占位节点也算了进去，而封顶只算展开的符号。

## Go 与 Python 支持 — 2026-10-15（晚）
- 四个代理并行：Go 索引（`GoAdapter` + go.mod / import 解析，同包跨文件、别名 / 点导入、局部变量与 `NewXxx()` 返回值的类型）、Python 索引（`PythonAdapter` + 包根推断、相对导入、`self` 沿基类、类属性别名 `serializer_class = X`）、架构层（Go 模块 / Python 项目 / Node 服务成为容器；go.mod、requirements / pyproject 依赖映射外部系统；Go / Python 的 HTTP 与 gRPC 出口；**跨语言 gRPC 按服务名连线**——客户端桩 `NewXxxClient` / `XxxStub` / `XxxClient` 对服务端注册 `RegisterXxxServer` / `add_XxxServicer_to_server` / `MapGrpcService<T>`）、入口与地图（net/http · gorilla/mux · gin · echo · chi · gRPC 服务端 · main；FastAPI 含 `APIRouter` / `include_router` 前缀链与常量 · Flask · Django urls / DRF router · Celery · `__main__`；gRPC 调用按 `Service/Method` 续接进对端服务；DSM 按包目录）。主线：统一语言注册表 `LanguageAdapters.Create()`、保存时索引加 `.go` / `.py`、页面新容器类型中文名（加了覆盖测试）、文档、回归新增 4 个场景。
- 验收项目（GitHub 公开示例，浅克隆到 `D:\github\reference`，只解析不运行）：GoogleCloudPlatform/microservices-demo（Online Boutique，Go + Python + C# + Node，gRPC）、fastapi/full-stack-fastapi-template（FastAPI + React）、gothinkster/golang-gin-realworld-example-app、gothinkster/django-realworld-example-app。
- 测试：.NET 421（Core 126 / TreeSitter 81 / Server 170 / Cli 20 / Roslyn 24），vitest 180。

| 场景 | 检查 | 结果 | 实测 |
|---|---|---|---|
| 7 Online Boutique | 多语言容器 | 通过 | 11 个（Go 4、Python 4、Node 2、C# 1；Java 的 adservice 不支持），索引 9s |
| 7 | gRPC 按服务名连线 | 通过 | 0.9 的 grpc 边 13 条（frontend → AdService 因 Java 不支持降为 0.5） |
| 7 | Go 首页 → gRPC 续接 | 通过 | `/cart/checkout`：`placeOrderHandler`（Go）→ `RecommendationService/ListRecommendations`（Python）→ `ProductCatalogService/ListProducts`（Go）→ `loadCatalogFromAlloyDB` |
| 8 FastAPI 模板 | 前端 → 后端路由匹配 | 通过 | 23，容器边 0.9 |
| 8 | 入口带前缀 | 通过 | 23 个全部 `/api/v1/*`（`settings.API_V1_STR` 常量已解析） |
| 9 gin RealWorld | 路由入口 | 通过 | 20（跨函数传 group 的前缀也拼上了，如 `/api/articles/:slug/comments/:id`） |
| 9 | handler 流程 | 通过 | `POST /api/articles` 38 节点 |
| 10 Django RealWorld | urls / DRF 入口 | 通过 | 17 |
| 10 | 视图流程 | 通过 | `POST /api/articles` 7 节点（走到 `ArticleSerializer`） |

场景 1–6（.NET / 前端 / 多项目）同一构建全部通过，结果与上一轮一致。回归第一次跑时场景 10 报"没认出容器"，查明是检查脚本把 JSON 里的枚举数值 0 当成了字符串比较，工具本身输出正确；修脚本后重跑通过。

### 已知
- Go：不推断 struct 字段、方法返回值、range 变量的类型；函数值当参数（`router.POST("", h)`）不产生调用边（入口规则直接扫注册语句）；包名与目录名不一致且没写别名的 import 认不出。
- Python：`from x import *`、局部变量 / 实例属性类型、运行时生成的 protobuf 类不解析；Django ORM `Model.objects.*` 记为外部调用。
- 不支持 Java（adservice）；Node 服务有容器但没有符号，续接到它们时停在 hop。k8s / compose 清单里的端口与环境变量值没读。

## 前端第一屏 + 旧索引自动重建 — 2026-10-15（下午）
- 用户装上新 VSIX 后，单独打开 ccs-ui 项目地图"还是光秃秃"。查用户本机索引（`ccs-ui/.codeatlas/index.db`，9:30 旧插件建的）：组管理页流程仍是 `page.tsx → GroupsPage` 2 个节点。**根因是我的疏漏**：TS 解析规则变了却没升索引格式版本（仍是 7），新插件认为旧索引可用，只重算了容器图（出口 389），调用链没重建；而且"版本过旧自动重建"只在右键"在 CodeAtlas 中查看"时触发，直接开面板看地图的人永远用旧索引。
- 修：索引版本 7 → 8；服务启动时自己检查，旧版本就全量重建（`StartupRebuildTests`，去掉修复会红）。用用户那份旧索引的副本实测：服务起来自动重建 1577 个文件，`needsRebuild` 变 false。
- 同一问题用户第二次提出，按流程先确认目标行为：用户选"只有一个自己的容器时第一屏直接进入入口流程"、"检测到旁边的后端时提示一次是否一起索引"。实现：`firstScreen.ts`（唯一容器 → 自动进入口流程；默认入口挑接口调用最多的流程——第一版按路由最短选到了登录页、没有任何接口调用，改了）、`backendDetect.ts`（上级目录根下有 sln 就只提上级）、时序图新增 kind `http`（hook 里没对上后端的接口调用，灰蓝虚线、"接口"标记、标签贴调用方一端）。
- 浏览器实测（用户旧索引副本 → 自动重建后）：点"项目地图"直接进入入口流程，默认选中 `roles-management/role-groups/{id}`（90 节点、9 个接口调用：`POST /api/v5/roles/role-groups`、`DELETE …/${id}`、`GET /api/v5/roles` …）；组管理页 hook 下列出 `GET /api/v5/mt4/groups`、`GET /api/v5/mt5/groups` 等。
- 测试：.NET 327，vitest 179。回归 16 项全过（新增"hook 下显示接口调用"）。
- 已知：自动检测提示、写 `.vscode/settings.json` 后全量重建这条只做了单测与对真实目录的检测，没在真实 VS Code 里点过。

## 前端调用链 + 前端接口识别 + 多项目索引 + 交付回归 — 2026-10-15
- 起因：用户单独打开 ccs-ui，项目地图只有 1 个前端容器 + 4 个"API"占位框，组管理页的入口流程 2 个节点。用户问"单独打开任何项目都出问题还算合格的插件吗"。补测设计稿要求的四个项目单独打开：C# 后端都合格，前端不合格；5c 验收时漏测了 ccs-ui（当时它没有符号就当已知限制带过了）。
- 三个代理并行：
  - **前端调用链**：JSX 组件引用算 `calls` + `via = render`（只认首字母大写的标签，含 `<X.Y/>` 与作为 prop 传入的元素；npm 包组件记为 external `包.名字`）；TS / TSX / JS 调用按 import 绑定解析（相对路径、tsconfig `paths` / `baseUrl` / `extends`、`index.*`、桶文件最多 3 层），解析不到的不再按全仓同名猜；Next.js 页面入口改为默认导出的组件，App Router `route.ts` 按导出的方法各一个入口；修掉"tsx"与空名字标签。
  - **前端接口识别**：`TsConstantResolver` 解析 `API_ROUTES.X.Y` 嵌套对象、模板字符串与跨文件常量；认 `useApi()` / `createXxx()` 返回的客户端上的 `.get/.post/.put/.patch/.delete`（判据是工厂函数体用到 fetch / axios，`map.get` 不误判）；拼上 axios `baseURL`；从 Next API 路由里的转发代码自动认出代理前缀 `/api/proxy-gateway`（也可在 `architecture.yaml` 的 `proxies:` 声明），匹配后端路由前剥掉。
  - **多项目**：嵌套仓库（独立克隆与子模块）默认纳入，worktree 总是跳过；每个仓库的 `.sln` 各跑一次 Roslyn；`--project <路径>`（可重复）与 `.codeatlas/config.json` 的 `projects` 接入外部根，文件以相对主根的 `../…` 入库并按真实路径去重；扩展把多根工作区其余文件夹、设置 `codeatlas.projects` 传给服务，`codeatlas.includeNestedRepos` 控制嵌套仓库。
- 主线集成：时序图归属判断改用与容器规则同一判据（成员路径为空的主根容器不吞 `../` 文件）；**节点名额改为按子树分配**——合并前后端后，组管理页的 MT4 分支续接进后端把 300 个名额吃光、MT5 分支整支没了；改为兄弟分支平均分、前三层再把浅分支用剩的补给被截断的分支；置信度统一两位小数（0.95 不再显示成 1.0 冒充 Roslyn 精确）。新增 `scripts/regression.ps1`（六个场景 15 项检查）与 `scripts/pack-source.ps1`。
- 测试：.NET 323（Core 101 / TreeSitter 48 / Server 130 / Cli 20 / Roslyn 24），vitest 150。新增用例都先确认在旧实现上会红（名额分配两条、外部根归属一条用临时还原验证过）。

### ccs-ui 单独打开（tree-sitter）
| 项 | 改前 | 改后 |
|---|---|---|
| 识别出的后端接口调用 | 7 | 389（经代理 376；仍解析不出 38 处：参数透传、三元、动态下标等） |
| 组管理页流程节点 | 2（`tsx` → 空名字） | 181：`GroupsPage` → 渲染 `MT5GroupTable` / `MT4GroupTable` / … → `useMT5GroupByServerId` |
| 按名字瞎猜的 0.3 多候选边 | 8196 | 142 |

### 前端续接进后端（ccs-ui 与 ccs-backend 在同一个索引里）
`GroupsPage → MT4GroupTable（渲染）→ useMT4Group → HTTP /api/v5/mt4/groups → Mt4GroupController.ListGroups → ListMt4GroupsHandler.HandleAsync（分派）→ MT4GroupService.GetAllGroupAsync`；MT5 一支同样续接到 `Mt5GroupController`。两种打开方式都成立：打开 ccs-backend（嵌套仓库自动纳入），或打开 ccs-ui + `--project ccs-backend`。路由匹配 414 处。

### 交付回归（`scripts/regression.ps1`，Roslyn 开）
| 场景 | 检查 | 结果 | 实测 |
|---|---|---|---|
| 1 ccs-backend（含嵌套仓库） | 索引 | 通过 | 6785 文件，173s |
| 1 | MT5 批量建组流程 | 通过 | 73 节点，走到 `SendAndReadDataAsync` |
| 1 | 嵌套仓库 mt4proxy 有入口 | 通过 | http 76 |
| 1 | 组管理页 → 组件 → hook | 通过 | 305 节点 |
| 1 | 前端续接进后端控制器 | 通过 | hop 9 个，含 `Mt5GroupController` |
| 2 mt4proxy 单独 | 控制器 → Service → P23 | 通过 | 54 节点，13s |
| 3 mt5-event-publisher 单独 | Quartz Job 入口 | 通过 | 3 个，18s |
| 3 | 有入口流程走到 Kafka 生产 | 通过 | `StatusEventPublisher` 24 节点，Kafka 14 |
| 4 eShop | 跨服务路由匹配 | 通过 | 28，27s |
| 4 | 下单只分派到对应 Handler | 通过 | `CreateOrderIdentifiedCommandHandler` |
| 5 ccs-ui 单独 | 前端接口调用识别 | 通过 | 389，9s |
| 5 | 组管理页 → 组件 → hook | 通过 | 181 节点，根 `GroupsPage` |
| 6 ccs-ui + `--project ccs-backend` | 前后端五个容器 | 通过 | 5，165s |
| 6 | 组管理页 → 组件 → hook | 通过 | 305 节点 |
| 6 | 前端续接进后端控制器 | 通过 | hop 9 个，含 `Mt5GroupController` |

共 15 项，不通过 0 项。设计稿原写"mt5-event-publisher 一条 Job 流程以 Kafka 收尾"，核实后不成立：Jobs 目录里没有 Kafka，生产 Kafka 的是常驻服务 `StatusEventPublisher`（→ `KafkaService.PublishMessage` → Confluent），检查改成"有入口流程走到 Kafka 生产"。

### 已知
- 组件到事件处理函数（`onClick={handleX}`）这段还连不上；`api.get` 本身显示为外部调用，续接由路由匹配挂在 hook 上。
- 外部根的容器键带 `../` 前缀；索引不记住根，命令行 `arch` 要给同样的 `--project`（或用 config.json）。
- 只打包 win-x64 服务。

## AI 叙述：联网服务 + 本地保留 + 持久缓存 — 2026-10-14
- 用户反馈："本地 AI 太慢且经常失败，要支持联网 AI"，随后补充"不要把本地移除"、"做个缓存，对照 git 版本或别的判断过期，本地慢但能拿到结果"。
- **两种协议，本地仍是默认**：OpenAI 兼容（本地 llama-server、DeepSeek、通义、OpenAI 等，有 Key 时带 Bearer；只有回环端点才发 llama-server 专用的关思考开关；端点不认 `response_format` 时自动去掉重发）与 Anthropic Messages API（`x-api-key` + `anthropic-version`）。401 / 403 直接提示 Key 问题不重试；429 按 Retry-After 退避最多 3 次；并发可配（`--ai-concurrency`，本地 1）；`--ai-no-source` 不发入口源码。API Key 只走环境变量 `CODEATLAS_AI_API_KEY`，扩展里存 VS Code SecretStorage，`ToString` 与日志都打码。
- **扩展**：命令"选择 AI 服务"（本地 / DeepSeek / 通义 / OpenAI / Claude / 自定义）、"设置 / 清除 AI API Key"；设置 `provider / concurrency / timeoutSeconds / includeSource`；叙述卡显示当前服务、未设 Key 的提示与按钮。
- **缓存**：挪到索引库旁的 `narratives.db`（全量重建索引不清）；按稳定键存（流程 = 容器 | 入口方法全名），证据里的 `sym:` / `entry:` 存成 `sym@全名`、读出时换回当前 id。过期按两样判：相关文件内容哈希（索引的 files.hash，未提交的改动也算）与架构事实指纹（不含 git 计数这类每次提交都变的数字）；同时记下生成时的 git HEAD 给人看。过期的照样显示并标原因，不自动重算；换模型不算过期；失败不覆盖已有的成功结果；端点"连不上"的结论 60 秒后过期，下次打开会重试。新增 `arch --narrate-all [--flows N] [--force]` 夜里批量补齐缺失或过期的，可中断续跑。
- 测试：.NET 266（Core 75 / TreeSitter 31 / Server 120 / Cli 17 / Roslyn 23），vitest 142。新增的服务端用例覆盖三种请求形状、Key 错、限流、降级、并发、Key 不进 ToString、过期两种原因、重建后稳定键与证据换回、换模型不作废、失败不覆盖、连不上会过期。

### 真实数据（ccs-backend，本地 llama-server qwen3.6-27b）
| 步骤 | 结果 |
|---|---|
| 新代码走本地模型生成 `mt4proxy/MT4Proxy.Web` 容器叙述 | ready，174s，2.5 tok/s——本地路径没被改坏 |
| 全量重建索引（Roslyn，符号 id 全部重排） | 6 分钟 |
| 重建后再读同一条 | **1 秒，缓存命中，未过期** |
| 换个模型名再读 | 仍命中，显示由 qwen3.6-27b 生成 |

- 联网服务没有用真 Key 测过（我没有、也不该替用户填 Key）：请求形状、鉴权头、错误归类都由假端点测试钉住，首次接 DeepSeek / 通义 / Claude 时请看一眼叙述卡是否 ready。
- 预设里的联网模型名是默认值，可能随厂商更新而变，设置里可改。

## 里程碑 5d + 5e + 精度补丁：本地 AI 叙述、NetArchTest 规则叠加、泛型分派、三种入口规则 — 2026-10-14
- 用户要求"别下一步下一步了，多开几个 agent 一起做完"。四个代理并行（Roslyn 精度 + 入口规则、5d 服务端、5d 页面、5e 规则叠加）；中途模型额度用完，四个都中断在半路，换 Opus 起替身从磁盘半成品接着做完。主线做集成：DSM 构建传入规则、index / arch 打印 rules 行、页面 gRPC 标签、时序图节点显示"类型.方法"、超时默认值、自动排队策略。
- 测试：.NET 241（Core 75 / TreeSitter 31 / Server 102 / Cli 10 / Roslyn 23），vitest 98。

### 5d 本地 AI 叙述
- 服务端 `Server/Narration/*`：OpenAI 兼容 provider（llama-server `http://127.0.0.1:8080/v1`，`qwen3.6-27b`）、三种粒度的事实子图（容器 / 组件 / 流程，每条事实带 id）、宽松 JSON 解析 + 引用校验（引用不在输入 id 里 → `inferred`）+ 版式校验 + 重试一次、单线程队列（可停可续）、`narratives` 表缓存（哈希 = 模型名 + 事实文本，索引版本 7）。接口 `GET /api/arch/narrative`、`POST /api/arch/narrate`、`GET /api/arch/narrate/status`、`POST …/stop|resume`；CLI `arch --narrate <kind> <key> [--facts]`。
- 页面：右侧叙述卡（320px 可收起），容器单击 / DSM 行头单击 / 入口选中触发；证据标签可点（节点 → 容器图选中，入口 / 符号 → 调用图聚焦）；面包屑栏"版式"下拉，三组预设 `三列 | 拓扑分层`、`依赖序 | 字母序`、`首次出现 | 外部合并`；"按 AI 建议排版"只在预设里选。设置 `codeatlas.ai.enabled / endpoint / model`，改了重启服务。
- **实测逼出的两处裁定**：
  1. 用户机器上 qwen3.6-27b 只有 2.1–2.8 tok/s，单条 150–233s；设计稿的 120s 超时必然失败，默认改为 **300s**（`--ai-timeout` 可调）。
  2. 打开叙述卡会自动排队（设计稿 §3.1"单击 → 叙述卡"），但一条要两三分钟，随手点十个就是半小时；改成**自动排队只保留最近打开的一个**，还没开始的旧自动项撤掉，用户点"生成"的显式项不撤。测试 `AutoQueuedByGet_KeepsOnlyTheLatest_ExplicitEnqueueSurvives`（旧逻辑下队列是 4 项，断言 1 项，必红）。

| 对象（ccs-backend） | 耗时 | prompt / completion | 速度 |
|---|---|---|---|
| 容器 `CentralizedConfigurationSystem.Api` | 233.5s | 3101 / 638 tok | 2.7 tok/s |
| 组件 `Api|Application` | 176.2s | 1029 / 496 tok | 2.8 tok/s |
| 流程 `POST /api/v5/mt5/groups/batch-create` | 154.8s | 1836 / 320 tok | 2.1 tok/s |

人工核对（对照 `--facts` 原文）：
- 容器：五条关键点全部能在事实里找到（ccs-ui 调用、6 个成员项目、→ MT4Proxy / Publisher、PostgreSQL / S3 / 飞书 / Prometheus / OpenTelemetry）；风险"外部依赖置信度 0.5"属实。浏览器模式里卡片从"生成中"轮询到完成，显示正常。
- 组件："Application 与 Infrastructure 成环、反向 20 条边"——**事实本身是错的**：那次用的是不开 Roslyn 的索引，20 条是按方法名唯一解析到实现类的启发式边（置信度 0.5）。Roslyn 全量索引后 Application → Infrastructure 为 0，环消失。叙述忠实复述了错误事实——**叙述质量的上限是索引质量**，验收与日常都应在开 Roslyn 的索引上看。
- 流程：入口、行为链五个、MT5GroupApiClient 外部调用正确；"分派置信度 0.4"同样来自启发式索引；模型把 `dispatch:…` 当成证据 id 引用，正确地被标成"推断"。

### 5e NetArchTest 规则叠加到 DSM
- `ArchRuleParser` 从测试项目解析 `Types.…GetResult()` 链（跨行、同文件 `const string` 替换、`HaveDependencyOn / Any / All`、`ShouldNot` / `NotHave…`），存 `arch_rules`；`DsmBuilder` 判每个格子，违规红实框、必须依赖未满足红虚框 `!`，格子提示列测试名与文件:行；组件比规则粗时不判。
- ccs-backend `PipelineArchitectureTests.cs`：**9 条规则**一一对应（placement 5：ICommand / IQuery / ICommandHandler / IQueryHandler → Modules，IPipelineBehavior → Application.Behaviors；forbid 4：Application ↛ Infrastructure ×3、Application ↛ Api），跳过 0。
- 违规：不开 Roslyn 的索引上 Api 容器项目层 1 处（Application → Infrastructure 20 条，命中三条 forbid）；**Roslyn 全量索引上 0 处**，与该测试实际通过一致——再次说明启发式边会制造假违规。

### 精度补丁
- **泛型分派**：Roslyn Handler 字典按构造泛型键（含类型实参）登记，查不到再退回泛型定义键；Handler 子类没有自己的 `Handle` 时，补一条到基类 `Handle` 的调用边，并把基类里 `Send(message.Command)` 这类按类型形参的分派用子类的实参替换后再分派。eShop `POST /api/orders`：改前分派到 10 个 `IdentifiedCommandHandler<T,R>` 子类；改后只到 `CreateOrderIdentifiedCommandHandler` → `CreateOrderCommandHandler.Handle` → `OrderingIntegrationEventService.AddAndSaveEventAsync` / `OrderingContext.SaveEntitiesAsync` / `OrderRepository.Add`，30 节点。
- **入口规则**：gRPC 服务端（`MapGrpcService<T>` → override 方法，eShop Basket.API 3 个：`Basket/GetBasket`、`UpdateBasket`、`DeleteBasket`）、Hangfire（`RecurringJob.AddOrUpdate<T>` / `BackgroundJob.Enqueue<T>`）、MassTransit `IConsumer<T>`。eShop 入口 107 → 110（grpc 3）。
- **时序图标签**：方法节点显示"类型.方法"，一屏多个 `Handle` 时分得清。

### 两个项目的最新数字（索引版本 7，Roslyn 全量）
| 项 | ccs-backend | eShop |
|---|---|---|
| 索引 | 352s（Roslyn 8 项目 / 4264 文档 / 0 错误） | Roslyn 28 项目 / 547 文档 / 0 错误 |
| 容器 / 外部 / 边 | 5 / 17 / 29 | 12 / 8 / 38 |
| 入口 | 1562（handler 1058、http 474、hosted 18、hub 12） | 110（grpc 3、handler 41、hosted 8、http 58） |
| 路由匹配 | 4 | 28 |
| 声明规则 / 违规 | 9 / 0 | 0 / 0 |

### 已知
- 端点连不上后不会自己恢复：卡片一直显示"连不上"，直到用户点"生成"重新排队。
- 叙述事实还没用上 5e 的违规信息。
- Hangfire 跨行注册、接口类型注册的 Job、子类 override 的虚方法（流程仍显示基类版本）未处理。
- 主语带接口限定的 forbid 规则按整个程序集判，比原规则宽，格子提示里注明了。

## 里程碑 5c：入口识别 + 按入口的时序图 + 跨容器路由匹配 — 2026-10-14
- 用户要求"多开几个 agent 一次性做完再验收"（分步交付看不出在 ccs-backend 上的效果）。本轮三个代理并行（服务端接线、页面、eShop 验证）+ 主线补通用性缺口，一次交付。
- 事实层：`EntryPoint` / `RouteMatch`（`entry_points` / `route_matches` 表，索引版本 6）。`EntryDiscovery`：控制器（经继承链到 ControllerBase + 特性文本，Roslyn 符号起始行在特性行时向下扫）、Minimal API（`MapGroup` 前缀沿变量链 / 同一行链式拼进路由，`MapGet("/x", Handler)` 方法组时入口落到 Handler）、SignalR Hub、`BackgroundService` / `IHostedService`、Quartz `IJob`、分发处理器（handles 边）、Next.js 文件路由。`RouteLinker`：前端 fetch 路径与 C# 方法里的路径（字面量、用本文件 `string` 字段展开的插值串、直接传字段名）对上另一容器的 http 入口 → 0.9 边，占位"API /xxx"全部对上时移除；同路径 GET / POST 两个入口按调用方式挑。`FlowBuilder`：从入口出发走运行时去向（calls 含 via=impl、dispatch:*；排除 handles / conduit / references），深度 12、每节点 6 分支、300 节点封顶，外部调用合并成"N 个框架调用"，相邻相同子树 ×N，actor = 容器内成员项目 / 其他容器 / 外部首段命名空间（tree-sitter 兜底无命名空间时统一一列）。索引末尾 arch → git → entries 三阶段；`/api/arch/entries`、`/api/arch/flow`；CLI `arch --entries` / `--flow`。
- 页面：容器内子标签"组件矩阵 | 入口流程"；左栏入口按种类分组 + 搜索；右侧 CSS Grid 时序图（列 = actor，行 = 调用，默认展开到深度 3，点行展开 / 折叠，点标签聚焦调用图，分派橙色虚线、跨容器紫色、外部灰色点线、置信度 < 0.7 淡化）。
- 测试：.NET 205（Core 69 / TreeSitter 30 / Server 77 / Cli 10 / Roslyn 19），vitest 68（flowLayout 7 个新增）。
- 性能：`--flow` 在 ccs-backend 上 6.9s → 1.1s（行为链扫描改为按 implements 边建索引；IndexService 整库符号 / 边只加载一次，索引结束时失效）。

### ccs-backend 实测（`dist/codeatlas.exe arch <repo> --db 临时库`）
| 项 | 数值 |
|---|---|
| 入口总数 | 1623（http 491、handler 1094、hosted 22、hub 16；`entries` 阶段 8.3s） |
| Api 容器 | http 489（控制器动作）、hosted 12（Approval 执行 / 提醒、BackgroundJob Reaper / Worker、Kafka 消费、EditLock 通知、PluginInstallationSync、Realtime 广播 …）、hub 8、handler 547、job 0（没有 Quartz） |
| Worker 容器 | 567（handler 547 与 Api 共用 Application，hosted 10，http 2，hub 8） |
| 路由匹配 | 4 处：ccs-ui `app/api/chat/route.ts` 等对上 Api 的 `/api/v5/...` → **ccs-ui → Api 0.9**；`/api/proxy-gateway/*`、`/api/health`、`/negotiate` 不对（代理前缀与 Next 自身路由，按设计不猜） |
| `POST /api/v5/mt5/groups/batch-create` | 57 节点 / 91ms：`BatchCreateGroups` → 分派 `BatchCreateMt5GroupsCommand` → `HandleAsync`[Application] → `BulkCreateGroupsAsync`[Infrastructure, via impl] → `MT5RawGroupBulkUpdateClientRequest` → `SendAndReadDataAsync`（MT5 管理 API HTTP 调用）→ 错误处理链到 `ServiceUnavailableException`[Shared]；行为链 Approval / Audit / ComparePreviewCaching / Permission / Validation |
| Kafka 消费流程 | `KafkaEventConsumerHostedService.ExecuteAsync` → `ConsumeLoop` → `ProcessMessage` → `DispatchAsync`[impl] → `Mt5StatusChangedHandlerBase` → `SendToTopicAsync` / `Deserialize`[MessagePack]；17 节点 |
| 浏览器模式 | 容器图 → 双击 Api → DSM 与子标签 → 入口流程：1056 个入口的列表、搜索 `groups/batch-create` 剩 2 条、选中后时序图 7 个 actor（Api、Application、Infrastructure、Domain、Shared、System、Microsoft）、折叠行带 `(N)`、点标签切到调用图并聚焦 `BulkCreateGroupsAsync` |

### dotnet/eShop 实测（换一个项目：Aspire + Minimal API + MediatR + gRPC + RabbitMQ）
第一次跑出来 12 个容器全是孤岛、路由匹配 0。逐条对账后补的都是通用规则，没有写任何 eShop 专用判断：
| 缺口 | 补法 | 补后 |
|---|---|---|
| `Sdk="MSTest.Sdk"` 测试项目被当容器 | Sdk 名含 Test 即测试 | 测试项目不出现 |
| Aspire.* 客户端包不认识 | 包映射加 Aspire.Npgsql / StackExchange.Redis / RabbitMQ.Client / Azure.AI.OpenAI 等 | Catalog / Identity / Ordering / Webhooks → PostgreSQL，Basket → Redis |
| `o.BaseAddress = new("https+http://catalog-api")` 目标类型 new + 服务发现方案 | `NewUri` 接受 `new(`；`https+http://` 归一化；非本机主机名去分隔符后与容器名 / 目录名比对 | **WebApp → Catalog.API / Ordering.API 0.7，WebhookClient → Webhooks.API 0.7** |
| `AddGrpcClient<Basket.BasketClient>(o => o.Address = new("http://basket-api"))` | GrpcChannelRule 加 AddGrpcClient 注册 | **WebApp → Basket.API grpc 0.7** |
| `BasicPublishAsync` / `AddNpgsqlDbContext` 新 API 名 | 模式加 Async 后缀与 Aspire 注册方法 | 7 个容器 ↔ RabbitMQ 生产 / 消费 |
| Minimal API `vApi.MapGroup("api/catalog")` + `v1.MapGet("/items", GetAllItemsV1)` 路由只剩 `/items`、入口落在 `MapCatalogApi` | MapGroup 前缀链 + 方法组处理器 | Catalog 18 条 `/api/catalog/...`、Ordering 8 条 `/api/orders/...`，入口是真正的处理方法 |
| WebApp `private readonly string remoteServiceBaseUrl = "api/catalog/"` + `$"{remoteServiceBaseUrl}items/{id}"` | 路径字段展开 | **路由匹配 0 → 28**：ClientApp / HybridApp / WebApp → Catalog.API、ClientApp / WebApp → Ordering.API、WebhookClient → Webhooks.API 升到 0.9 |

- 入口：tree-sitter 兜底 106（http 58、handler 40、hosted 8）；`dotnet restore eShop.Web.slnf` 后 Roslyn 全量（28 项目 / 547 文档 / 0 错误 / 124s）107（handler 41）。整包 `restore eShop.slnx` 因 MAUI 项目缺 maui 工作负载失败，不 restore 时 Roslyn 解析不到 MediatR 类型，分派边为 0——是环境问题不是规则问题，记在已知里。
- 流程：`POST /api/orders` → `CreateOrderAsync` 分派 → `IdentifiedCommandHandler` → `CreateRequestForCommandAsync`[Infrastructure] → `ExistAsync` → EF；tree-sitter 索引上还能续接到 **Basket.API 的 `OrderStartedIntegrationEvent` Handler → `DeleteBasketAsync` → Redis**（跨容器分派）。`GET /api/catalog/items/{id:int}` 只有 6 个框架调用（EF 查询内联），符合代码。

### 已知
- Roslyn 的分派键按未绑定泛型归并：`mediator.Send(IdentifiedCommand<CreateOrderCommand,bool>)` 分派到全部 10 个 `IdentifiedCommandHandler<T,R>` 子类，真正的 `CreateOrderCommandHandler` 折在"还有 5 个"里。要在分析器里给构造泛型单独建键（下一轮）。
- 共享库里的 `UseDefaultOpenApi` 的 `MapGet("/")` 每个容器各出现一次（eShop.ServiceDefaults 是所有容器的成员）——是事实，但噪音。
- 子仓库（mt4proxy、mt5-event-publisher、ccs-ui）没有符号，容器有、入口无；`/api/proxy-gateway/*` 代理前缀不猜。
- gRPC 服务端（`MapGrpcService<T>`）、Hangfire、MassTransit 消费者还没有入口规则。

## 里程碑 5b：容器内组件 DSM + git 叠加 — 2026-10-14
- 事实层：`GitHistory`（解析 `git log --all --numstat --no-renames`，code-maat 的 revisions / churn / age / coupling 子集；嵌套 git 仓库各跑一次加前缀；> 30 文件的提交不算耦合）、`GitStorage`（`git_file_stats` / `git_coupling` / `git_meta`，索引版本 5）。`DsmBuilder`（Server）：组件按容器切片（项目 → 主命名空间的子命名空间 → …），Tarjan 找环，分量间"被依赖者在前"拓扑排序，叠加热点与隐性耦合。接口 `GET /api/arch/dsm?container=&root=`，CLI `arch --dsm`。
- 测试：Core git 5 个（解析、阈值、前缀、存储、真跑本仓库）、Server DSM 5 个、vitest 2 个。
- **真实数据逼出的裁定**：DSM 只算编译期依赖。第一次跑出来 Application → Infrastructure 有 3050 条"依赖"、六个项目全在一个环里，与 csproj 引用矛盾——是里程碑 3 的"接口调用 → 实现"派生边（via=impl）和分派边混进来了，排除后矩阵变成严格下三角、零环。module 符号（每文件一个）也要排除，否则 2841 个文件堆成一个空名组件。

### ccs-backend 实测（`arch --dsm CentralizedConfigurationSystem.Api`）
| 项 | 数值 |
|---|---|
| git 统计 | 19654 次提交、14162 个文件、1600 对耦合文件、335 个大提交未计耦合，17.5s |
| Api 容器项目层 | 6 组件 / 14 格 / 0 环，严格下三角：Shared ← Domain(28) ← Application(3045 / 976) ← Infrastructure(1756 / 662 / 6821) ← Bootstrap(39 / 65 / 64) ← Api(125 / 4 / 1922 / 13 / 12)；**Api → Infrastructure 13 条直接依赖**（宿主绕过 Bootstrap 装配）被量化出来了；200ms |
| 钻入 Application | 15 组件 / 49 格 / 1 个环：Modules ↔ Dtos ↔ Utilities ↔ Interfaces ↔ Features ↔ CompareHandlers ↔ Abstractions ↔ Configurations 互相依赖成一团；Modules → Dtos 3558、→ Interfaces 3524、→ Abstractions 6197；漏前缀的 `RolePermissionLibrary.*` 与全局命名空间以全名单独成组件 |
| 热点 | Application 14482 次修订，Modules 7806、Dtos 3347、Interfaces 1213；全部 0 月前（活跃仓库） |
| 隐性耦合 | 项目层与 Application 层各 0 对（相邻组件之间都有静态边，耦合落在格子里而不是虚框） |
| mt5-event-publisher 等子仓库 | 容器有、符号无（父仓库 .gitignore 忽略子仓库，tree-sitter 没索引），DSM 显示说明 |

### 已知
- 组件修订数按文件求和，同一提交改了组件内多个文件会重复计。
- NetArchTest 规则还原（声明 vs 实际叠加）未做（5e）。

## 里程碑 5a：架构事实层 + 容器图（第一屏）— 2026-10-14
- 起因：用户否定了命名空间 / 管道泳道式的"整个项目"视图（"只是把代码翻译一下"、"Approvals 不是业务、MT4 MT5 又没有细分"、"换了别的项目呢"）。调研了 8 个开源项目后定下 C4 路线（`docs/superpowers/specs/2026-10-14-reference-survey.md`、`2026-10-14-architecture-map-design.md`），第一屏改为容器图。
- 事实层：`ProjectInventory`（csproj / package.json / Dockerfile / launchSettings / appsettings）→ `ContainerDiscovery`（可执行 .NET 项目与前端应用，成员为传递项目引用）→ 6 条发现规则（包引用、本地程序集、`AddHttpClient`、`GrpcChannel.ForAddress`、Kafka / RabbitMQ / Redis / 数据库 / `DllImport` 模式、前端 `fetch` / `axios`）→ `EdgeMatcher`（URL 的 host:port 对上另一容器的监听端口 0.7，否则外部系统 0.5；`*-consume` 方向反转）→ `.codeatlas/architecture.yaml` 声明合并。索引版本 4，新表 `arch_nodes / arch_edges / arch_outbound / arch_conflicts`。
- 测试：Core 架构测试 12 个（清单、容器、每条规则正反例、地址回溯含 ccs-backend 的跨行链式写法、匹配三档、声明合并与冲突、写库读库）、Server 1、vitest 5（布局）。

### 四个项目实测（`codeatlas arch <repo>`，均未写 architecture.yaml）
| 项目 | 结果 | 耗时 |
|---|---|---|
| ccs-backend（含三个子仓库） | 5 容器：Api、Worker、ccs-ui（Next.js）、MT4Proxy.Web、Publisher；17 外部系统；33 条边。**Api / Worker → MT4Proxy.Web 0.7**（`MetaTraderManagerMT4Api:Url` = localhost:5162 对上 launchSettings）、**Api / Worker → Publisher 0.7**（`MetaTraderManagerApi:Url` = localhost:5003）；Publisher → Kafka 生产，Kafka → Api / Worker 消费（事件回流闭环）；Lark、Prometheus、PostgreSQL、Redis、RabbitMQ、AWS S3、OpenTelemetry、MetaQuotes / MetaTrader4 SDK；测试项目不出现 | 9.1s |
| mt4proxy 单独 | 1 容器 MT4Proxy.Web（6477 / 5162）→ MetaTrader4 Manager API（NuGet 原生封装）、OpenTelemetry | 0.1s |
| mt5-event-publisher 单独 | 1 容器 Publisher（12996 / 5003）→ Kafka 生产、RabbitMQ、Redis、MetaQuotes MT5Common / MT5Manager、CcpSharedContracts、OpenTelemetry | 0.2s |
| ccs-ui 单独 | 1 前端容器（3000）→ 后端 API 按路径前缀聚成 4 组：`/api/v5`（4 处）、`/api/proxy-gateway`、`/api/health`、`/negotiate`；7 处 HTTP 调用全部取到路径 | 0.9s |

与手工 C4 草稿（`2026-10-14-ccs-backend-c4-draft.md`）对照：容器与外部系统一致；两处"待核实"的 HTTP 边都被配置匹配确认；gRPC 对端（`Mt4X86ServiceClientFactory`）仍未对上，等 5c。

### 页面
- 地图模式三视图"容器 | 管道 | 命名空间"，有容器时默认容器图：左使用者 / 前端、中容器、右外部系统（超过 10 个折两列），边按档位实线 / 虚线，消费边橙色，声明边紫色点线；单击看证据，双击容器钻到命名空间地图（根 = 成员项目的多数派公共命名空间），面包屑"项目"回容器图。
- 浏览器模式验证：ccs-backend 第一屏 5 容器 / 17 外部 / 33 边，详情条"其中 4 条按配置或路由对上"。

### 已知
- 根命名空间钻取是过渡方案，5b 的 DSM 按容器切片后替换。
- 规则是 C# 类不是 YAML（计划里的裁定 1）；仓库级扩展先靠 `architecture.yaml`。
- `AddHttpClient` 的地址解析是文本回溯，覆盖了字面量、`configuration["A:B"]`、`GetSection("A").Get<T>()` + `.Url`、跨行链式赋值；更绕的写法解析不到就降到 0.5 并标出来。

## 里程碑 4：项目地图的管道视图 — 2026-10-14
- 起因：ccs-backend 的命名空间地图顶层只有一个大模块，钻进去是类库分布；用户指出项目是管道模式，"所有业务都通过管道串起来"。设计稿 `docs/superpowers/specs/2026-10-14-pipeline-map-design.md`。
- 服务 `GET /api/pipeline?root=`（`PipelineBuilder`，与 `MapBuilder` 用同一份全库符号与边，`MapEdge` 多了 `via`，另读 `di_registrations`）：Handler = `handles` 边起点，消息 = 其目标，入口 = `dispatch:T` 边起点所在类型（排除 Handler 与测试代码），行为 = 实现 `*Behavior` 接口的类型（按 DI 注册排序），下游 = Handler 方法的 calls 目标按命名空间聚合；行 = 消息命名空间，root 为空时取公共前缀再按 ≥ 90% 下钻；跨行流：嵌套分发、绕过管道。
- 页面：地图模式内"管道 | 命名空间"切换，有分发数据默认管道；泳道用 preset 坐标（`pipelineLayout.ts` 纯函数），橙色虚线嵌套分发、红色实线绕过，孤儿消息 / 绕过 / 顺序未知用警示边框；双击钻入，叶子双击消息切回调用图。
- 测试：Core 2（via、DI 读取）、Server 10（规则 + API 回退）、vitest 6（goToPath、布局）；对 `PipelineBuilder` 做了 5 个变异（不排除测试、不自动下钻、行为链按名字、同行嵌套也画流、下游不滤 0.3），每个都让至少一个测试变红，恢复后全绿。

### ccs-backend 实测（`dist/codeatlas.exe index --full --roslyn on` 到临时库：tree-sitter 42.8s + Roslyn 71.9s，61289 符号，512 条 DI）
| 项 | 数值 |
|---|---|
| `/api/pipeline` 首次 / 缓存 | 1.2–1.3s / 3ms |
| 自动 root | `CentralizedConfigurationSystem.Application.Modules`（公共前缀是空串——有全局命名空间的消息——逐层 ≥ 90% 下钻到这里） |
| 行 | Approvals 8 / Common 7 / MT4 144 / MT5 254 / PluginHub 88 个消息；root 之外 31 个（`Features.Approvals.Events` 等） |
| 行为链 | Validation → Permission → Approval → Audit → ComparePreviewCaching，顺序来自 DI，与 `Bootstrap/PipelineRegistration.cs` 一致 |
| 入口 | MT5 26 个类型（Mt5TradingAccountsController ×35 分发居首），MT4 10，PluginHub 12；排除测试入口 319 个类型 |
| 孤儿消息 | MT5 31 / MT4 26 / PluginHub 4 / Approvals 2——多为 BackgroundJob 请求与只在测试里分发的命令 |
| 嵌套分发 | 23 条；跨行流 4 条：Common → MT4 ×3、Common → MT5 ×5、MT4 → PluginHub ×3、MT5 → PluginHub ×4 |
| 绕过管道 | 0（没有入口直接调 Handler 方法） |
| 下游 | MT5：Application.Interfaces 520、Application.Dtos 423、Infrastructure.MT5 262、Application.Modules 213、Application.Utilities 135；框架调用 1749 |
| 钻取 | MT5 → Accounts（Clients 1 / Managers 23 / TradingAccounts 44）→ TradingAccounts（BackgroundJobs 7 / Commands 23 / Queries 14）→ Commands 叶子 23 行，例如 `CreateMt5TradingAccountCommand`：入口 Mt5TradingAccountsController，Handler CreateMt5TradingAccountHandler，下游 Interfaces 3 / MT5 3 / Dtos 1 / Exceptions 1 |

### 与取证数字的对账
- 取证时用的是扩展正在用的旧索引（tree-sitter、索引版本 2、无 Roslyn）：消息 519、Handler 468、PluginHub 没有 `handles` 边（Handler 经基类间接实现接口）。Roslyn 索引后 PluginHub 88 个消息出现了；MT5 从 230 涨到 254。
- 实测过程中改了两条规则：Roslyn 限定名带 `<TRequest, TResponse>`，显示名要剥掉泛型参数；下游分组原定"公共前缀段数 + 1"，公共前缀为空时全部塌成一组，改为与自动 root 同层。

### 已知
- `TestPathPattern` 的 `Tests?\.cs$` 不区分大小写也不看单词边界，`Contests.cs` 会被当成测试文件；页面与服务端同一条规则，没改。
- 行为链是否对某条消息生效取决于运行时 DI；闭合泛型的 Behavior（只对某类消息）与开放泛型分不开，全部按"对所有消息生效"显示。
- 旧索引（版本 2）的工作区要全量重建一次才有 `di_registrations`；否则行为链显示"顺序未知"。
