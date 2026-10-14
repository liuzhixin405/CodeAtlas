# CodeAtlas 里程碑 2：本地服务 + VS Code 扩展 设计文档

日期：2026-10-13
状态：已批准（用户选择跳过书面审阅，以实际使用为准）
上游：`2026-10-13-codeatlas-design.md`（总体设计）。本文件只写里程碑 2 新增的部分，与总体设计冲突处以本文件为准。

## 0. 调整了什么

总体设计把 Web 界面（里程碑 2）排在编辑器插件（后续子项目）之前。用户试用命令行后反馈"太命令化"，因此调整为：**直接做 VS Code 扩展，把图形面板做成扩展里的 Webview**，不先做独立浏览器版。独立浏览器版以后把同一份页面放进服务的 wwwroot 即可。Visual Studio 扩展排在 VS Code 之后。

## 1. 范围

**做**：

1. 右键菜单 / 快捷键："在 CodeAtlas 中查看"，光标处符号成为焦点，面板打开。
2. 邻域图面板：焦点居中，出边在左、入边在右；双击展开一层；超过 30 个邻居先弹清单勾选；右键节点设为焦点 / 折叠 / 隐藏 / 在编辑器中打开。
3. 多候选（confidence 0.3）折叠成一条虚线连到"N 个可能目标"胶囊节点，点开成清单。
4. 双击节点用 VS Code API 跳回代码行。
5. 面板顶部搜索框，前缀搜索，选中即焦点。
6. 保存文件后防抖 1.5 秒自动增量索引；首次无索引时提示建立并显示进度。
7. 面包屑：焦点历史，回退时恢复当时的展开集合。

**不做**：找路径、AI 解释、DI 标注、源码片段、导出、Visual Studio 版。

## 2. 组件与接口

```
VS Code 扩展（TypeScript，esbuild 打包）
├─ 命令、右键菜单、快捷键、状态栏、保存监听
├─ ServerManager：每个工作区文件夹一个 codeatlas serve 子进程
└─ Webview 面板：页面随扩展打包在 media/，页面用 fetch 访问本地服务

codeatlas serve --repo <path> [--db <path>] [--port 0] [--parent-pid N]   ← CodeAtlas.Server 项目 + CLI 子命令
├─ ASP.NET Core 最小 API，只监听 127.0.0.1；端口 0 由系统分配
├─ 启动后向 stdout 打印一行 {"port":12345}
├─ 索引任务串行；查询用独立只读连接（WAL），索引期间查询不阻塞
└─ 看门狗：--parent-pid 指定的进程消失 10 秒后自行退出
```

页面放扩展里而不是服务里：Webview 本来就由扩展加载本地文件，服务只管数据。

### 2.1 服务接口（JSON）

| 方法 路径 | 用途 |
|---|---|
| `GET /api/health` | `{repo, dbPath, files, symbols, indexedAt}`；files 为 0 表示未索引 |
| `POST /api/index` body `{full: bool}` | 启动索引并立即返回 202；已在跑返回 409 |
| `GET /api/index/status` | `{running, phase, done, total, lastReport}`；面板每 500ms 轮询 |
| `GET /api/search?q=&limit=` | 同 CLI search |
| `GET /api/symbols/{id}` | 符号详情，含文件路径；不存在 404 |
| `GET /api/symbols/at?path=&line=&col=` | 光标位置的最内层非 module 符号；path 可为绝对或相对仓库根；无则 404 |
| `GET /api/symbols/{id}/neighbors?direction=out\|in\|both&kinds=a,b` | 邻居列表；每条含 edgeId、kind、direction、confidence、line、other（可空＝悬空）、toName；confidence 0.3 的边带 `ambiguousGroup = "<kind>:<line>"` |

Core 新增 `IndexReader.GetSymbolAt(fileId, line, col)`：范围包含该位置、起点最靠后的非 module 符号。

### 2.2 索引新鲜度

扩展监听保存，防抖 1.5 秒后 `POST /api/index`（增量）。索引中再触发则置脏标记，结束后再跑一次。不做单文件接口。

## 3. 面板交互

布局：顶部搜索框与面包屑；中间 Cytoscape 画布（elkjs layered 布局，从左到右）；底部选中节点与边的详情条。

规则：

- 焦点模式，切焦点重新布局，200ms 过渡。
- 初始一层；节点右上角数字表示未展开数量。
- 双击展开两个方向；超过 30 个弹清单按 kind 分组勾选。
- 同一 `ambiguousGroup` 的边合并为一条虚线到灰色胶囊"N 个可能目标"，点开成清单，勾选实化。
- 边样式：1.0 / 0.9 粗实线，0.7 实线，0.5 细线，0.3 只在胶囊内。kind 用颜色区分。
- 节点样式：类矩形、方法/函数圆角、接口菱形、external 灰色不可展开；标签短名，悬停显示限定名与位置。
- 右键节点：设为焦点 / 折叠邻居 / 隐藏 / 在编辑器中打开。
- 双击跳代码：发 `open` 消息给扩展；external 无效。
- 面包屑：记录焦点与当时的展开集合，可回退。
- 搜索：输入防抖 300ms，下拉前 20 条。
- 主题：跟随 VS Code 注入的 CSS 变量。

页面与扩展消息：

| 方向 | 消息 |
|---|---|
| 扩展 → 页面 | `init {apiBase}`、`focus {symbolId}`、`indexChanged` |
| 页面 → 扩展 | `ready`、`open {path, line, col}`、`requestIndex {full}` |

页面结构：`GraphState`（焦点、展开集合、折叠组、隐藏集合、面包屑栈）是无依赖的纯 TypeScript 模块，所有交互先改状态再渲染；Cytoscape 层只负责画。

## 4. 扩展生命周期

命令：

| 命令 | 触发 | 行为 |
|---|---|---|
| `codeatlas.showSymbol` | 编辑器右键"在 CodeAtlas 中查看"、`Ctrl+Alt+G` | `symbols/at` → 打开面板 → `focus`。找不到时状态栏提示，不弹窗 |
| `codeatlas.openPanel` | 命令面板 | 打开面板 |
| `codeatlas.indexWorkspace` / `codeatlas.indexWorkspaceFull` | 命令面板、面板按钮 | 增量 / 全量索引 |
| `codeatlas.restartServer` | 命令面板 | 重启服务 |

- `onStartupFinished` 激活，首次用到命令时才拉起服务。多根工作区按文件夹各一个服务。
- ServerManager：设置 `codeatlas.serverPath`，空则 PATH 中找 `codeatlas`；找不到弹一次带"打开设置"的提示。启动读 stdout 首行端口，5 秒超时视为失败，stderr 进输出通道。请求连接失败自动重启一次，再失败状态栏红色并停止重试。deactivate 时 kill 子进程。
- IndexWatcher：只对已支持扩展名响应；防抖 1.5 秒；脏标记；状态栏显示符号数 / 索引进度 / 未索引。
- 错误处理：全部走状态栏与输出通道，唯一的模态提示是"找不到服务可执行文件"。

## 5. 测试

- Server：真实启动在随机端口的集成测试（fixture 仓库先索引进临时文件库），覆盖所有端点、`symbols/at` 边界、`ambiguousGroup`、并发索引 409、看门狗。
- Core：`GetSymbolAt` 单元测试。
- Webview：Vitest 测 `GraphState`。
- 扩展：Vitest 测 ServerManager 端口解析与超时、IndexWatcher 防抖与脏标记（假计时器）；VS Code API 以薄接口隔离。
- 端到端：Extension Development Host 打开 ccs-backend 手工走清单，记入 `docs/acceptance.md`。

## 6. 里程碑

- **2a**：CodeAtlas.Server + `serve` 子命令 + `GetSymbolAt`。验收：curl 查到 ccs-backend 任意符号的邻居。
- **2b**：Webview 页面先在普通浏览器对着 2a 跑通。验收：焦点、展开、折叠、面包屑。
- **2c**：VS Code 扩展壳。验收：ccs-backend 上端到端清单。
