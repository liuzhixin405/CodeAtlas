import cytoscape from "cytoscape";
import type { MapDto, MapEdgeDto, MapGroupDto } from "./types";

export interface MapRendererCallbacks {
  onGroupDoubleClick(group: MapGroupDto): void;
  onGroupSelect(group: MapGroupDto | null): void;
  onEdgeSelect(edge: MapEdgeDto | null): void;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

/** 项目地图：分组节点按符号数定大小，边按依赖数定粗细。与调用图分开用一个 cytoscape 实例。 */
export class MapRenderer {
  private cy: cytoscape.Core;

  constructor(container: HTMLElement, cb: MapRendererCallbacks) {
    const fg = cssVar("--vscode-editor-foreground", "#333");
    const muted = cssVar("--vscode-disabledForeground", "#999");
    const accent = cssVar("--vscode-focusBorder", "#007acc");
    const groupBg = cssVar("--vscode-editorWidget-background", "#f3f3f3");
    const border = cssVar("--vscode-editorWidget-border", "#c8c8c8");
    const typeBg = cssVar("--vscode-editor-background", "#fff");
    const edge = cssVar("--vscode-charts-blue", "#3b82f6");
    const dispatch = cssVar("--vscode-charts-orange", "#f59e0b");

    this.cy = cytoscape({
      container,
      wheelSensitivity: 0.25,
      minZoom: 0.2,
      maxZoom: 2.5,
      style: [
        {
          selector: "node",
          style: {
            label: "data(label)",
            "font-size": 12,
            "font-family": cssVar("--vscode-font-family", "system-ui, sans-serif"),
            color: fg,
            "background-color": groupBg,
            "border-color": border,
            "border-width": 1.5,
            shape: "round-rectangle",
            width: "data(size)",
            height: "data(size)",
            "text-valign": "center",
            "text-halign": "center",
            "text-wrap": "wrap",
            "text-max-width": "data(size)",
          } as cytoscape.Css.Node,
        },
        { selector: "node[?hasChildren]", style: { "border-width": 3, "border-style": "double" } },
        { selector: "node[?isType]", style: { "background-color": typeBg, shape: "rectangle" } },
        { selector: "node:selected", style: { "border-color": accent, "border-width": 3 } },
        {
          selector: "edge",
          style: {
            "curve-style": "bezier",
            "target-arrow-shape": "triangle",
            "line-color": edge,
            "target-arrow-color": edge,
            width: "data(width)",
            opacity: 0.75,
            label: "data(label)",
            "font-size": 10,
            color: muted,
            "text-rotation": "autorotate",
            "text-margin-y": -8,
          } as cytoscape.Css.Edge,
        },
        { selector: "edge[?hasDispatch]", style: { "line-color": dispatch, "target-arrow-color": dispatch } },
        { selector: "edge:selected", style: { "line-color": accent, "target-arrow-color": accent, opacity: 1 } },
      ],
    });

    this.cy.on("dbltap", "node", (e) => cb.onGroupDoubleClick(e.target.data("payload") as MapGroupDto));
    this.cy.on("tap", "node", (e) => cb.onGroupSelect(e.target.data("payload") as MapGroupDto));
    this.cy.on("tap", "edge", (e) => cb.onEdgeSelect(e.target.data("payload") as MapEdgeDto));
    this.cy.on("tap", (e) => { if (e.target === this.cy) { cb.onGroupSelect(null); cb.onEdgeSelect(null); } });
  }

  render(map: MapDto): void {
    this.cy.elements().remove();
    const maxSymbols = Math.max(1, ...map.groups.map((g) => g.symbols));
    const maxWeight = Math.max(1, ...map.edges.map((e) => e.weight));
    this.cy.add([
      ...map.groups.map((g) => ({
        group: "nodes" as const,
        data: {
          id: g.key,
          label: g.symbolId ? g.label : `${g.label}\n${g.symbols}`,
          size: 48 + Math.round(72 * Math.sqrt(g.symbols / maxSymbols)),
          hasChildren: g.hasChildren,
          isType: g.symbolId !== null,
          payload: g,
        },
      })),
      ...map.edges.map((e) => ({
        group: "edges" as const,
        data: {
          id: `${e.from}=>${e.to}`,
          source: e.from,
          target: e.to,
          width: 1 + Math.round(5 * Math.sqrt(e.weight / maxWeight)),
          label: String(e.weight),
          hasDispatch: (e.kinds["dispatches"] ?? 0) > 0,
          payload: e,
        },
      })),
    ]);
    const layout = this.cy.layout({
      name: "elk",
      animate: false,
      fit: false,
      elk: { algorithm: "layered", "elk.direction": "RIGHT", "elk.spacing.nodeNode": 30, "elk.layered.spacing.nodeNodeBetweenLayers": 90 },
    } as unknown as cytoscape.LayoutOptions);
    layout.one("layoutstop", () => {
      this.cy.one("render", () => {
        type FitApi = { getFitViewport(eles: cytoscape.Collection, padding: number): { zoom: number; pan: cytoscape.Position } | undefined };
        const target = (this.cy as unknown as FitApi).getFitViewport(this.cy.elements(), 40);
        if (target) this.cy.animate({ zoom: target.zoom, pan: target.pan, duration: 200 });
      });
    });
    layout.run();
  }

  resize(): void {
    this.cy.resize();
  }
}
