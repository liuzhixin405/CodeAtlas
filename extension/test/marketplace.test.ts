import { existsSync, readFileSync } from "node:fs";
import * as path from "node:path";
import { describe, expect, it } from "vitest";

interface Manifest {
  name: string;
  displayName: string;
  version: string;
  icon: string;
  repository: { url: string };
  contributes: {
    commands: { command: string; category: string }[];
    configuration: { properties: Record<string, unknown> };
    viewsContainers: { activitybar: { id: string; title: string; icon: string }[] };
  };
}

const extensionDir = path.join(__dirname, "..");
const manifest = JSON.parse(readFileSync(path.join(extensionDir, "package.json"), "utf8")) as Manifest;

describe("Marketplace branding", () => {
  it("keeps package and lockfile identity aligned", () => {
    const lock = JSON.parse(readFileSync(path.join(extensionDir, "package-lock.json"), "utf8")) as {
      name: string;
      version: string;
      packages: Record<string, { name?: string; version: string }>;
    };
    expect(manifest.name).toBe("codetrail-graph");
    expect(manifest.displayName).toBe("CodeTrail Graph");
    expect(lock.name).toBe(manifest.name);
    expect(lock.version).toBe(manifest.version);
    expect(lock.packages[""]?.name).toBe(manifest.name);
    expect(lock.packages[""]?.version).toBe(manifest.version);
    expect(manifest.repository.url).toBe("https://github.com/liuzhixin405/CodeAtlas.git");
  });

  it("ships a 256 by 256 PNG marketplace icon", () => {
    const icon = readFileSync(path.join(extensionDir, manifest.icon));
    expect(icon.subarray(0, 8).toString("hex")).toBe("89504e470d0a1a0a");
    expect(icon.subarray(12, 16).toString("ascii")).toBe("IHDR");
    expect(icon.readUInt32BE(16)).toBe(256);
    expect(icon.readUInt32BE(20)).toBe(256);
  });

  it("rebrands contributions without changing existing command and setting namespaces", () => {
    expect(manifest.contributes.commands.length).toBeGreaterThan(0);
    for (const command of manifest.contributes.commands) {
      expect(command.command).toMatch(/^codeatlas\./);
      expect(command.category).toBe(manifest.displayName);
    }
    const settings = Object.keys(manifest.contributes.configuration.properties);
    expect(settings.length).toBeGreaterThan(0);
    for (const setting of settings) expect(setting).toMatch(/^codeatlas\./);
    expect(manifest.contributes.viewsContainers.activitybar).toEqual([
      { id: "codeatlas", title: manifest.displayName, icon: "webview/icon.svg" },
    ]);
    expect(existsSync(path.join(extensionDir, "webview", "icon.svg"))).toBe(true);
  });
});
