# 验收记录

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

