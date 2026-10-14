import { describe, expect, it } from "vitest";
import { lastSegment, MapState } from "../webview/mapState";

describe("MapState", () => {
  it("starts at the top level and drills down building breadcrumbs", () => {
    const s = new MapState();
    expect(s.root).toBe("");
    s.drill("Acme");
    s.drill("Acme.Infra");
    expect(s.root).toBe("Acme.Infra");
    expect(s.crumbs.map((c) => c.label)).toEqual(["项目", "Acme", "Infra"]);
  });

  it("goTo a crumb truncates deeper levels; goTo top clears", () => {
    const s = new MapState();
    s.drill("Acme"); s.drill("Acme.Infra"); s.drill("Acme.Infra.Repo");
    s.goTo("Acme");
    expect(s.root).toBe("Acme");
    expect(s.crumbs).toHaveLength(2);
    s.goTo("");
    expect(s.root).toBe("");
  });

  it("up pops one level and is a no-op at top", () => {
    const s = new MapState();
    s.up();
    expect(s.root).toBe("");
    s.drill("src"); s.drill("src/app");
    s.up();
    expect(s.root).toBe("src");
  });

  it("goToPath expands an auto-derived root into one crumb per level", () => {
    const s = new MapState();
    s.goToPath("Acme.App.Modules");
    expect(s.root).toBe("Acme.App.Modules");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme", "Acme.App", "Acme.App.Modules"]);
    expect(s.crumbs.map((c) => c.label)).toEqual(["项目", "Acme", "App", "Modules"]);
    s.goTo("Acme.App");
    expect(s.root).toBe("Acme.App");
    s.goToPath("src/app");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "src", "src/app"]);
    s.goToPath("");
    expect(s.root).toBe("");
  });

  it("goToPathFrom uses the floor as the first level and hides levels above it", () => {
    const s = new MapState();
    s.goToPathFrom("Acme.App.Modules", "Acme.App.Modules.MT5.Accounts");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme.App.Modules", "Acme.App.Modules.MT5", "Acme.App.Modules.MT5.Accounts"]);
    expect(s.crumbs.map((c) => c.label)).toEqual(["项目", "Modules", "MT5", "Accounts"]);
    s.goToPathFrom("Acme.App.Modules", "Acme.App.Modules");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme.App.Modules"]);
    // root 不在 floor 之下（比如用户手动传了别的 root）：退化为完整路径
    s.goToPathFrom("Acme.App.Modules", "Acme.Other");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme", "Acme.Other"]);
    // 前缀相同但不是层级关系（Acme.App.ModulesX）不算在 floor 之下
    s.goToPathFrom("Acme.App.Modules", "Acme.App.ModulesX.Y");
    expect(s.crumbs.map((c) => c.root)).toEqual(["", "Acme", "Acme.App", "Acme.App.ModulesX", "Acme.App.ModulesX.Y"]);
  });

  it("lastSegment handles namespaces, paths and plain keys", () => {
    expect(lastSegment("Acme.Infra")).toBe("Infra");
    expect(lastSegment("src/app")).toBe("app");
    expect(lastSegment("src")).toBe("src");
  });
});
