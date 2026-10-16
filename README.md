# CodeAtlas

本地代码阅读器，帮人重新读懂（尤其是 AI 写的）代码。两个方向：**从项目往下**（容器图 → 框架流程 → 入口流程），**从方法出发**（调用时序、调用大纲、影响范围、模块关系、编辑器里的 CodeLens 跳转）。

当前状态：有 VS Code 扩展（面板 + 侧边栏 + CodeLens）、本地服务和命令行。能把 TypeScript / TSX / JavaScript / C# / Go / Python / Java 仓库索引进 SQLite（C# 另有 Roslyn 精确语义），识别部分分派链路和框架管道，并支持本地或联网 AI 叙述。早期里程碑规划见 [设计文档](docs/superpowers/specs/2026-10-13-codeatlas-design.md) 与 [扩展设计](docs/superpowers/specs/2026-10-13-vscode-extension-design.md)，当前能力和限制以使用指南为准。

## 在 VS Code 里用（推荐）

市场品牌为 **CodeTrail Graph**，后端项目仍叫 CodeAtlas；内部 `codeatlas.*` 命令、设置及 `.codeatlas` 索引目录不变。

1. 在扩展市场搜索 **CodeTrail Graph**，或运行 `code --install-extension 164910441.codetrail-graph`。离线包及源码构建方式见 [使用指南](docs/vscode-extension-guide.md)；构建需要 .NET 10 SDK，最终用户无需另装 .NET 运行时。
2. 打开仓库，把光标放在任意方法或类名上，**右键 → CodeTrail Graph → 在调用图中查看**（或 `Ctrl+Alt+G`）。第一次会提示建立索引，耗时取决于项目规模与是否启用 Roslyn。
3. 调用图默认是**时序图**，按调用顺序从上到下、按项目分列；工具栏「时序 | 网状」可切换画法。网状图自动向下展开主分支，「↑ 到源头」追调用者。项目地图包括容器、框架流程、命名空间，面板支持跨视图后退 / 前进。
4. 保存文件后自动增量索引（可在设置 `codeatlas.indexOnSave` 关闭）；C# 有待更新文件时会自动补算 Roslyn。服务进程由扩展管理，命令面板里有「CodeTrail Graph: 重启服务」。

0.1.1 使用索引格式 **12**：旧索引会自动全量重建。原始引用持久化后，增量索引可重新绑定未修改的调用方；索引请求串行排队、全量请求优先，过期的异步视图响应不会覆盖新选择。性能变化需在实际项目上测量，不保证固定耗时。

命令行方式仍然可用，见下面。

完整的安装与使用指南（前提、更新、看懂这张图、工具栏、项目地图、常见问题）见 [docs/vscode-extension-guide.md](docs/vscode-extension-guide.md)。

多平台打包、Marketplace 上传和后续升级见 [发布指南](docs/vscode-extension-publishing.md)。

## 快速上手

### 1. 拿到可执行文件

发布后在仓库的 `dist\codeatlas.exe`（`dist/` 不入 git；自带 .NET 运行时，不用另装）。

想自己重新发布：

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

会得到 `codeatlas.exe`（自带运行时）和 `roslyn\codeatlas-roslyn.exe`（C# 精确语义用的 Roslyn worker，需要本机 .NET 10 SDK；没有时 C# 退回 tree-sitter）。其他平台加 `-Rid linux-x64` / `osx-arm64` 等。建议把 `dist` 目录加进 PATH，下面的例子直接写 `codeatlas`。

`codeatlas index <repo>` 在 tree-sitter 之后会自动用 Roslyn 覆盖 C# 的调用/实现/引用边（限定名带参数类型，重载不再混淆；分发链路按真实类型解析）。`--roslyn off` 关掉，`--roslyn on` 缺 worker 或解决方案时报错，`--sln <path>` 指定解决方案（默认取仓库里最大的 .sln/.slnx）。

### 2. 索引一个仓库

```bash
codeatlas index D:\code\shop-api
```

- 索引库默认写在仓库的 `.codeatlas\index.db`。**记得把 `.codeatlas/` 加进那个仓库的 `.gitignore`**，工具会提示但不会替你改。
- 不想污染仓库，用 `--db` 指到别处：`codeatlas index <repo> --db C:\tmp\shop.db`。之后 `query` / `search` 也要带同一个 `--db`。
- 第二次运行复用未变化文件的解析结果（大小、修改时间及内容哈希），重新绑定需要更新的引用；架构输入或 Git ref 不变时跳过相应后处理。CLI 输出各阶段 `timings`，实际耗时以项目实测为准。
- `--full` 强制全部重解析。改了工具版本或者感觉索引不对劲时用。
- 自动跳过 `node_modules`、`bin`、`obj`、`.git`、`dist`、`build`、`.vs`、`.idea`、`.codeatlas` 等生成目录。嵌套仓库和子模块默认纳入，`--no-nested` 关闭；Git worktree 副本始终跳过。
- 大于 2MB 的文件标记为 skipped 不解析。

参考数据（一个 C# + TS 的中型后端）：4373 个文件、5.9 万个符号、73 万条边，全量 21 秒。

### 3. 搜一个符号

```bash
codeatlas search D:\code\shop-api OrderApproval
```

按名字前缀搜，不区分大小写，多个词用空格隔开表示都要命中。输出是：限定名、种类、文件:行号。

### 4. 看它的调用关系

```bash
codeatlas query D:\code\shop-api Acme.Shop.Api.Controllers.OrderApprovalController.ApproveAsync
```

输出长这样：

```
Acme.Shop.Api.Controllers.OrderApprovalController.ApproveAsync  [method]  Acme.Shop.Api/Controllers/OrderApprovalController.cs:74
  → references  Acme.Shop.Application.Dtos.ApproveDto  (0.7)  @ line 81
  → calls  Acme.Shop.Application.Orders.Commands.ApproveOrderCommand  (0.7)  @ line 84
  → calls  Acme.Shop.Application.Abstractions.IDispatcher.SendAsync  (0.7)  @ line 85
  ← calls  <调用它的地方>  (0.9)  @ line 12
```

- `→` 是它调出去的，`←` 是谁调它。
- 括号里是 **confidence**，表示这条边是怎么推出来的（见下节）。
- `@ line` 是调用发生在哪一行（在 `→` 的情况下是当前方法里的行；`←` 的情况下是调用方文件里的行）。

常用参数：

| 参数 | 作用 |
|---|---|
| `--in` / `--out` | 只看调用者 / 只看被调用者。默认两个方向都看 |
| `--depth N` | 递归展开 N 层（最多 6），缩进表示层级 |
| `--kinds calls,implements` | 只看某几种边。可选：calls、references、inherits、implements、imports、contains、dispatches。默认不显示 contains 和 imports |
| `--db <path>` | 索引库位置，和 index 时保持一致 |

符号可以用短名或限定名。短名有多个同名定义时会列出所有候选让你挑限定名：

```bash
codeatlas query D:\code\shop-api GetAsync
```

几个值得试的问题：

- 一个接口都有哪些实现：`codeatlas query <repo> <接口限定名> --in --kinds implements`
- 一个方法往下三层都碰了什么：`codeatlas query <repo> <方法限定名> --out --depth 3`
- 一个 Repository 方法被哪些 Handler 用：`codeatlas query <repo> <方法限定名> --in`

TS 仓库的限定名格式是 `相对路径:类.方法`，比如 `web/src/components/LoginPage/index.tsx:LoginPage`；C# 是命名空间链 `Acme.Orders.OrderService.Find`。

## 怎么读 confidence

这个里程碑的解析是 **tree-sitter 按名字做的启发式**，不是编译器级语义。每条边都带一个 0 到 1 的分数，告诉你它是怎么猜的：

| 分数 | 含义 | 可信度 |
|---|---|---|
| 1.0 | 包含关系、import 解析到了具体文件或命名空间 | 确定 |
| 0.9 | 同一个文件里有且只有一个同名定义 | 很高，但同文件里 `repository.Foo()` 恰好和自己的 `Foo()` 重名时会错 |
| 0.7 | 在 import / using 范围内有且只有一个同名定义 | 高 |
| 0.5 | 全仓库有且只有一个同名定义；或者没找到定义、归为外部库（`external:xxx`） | 中 |
| 0.3 | 有多个候选，分不清是哪个，每个候选各一条边（最多 20 条） | 低，当"可能是这些之一"看 |

0.3 的边是常见的噪声来源。同名方法无法按导入与接收者信息唯一定位时，会列出候选。C# 的 Roslyn 增强器已可用，会用真实类型信息覆盖可解析的边；其他语言和缺 SDK 时仍以启发式结果为主。

`external:xxx` 是仓库外的目标（框架、第三方包、标准库），也包括暂时没解析出来的动态目标。支持常见 tsconfig `paths` 别名，但复杂再导出与解构仍有限制；外部节点不能展开。

## 现在能做什么、不能做什么

能：

- TS / TSX / JS / C# / Go / Python / Java 的符号与调用关系（各语言适用的符号种类不同）。
- 直接调用、`new`、继承、接口实现、类型引用、import / using。
- C# 文件级命名空间，`I` 开头的基类型自动判为 implements。
- 增量索引、文件删除后的悬空边保留、重建后自动重挂。
- VS Code 调用时序 / 网状图、调用大纲、影响范围、模块关系、CodeLens，以及项目地图与框架流程。
- C# Roslyn 精确语义、可静态识别的分派链路、部分架构规则与 AI 叙述（可关闭）。

还不能（都有计划）：

- 任意反射、动态分派、运行时生成路由的完整还原；图中框架阶段与顺序仅代表静态证据，未知顺序会标注。
- 无 Roslyn 时，tree-sitter 分派兜底只覆盖直接消息构造实参；先赋变量或工厂生成的消息不保证识别。
- Blazor `.razor` 解析、复杂重命名转导出和完整的跨语言类型推断。
- Kotlin / Rust 等其他语言（底座支持，还没写查询文件）。Java 已支持（Maven / Gradle、Spring MVC / WebFlux / JAX-RS / gRPC / 定时任务 / 消息监听 / picocli），但 Gradle 版本目录、Kotlin、Lombok 生成的方法、按实参类型挑重载不支持。
- 类型推断有边界：调用返回的泛型 `T`、lambda 参数、元组解构、Go 的多返回值第二个起、Python 未注解的参数推不出来，这些调用按名字近似解析（置信度 0.7 / 0.3）。
- 列号按 UTF-8 字节算，含中文的行会偏。

更完整的验收数据和已知问题见 [docs/acceptance.md](docs/acceptance.md)。

## 开发

```bash
dotnet build
dotnet test CodeAtlas.slnx --verbosity minimal
npm --prefix extension run typecheck
npm --prefix extension test
```

项目结构：

```
src/CodeAtlas.Core                 模型、SQLite 读写、启发式解析器、IEdgeResolver 扩展点
src/CodeAtlas.Indexing.TreeSitter  适配器底座 + 每语言一对 .scm 查询 + 薄适配器；扫描器；索引器
src/CodeAtlas.Cli                  index / query / search / arch / serve
src/CodeAtlas.Server               本地 API、项目地图、框架流程与 AI 叙述
src/CodeAtlas.Indexing.Roslyn       C# 精确分析与独立 worker
extension/                        VS Code 扩展与 Webview
tests/                             每个项目一个测试项目，fixtures 是真实的小源文件
docs/superpowers/specs             设计文档
docs/superpowers/plans             实施计划（里程碑 1 已完成）
docs/acceptance.md                 真实仓库验收记录
```

加一种语言：在 `Queries/` 下写 `<lang>.definitions.scm` 和 `<lang>.references.scm`，在 `Adapters/` 下写一个几十行的子类，在 `tests/.../Fixtures/<lang>/` 放一个小源文件写快照测试。约定见 `LanguageAdapterBase` 的注释。
