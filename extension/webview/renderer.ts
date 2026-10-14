import cytoscape from "cytoscape";
import elk from "cytoscape-elk";
import type { Elements, GraphEdge, GraphNode } from "./graphState";

cytoscape.use(elk);

export interface RendererCallbacks {
  onNodeDoubleClick(node: GraphNode): void;
  onNodeContext(node: GraphNode, x: number, y: number): void;
  onNodeSelect(node: GraphNode | null): void;
  onEdgeSelect(edge: GraphEdge | null): void;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

/**
 * 按标签估算节点宽度（11px 字体），上限与 text-max-width 一致。
 * 不用 cytoscape 的 `width: "label"`：它已弃用，而且重画时删除并行边会回头查已删节点的样式，
 * 在 poolIndex 上抛 "Cannot read properties of undefined (reading 'index')"，整张图留在半删状态。
 */
export function labelWidth(label: string, bold = false): number {
  let w = 0;
  for (const ch of label) {
    if (ch.charCodeAt(0) > 0x2e7f) w += 11;            // 中日韩全角
    else if (/[A-Z_@#%&<>]/.test(ch)) w += 7.2;        // 大写与宽符号
    else if (/[il.,:;'|! ]/.test(ch)) w += 3.2;        // 窄字符
    else w += 6.2;
  }
  return Math.min(180, Math.max(24, Math.round(w * (bold ? 1.08 : 1))));
}

/** 只负责把 GraphState.toElements() 画出来。节点形状按 kind，线宽按 confidence。 */
export class Renderer {
  private cy: cytoscape.Core;

  constructor(container: HTMLElement, private readonly cb: RendererCallbacks) {
    const fg = cssVar("--vscode-editor-foreground", "#333");
    const bg = cssVar("--vscode-editor-background", "#fff");
    const muted = cssVar("--vscode-disabledForeground", "#999");
    const accent = cssVar("--vscode-focusBorder", "#007acc");
    const calls = cssVar("--vscode-charts-blue", "#3b82f6");
    const types = cssVar("--vscode-charts-purple", "#a855f7");
    const imports = cssVar("--vscode-charts-green", "#22c55e");
    const dispatch = cssVar("--vscode-charts-orange", "#f59e0b");
    const nodeBg = cssVar("--vscode-editorWidget-background", "#f3f3f3");
    const border = cssVar("--vscode-editorWidget-border", "#c8c8c8");

    this.cy = cytoscape({
      container,
      wheelSensitivity: 0.25,
      minZoom: 0.2,
      maxZoom: 2,
      style: [
        {
          selector: "node",
          style: {
            label: "data(label)",
            "font-size": 11,
            "font-family": cssVar("--vscode-font-family", "system-ui, sans-serif"),
            color: fg,
            "background-color": nodeBg,
            "border-color": border,
            "border-width": 1,
            "text-valign": "center",
            "text-halign": "center",
            width: "data(width)",
            height: 26,
            padding: "8px",
            shape: "round-rectangle",
            "text-max-width": "180px",
            "text-wrap": "ellipsis",
          } as cytoscape.Css.Node,
        },
        { selector: 'node[kind = "class"], node[kind = "struct"]', style: { shape: "rectangle" } },
        { selector: 'node[kind = "interface"]', style: { shape: "diamond", padding: "14px" } },
        { selector: 'node[kind = "namespace"], node[kind = "module"]', style: { shape: "hexagon" } },
        { selector: 'node[kind = "external"]', style: { "background-color": bg, "border-style": "dotted", color: muted } },
        { selector: 'node[type = "dangling"]', style: { "background-color": bg, "border-style": "dashed", color: muted } },
        { selector: 'node[type = "ambiguous"]', style: { "background-color": bg, "border-style": "dashed", "border-color": muted, color: muted, shape: "round-rectangle" } },
        { selector: 'node[?externalGroup]', style: { "background-color": bg, "border-style": "dotted", "border-color": muted, color: muted } },
        { selector: "node.dim", style: { opacity: 0.55 } },
        { selector: "node.focus", style: { "border-color": accent, "border-width": 2.5, "font-weight": "bold" } },
        { selector: "node.expanded", style: { "background-opacity": 0.6 } },
        { selector: "node:selected", style: { "overlay-color": accent, "overlay-opacity": 0.15, "overlay-padding": 4 } },
        {
          selector: "edge",
          style: {
            "curve-style": "bezier",
            "target-arrow-shape": "triangle",
            "arrow-scale": 0.9,
            width: 1.5,
            "line-color": calls,
            "target-arrow-color": calls,
            opacity: 0.85,
          },
        },
        { selector: 'edge[kind = "implements"], edge[kind = "inherits"]', style: { "line-color": types, "target-arrow-color": types, "target-arrow-shape": "triangle-tee" } },
        { selector: 'edge[kind = "references"]', style: { "line-color": muted, "target-arrow-color": muted } },
        { selector: 'edge[kind = "imports"]', style: { "line-color": imports, "target-arrow-color": imports } },
        {
          selector: 'edge[kind = "dispatches"]',
          style: { "line-color": dispatch, "target-arrow-color": dispatch, "line-style": "dashed", "line-dash-pattern": [8, 3], label: "data(label)", "font-size": 9, color: dispatch, "text-rotation": "autorotate", "text-margin-y": -6 } as cytoscape.Css.Edge,
        },
        { selector: "edge[confidence >= 0.9]", style: { width: 3 } },
        { selector: "edge[confidence >= 0.7][confidence < 0.9]", style: { width: 2 } },
        { selector: "edge[confidence < 0.7]", style: { width: 1.2, opacity: 0.7 } },
        { selector: "edge[?dashed]", style: { "line-style": "dashed" } },
        { selector: "edge.trunk", style: { width: 4, opacity: 1 } },
        { selector: "edge.dim", style: { opacity: 0.3 } },
        { selector: "edge:selected", style: { "line-color": accent, "target-arrow-color": accent, width: 3.5, opacity: 1 } },
      ],
    });

    // 调试：浏览器模式下可在控制台用 __codeatlasCy 检查
    (window as unknown as { __codeatlasCy?: cytoscape.Core }).__codeatlasCy = this.cy;

    this.cy.on("dbltap", "node", (e) => cb.onNodeDoubleClick(e.target.data("payload") as GraphNode));
    this.cy.on("cxttap", "node", (e) => {
      const pos = e.renderedPosition ?? { x: 0, y: 0 };
      const rect = container.getBoundingClientRect();
      cb.onNodeContext(e.target.data("payload") as GraphNode, rect.left + pos.x, rect.top + pos.y);
    });
    this.cy.on("tap", "node", (e) => cb.onNodeSelect(e.target.data("payload") as GraphNode));
    this.cy.on("tap", "edge", (e) => cb.onEdgeSelect(e.target.data("payload") as GraphEdge));
    this.cy.on("tap", (e) => {
      if (e.target === this.cy) {
        cb.onNodeSelect(null);
        cb.onEdgeSelect(null);
      }
    });
  }

  /** 全量替换元素并重新布局；调用者在左、被调用者在右（ELK direction RIGHT），调用链从左往右读。 */
  render(elements: Elements, focusId: string | null): void {
    const selected = new Set(this.cy.$(":selected").map((el) => el.id()));
    const hasTrunk = elements.edges.some((e) => e.trunk);
    // 先删边再删点：删边时 cytoscape 要查两端节点的样式，节点必须还在
    this.cy.edges().remove();
    this.cy.nodes().remove();
    this.cy.add([
      ...elements.nodes.map((n) => ({
        group: "nodes" as const,
        data: { id: n.id, label: n.label, width: labelWidth(n.label, n.id === focusId), kind: n.kind, type: n.type, externalGroup: Boolean(n.externalGroup), payload: n },
        classes: [n.id === focusId ? "focus" : "", n.expanded ? "expanded" : "", hasTrunk && !n.trunk && n.id !== focusId ? "dim" : ""].join(" ").trim(),
      })),
      ...elements.edges.map((e) => ({
        group: "edges" as const,
        data: { id: e.id, source: e.source, target: e.target, kind: e.kind, confidence: e.confidence, dashed: e.dashed, payload: e },
        classes: [e.trunk ? "trunk" : "", hasTrunk && !e.trunk ? "dim" : ""].join(" ").trim(),
      })),
    ]);
    for (const id of selected) this.cy.$id(id).select();

    // cytoscape-elk 的 fit 在 animate 模式下不可靠，布局结束后自己 fit
    const layout = this.cy.layout({
      name: "elk",
      animate: false,
      fit: false,
      elk: {
        algorithm: "layered",
        "elk.direction": "RIGHT",
        "elk.spacing.nodeNode": 18,
        "elk.layered.spacing.nodeNodeBetweenLayers": 70,
        "elk.layered.nodePlacement.strategy": "NETWORK_SIMPLEX",
      },
    } as unknown as cytoscape.LayoutOptions);
    layout.one("layoutstop", () => {
      // 标签宽度在首次渲染时才测量，布局刚结束时包围盒不准：等一帧渲染后再算视口并动画过去
      this.cy.one("render", () => {
      type FitApi = { getFitViewport(eles: cytoscape.Collection, padding: number): { zoom: number; pan: cytoscape.Position } | undefined };
      const target = (this.cy as unknown as FitApi).getFitViewport(this.cy.elements(), 30); // @types/cytoscape 的签名落后于运行时
      if (target) this.cy.animate({ zoom: target.zoom, pan: target.pan, duration: 200, easing: "ease-in-out" });
      });
    });
    layout.run();
  }

  resize(): void {
    this.cy.resize();
  }
}
