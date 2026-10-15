import cytoscape from "cytoscape";
import { layoutRelations, TierWidths, UnitHeight, UnitWidth, unitElementId, componentTitleId, componentTitlePosition } from "./relationsLayout";
import type { RelationComponentDto, RelationEdgeDto, RelationNodeDto, RelationsDto } from "./types";

export interface RelationsRendererCallbacks {
  onUnitSelect(node: RelationNodeDto): void;
  onUnitDoubleClick(node: RelationNodeDto): void;
  onUnitContext(node: RelationNodeDto, x: number, y: number): void;
  onComponentSelect(component: RelationComponentDto): void;
  onEdgeSelect(edge: RelationEdgeDto): void;
  onClear(): void;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

/**
 * 模块关系：位置全部由 layoutRelations 算好（preset），组件是 compound 父节点。
 * 边的颜色 / 线型按主关系，线宽按权重档，反向依赖红色描边；标签只在连着中心的边、以及选中节点的边上显示，避免满屏字。
 */
export class RelationsRenderer {
  private cy: cytoscape.Core;

  constructor(container: HTMLElement, cb: RelationsRendererCallbacks) {
    const fg = cssVar("--vscode-editor-foreground", "#333");
    const bg = cssVar("--vscode-editor-background", "#fff");
    const muted = cssVar("--vscode-disabledForeground", "#999");
    const accent = cssVar("--vscode-focusBorder", "#007acc");
    const widgetBg = cssVar("--vscode-editorWidget-background", "#f3f3f3");
    const border = cssVar("--vscode-editorWidget-border", "#c8c8c8");
    const calls = cssVar("--vscode-charts-blue", "#3b82f6");
    const types = cssVar("--vscode-charts-purple", "#a855f7");
    const dispatch = cssVar("--vscode-charts-orange", "#f59e0b");
    const danger = cssVar("--vscode-errorForeground", "#e5484d");

    this.cy = cytoscape({
      container,
      wheelSensitivity: 0.25,
      minZoom: 0.15,
      maxZoom: 2.5,
      autoungrabify: true,
      style: [
        {
          // 框本身不画字：标题由单独的 node.component-title 画（见 placeTitles），这样线上的文字压不住它
          selector: "node.component",
          style: {
            label: "",
            "z-index": 0,
            shape: "round-rectangle",
            "background-color": widgetBg,
            "background-opacity": 0.45,
            "border-color": border,
            "border-width": 1,
            padding: "14px",
          } as cytoscape.Css.Node,
        },
        { selector: "node.component.has-center", style: { "border-color": accent } },
        {
          // 组件标题：骑在框上边线上的标签，不透明底色，画在所有线与线上文字之上（z-index 最高、不参与淡出）
          selector: "node.component-title",
          style: {
            label: "data(label)",
            width: 1,
            height: 1,
            "background-opacity": 0,
            "border-width": 0,
            "font-size": 11,
            "font-weight": "bold",
            "font-family": cssVar("--vscode-font-family", "system-ui, sans-serif"),
            color: muted,
            "text-valign": "center",
            "text-halign": "center",
            "text-max-width": `${UnitWidth}px`,
            "text-wrap": "ellipsis",
            "text-background-color": bg,
            "text-background-opacity": 1,
            "text-background-padding": "3px",
            "text-background-shape": "round-rectangle",
            "text-border-color": border,
            "text-border-width": 1,
            "text-border-opacity": 1,
            "z-index": 100,
            "z-compound-depth": "top",
          } as unknown as cytoscape.Css.Node,
        },
        { selector: "node.component-title.has-center", style: { color: fg, "text-border-color": accent } as unknown as cytoscape.Css.Node },
        { selector: "node.component.external", style: { "border-style": "dashed", "background-opacity": 0.15 } as cytoscape.Css.Node },
        {
          selector: "node.unit",
          style: {
            label: "data(label)",
            "font-size": 11,
            "font-family": cssVar("--vscode-font-family", "system-ui, sans-serif"),
            color: fg,
            "background-color": bg,
            "border-color": border,
            "border-width": 1.2,
            shape: "round-rectangle",
            width: UnitWidth,
            height: UnitHeight,
            "text-valign": "center",
            "text-halign": "center",
            "text-max-width": `${UnitWidth - 14}px`,
            "text-wrap": "ellipsis",
            "z-index": 20,
          } as cytoscape.Css.Node,
        },
        { selector: "node.unit.italic", style: { "font-style": "italic" } as cytoscape.Css.Node },
        { selector: "node.unit.kind-module", style: { shape: "cut-rectangle" } as cytoscape.Css.Node },
        { selector: "node.unit.d2", style: { opacity: 0.8 } },
        { selector: "node.unit.external", style: { "border-style": "dashed", "border-color": muted, color: muted, "background-color": widgetBg } as cytoscape.Css.Node },
        { selector: "node.unit.center", style: { "border-color": accent, "border-width": 3, "font-weight": "bold" } as cytoscape.Css.Node },
        { selector: "node.unit:selected", style: { "overlay-color": accent, "overlay-opacity": 0.15, "overlay-padding": 4 } as cytoscape.Css.Node },
        { selector: "node.component:selected", style: { "border-color": accent, "border-width": 2 } },
        {
          selector: "edge",
          style: {
            "curve-style": "bezier",
            "target-arrow-shape": "triangle",
            "arrow-scale": 0.9,
            "line-color": calls,
            "target-arrow-color": calls,
            opacity: 0.85,
            label: "",
            "font-size": 9,
            color: muted,
            "text-rotation": "autorotate",
            "text-margin-y": -7,
            "text-background-color": bg,
            "text-background-opacity": 0.85,
            "text-background-padding": "2px",
            "z-index": 10,
          } as cytoscape.Css.Edge,
        },
        { selector: "edge.w1", style: { width: TierWidths[1] } },
        { selector: "edge.w2", style: { width: TierWidths[2] } },
        { selector: "edge.w3", style: { width: TierWidths[3] } },
        {
          selector: "edge.structural",
          style: { "line-color": types, "target-arrow-color": types, "line-style": "dashed", "line-dash-pattern": [6, 3], "target-arrow-shape": "triangle", "target-arrow-fill": "hollow", "arrow-scale": 1.4 } as cytoscape.Css.Edge,
        },
        { selector: "edge.dispatch", style: { "line-color": dispatch, "target-arrow-color": dispatch, "line-style": "dashed", "line-dash-pattern": [8, 3] } as cytoscape.Css.Edge },
        { selector: "edge.reference", style: { "line-color": muted, "target-arrow-color": muted, "line-style": "dotted", opacity: 0.7 } as cytoscape.Css.Edge },
        { selector: "edge.reverse", style: { "line-outline-width": 1.6, "line-outline-color": danger, color: danger } as unknown as cytoscape.Css.Edge },
        { selector: "edge.intra", style: { "curve-style": "unbundled-bezier", "control-point-distances": [-46], "control-point-weights": [0.5], "text-rotation": "none", "text-margin-y": 0, "text-margin-x": 0 } as cytoscape.Css.Edge },
        { selector: "edge.near, edge.hl, edge:selected", style: { label: "data(label)" } },
        { selector: ".faded", style: { opacity: 0.18 } },
        { selector: "edge:selected", style: { "line-color": accent, "target-arrow-color": accent, opacity: 1 } },
      ],
    });

    // 调试：浏览器模式下可在控制台用 __codeatlasRelCy 检查
    (window as unknown as { __codeatlasRelCy?: cytoscape.Core }).__codeatlasRelCy = this.cy;

    this.cy.on("tap", "node.unit", (e) => { this.highlight(e.target); cb.onUnitSelect(e.target.data("payload") as RelationNodeDto); });
    this.cy.on("tap", "node.component, node.component-title", (e) => { this.clearHighlight(); cb.onComponentSelect(e.target.data("payload") as RelationComponentDto); });
    this.cy.on("dbltap", "node.unit", (e) => cb.onUnitDoubleClick(e.target.data("payload") as RelationNodeDto));
    this.cy.on("cxttap", "node.unit", (e) => {
      const pos = e.renderedPosition ?? { x: 0, y: 0 };
      const rect = container.getBoundingClientRect();
      cb.onUnitContext(e.target.data("payload") as RelationNodeDto, rect.left + pos.x, rect.top + pos.y);
    });
    this.cy.on("tap", "edge", (e) => { this.clearHighlight(); cb.onEdgeSelect(e.target.data("payload") as RelationEdgeDto); });
    this.cy.on("tap", (e) => { if (e.target === this.cy) { this.clearHighlight(); cb.onClear(); } });
  }

  render(dto: RelationsDto): void {
    const { components, units, edges } = layoutRelations(dto);
    this.cy.edges().remove();
    this.cy.nodes().remove();
    this.cy.add([
      ...components.map((c) => ({ group: "nodes" as const, data: c.data, classes: c.classes })),
      ...units.map((u) => ({ group: "nodes" as const, data: u.data, position: u.position, classes: u.classes })),
      ...edges.map((e) => ({ group: "edges" as const, data: e.data, classes: e.classes })),
    ]);
    this.cy.layout({ name: "preset", fit: false } as cytoscape.LayoutOptions).run();
    this.placeTitles();
    this.fit();
  }

  /** 每个组件框上边线正中放一个标题节点（框的范围要等子节点就位后才知道）。 */
  private placeTitles(): void {
    const titles = this.cy.nodes("node.component").map((c) => ({
      group: "nodes" as const,
      data: { id: componentTitleId(c.id()), label: c.data("label"), payload: c.data("payload") },
      position: componentTitlePosition(c.boundingBox({ includeLabels: false, includeOverlays: false })),
      classes: ["component-title", c.hasClass("has-center") ? "has-center" : ""].filter(Boolean).join(" "),
      grabbable: false,
    }));
    this.cy.add(titles);
  }

  clear(): void {
    this.cy.edges().remove();
    this.cy.nodes().remove();
  }

  /** 选中一个单元（不触发回调）；不存在返回 false。 */
  selectUnit(unitId: number): boolean {
    const n = this.cy.getElementById(unitElementId(unitId));
    if (n.empty()) return false;
    this.cy.elements().unselect();
    n.select();
    this.highlight(n);
    return true;
  }

  /** 选中节点：它的边显示标签，其余单元与边淡出。 */
  private highlight(node: cytoscape.NodeSingular): void {
    this.clearHighlight();
    const keep = node.closedNeighborhood();
    keep.edges().addClass("hl");
    this.cy.elements(".unit").difference(keep).addClass("faded");
    this.cy.edges().difference(keep.edges()).addClass("faded");
  }

  private clearHighlight(): void {
    this.cy.elements().removeClass("faded hl");
  }

  private fit(): void {
    this.cy.resize();
    this.cy.fit(this.cy.elements(), 36);
  }

  resize(): void {
    if (this.cy.elements().length > 0) this.fit();
    else this.cy.resize();
  }
}
