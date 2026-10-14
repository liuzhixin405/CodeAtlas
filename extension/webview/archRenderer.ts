import cytoscape from "cytoscape";
import { ArchNodeHeight, ArchNodeWidth, layoutArch, type ArchPreset } from "./archLayout";
import type { ArchDto, ArchEdgeDto, ArchNodeDto } from "./types";

export interface ArchRendererCallbacks {
  onNodeSelect(node: ArchNodeDto | null): void;
  onEdgeSelect(edge: ArchEdgeDto | null): void;
  onContainerDoubleClick(node: ArchNodeDto): void;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

/** L1 容器图：位置由 layoutArch 算好（preset）。容器实底、外部系统虚框、使用者圆角；边按匹配档位实线 / 虚线。 */
export class ArchRenderer {
  private cy: cytoscape.Core;

  constructor(container: HTMLElement, cb: ArchRendererCallbacks) {
    const fg = cssVar("--vscode-editor-foreground", "#333");
    const muted = cssVar("--vscode-disabledForeground", "#999");
    const accent = cssVar("--vscode-focusBorder", "#007acc");
    const containerBg = cssVar("--vscode-button-background", "#007acc");
    const containerFg = cssVar("--vscode-button-foreground", "#fff");
    const widgetBg = cssVar("--vscode-editorWidget-background", "#f3f3f3");
    const border = cssVar("--vscode-editorWidget-border", "#c8c8c8");
    const edgeColor = cssVar("--vscode-charts-blue", "#3b82f6");
    const inbound = cssVar("--vscode-charts-orange", "#f59e0b");
    const declared = cssVar("--vscode-charts-purple", "#a855f7");

    this.cy = cytoscape({
      container,
      wheelSensitivity: 0.25,
      minZoom: 0.2,
      maxZoom: 2.5,
      autoungrabify: true,
      style: [
        {
          selector: "node",
          style: {
            label: "data(label)",
            "font-size": 11,
            "font-family": cssVar("--vscode-font-family", "system-ui, sans-serif"),
            color: fg,
            "background-color": widgetBg,
            "border-color": border,
            "border-width": 1.5,
            "border-style": "dashed",
            shape: "round-rectangle",
            width: ArchNodeWidth,
            height: ArchNodeHeight,
            "text-valign": "center",
            "text-halign": "center",
            "text-wrap": "wrap",
            "text-max-width": `${ArchNodeWidth - 14}px`,
            "line-height": 1.3,
          } as cytoscape.Css.Node,
        },
        { selector: "node.container", style: { "background-color": containerBg, color: containerFg, "border-style": "solid", "border-color": containerBg, "font-weight": "bold" } as cytoscape.Css.Node },
        { selector: "node.frontend", style: { "background-color": widgetBg, color: fg, "border-style": "solid", "border-color": containerBg, "border-width": 2.5 } as cytoscape.Css.Node },
        { selector: "node.person", style: { shape: "ellipse", width: 120, height: 60, "border-style": "solid" } as cytoscape.Css.Node },
        { selector: "node.declared", style: { "border-color": declared } },
        { selector: "node:selected", style: { "border-color": accent, "border-width": 3, "border-style": "solid" } },
        {
          selector: "edge",
          style: {
            "curve-style": "bezier",
            "target-arrow-shape": "triangle",
            "line-color": edgeColor,
            "target-arrow-color": edgeColor,
            width: 2,
            label: "data(label)",
            "font-size": 9,
            color: muted,
            "text-rotation": "autorotate",
            "text-margin-y": -7,
            "text-background-color": cssVar("--vscode-editor-background", "#fff"),
            "text-background-opacity": 0.85,
            "text-background-padding": "2px",
          } as cytoscape.Css.Edge,
        },
        { selector: "edge.weak", style: { "line-style": "dashed", opacity: 0.7, width: 1.5 } as cytoscape.Css.Edge },
        { selector: "edge.inbound", style: { "line-color": inbound, "target-arrow-color": inbound } },
        { selector: "edge.declared", style: { "line-color": declared, "target-arrow-color": declared, "line-style": "dotted" } as cytoscape.Css.Edge },
        { selector: "edge:selected", style: { "line-color": accent, "target-arrow-color": accent, opacity: 1, width: 3 } },
      ],
    });

    this.cy.on("tap", "node", (e) => cb.onNodeSelect(e.target.data("payload") as ArchNodeDto));
    this.cy.on("dbltap", "node", (e) => { const n = e.target.data("payload") as ArchNodeDto; if (n.kind === "container") cb.onContainerDoubleClick(n); });
    this.cy.on("tap", "edge", (e) => cb.onEdgeSelect(e.target.data("payload") as ArchEdgeDto));
    this.cy.on("tap", (e) => { if (e.target === this.cy) { cb.onNodeSelect(null); cb.onEdgeSelect(null); } });
  }

  /** preset：`columns`（默认）或 `topological`，见 layoutArch。 */
  render(dto: ArchDto, preset: ArchPreset | string = "columns"): void {
    this.cy.elements().remove();
    const { nodes, edges } = layoutArch(dto, preset);
    this.cy.add([
      ...nodes.map((n) => ({ group: "nodes" as const, data: n.data, position: n.position, classes: n.classes })),
      ...edges.map((e) => ({ group: "edges" as const, data: e.data, classes: e.classes })),
    ]);
    this.cy.layout({ name: "preset", fit: false } as cytoscape.LayoutOptions).run();
    this.fit();
  }

  /** 叙述卡点 `node:<键>` 证据：选中并居中该节点。不触发 onNodeSelect（那是 tap 事件），详情条由调用方更新。不存在返回 false。 */
  selectNode(key: string): boolean {
    const n = this.cy.getElementById(key);
    if (n.empty()) return false;
    this.cy.elements().unselect();
    n.select();
    this.cy.animate({ center: { eles: n } }, { duration: 200 });
    return true;
  }

  /** 叙述卡点 `edge:<from>→<to>:<protocol>` 证据：选中该边。协议为空时选两点之间任意一条。 */
  selectEdge(from: string, to: string, protocol: string | null): boolean {
    const edges = protocol !== null
      ? this.cy.getElementById(`${from}=>${to}:${protocol}`)
      : this.cy.edges().filter((e) => e.data("source") === from && e.data("target") === to);
    if (edges.empty()) return false;
    this.cy.elements().unselect();
    edges.select();
    this.cy.animate({ center: { eles: edges } }, { duration: 200 });
    return true;
  }

  private fit(): void {
    this.cy.resize();
    this.cy.fit(this.cy.elements(), 40);
  }

  resize(): void {
    if (this.cy.elements().length > 0) this.fit();
    else this.cy.resize();
  }
}
