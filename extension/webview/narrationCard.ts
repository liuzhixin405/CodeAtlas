import { isLoopbackEndpoint, serviceName, type WebviewCommand } from "../src/aiServices";
import { isKnownPreset, presetLabel } from "./presets";
import type { NarrationStatusDto, NarrativeDto, NarrativeKind, NarrativePointDto } from "./types";

// ---------- 纯函数（可测）----------

export type EvidenceKind = "node" | "edge" | "entry" | "sym" | "comp" | "cell" | "unknown";

export interface EvidenceRef {
  kind: EvidenceKind;
  /** 冒号后面的部分；unknown 时是整个 id。 */
  ref: string;
}

const EvidencePrefixes: EvidenceKind[] = ["node", "edge", "entry", "sym", "comp", "cell"];

/** 解析事实 id 的前缀：`node:Acme.Api` → { kind: "node", ref: "Acme.Api" }；不认识的前缀 → unknown。 */
export function evidenceKind(id: string): EvidenceRef {
  const colon = id.indexOf(":");
  if (colon > 0) {
    const prefix = id.slice(0, colon) as EvidenceKind;
    if (EvidencePrefixes.includes(prefix)) return { kind: prefix, ref: id.slice(colon + 1) };
  }
  return { kind: "unknown", ref: id };
}

/** `edge:<from>→<to>:<protocol>` 的 ref 部分拆成三段；协议是最后一个冒号之后（from / to 自己可能带冒号，如 external:database:postgresql）。 */
export function parseEdgeEvidence(ref: string): { from: string; to: string; protocol: string | null } | null {
  const arrow = ref.indexOf("→");
  if (arrow < 0) return null;
  const from = ref.slice(0, arrow);
  const rest = ref.slice(arrow + 1);
  const colon = rest.lastIndexOf(":");
  // to 本身形如 external:x:y 时，没有协议段就分不开；按契约协议总是有的
  return colon < 0 ? { from, to: rest, protocol: null } : { from, to: rest.slice(0, colon), protocol: rest.slice(colon + 1) };
}

/** 键的最后一段：外部系统先去掉 `external:<类别>:` 前缀，再取最后一个 `.` / `/` 之后。 */
function shortKey(k: string): string {
  const s = k.replace(/^external:[^:]*:/, "");
  return s.slice(Math.max(s.lastIndexOf("."), s.lastIndexOf("/")) + 1);
}

/** 证据小标签上的短文字。 */
export function evidenceLabel(id: string): string {
  const { kind, ref } = evidenceKind(id);
  switch (kind) {
    case "node": return ref.replace(/^external:[^:]*:/, "");
    case "edge": { const e = parseEdgeEvidence(ref); return e ? `${shortKey(e.from)} → ${shortKey(e.to)}` : ref; }
    case "entry": return `入口 #${ref}`;
    case "sym": return `符号 #${ref}`;
    case "comp": return shortKey(ref);
    case "cell": { const arrow = ref.indexOf("→"); return arrow < 0 ? ref : `${shortKey(ref.slice(0, arrow))} → ${shortKey(ref.slice(arrow + 1))}`; }
    default: return ref;
  }
}

export const KindLabels: Record<NarrativeKind, string> = { container: "容器", component: "组件", flow: "入口流程" };

function pad(n: number): string { return n < 10 ? `0${n}` : String(n); }

/** ISO 时间 → `YYYY-MM-DD HH:mm`（本地时区）；解析不了原样返回。 */
export function formatTime(iso: string | null): string {
  if (!iso) return "时间未知";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** 叙述卡上提示设置 key 的固定文案。 */
export const ApiKeyHint = "未设置 API Key（命令面板：CodeTrail Graph: 设置 AI API Key）";
const ApiKeyCommandHint = "（命令面板：CodeTrail Graph: 设置 AI API Key）";

/** 当前服务：「DeepSeek · deepseek-chat」；回环端点显示「本地 · 模型」，未知主机显示主机名。 */
export function describeService(status: NarrationStatusDto | null): string {
  if (!status) return "";
  const name = serviceName(status.provider, status.endpoint);
  return status.model ? `${name} · ${status.model}` : name;
}

/** 服务名的悬停说明：协议 + 完整端点。 */
export function serviceTooltip(status: NarrationStatusDto): string {
  const protocol = status.provider === "anthropic" ? "Anthropic Messages API" : "OpenAI 兼容协议";
  const extra = [status.concurrency !== undefined ? `并发 ${status.concurrency}` : "", status.includeSource === false ? "不发送源码" : ""].filter(Boolean).join(" · ");
  return `${protocol} · ${status.endpoint}${extra ? `\n${extra}` : ""}\n点击切换 AI 服务`;
}

/** 联网端点且服务进程没拿到 key。老服务没有 hasApiKey 字段时不判断。 */
export function missingApiKey(status: NarrationStatusDto | null): boolean {
  return Boolean(status && status.enabled && status.hasApiKey === false && !isLoopbackEndpoint(status.endpoint));
}

/** 错误文案里带 HTTP 401 / 403：key 无效或没有权限。 */
export function isAuthError(text: string | null | undefined): boolean {
  return Boolean(text && /(^|\D)(401|403)(\D|$)/.test(text));
}

/** 状态行要不要放「设置 API Key」按钮：不可用 / 失败且原因是 key。 */
export function needsApiKeyAction(dto: NarrativeDto | null | undefined, status: NarrationStatusDto | null): boolean {
  if (!dto) return false;
  if (dto.status === "unavailable") return missingApiKey(status) || isAuthError(dto.error);
  if (dto.status === "failed") return isAuthError(dto.error);
  return false;
}

/** 卡上有可看的旧内容（摘要 / 关键点 / 风险任一非空）；重算期间服务端照样返回这些。 */
export function hasContent(dto: NarrativeDto | null | undefined): boolean {
  return Boolean(dto && (dto.summary || dto.keyPoints.length > 0 || dto.risks.length > 0));
}

function isBusy(dto: NarrativeDto | null | undefined): boolean {
  return Boolean(dto && (dto.status === "pending" || dto.status === "running"));
}

/**
 * ready 时的状态行：「由 qwen3.6-27b 生成 · 2026-10-14 20:40 · commit a1b2c3d」。
 * 生成它的模型与当前服务的模型不同时追加「（当前服务：deepseek-chat）」——换模型不算过期，只说明一下。
 * 老服务没有 gitCommit 字段时不显示 commit 段。
 */
export function describeProvenance(dto: NarrativeDto, status: NarrationStatusDto | null): string {
  const parts = [`由 ${dto.model ?? "未知模型"} 生成`, formatTime(dto.generatedAt)];
  if (dto.gitCommit) parts.push(`commit ${dto.gitCommit}`);
  const text = parts.join(" · ");
  return dto.model && status?.model && status.model !== dto.model ? `${text}（当前服务：${status.model}）` : text;
}

/**
 * 过期提示条：stale 时返回文案，否则 null。重算中（pending / running）也返回 null——状态行已经在说「正在更新…」。
 * 服务端没给原因时按改动文件数兜底，再兜底为「相关代码有变化」。
 */
export function staleBanner(dto: NarrativeDto | null | undefined): { text: string } | null {
  if (!dto?.stale || isBusy(dto)) return null;
  const reason = dto.staleReason?.trim()
    || (dto.changedFiles && dto.changedFiles > 0 ? `生成后 ${dto.changedFiles} 个相关文件有改动` : "相关代码有变化");
  return { text: `可能过期：${reason}` };
}

/**
 * 状态行文案。dto 为 null 表示服务返回 404（没有叙述路由）。
 * 有旧内容且在排队 / 生成时显示「正在更新…」，正文保留。
 * unavailable 的原因优先看全局状态：未启用 → 端点连不上 → 未设置 key → 服务端给的 error（401/403 附命令提示）。
 */
export function describeStatus(dto: NarrativeDto | null, status: NarrationStatusDto | null): string {
  if (dto === null) return "服务版本太旧，没有叙述接口；重启服务或更新扩展";
  if (isBusy(dto) && hasContent(dto)) return "正在更新…";
  switch (dto.status) {
    case "ready": return describeProvenance(dto, status);
    case "pending": return status && status.queued > 0 ? `排队中（队列 ${status.queued}）…` : "排队中…";
    case "running": return "生成中…";
    case "failed": return `生成失败：${dto.error ?? "未知原因"}${isAuthError(dto.error) ? ApiKeyCommandHint : ""}`;
    case "unavailable":
      if (status && !status.enabled) return "AI 未启用（设置 codeatlas.ai.enabled）";
      if (status && status.reachable === false) return `端点连不上：${status.endpoint}`;
      if (missingApiKey(status)) return ApiKeyHint;
      if (isAuthError(dto.error)) return `${dto.error}${ApiKeyCommandHint}`;
      return dto.error ?? "AI 不可用";
    default: return String(dto.status);
  }
}

/** 底部一行：队列 / 运行中 / 完成 / 失败 / 已暂停。服务名单独一行，见 describeService。 */
export function describeQueue(status: NarrationStatusDto | null): string {
  if (!status) return "";
  const parts: string[] = [];
  if (status.paused) parts.push("已暂停");
  if (status.queued > 0) parts.push(`队列 ${status.queued}`);
  if (status.running) parts.push(`正在生成 ${shortKey(status.running)}`);
  if (status.done > 0) parts.push(`完成 ${status.done}`);
  if (status.failed > 0) parts.push(`失败 ${status.failed}`);
  return parts.join(" · ") || (status.enabled ? "就绪" : "AI 未启用");
}

/** 「生成 / 重新生成」按钮：ready（或失败但还有旧内容）时是重新生成（force）；排队 / 生成中禁用；404 时不显示。 */
export function generateButtonState(dto: NarrativeDto | null): { label: string; force: boolean; disabled: boolean; visible: boolean } {
  if (dto === null) return { label: "生成", force: false, disabled: true, visible: false };
  const busy = isBusy(dto);
  const regen = dto.status === "ready" || (!busy && hasContent(dto));
  return { label: regen ? "重新生成" : busy ? "生成中" : "生成", force: regen, disabled: busy, visible: true };
}

// ---------- 组件 ----------

export interface NarrationCardCallbacks {
  fetch(kind: NarrativeKind, key: string): Promise<NarrativeDto | null>;
  generate(kind: NarrativeKind, key: string, force: boolean): Promise<NarrativeDto | null>;
  status(): Promise<NarrationStatusDto | null>;
  control(action: "stop" | "resume"): Promise<NarrationStatusDto | null>;
  /** 点了一个证据标签。 */
  onEvidence(ref: EvidenceRef, raw: string): void;
  /** 点了「按 AI 建议排版」。 */
  onApplyLayout(kind: NarrativeKind, layout: string): void;
  /** 当前该粒度用的预设 id，用来决定要不要显示排版按钮。 */
  currentPreset(kind: NarrativeKind): string;
  onToggle(collapsed: boolean): void;
  /** 让扩展执行一条白名单命令（设置 API Key / 选择 AI 服务）。 */
  runCommand(command: WebviewCommand): void;
}

/** 轮询间隔：排队 / 生成中每 3 秒再读一次，直到终态。 */
export const PollIntervalMs = 3000;

interface Target { kind: NarrativeKind; key: string; title: string }

/**
 * 右侧叙述卡：一个对象（容器 / 组件 / 入口流程）的 AI 一段话 + 关键点 + 风险 + 版式建议。
 * 纯 DOM；数据和跳转都经回调交给 main.ts。
 */
export class NarrationCard {
  private readonly body: HTMLElement;
  private readonly toggleBtn: HTMLButtonElement;
  private target: Target | null = null;
  private dto: NarrativeDto | null | undefined = undefined;
  private statusDto: NarrationStatusDto | null = null;
  private pollTimer = 0;
  private seq = 0;
  private collapsed = false;

  constructor(private readonly root: HTMLElement, private readonly cb: NarrationCardCallbacks) {
    root.classList.add("narration");
    root.innerHTML = "";
    this.toggleBtn = document.createElement("button");
    this.toggleBtn.className = "nc-toggle";
    this.toggleBtn.onclick = () => this.setCollapsed(!this.collapsed);
    this.body = document.createElement("div");
    this.body.className = "nc-body";
    root.append(this.toggleBtn, this.body);
    this.setCollapsed(false);
    this.clear();
  }

  get isCollapsed(): boolean { return this.collapsed; }

  /** 当前显示的对象粒度；空态为 null。 */
  get currentKind(): NarrativeKind | null { return this.target?.kind ?? null; }

  setCollapsed(v: boolean): void {
    this.collapsed = v;
    this.root.classList.toggle("collapsed", v);
    this.toggleBtn.textContent = v ? "‹" : "›";
    this.toggleBtn.title = v ? "展开叙述卡" : "收起叙述卡";
    this.cb.onToggle(v);
  }

  /** 没有选中对象时的空态。 */
  clear(hint = "单击一个容器、组件行头或入口，这里显示它的 AI 叙述。"): void {
    this.stopPolling();
    this.target = null;
    this.dto = undefined;
    this.body.innerHTML = `<div class="nc-head"><span class="nc-title muted">叙述</span></div><p class="nc-empty">${esc(hint)}</p>`;
    void this.refreshStatus();
  }

  /** 切到一个对象：先 GET 已有叙述（不自动 POST），pending / running 就轮询。 */
  show(kind: NarrativeKind, key: string, title: string): void {
    if (this.target && this.target.kind === kind && this.target.key === key) { this.target.title = title; this.render(); return; }
    this.stopPolling();
    this.target = { kind, key, title };
    this.dto = undefined;
    this.render();
    void this.load();
  }

  /** 预设变了之后重画（排版按钮的显隐取决于当前预设）。 */
  refresh(): void { this.render(); }

  private stopPolling(): void {
    window.clearTimeout(this.pollTimer);
    this.pollTimer = 0;
    this.seq++;
  }

  private async load(): Promise<void> {
    const t = this.target;
    if (!t) return;
    const mySeq = ++this.seq;
    let dto: NarrativeDto | null;
    try {
      dto = await this.cb.fetch(t.kind, t.key);
    } catch (e) {
      if (mySeq !== this.seq) return;
      this.dto = this.failedDto(t, `读取失败：${String(e)}`);
      this.render();
      return;
    }
    if (mySeq !== this.seq) return; // 等待期间换了对象
    this.dto = dto;
    await this.refreshStatus();
    if (mySeq !== this.seq) return;
    this.render();
    this.schedulePoll(mySeq);
  }

  private schedulePoll(mySeq: number): void {
    const s = this.dto?.status;
    if (s !== "pending" && s !== "running") return;
    this.pollTimer = window.setTimeout(() => { if (mySeq === this.seq) void this.load(); }, PollIntervalMs);
  }

  private async refreshStatus(): Promise<void> {
    try { this.statusDto = await this.cb.status(); } catch { this.statusDto = null; }
  }

  private async generate(force: boolean): Promise<void> {
    const t = this.target;
    if (!t) return;
    this.stopPolling();
    const mySeq = ++this.seq;
    try {
      const dto = await this.cb.generate(t.kind, t.key, force);
      if (mySeq !== this.seq) return;
      this.dto = dto;
    } catch (e) {
      if (mySeq !== this.seq) return;
      this.dto = this.failedDto(t, String(e));
    }
    await this.refreshStatus();
    if (mySeq !== this.seq) return;
    this.render();
    this.schedulePoll(mySeq);
  }

  /** 请求本身失败时的占位；已有旧内容就保留它，只把状态改成失败。 */
  private failedDto(t: Target, error: string): NarrativeDto {
    const prev = this.dto;
    if (prev && hasContent(prev) && prev.kind === t.kind && prev.key === t.key) return { ...prev, status: "failed", error };
    return { kind: t.kind, key: t.key, status: "failed", model: null, generatedAt: null, hash: null, summary: null, keyPoints: [], risks: [], layout: null, error };
  }

  private async control(action: "stop" | "resume"): Promise<void> {
    try { this.statusDto = await this.cb.control(action); } catch { /* 底部状态保持 */ }
    this.render();
  }

  // ---------- 渲染 ----------

  private render(): void {
    const t = this.target;
    if (!t) return;
    const dto = this.dto;
    const body = this.body;
    body.innerHTML = "";

    const head = document.createElement("div");
    head.className = "nc-head";
    head.innerHTML = `<span class="kind">${esc(KindLabels[t.kind])}</span><span class="nc-title" title="${esc(t.key)}">${esc(t.title)}</span>`;
    if (dto !== undefined) {
      const g = generateButtonState(dto);
      if (g.visible) {
        const btn = document.createElement("button");
        btn.className = "secondary nc-gen";
        btn.textContent = g.label;
        btn.disabled = g.disabled;
        btn.title = g.force ? "忽略缓存，重新让模型写一遍" : "把这个对象排进生成队列";
        btn.onclick = () => void this.generate(g.force);
        head.appendChild(btn);
      }
    }
    body.appendChild(head);

    const status = document.createElement("div");
    if (dto === undefined) {
      status.className = "nc-status muted";
      status.innerHTML = `<span class="nc-spin"></span>读取中…`;
      body.appendChild(status);
      body.appendChild(this.foot());
      return;
    }
    const st = dto?.status ?? "unavailable";
    status.className = `nc-status st-${dto === null ? "unavailable" : st}`;
    const spin = st === "pending" || st === "running" ? `<span class="nc-spin"></span>` : "";
    status.innerHTML = `${spin}<span>${esc(describeStatus(dto, this.statusDto))}</span>`;
    if (dto && st === "failed") {
      const retry = document.createElement("button");
      retry.className = "secondary nc-retry";
      retry.textContent = "重试";
      retry.onclick = () => void this.generate(true);
      status.appendChild(retry);
    }
    if (needsApiKeyAction(dto, this.statusDto)) {
      const keyBtn = document.createElement("button");
      keyBtn.className = "secondary nc-retry";
      keyBtn.textContent = "设置 API Key";
      keyBtn.title = "运行「CodeTrail Graph: 设置 AI API Key」；保存后服务自动重启";
      keyBtn.onclick = () => this.cb.runCommand("codeatlas.ai.setApiKey");
      status.appendChild(keyBtn);
    }
    body.appendChild(status);

    if (dto) {
      // 过期：内容照常显示、不变灰，只在提示条里说明；「更新」= 忽略缓存重算
      const banner = staleBanner(dto);
      if (banner) {
        const bar = document.createElement("div");
        bar.className = "nc-stale";
        const text = document.createElement("span");
        text.className = "nc-stale-text";
        text.textContent = banner.text;
        const upd = document.createElement("button");
        upd.className = "secondary nc-stale-btn";
        upd.textContent = "更新";
        upd.title = dto.gitCommit ? `按当前代码重新生成（旧结果生成于 commit ${dto.gitCommit}）` : "按当前代码重新生成";
        upd.onclick = () => void this.generate(true);
        bar.append(text, upd);
        body.appendChild(bar);
      }
      if (dto.summary) {
        const p = document.createElement("p");
        p.className = "nc-summary";
        p.textContent = dto.summary;
        body.appendChild(p);
      } else if (st === "unavailable" || st === "failed") {
        // 原因已在状态行
      } else if (st === "ready") {
        body.insertAdjacentHTML("beforeend", `<p class="nc-empty">模型没有给出摘要。</p>`);
      }
      if (dto.keyPoints.length > 0) body.append(this.section("关键点", dto.keyPoints, "nc-points"));
      if (dto.risks.length > 0) body.append(this.section("风险", dto.risks, "nc-risks"));
      if (isKnownPreset(t.kind, dto.layout) && dto.layout !== this.cb.currentPreset(t.kind)) {
        const layout = dto.layout;
        const btn = document.createElement("button");
        btn.className = "nc-layout";
        btn.textContent = `按 AI 建议排版：${presetLabel(t.kind, layout)}`;
        btn.title = "AI 只在预设版式里选，不自己画；点一下等于在「版式」下拉里选它";
        btn.onclick = () => this.cb.onApplyLayout(t.kind, layout);
        body.appendChild(btn);
      }
    }
    body.appendChild(this.foot());
  }

  private section(title: string, points: NarrativePointDto[], cls: string): HTMLElement {
    const wrap = document.createElement("div");
    wrap.className = `nc-section ${cls}`;
    wrap.innerHTML = `<h4>${esc(title)}</h4>`;
    const ul = document.createElement("ul");
    for (const p of points) {
      const li = document.createElement("li");
      li.className = p.inferred ? "inferred" : "";
      const text = document.createElement("span");
      text.className = "nc-text";
      text.textContent = p.text;
      li.appendChild(text);
      if (p.inferred) li.insertAdjacentHTML("beforeend", `<span class="nc-tag" title="引用对不上事实子图，是模型自己推的">推断</span>`);
      for (const ev of p.evidence) {
        const ref = evidenceKind(ev);
        const chip = document.createElement("span");
        chip.className = `nc-ev ev-${ref.kind}`;
        chip.textContent = evidenceLabel(ev);
        chip.title = ev;
        chip.onclick = () => this.cb.onEvidence(ref, ev);
        li.appendChild(chip);
      }
      ul.appendChild(li);
    }
    wrap.appendChild(ul);
    return wrap;
  }

  private foot(): HTMLElement {
    const foot = document.createElement("div");
    foot.className = "nc-foot";
    const s = this.statusDto;
    if (s?.enabled) {
      const svc = document.createElement("a");
      svc.className = "nc-service";
      svc.textContent = describeService(s);
      svc.title = serviceTooltip(s);
      svc.onclick = () => this.cb.runCommand("codeatlas.ai.selectService");
      foot.appendChild(svc);
    }
    const text = document.createElement("span");
    text.className = "muted";
    text.textContent = s ? describeQueue(s) : (this.dto === null ? "" : "状态未知");
    foot.appendChild(text);
    if (s && (s.queued > 0 || s.running || s.paused)) {
      const a = document.createElement("a");
      a.textContent = s.paused ? "继续" : "暂停";
      a.title = s.paused ? "继续处理队列" : "停下队列（已排队的保留）";
      a.onclick = () => void this.control(s.paused ? "resume" : "stop");
      foot.appendChild(a);
    }
    // 状态行已经在提示 key 时不重复
    if (missingApiKey(s) && !needsApiKeyAction(this.dto, s)) {
      const hint = document.createElement("div");
      hint.className = "nc-keyhint";
      const t = document.createElement("span");
      t.textContent = ApiKeyHint;
      const a = document.createElement("a");
      a.textContent = "设置";
      a.title = "运行「CodeTrail Graph: 设置 AI API Key」";
      a.onclick = () => this.cb.runCommand("codeatlas.ai.setApiKey");
      hint.append(t, a);
      foot.appendChild(hint);
    }
    if (s?.remoteWarning) {
      const warn = document.createElement("div");
      warn.className = "nc-remote";
      warn.textContent = s.remoteWarning;
      warn.title = `端点不是本机回环地址：对象的事实子图${s.includeSource === false ? "" : "和入口源码"}会发到那里；请确认符合公司数据政策`;
      foot.appendChild(warn);
    } else if (s?.lastError && !s.running) {
      const err = document.createElement("div");
      err.className = "muted nc-lasterr";
      err.textContent = `上次错误：${s.lastError}`;
      foot.appendChild(err);
    }
    return foot;
  }
}

function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
