# CodeAtlas 里程碑 4：项目地图管道视图 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 让"项目地图"能按管道维度（入口 → 消息 → 行为链 → Handler → 下游）而不是只按命名空间聚合，在 ccs-backend 上一屏看清各业务模块的管道全貌与异常（嵌套分发、绕过管道、孤儿消息、行为链顺序未知）。

**Architecture:** 服务端新增纯函数 `PipelineBuilder.Build(symbols, edges, di, root)`（与 `MapBuilder` 并列，输入同一份全库符号与边，`MapEdge` 增加 `Via`），由 `IndexService.Pipeline(root)` 缓存并经 `GET /api/pipeline` 暴露。页面在地图模式内加"管道 / 命名空间"切换，管道视图用独立 cytoscape 实例 + preset 坐标画泳道，布局计算放在纯函数 `pipelineLayout.ts` 里便于测试。

**Tech Stack:** 不变（.NET 10、xunit、cytoscape 3.34、vitest 5）。

**Spec:** `docs/superpowers/specs/2026-10-14-pipeline-map-design.md`。

## Global Constraints

- 不新增边、不改索引器、不改索引版本；Core 只加两个读取方法。
- 所有角色规则都写在 `PipelineBuilder` 里，页面不再做二次判定。
- 测试代码判定规则与页面 `TestPathPattern` 保持一字不差（移植到 C# `PipelineBuilder.IsTest`）。
- 计数钉到具体数字；禁止 `NotEmpty` / `Count > 0` 式断言。

## Review Focus

1. **入口排除测试代码后 ccs-backend 的入口列不能为空**：非测试 `dispatch:*` 有 556 条 / 156 个类型，必须出现在表里。
2. **自动 root 的下钻不能越过叶子**：某行 ≥ 90% 但它已经是叶子（没有更深的消息命名空间）时停下。
3. **行为链顺序只来自 DI**：没有 `di_registrations` 时必须报 `behaviorsOrdered = false`，不能用名字顺序冒充。
4. **叶子行双击切到调用图的 symbolId 必须是消息类型**，不是 Handler。
5. **preset 布局的坐标函数要稳定**：同一份数据永远同样的图（行按键排序，流按 from/to 排序）。

## File Structure

```
src/CodeAtlas.Core/Storage/IndexReader.cs         GetEdgeTriples 多返回 Via；新增 GetDiRegistrations()
src/CodeAtlas.Server/MapBuilder.cs                MapEdge 增加 Via（默认 null）；ChildKey / LastSegment 改 internal 复用
src/CodeAtlas.Server/PipelineBuilder.cs           角色判定、泳道、流、DTO
src/CodeAtlas.Server/IndexService.cs              Pipeline(root) + 缓存清理
src/CodeAtlas.Server/ServerHost.cs                GET /api/pipeline
tests/CodeAtlas.Server.Tests/PipelineTests.cs     纯函数测试
tests/CodeAtlas.Server.Tests/ApiTests.cs          /api/pipeline 在纯 TS 仓库上的回退
extension/webview/types.ts                        PipelineDto 等
extension/webview/api.ts                          pipeline(root)
extension/webview/mapState.ts                     goToPath(root)
extension/webview/pipelineLayout.ts               DTO → 带坐标的元素（纯函数）
extension/webview/pipelineRenderer.ts             cytoscape 实例、样式、事件
extension/webview/main.ts                         视图切换、默认视图、详情条、钻取与聚焦
extension/webview/panel.css                       切换按钮与警示色
extension/test/mapState.test.ts                   goToPath
extension/test/pipelineLayout.test.ts             坐标、行内连线、流样式
docs/vscode-extension-guide.md                    §8 管道视图
docs/acceptance.md                                ccs-backend 实测
```

### Task 1: Core 读取 + MapEdge.Via（TDD）
- [x] `IndexReader.GetEdgeTriples()` 返回 `(FromId, ToId, Kind, Confidence, Via)`；`IndexService.Map` 适配。
- [x] `IndexReader.GetDiRegistrations()` 返回 `(InterfaceId, ImplId, FileId, Line)`（接口或实现为 NULL 的行跳过）。
- [x] `MapEdge` 增加 `string? Via = null`；现有 `MapTests` 不改即通过。
- [x] 证伪：Core 测试用内存库写带 via 的边与三条 DI 记录，断言读回的 via / 顺序 / 半解析跳过。

### Task 2: PipelineBuilder（TDD）
- [x] `PipelineTests` 10 个用例：自动 root、≥ 90% 下钻（含 90.4% / 89.5% 边界）、行键与每格计数、orphans、nested、flows、测试入口排除、行为链顺序（DI / 无 DI / 部分 DI）、显式 root 与叶子行、无 handles 时为空、`IsTest`。
- [x] 实现 `PipelineBuilder`。
- [x] 变异证伪（`scratchpad/falsify.ps1`）：M1 不排除测试 → 4 个 FAIL；M2 不自动下钻 → 2；M3 行为链按名字 → 2；M4 同行嵌套也画流 → 2；M5 下游不滤 0.3 → 3；恢复后 0。

### Task 3: 服务接线
- [x] `IndexService.Pipeline(root)`：与 `Map` 共用 `LoadAll`，按 root 缓存，索引结束与 `_mapCache` 一起清空。
- [x] `ServerHost`：`GET /api/pipeline`。
- [x] `ApiTests.Pipeline_OnScriptRepoWithoutDispatch_ReturnsEmptyRows`。

### Task 4: 页面（TDD 纯函数部分）
- [x] `types.ts` / `api.ts`。
- [x] `MapState.goToPath(root)` + 测试。
- [x] `pipelineLayout.ts` + 5 个测试（坐标、标签与警示、行内连线与流、确定性、叶子行）。
- [x] `pipelineRenderer.ts`：preset 布局；容器刚可见时先 `resize` 再 `fit`（否则左侧行标签被裁）。
- [x] `main.ts`：切换按钮、默认视图探测、两套 `MapState`、详情条（总数用 count 累加而不是数 items，items 只带 20 个代表项）、钻取与聚焦。
- [x] `panel.css`。
- [x] `npm run typecheck && npm test && npm run build` 通过（53 个 vitest）。

### Task 5: 文档与验收
- [x] `docs/vscode-extension-guide.md` §8.1 管道视图。
- [x] ccs-backend 实测记入 `docs/acceptance.md`（里程碑 4 节），含与取证数字的对账与实测中改掉的两条规则。
- [ ] 提交（带 pathspec，`git diff --cached --stat` 核对文件数）。
