// 快速跳转命令：「转到调用方…」「转到被调方…」「转到受影响的入口…」。vscode 薄层；QuickPick 项与跳转位置在 jumpModel.ts。
// 对象是光标所在方法（与调用大纲 / 影响范围共用 CursorSource 的"光标 → 当前方法"，同一行有缓存就不再请求），
// 或 CodeLens 传进来的方法。跳转走 cursorFollow.openAt：showTextDocument 带 selection，进 VS Code 的前进 / 后退历史。
import * as vscode from "vscode";
import type { SymbolDto } from "../webview/types";
import { currentPos, openAt, type SharedFollow } from "./cursorFollow";
import { calleePickItems, callerPickItems, entryPickItems, type JumpPickItem } from "./jumpModel";
import type { ImpactClient, OutlineClient } from "./outlineFollow";
import { shortName } from "./outlineModel";

/** CodeLens 传给命令的参数；命令面板 / 快捷键调用时没有参数，用光标所在方法。 */
export interface JumpArgs {
  repoRoot: string;
  symbol: SymbolDto;
  /** 调用方里包含测试代码（CodeLens「测试 K」）。 */
  tests?: boolean;
}

export interface JumpHost {
  connect(root: string): Promise<(OutlineClient & ImpactClient) | null>;
  log(line: string): void;
}

type Kind = "callers" | "callees" | "entries";

export function registerJumpCommands(context: vscode.ExtensionContext, host: JumpHost, follow: SharedFollow): void {
  const run = (kind: Kind) => (args?: JumpArgs) => void runJump(kind, host, follow, args).catch((e) => {
    host.log(`快速跳转失败：${e}`);
    void vscode.window.showWarningMessage(`CodeTrail Graph：请求失败，详见输出面板「CodeTrail Graph」。`);
  });
  context.subscriptions.push(
    vscode.commands.registerCommand("codeatlas.gotoCallers", run("callers")),
    vscode.commands.registerCommand("codeatlas.gotoCallees", run("callees")),
    vscode.commands.registerCommand("codeatlas.gotoEntries", run("entries")),
  );
}

async function runJump(kind: Kind, host: JumpHost, follow: SharedFollow, args?: JumpArgs): Promise<void> {
  const subject = isJumpArgs(args) ? args : await cursorMethod(follow);
  if (!subject) return;
  const client = await host.connect(subject.repoRoot);
  if (!client) return; // 错误已报
  const name = shortName(subject.symbol);
  const qp = vscode.window.createQuickPick<PickItem>();
  qp.matchOnDescription = true;
  qp.matchOnDetail = true;
  qp.busy = true;
  qp.title = kind === "callers" ? `${name} 的调用方${subject.tests ? "（含测试）" : ""}` : kind === "callees" ? `${name} 调用了` : `改 ${name} 会影响的入口`;
  qp.placeholder = "加载中…";
  qp.show();
  let closed = false;
  qp.onDidHide(() => { closed = true; qp.dispose(); });

  const loaded = await load(kind, client, subject);
  if (closed) return;
  if (!loaded) { qp.hide(); void vscode.window.showInformationMessage("这个方法不在索引里了（重新索引过？），或服务版本过旧。"); return; }
  if (loaded.items.length === 0) { qp.hide(); void vscode.window.showInformationMessage(loaded.empty); return; }
  qp.items = loaded.items.map(toPickItem);
  qp.placeholder = loaded.placeholder;
  qp.busy = false;

  qp.onDidAccept(() => {
    const item = qp.selectedItems[0];
    qp.hide();
    if (item) void openAt(subject.repoRoot, item.jump.target, { preserveFocus: false });
  });
  qp.onDidTriggerItemButton((e) => {
    qp.hide();
    if (e.item.jump.definition) void openAt(subject.repoRoot, e.item.jump.definition, { preserveFocus: false });
  });
}

interface Loaded {
  items: JumpPickItem[];
  placeholder: string;
  /** 没有项时的提示。 */
  empty: string;
}

async function load(kind: Kind, client: OutlineClient & ImpactClient, subject: JumpArgs): Promise<Loaded | null> {
  const id = subject.symbol.id;
  if (kind === "callees") {
    // 与 CodeLens「调用 M 个」同一口径：调用大纲第一层，含框架调用
    const outline = await client.outline(id, { externals: true, tests: false });
    if (!outline) return null;
    return {
      items: calleePickItems(outline),
      placeholder: `按源码顺序 ${outline.steps.length} 步${outline.truncated ? "（太多，后面的省略了）" : ""}；选一个跳到调用处，右侧按钮转到定义`,
      empty: "这个方法里没有找到调用。",
    };
  }
  if (kind === "callers") {
    const tests = subject.tests ?? false;
    const impact = await client.impact(id, { entries: false, tests });
    if (!impact) return null;
    const testNote = !tests && impact.testCallers > 0 ? `；测试里还有 ${impact.testCallers} 个调用方（CodeLens「测试」看全部）` : "";
    return {
      items: callerPickItems(impact),
      placeholder: `${impact.callers.length} 个直接调用方${impact.truncated ? "（太多，后面的省略了）" : ""}${testNote}；选一个跳到第一处调用`,
      empty: impact.testCallers > 0 && !tests ? `没有找到调用方（不含测试；测试里有 ${impact.testCallers} 个）。` : "没有找到调用方：可能是死代码，也可能经反射 / 依赖注入 / 动态调用。",
    };
  }
  const impact = await client.impact(id, { entries: true, tests: false });
  if (!impact) return null;
  if (impact.entries === null) return { items: [], placeholder: "", empty: "服务版本过旧，还不支持受影响的入口。" };
  return {
    items: entryPickItems(impact.entries),
    placeholder: `${impact.entries.length} 个入口${impact.entriesTruncated ? "（太多，后面的省略了）" : ""}；按往上的层数排，选一个跳到入口方法`,
    empty: "往上没有找到入口：可能是死代码，或调用链在反射 / 依赖注入处断开了。",
  };
}

/** 光标所在方法；不在方法里 / 没索引 / 服务起不来时提示并返回 null。 */
async function cursorMethod(follow: SharedFollow): Promise<JumpArgs | null> {
  const pos = currentPos();
  if (!pos || !follow.source.isSupported(pos.filePath)) { flash("把光标放进一个方法再用"); return null; }
  const r = await follow.source.resolveFor(pos);
  switch (r.state) {
    case "method": return { repoRoot: r.repoRoot, symbol: r.symbol };
    case "noMethod": flash("光标不在已索引的方法里"); return null;
    case "ignore": flash("文件不在任何工作区文件夹或 codeatlas.projects 里"); return null;
    case "noIndex": {
      const pick = await vscode.window.showInformationMessage("这个工作区还没有 CodeTrail Graph 索引。", "建立索引");
      if (pick) void vscode.commands.executeCommand("codeatlas.indexWorkspace");
      return null;
    }
    case "noServer": return null; // 错误已报
    case "error": void vscode.window.showWarningMessage(`CodeTrail Graph：${r.message}`); return null;
  }
}

type PickItem = vscode.QuickPickItem & { jump: JumpPickItem };

const DefinitionButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon("go-to-file"), tooltip: "转到定义" };

function toPickItem(j: JumpPickItem): PickItem {
  return { label: j.label, description: j.description, detail: j.detail, buttons: j.definition ? [DefinitionButton] : [], jump: j };
}

function isJumpArgs(a: unknown): a is JumpArgs {
  return !!a && typeof a === "object" && typeof (a as JumpArgs).repoRoot === "string" && !!(a as JumpArgs).symbol && typeof (a as JumpArgs).symbol.id === "number";
}

function flash(message: string): void {
  vscode.window.setStatusBarMessage(`CodeTrail Graph：${message}`, 3000);
}
