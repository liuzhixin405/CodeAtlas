# CodeAtlas 里程碑 2c：VS Code 扩展壳 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 VS Code 里右键"在 CodeAtlas 中查看"就能打开面板看到光标处符号的邻域图；保存文件自动增量索引；一个 VSIX 装上即用。

**Architecture:** 扩展（`extension/src/`）是薄壳：`ServerManager` 为每个工作区文件夹拉起 `codeatlas serve` 子进程并从 stdout 读端口；`IndexScheduler` 把保存事件防抖成一次 `POST /api/index`，运行中再触发则置脏标记；`PanelHost` 承载 2b 的页面（CSP + nonce），转发 `open` / `requestIndex` 消息；状态栏显示符号数与索引进度。服务可执行文件按 `<扩展目录>/server/codeatlas.exe` → 设置 `codeatlas.serverPath` → PATH 的顺序查找，VSIX 打包时把 `dist/` 拷进 `server/`。

**Tech Stack:** VS Code API 1.100+、Node 22 `child_process` / `fetch`、esbuild、vitest（假计时器）、@vscode/vsce。

**Spec:** `docs/superpowers/specs/2026-10-13-vscode-extension-design.md` 第 4 节、里程碑 2c。

## Global Constraints

- 不依赖 `vscode` 模块的逻辑（端口解析、等待、调度）放在独立文件里，用 vitest 测；`extension.ts` / `panel.ts` 只做接线。
- 所有失败走状态栏与输出通道；唯一的模态提示是"找不到服务可执行文件"。
- 服务进程用 `--parent-pid` 让它在扩展宿主崩溃后自杀；deactivate 时主动 kill。
- Webview CSP：`default-src 'none'`，脚本带 nonce，`connect-src http://127.0.0.1:*`。

## Review Focus

1. **服务启动慢或失败**：5 秒内没读到端口行要失败并把 stderr 放进输出通道，不能挂死。→ `waitForPortLine_timesOut`。
2. **保存风暴**：连续保存 10 次只触发一次索引；索引进行中保存则结束后再跑一次。→ `scheduler_debounces` / `scheduler_rerunsWhenDirty`。
3. **光标不在符号上**：`symbols/at` 404 时状态栏提示，不弹窗、不报错。→ 手工验收。
4. **多根工作区**：右键时按文件所属文件夹选服务。→ 手工验收（代码路径按 `workspace.getWorkspaceFolder`）。
5. **扩展卸载/重载**：子进程不残留。→ 手工验收：Reload Window 后任务管理器无 codeatlas.exe。

## File Structure

```
extension/src/serverManager.ts   parsePortLine / waitForPortLine / findServerExecutable / ServerManager
extension/src/indexScheduler.ts  IndexScheduler（防抖 + 脏标记 + 运行回调）
extension/src/client.ts          扩展侧的 fetch 封装（health / symbolAt / startIndex / status）
extension/src/panel.ts           PanelHost（Webview 创建、HTML、消息）
extension/src/extension.ts       activate / deactivate / 命令 / 状态栏
extension/test/serverManager.test.ts, extension/test/indexScheduler.test.ts
extension/package.json           contributes（命令、菜单、快捷键、设置）、publisher、main
extension/.vscodeignore, extension/README.md, extension/LICENSE
extension/scripts/package.mjs    拷贝 ../dist → server/ 后调用 vsce package
```

### Task 1: 纯逻辑模块（TDD）
- [ ] 测试：`parsePortLine('{"port":123}') === 123`，非 JSON / 无 port 返回 null；`waitForPortLine(stream, 5000)` 在首行到达时 resolve，超时 reject；`IndexScheduler` 防抖、合并、脏标记重跑、`runNow()` 立即执行。
- [ ] 实现 → 通过 → 提交。

### Task 2: 扩展主体
- [ ] `client.ts`、`panel.ts`、`extension.ts`、`package.json` contributes；`npm run build` 产出 `dist/extension.js`；`tsc --noEmit` 干净。提交。

### Task 3: 打包与安装
- [ ] `scripts/package.mjs`：复制 `../dist` 到 `server/`，`vsce package` 产出 `codeatlas-0.1.0.vsix`。
- [ ] `code --install-extension codeatlas-0.1.0.vsix`，在 ccs-backend 上走：右键 → 面板 → 展开 → 胶囊 → 双击跳回 → 保存触发增量。记入 acceptance.md。
