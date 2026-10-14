# CodeAtlas for VS Code

点开一个符号，看它调了谁、被谁调、实现了什么，沿调用链向下走到底、向上追到入口。支持 TypeScript / TSX / JavaScript / C#。所有数据都在本机。

完整指南见仓库 `docs/vscode-extension-guide.md`。

## 安装

前提：Windows x64、VS Code ≥ 1.100、本机已安装 .NET 10 运行时（`dotnet --list-runtimes` 里要有 `Microsoft.NETCore.App 10.x`）。

```bash
code --install-extension codeatlas-0.1.0.vsix --force
```

装完**完全退出并重新打开 VS Code**。更新时也必须先完全退出，否则服务文件被占用，安装会报 `EPERM`。

## 三步上手

1. 用 VS Code 打开一个仓库。
2. 把光标放到方法名或类名上，右键 → **在 CodeAtlas 中查看**（或 `Ctrl+Alt+G`）。
3. 首次会提示"这个工作区还没有 CodeAtlas 索引"，点 **建立索引**（ccs-backend 规模约 40 秒）。索引完成后面板在右侧打开并聚焦到该符号。

## 功能一览

- **图的方向**：调用者在左，被调用者在右。橙色虚线是分发到 Handler，线上写 `new 消息类型`；灰色点状虚框是外部库，不能展开。线越粗 confidence 越高。
- **自动向下**（默认开）：聚焦后自动沿调用链展开到叶子；**↑ 到源头** 向上追到没有调用者的入口。
- **双击节点**：未展开的展开一层；已展开的在编辑器中打开；"还有 N 个…"/"N 个可能目标"胶囊弹清单勾选。
- **右键节点**：设为焦点 / 展开一层 / 折叠邻居 / 从图中隐藏 / 在编辑器中打开。
- **工具栏开关**（默认都关）：**类型引用**、**测试代码**、**分发细节**。
- **搜索框**：按名字前缀搜，多个词用空格；**面包屑**：焦点历史。
- **项目地图**：按命名空间 / 目录聚合的模块图，节点大小按符号数，边粗细按依赖数，橙色边含分发；双击模块钻入，双击类型切回调用图。
- **索引**：保存 `.ts/.tsx/.js/.jsx/.cs` 等文件后自动增量索引（设置 `codeatlas.indexOnSave`）；面板 **⟳ 索引** 按钮或命令面板"CodeAtlas: 索引工作区（增量 / 全量重建）"手动触发。索引库在 `<仓库>/.codeatlas/index.db`，**请加入 `.gitignore`**。
- **服务**：由扩展自动管理，查找顺序 扩展自带 `server/` → 设置 `codeatlas.serverPath` → `PATH`。日志在输出面板 **CodeAtlas** 通道。

## AI 叙述（本地或联网）

项目地图的容器图 / 组件矩阵 / 入口流程右侧有一张**叙述卡**：单击一个容器、组件行头或入口，显示模型写的一段话、关键点和风险，每条后面的小标签是它引用的事实（点一下定位到容器图节点或调用图符号；灰色标"推断"的是引用对不上事实、模型自己推的）。打开卡片时有缓存就直接显示；没有缓存会自动排队生成，但自动排队的只保留**最近打开的那一个**——随手点过的其他对象如果还没开始就撤掉，不会排出一长串。点 **生成 / 重新生成** 是显式排队，不会被撤。卡片状态行写着由哪个模型、什么时间、在哪个 commit 上生成。AI 只能在预设版式里挑一个（面包屑栏右侧"版式"下拉里的那几个），卡上"按 AI 建议排版"等于在下拉里选它。

### 缓存与过期

- 结果存在 `.codeatlas/narratives.db`，与索引库分开，**重建索引不丢**；流程按入口方法全名存，符号 id 变了也认得。
- 生成时记下相关文件的内容哈希、架构事实指纹和 git HEAD。之后打开时，相关文件内容变了或架构事实变了就算**过期**：旧结果照样显示，摘要上方出现一条"可能过期：<原因>"的提示条，点右侧 **更新** 才重算（不会自动重算）。重算期间旧内容保留，状态行显示"正在更新…"。
- **换模型不算过期**（比如从本地切到联网）：旧结果照样显示，状态行写明它是哪个模型生成的，与当前服务不同时附"（当前服务：…）"；想用新模型重写就点 **重新生成**。
- 本地慢模型适合夜里批量预生成：`codeatlas arch <repo> --narrate-all` 生成全部容器与项目层组件里缺失或过期的叙述（`--flows N` 再加每个容器前 N 个 HTTP 入口的流程，`--force` 全部重算），每条生成完就存，中途停了再跑会接着做，第二天打开都是现成的。

### 选服务

命令面板 → **CodeAtlas: 选择 AI 服务**，从预设里挑一个，会一次写好协议、端点和模型（默认写到用户设置；如果当前工作区已经覆盖了这几项，就写到工作区设置）。预设里的模型名只是默认值，可在设置 `codeatlas.ai.model` 里改。

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

联网预设选完如果还没有 key，会紧接着弹出密码输入框。之后也可以用 **CodeAtlas: 设置 AI API Key** / **CodeAtlas: 清除 AI API Key**。key 存在 **VS Code SecretStorage**（系统凭据库），**不进 settings.json**；按"协议 + 端点主机"分别保存，切换服务不用重输。启动服务时 key 只经环境变量 `CODEATLAS_AI_API_KEY` 传给服务进程，不出现在命令行参数和日志里（CI 等场景也可以直接在外部环境里设这个变量，扩展里没存 key 时沿用它）。叙述卡上没 key 会提示"未设置 API Key"，key 无效（HTTP 401/403）时卡上有 **设置 API Key** 按钮。

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
- **服务不可用**：命令面板 → **CodeAtlas: 重启服务**；看输出面板 CodeAtlas 通道。确认已装 .NET 10 运行时。
- **右键菜单里没有该项**：没有完全退出 VS Code，退出所有窗口再打开。
- **链条在某处断了**：可能是未识别的解耦模式，请把方法名反馈给维护者。
- **"N 个可能目标"里几十个候选**：tree-sitter 按名字解析的固有限制，Roslyn 版本（里程碑 3）会消除。

目前不支持：Python / Go / Java、调用图里的 AI 解释、Visual Studio 版、JSX 组件引用、tsconfig `paths` 别名。
