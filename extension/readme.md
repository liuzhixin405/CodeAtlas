# CodeTrail Graph for VS Code

本地代码地图，两个方向看代码：

- **从项目往下**：容器图看有哪些服务、怎么互相调用；框架流程看每个请求都要经过的那套框架（C# 的中间件 → 分派器 → 行为链 → Handler，Spring 的过滤器 → 拦截器 → 切面，gin / Express 的中间件链，React 的 Provider → 路由守卫 → 请求拦截器……）；入口流程看某一个请求一步步做了什么，连同它实际经过的管道（校验、权限……）和按消息类型注入的验证器。
- **从方法出发**：调用图（时序）看它按顺序调了谁；侧边栏的调用大纲、影响范围看它的步骤和改了会波及哪些入口；模块关系看它在哪些组件之间来回；方法上方的 CodeLens 一键跳到调用方 / 被调方。

支持 TypeScript / TSX / JavaScript / C# / Go / Python / Java。索引默认在本机；若配置远程 AI 服务，事实子图和可选源码会发往该端点。

基于 [CodeAtlas](https://github.com/liuzhixin405/CodeAtlas) 后端。市场品牌是 **CodeTrail Graph**，内部 `codeatlas.*` 命令 / 设置和 `.codeatlas` 索引目录不变。

[完整指南](https://github.com/liuzhixin405/CodeAtlas/blob/main/docs/vscode-extension-guide.md) · [发布指南](https://github.com/liuzhixin405/CodeAtlas/blob/main/docs/vscode-extension-publishing.md)

## 0.1.1 更新

- 调用图默认使用按调用顺序和项目分列的时序图，可切回网状图；增加框架流程和跨视图后退 / 前进。
- 索引格式升级到 **12**，旧索引会自动全量重建。原始引用持久化与事务提交，增量重新绑定未修改调用方，避免解析失败留下部分索引。
- C# 有待更新文件时自动补算 Roslyn；保存与手动索引统一排队，全量需求优先。
- 搜索、焦点、展开与 DSM 等视图隔离过期异步响应，索引后刷新当前视图；后处理根据输入指纹和 Git ref 复用结果。
- 恢复已发布品牌与图标，并保留 SQLite / Roslyn 安全依赖更新。

## 安装

前提：VS Code ≥ 1.100；按系统装对应平台的包：Windows x64、Linux x64 / arm64、macOS x64 / arm64。**不需要装 .NET**，服务自带运行时。只有 C# 项目想要 Roslyn 精确语义时，才需要 .NET 10 SDK；没有时 C# 用 tree-sitter 解析，首次索引时提示一次。

在扩展市场搜索 **CodeTrail Graph**，或运行 `code --install-extension 164910441.codetrail-graph`。以下是 Windows x64 离线包安装示例：

```bash
code --install-extension codetrail-graph-win32-x64-0.1.1.vsix --force
```

装完**完全退出并重新打开 VS Code**。更新时也必须先完全退出，否则服务文件被占用，安装会报 `EPERM`。

## 三步上手

1. 用 VS Code 打开一个仓库。
2. 把光标放到方法名或类名上，右键 → **CodeTrail Graph → 在调用图中查看**（或 `Ctrl+Alt+G`）。CodeTrail Graph 的右键命令都在「CodeTrail Graph」子菜单里。
3. 首次会提示"这个工作区还没有 CodeTrail Graph 索引"，点 **建立索引**（四五千个文件的仓库约 40 秒）。索引完成后面板在右侧打开并聚焦到该符号。

## 功能一览

- **调用图默认是时序图**：从焦点方法往下，按调用顺序从上往下、按项目分列，名字一律 `类型.方法`（和项目地图的入口流程同一种图）；单击一行看详情、双击以它为新焦点、点 ▸ 展开折叠。工具栏 **时序 | 网状** 切到原来的网状图（面板记住你的选择）。
- **网状图的方向**：调用者在左，被调用者在右。默认只画它调了谁，调用者收在焦点旁的 **被 N 处调用** 胶囊里，点一下再加入。橙色虚线是分发到 Handler，线上写 `new 消息类型`；接口调用与唯一实现合成一个节点；灰色点状虚框是外部库，不能展开。线越粗 confidence 越高。
- **自动向下**（网状，默认开）：聚焦后自动沿调用链展开到叶子，每个节点挑最像主流程的几个分支（调用与分发合计；`new` 对象排在普通调用之后）；**↑ 到源头** 向上追到没有调用者的入口（时序模式下点它会切到网状再追）。
- **双击节点**（网状）：没展开的展开一层它调的；已展开的在编辑器中打开；"还有 N 个…"/"N 个可能目标"胶囊弹清单勾选。
- **右键节点**（网状）：设为焦点 / 展开一层 / 展开调用者 / 折叠邻居 / 从图中隐藏 / 在编辑器中打开。
- **工具栏开关**（网状，默认都关）：**类型关系**（继承 / 实现；看类之间的关系请用模块关系）、**类型引用**、**测试代码**、**分发细节**。
- **搜索框**：按名字前缀搜，多个词用空格；**面包屑**：焦点历史。
- **调用大纲**（活动栏 CodeTrail Graph 图标）：光标所在方法按源码顺序调了什么，逐层展开；标出循环内 / 条件内 / 回调里的调用，单击跳到调用处，可固定、可隐藏框架调用。详见 `docs/vscode-extension-guide.md` 第 9 节。
- **影响范围**（同一活动栏，调用大纲下面）：反过来看"改它会影响谁"——光标所在方法的直接调用方（经接口 / 经分派 / 构造、×N、所在控制结构）逐层往上展开，以及最终走到的入口（按容器分组，如 `HTTP POST /api/orders`）；测试调用方默认只计数。与调用大纲共用一次光标解析，固定各管各的。详见第 10 节。
- **快速跳转**：方法上方的 CodeLens 首项带 `ctg:` 标识，显示 `被 N 处调用 · 调用 M 个 · 分派 K · 影响范围`（分派项只在有 Handler 时显示）；`没有调用方` 表示索引中没找到，不证明是死代码。点开可跳到调用处或经分派的 Handler；命令快捷键为 `Ctrl+Alt+H / J / R`，macOS `Ctrl+Cmd+H / J / R`，跳转可用编辑器 `Alt+←` 退回。设置 `codeatlas.codeLens.enabled` 可关闭。
- **模块关系**：面板顶部「调用图 | 模块关系 | 项目地图」的第二种模式，以类为中心画类与类之间的依赖，按组件分框、从左到右按依赖顺序排，反向依赖标红；编辑器右键「CodeTrail Graph → 在模块关系中查看」或侧边栏标题栏按钮打开。详见使用指南第 12 节。
- **项目地图**：按命名空间 / 目录聚合的模块图，节点大小按符号数，边粗细按依赖数，橙色边含分发；双击模块钻入，双击类型切回调用图。
- **框架流程**：项目地图「容器 | 框架流程 | 命名空间」的第二个，画每个请求都要经过的框架骨架（中间件 → 控制器 → 分派器 → 行为链 → Handler → 收尾，列 = 阶段）；「N 个 Handler ▸」点开按模块分组、可筛选的 Handler 清单，点一项打开它的入口流程；入口流程标题里的「经框架流程 ▸」跳回来。详见使用指南 8.2 节。
- **索引**：保存 `.ts/.tsx/.js/.jsx/.cs` 等文件后自动增量索引（设置 `codeatlas.indexOnSave`）；面板 **⟳ 索引** 按钮或命令面板"CodeTrail Graph: 索引工作区（增量 / 全量重建）"手动触发。索引库在 `<仓库>/.codeatlas/index.db`，**请加入 `.gitignore`**。
- **服务**：由扩展自动管理，查找顺序 扩展自带 `server/` → 设置 `codeatlas.serverPath` → `PATH`。日志在输出面板 **CodeTrail Graph** 通道。

## 多项目

一个服务、一个索引库可以同时覆盖好几个目录，前端里的 `fetch('/api/...')` 才能连到另一个仓库里的后端控制器，容器图、DSM、入口流程也都带上它们。

- **嵌套仓库默认纳入**：工作区里带自己 `.git` 目录的子仓库（独立克隆，例如 `shop-api/payment-gateway/`）和子模块一起索引，各自的 `.sln` 也各跑一次 Roslyn。git worktree（`.git` 是指向 `…/worktrees/…` 的文件，比如 `.claude/worktrees/*`）是本仓库的副本，**总是跳过**，不会重复。不想要嵌套仓库时把 `codeatlas.includeNestedRepos` 设为 `false`（服务以 `--no-nested` 启动）。
- **多根工作区**：第一个文件夹是主根，其余文件夹自动作为外部项目一起索引（不用另配）。命令和面板都作用于主根的服务；在任何一个文件夹里右键"CodeTrail Graph → 在调用图中查看"都能定位。
- **仓库外的目录**：设置 `codeatlas.projects`（字符串数组，相对第一个文件夹或绝对路径），例如只打开前端 `shop-web` 时写 `["../shop-api"]`。也可以写进仓库的 `.codeatlas/config.json`：`{"projects": ["../shop-api"]}`，命令行 `codeatlas index` 同样认。
- **自动检测旁边的后端**：单独打开一个前端仓库（3 层内的 `package.json` 依赖里有 `next` / `react` / `vue` / `@angular/core` / `vite`，2 层内没有 `*.sln` / `*.slnx`），且没配 `codeatlas.projects`、`.codeatlas/config.json` 也没写、不是多根工作区时，第一次启动服务会看一眼上级目录（根下有 `*.sln` / `*.slnx` 就只提它）或同级目录（根下有解决方案的，最多 3 个），提示一次"是否一起索引"。选"一起索引"会把相对路径（如 `".."`）写进工作区设置 `codeatlas.projects`（单文件夹工作区即 `.vscode/settings.json`），服务重启并全量重建（含 Roslyn，约 2–3 分钟）；选"不用"或"一起索引"后本工作区不再询问，直接关掉通知则下次会话再问。
- 外部目录的文件以**相对主根**的路径入库，例如 `../shop-api/Acme.Shop.Api/Controllers/FooController.cs`，容器键也是这样的路径。目录互相包含（主根是 `shop-api/web`、外部目录是 `shop-api`）时按真实路径去重，同一个文件只入库一次。
- 外部目录暂时不存在（盘没挂、改了名）时输出面板提示一次，之前入库的文件保留；从设置里去掉它，下次索引才删。
- 改 `codeatlas.projects`、`codeatlas.includeNestedRepos` 或增删工作区文件夹，服务自动重启并跑一次增量索引；新加的外部目录要跑 Roslyn 就点一次"索引工作区（全量重建）"。

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| `codeatlas.projects` | `[]` | 一起索引的其他目录（相对第一个文件夹或绝对路径） |
| `codeatlas.includeNestedRepos` | `true` | 是否索引带独立 `.git` 的子仓库与子模块；worktree 总是跳过 |

## AI 叙述（本地或联网）

项目地图的容器图 / 组件矩阵 / 入口流程右侧有一张**叙述卡**：单击一个容器、组件行头或入口，显示模型写的一段话、关键点和风险，每条后面的小标签是它引用的事实（点一下定位到容器图节点或调用图符号；灰色标"推断"的是引用对不上事实、模型自己推的）。打开卡片时有缓存就直接显示；没有缓存会自动排队生成，但自动排队的只保留**最近打开的那一个**——随手点过的其他对象如果还没开始就撤掉，不会排出一长串。点 **生成 / 重新生成** 是显式排队，不会被撤。卡片状态行写着由哪个模型、什么时间、在哪个 commit 上生成。AI 只能在预设版式里挑一个（面包屑栏右侧"版式"下拉里的那几个），卡上"按 AI 建议排版"等于在下拉里选它。

### 缓存与过期

- 结果存在 `.codeatlas/narratives.db`，与索引库分开，**重建索引不丢**；流程按入口方法全名存，符号 id 变了也认得。
- 生成时记下相关文件的内容哈希、架构事实指纹和 git HEAD。之后打开时，相关文件内容变了或架构事实变了就算**过期**：旧结果照样显示，摘要上方出现一条"可能过期：<原因>"的提示条，点右侧 **更新** 才重算（不会自动重算）。重算期间旧内容保留，状态行显示"正在更新…"。
- **换模型不算过期**（比如从本地切到联网）：旧结果照样显示，状态行写明它是哪个模型生成的，与当前服务不同时附"（当前服务：…）"；想用新模型重写就点 **重新生成**。
- 本地慢模型适合夜里批量预生成：`codeatlas arch <repo> --narrate-all` 生成全部容器与项目层组件里缺失或过期的叙述（`--flows N` 再加每个容器前 N 个 HTTP 入口的流程，`--force` 全部重算），每条生成完就存，中途停了再跑会接着做，第二天打开都是现成的。

### 选服务

命令面板 → **CodeTrail Graph: 选择 AI 服务**，从预设里挑一个，会一次写好协议、端点和模型（默认写到用户设置；如果当前工作区已经覆盖了这几项，就写到工作区设置）。预设里的模型名只是默认值，可在设置 `codeatlas.ai.model` 里改。

| 预设 | 协议 | 端点 | 默认模型 |
| --- | --- | --- | --- |
| 本地 llama-server | OpenAI 兼容 | `http://127.0.0.1:8080/v1` | `qwen3.6-27b` |
| DeepSeek | OpenAI 兼容 | `https://api.deepseek.com/v1` | `deepseek-chat` |
| 通义千问（阿里云百炼） | OpenAI 兼容 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| OpenAI | OpenAI 兼容 | `https://api.openai.com/v1` | `gpt-4o-mini` |
| Anthropic Claude | Anthropic Messages API | `https://api.anthropic.com/v1` | `claude-sonnet-5-5` |
| 自定义… | — | 打开设置页 `codeatlas.ai` 手填 | — |

**速度**：本机 27B 模型一条约 2–4 分钟；联网服务通常 5–20 秒。联网时可把 `codeatlas.ai.concurrency` 调到 2–4、`codeatlas.ai.timeoutSeconds` 降到 60。

### 设 API Key

联网预设选完如果还没有 key，会紧接着弹出密码输入框。之后也可以用 **CodeTrail Graph: 设置 AI API Key** / **CodeTrail Graph: 清除 AI API Key**。key 存在 **VS Code SecretStorage**（系统凭据库），**不进 settings.json**；按"协议 + 端点主机"分别保存，切换服务不用重输。启动服务时 key 只经环境变量 `CODEATLAS_AI_API_KEY` 传给服务进程，不出现在命令行参数和日志里（CI 等场景也可以直接在外部环境里设这个变量，扩展里没存 key 时沿用它）。叙述卡上没 key 会提示"未设置 API Key"，key 无效（HTTP 401/403）时卡上有 **设置 API Key** 按钮。

### 发送了什么

- **事实子图**：容器 / 组件 / 入口的名字、它们之间的边、配置键与 URL、入口的调用树。
- **入口方法源码**（≤80 行，只在入口流程叙述里）：设置 `codeatlas.ai.includeSource` 关掉后只发事实子图。

端点不是本机回环地址时，卡片底部会红字提示数据会发到哪里。**把公司代码发给第三方服务前，请确认符合公司数据政策。**

### 设置

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| `codeatlas.ai.enabled` | `true` | 关掉后服务以 `--no-ai` 启动，叙述卡显示"AI 未启用" |
| `codeatlas.ai.provider` | `openai` | `openai` = OpenAI 兼容协议（llama-server、DeepSeek、通义、OpenAI 等）；`anthropic` = Claude Messages API |
| `codeatlas.ai.endpoint` | `http://127.0.0.1:8080/v1` | API 基地址（到 `/v1` 为止） |
| `codeatlas.ai.model` | `qwen3.6-27b` | 请求里的模型名；换模型不让旧叙述过期，点"重新生成"才按新模型重写 |
| `codeatlas.ai.concurrency` | `1` | 同时生成几条（1–8）；联网 2–4，本地保持 1 |
| `codeatlas.ai.timeoutSeconds` | `300` | 单条超时；本地慢保持 300，联网 60 足够 |
| `codeatlas.ai.includeSource` | `true` | 是否发送入口方法源码（≤80 行） |

改任何一项（以及设置 / 清除 API Key）都会自动重启服务。端点连不上时卡片显示原因，其他功能不受影响。

## 常见问题

- **等待服务端口超时**：首次启动被杀毒扫描拖慢，等几秒再右键一次。
- **服务不可用**：命令面板 → **CodeTrail Graph: 重启服务**；看输出面板 CodeTrail Graph 通道。确认装的是本机平台的包。
- **右键菜单里没有该项**：没有完全退出 VS Code，退出所有窗口再打开。
- **链条在某处断了**：可能是未识别的解耦模式，请把方法名反馈给维护者。
- **"N 个可能目标"里几十个候选**：tree-sitter 无法唯一绑定时会保留候选；C# 的 Roslyn 可提高精度，但不能保证消除所有动态调用歧义。

目前不支持：Kotlin / Rust 等未实现适配器的语言、Visual Studio 版、Alpine 等 musl 发行版、Blazor `.razor` 解析，以及任意反射和动态路由的完整还原。Java、常见 CommonJS `require()` 和部分回调关系已支持；复杂解构、重命名转导出与运行时类型推断仍有限制。
