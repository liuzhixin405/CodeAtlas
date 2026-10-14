import cytoscape from "cytoscape";
import { CellHeight, CellWidth, layoutPipeline, type CellKind, type NodeKind } from "./pipelineLayout";
import type { PipelineBehaviorDto, PipelineDto, PipelineFlowDto, PipelineRowDto } from "./types";

export interface PipelineRendererCallbacks {
  /** 点了某一行的某个格子；点空白处两个参数都是 null。 */
  onCellSelect(row: PipelineRowDto | null, kind: CellKind | null): void;
  onBehaviorsSelect(behaviors: PipelineBehaviorDto[]): void;
  onFlowSelect(flow: PipelineFlowDto | null): void;
  onCellDoubleClick(row: PipelineRowDto, kind: CellKind): void;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

/** 管道泳道图：位置由 layoutPipeline 算好（preset），这里只管样式与事件。与命名空间地图分开用一个 cytoscape 实例。 */
export class PipelineRenderer {
  private cy: cytoscape.Core;

  constructor(container: HTMLElement, cb: PipelineRendererCallbacks) {
    const fg = cssVar("--vscode-editor-foreground", "#333");
    const muted = cssVar("--vscode-disabledForeground", "#999");
    const accent = cssVar("--vscode-focusBorder", "#007acc");
    const cellBg = cssVar("--vscode-editorWidget-background", "#f3f3f3");
    const border = cssVar("--vscode-editorWidget-border", "#c8c8c8");
    const warn = cssVar("--vscode-editorWarning-foreground", "#d18616");
    const lane = cssVar("--vscode-editorWidget-border", "#c8c8c8");
    const nested = cssVar("--vscode-charts-orange", "#f59e0b");
    const bypass = cssVar("--vscode-errorForeground", "#f14c4c");

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
            "background-color": cellBg,
            "border-color": border,
            "border-width": 1.5,
            shape: "round-rectangle",
            width: CellWidth,
            height: CellHeight,
            "text-valign": "center",
            "text-halign": "center",
            "text-wrap": "wrap",
            "text-max-width": `${CellWidth - 12}px`,
            "line-height": 1.3,
          } as cytoscape.Css.Node,
        },
        { selector: "node.header", style: { "background-opacity": 0, "border-width": 0, height: 24, "font-weight": "bold", color: muted } as cytoscape.Css.Node },
        { selector: "node.label", style: { "background-opacity": 0, "border-width": 0, width: 160, "font-weight": "bold", "font-size": 12, "text-halign": "center" } as cytoscape.Css.Node },
        { selector: "node.warn", style: { "border-color": warn, "border-width": 2.5 } },
        { selector: "node:selected", style: { "border-color": accent, "border-width": 3 } },
        {
          selector: "edge",
          style: {
            "curve-style": "bezier",
            "target-arrow-shape": "triangle",
            width: 2,
            label: "data(label)",
            "font-size": 10,
            color: muted,
            "text-rotation": "autorotate",
            "text-margin-y": -8,
            "text-background-color": cellBg,
            "text-background-opacity": 0.8,
            "text-background-padding": "2px",
          } as cytoscape.Css.Edge,
        },
        { selector: "edge.lane", style: { "curve-style": "straight", "line-color": lane, "target-arrow-color": lane, width: 1.5, opacity: 0.7 } as cytoscape.Css.Edge },
        { selector: "edge.nested", style: { "line-color": nested, "target-arrow-color": nested, "line-style": "dashed", opacity: 0.9 } as cytoscape.Css.Edge },
        { selector: "edge.bypass", style: { "line-color": bypass, "target-arrow-color": bypass, width: 2.5, opacity: 0.9 } as cytoscape.Css.Edge },
        { selector: "edge:selected", style: { "line-color": accent, "target-arrow-color": accent, opacity: 1 } },
      ],
    });

    this.cy.on("tap", "node", (e) => {
      const kind = e.target.data("kind") as NodeKind;
      if (kind === "header") return;
      if (kind === "behaviors") { cb.onBehaviorsSelect(e.target.data("payload") as PipelineBehaviorDto[]); return; }
      cb.onCellSelect(e.target.data("payload") as PipelineRowDto, kind);
    });
    this.cy.on("dbltap", "node", (e) => {
      const kind = e.target.data("kind") as NodeKind;
      if (kind === "header" || kind === "behaviors") return;
      cb.onCellDoubleClick(e.target.data("payload") as PipelineRowDto, kind);
    });
    this.cy.on("tap", "edge", (e) => {
      const flow = e.target.data("payload") as PipelineFlowDto | null;
      if (flow) cb.onFlowSelect(flow);
    });
    this.cy.on("tap", (e) => { if (e.target === this.cy) { cb.onCellSelect(null, null); cb.onFlowSelect(null); } });
  }

  render(dto: PipelineDto): void {
    this.cy.elements().remove();
    const { nodes, edges } = layoutPipeline(dto);
    this.cy.add([
      ...nodes.map((n) => ({ group: "nodes" as const, data: n.data, position: n.position, classes: n.classes })),
      ...edges.map((e) => ({ group: "edges" as const, data: e.data, classes: e.classes })),
    ]);
    this.cy.layout({ name: "preset", fit: false } as cytoscape.LayoutOptions).run();
    this.fit();
  }

  /** 容器刚从 display:none 变可见时 cytoscape 还没量过尺寸，先 resize 再 fit，否则左侧的行标签会被裁掉。 */
  private fit(): void {
    this.cy.resize();
    this.cy.fit(this.cy.elements(), 30);
  }

  resize(): void {
    if (this.cy.elements().length > 0) this.fit();
    else this.cy.resize();
  }
}
