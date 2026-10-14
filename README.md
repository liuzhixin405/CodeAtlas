# CodeAtlas

本地代码阅读器。核心动作只有一个：**点开一个符号，看它调了谁、被谁调、实现了什么**，逐层展开。目标是帮人重新读懂（尤其是 AI 写的）代码。

当前状态：**里程碑 2**。有 VS Code 扩展（右键 → 邻域图面板）、本地服务和命令行。能把 TypeScript / TSX / JavaScript / C# 仓库索引进 SQLite。Roslyn 精确语义（里程碑 3）、解耦调用链（3b）、AI 解释和 MCP（里程碑 4、5）见 [设计文档](docs/superpowers/specs/2026-10-13-codeatlas-design.md) 与 [扩展设计](docs/superpowers/specs/2026-10-13-vscode-extension-design.md)。

## 在 VS Code 里用（推荐）

1. 安装扩展：`cd extension && npm install && npm run package && code --install-extension codeatlas-0.1.0.vsix`（需要先 `powershell -File scripts/publish.ps1` 发布服务与 Roslyn worker 到 `dist/`，打包脚本会把它们一起装进 VSIX）。
2. 打开仓库，把光标放在任意方法或类名上，**右键 → 在 CodeAtlas 中查看**（或 `Ctrl+Alt+G`）。第一次会提示建立索引，ccs-backend 大约 40 秒。
3. 面板里：聚焦后**自动沿调用链向下展开到底**（橙色虚线是通过 Handler 的分发）；"↑ 到源头"从当前方法反向追到 Controller / 后台任务等入口；双击节点展开一层，已展开的节点双击跳回代码；"还有 N 个"/"N 个可能目标"胶囊双击后勾选要加入的；右键节点有设为焦点 / 折叠 / 隐藏 / 在编辑器中打开；顶部有搜索框、面包屑，以及"类型引用""测试代码"两个默认关闭的开关。
4. 保存文件后自动增量索引（可在设置 `codeatlas.indexOnSave` 关闭）。服务进程由扩展自动管理，命令面板里有"CodeAtlas: 重启服务"。

命令行方式仍然可用，见下面。

完整的安装与使用指南（前提、更新、看懂这张图、工具栏、项目地图、常见问题）见 [docs/vscode-extension-guide.md](docs/vscode-extension-guide.md)。

## 快速上手

### 1. 拿到可执行文件

本地已经发布了一份：`D:\github\CodeAtlas\dist\codeatlas.exe`（`dist/` 不入 git；需要本机有 .NET 10 运行时，你已经有）。

想自己重新发布：

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

会得到 `codeatlas.exe` 和 `codeatlas-roslyn.exe`（C# 精确语义用的 Roslyn worker，与 codeatlas 同目录即可被找到）。建议把 `D:\github\CodeAtlas\dist` 加进 PATH，下面的例子直接写 `codeatlas`。

`codeatlas index <repo>` 在 tree-sitter 之后会自动用 Roslyn 覆盖 C# 的调用/实现/引用边（限定名带参数类型，重载不再混淆；分发链路按真实类型解析）。`--roslyn off` 关掉，`--roslyn on` 缺 worker 或解决方案时报错，`--sln <path>` 指定解决方案（默认取仓库里最大的 .sln/.slnx）。

### 2. 索引一个仓库

```bash
codeatlas index D:\gitlab\RC_SZ\ccs-backend
```

- 索引库默认写在仓库的 `.codeatlas\index.db`。**记得把 `.codeatlas/` 加进那个仓库的 `.gitignore`**，工具会提示但不会替你改。
- 不想污染仓库，用 `--db` 指到别处：`codeatlas index <repo> --db C:\tmp\ccs.db`。之后 `query` / `search` 也要带同一个 `--db`。
- 第二次运行只重新解析改过的文件（按内容哈希判断），ccs-ui 全量 6 秒、增量 1 秒以内。
- `--full` 强制全部重解析。改了工具版本或者感觉索引不对劲时用。
- 自动跳过 `node_modules`、`bin`、`obj`、`.git`、`dist`、`build`、`.vs`、`.idea`、`.codeatlas`，以及任何含有自己 `.git` 的子目录（git worktree、子模块、嵌套仓库）。
- 大于 2MB 的文件标记为 skipped 不解析。

ccs-backend 的参考数据：4373 个文件、5.9 万个符号、73 万条边，全量 21 秒。

### 3. 搜一个符号

```bash
codeatlas search D:\gitlab\RC_SZ\ccs-backend ApprovalTask
```

按名字前缀搜，不区分大小写，多个词用空格隔开表示都要命中。输出是：限定名、种类、文件:行号。

### 4. 看它的调用关系

```bash
codeatlas query D:\gitlab\RC_SZ\ccs-backend CentralizedConfigurationSystem.Api.Controllers.V5.ApprovalTaskController.ApproveAsync
```

输出长这样：

```
CentralizedConfigurationSystem.Api.Controllers.V5.ApprovalTaskController.ApproveAsync  [method]  CentralizedConfigurationSystem.Api/Controllers/V5/Common/ApprovalTaskController.cs:74
  → references  CentralizedConfigurationSystem.Application.Dtos.ApproveDto  (0.7)  @ line 81
  → calls  CentralizedConfigurationSystem.Application.Modules.Approvals.Commands.ApproveTaskCommand  (0.7)  @ line 84
  → calls  CentralizedConfigurationSystem.Application.Abstractions.IDispatcher.SendAsync  (0.7)  @ line 85
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
codeatlas query D:\gitlab\RC_SZ\ccs-backend GetAsync
```

几个值得试的问题：

- 一个接口都有哪些实现：`codeatlas query <repo> <接口限定名> --in --kinds implements`
- 一个方法往下三层都碰了什么：`codeatlas query <repo> <方法限定名> --out --depth 3`
- 一个 Repository 方法被哪些 Handler 用：`codeatlas query <repo> <方法限定名> --in`

TS 仓库的限定名格式是 `相对路径:类.方法`，比如 `ccs-src/ccs-app/src/components/LoginPageNew/index.tsx:LoginPageNew`；C# 是命名空间链 `Acme.Orders.OrderService.Find`。

## 怎么读 confidence

这个里程碑的解析是 **tree-sitter 按名字做的启发式**，不是编译器级语义。每条边都带一个 0 到 1 的分数，告诉你它是怎么猜的：

| 分数 | 含义 | 可信度 |
|---|---|---|
| 1.0 | 包含关系、import 解析到了具体文件或命名空间 | 确定 |
| 0.9 | 同一个文件里有且只有一个同名定义 | 很高，但同文件里 `repository.Foo()` 恰好和自己的 `Foo()` 重名时会错 |
| 0.7 | 在 import / using 范围内有且只有一个同名定义 | 高 |
| 0.5 | 全仓库有且只有一个同名定义；或者没找到定义、归为外部库（`external:xxx`） | 中 |
| 0.3 | 有多个候选，分不清是哪个，每个候选各一条边（最多 20 条） | 低，当"可能是这些之一"看 |

0.3 的边是最常见的噪声来源。`GetAsync`、`Ok`、`List`、`For` 这种名字在大仓库里有几十到上千个定义，tree-sitter 不知道接收者类型，只能全列出来。**里程碑 3 的 Roslyn 增强器会用真实类型信息把这些边替换成精确边**，这正是它存在的理由。

`external:xxx` 是仓库外的目标（框架、第三方包、标准库），也包括暂时没解析出来的东西（比如 TS 里解构出来的局部变量、`@/` 路径别名的 import）。它们不能展开。

## 现在能做什么、不能做什么

能：

- TS / TSX / JS / C# 的类、接口、方法、函数、字段、属性、枚举、命名空间定义。
- 直接调用、`new`、继承、接口实现、类型引用、import / using。
- C# 文件级命名空间，`I` 开头的基类型自动判为 implements。
- 增量索引、文件删除后的悬空边保留、重建后自动重挂。

还不能（都有计划）：

- 图形界面。现在只能用命令行看文字版。
- 精确的 C# 语义：重载解析、泛型、扩展方法、接口方法到实现方法的边。
- MediatR、事件总线、AOP、中间件管道这类**解耦调用链**（设计文档 4.7 节，里程碑 3b）。
- TSX 里的 JSX 组件引用（`<Box>`）、tsconfig 的 `paths` 别名、JS 的 `require()`。
- Python / Go / Java（底座支持，还没写查询文件）。
- 列号按 UTF-8 字节算，含中文的行会偏。

更完整的验收数据和已知问题见 [docs/acceptance.md](docs/acceptance.md)。

## 开发

```bash
dotnet build
dotnet test          # 67 个测试，约 5 秒
```

项目结构：

```
src/CodeAtlas.Core                 模型、SQLite 读写、启发式解析器、IEdgeResolver 扩展点
src/CodeAtlas.Indexing.TreeSitter  适配器底座 + 每语言一对 .scm 查询 + 薄适配器；扫描器；索引器
src/CodeAtlas.Cli                  index / query / search
tests/                             每个项目一个测试项目，fixtures 是真实的小源文件
docs/superpowers/specs             设计文档
docs/superpowers/plans             实施计划（里程碑 1 已完成）
docs/acceptance.md                 真实仓库验收记录
```

加一种语言：在 `Queries/` 下写 `<lang>.definitions.scm` 和 `<lang>.references.scm`，在 `Adapters/` 下写一个几十行的子类，在 `tests/.../Fixtures/<lang>/` 放一个小源文件写快照测试。约定见 `LanguageAdapterBase` 的注释。
