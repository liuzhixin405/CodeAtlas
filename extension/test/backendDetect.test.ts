import * as path from "node:path";
import { describe, expect, it } from "vitest";
import {
  containsSolution, detectBackendsToSuggest, findBackendCandidates, hasFrontendDependency, looksLikeFrontend,
  projectsWithCandidate, readRepoConfigProjects, relativeProjectPath, suggestionMessage, type DetectFs, type PromptContext,
} from "../src/backendDetect";

const p = path.posix;

/** 假文件系统：键是 posix 绝对路径，值是文件内容；目录由路径前缀推出来。 */
function fakeFs(files: Record<string, string>): DetectFs & { reads: string[] } {
  const reads: string[] = [];
  return {
    reads,
    async readDir(dir) {
      reads.push(dir);
      const prefix = dir.endsWith("/") ? dir : dir + "/";
      const seen = new Map<string, boolean>();
      for (const f of Object.keys(files)) {
        if (!f.startsWith(prefix)) continue;
        const rest = f.slice(prefix.length);
        const slash = rest.indexOf("/");
        const name = slash < 0 ? rest : rest.slice(0, slash);
        if (name) seen.set(name, (seen.get(name) ?? false) || slash >= 0);
      }
      return [...seen].map(([name, isDirectory]) => ({ name, isDirectory }));
    },
    async readFile(file) {
      return files[file] ?? null;
    },
  };
}

const nextPkg = JSON.stringify({ name: "ccs-app", dependencies: { next: "15.0.0", react: "19.0.0" } });
const libPkg = JSON.stringify({ name: "tool", dependencies: { lodash: "4" } });

/** 真实布局：ccs-backend/ccs-ui 是前端，package.json 在 ccs-src/ccs-app 下；上级 ccs-backend 根下有 sln；还有两个带 sln 的同级仓库。 */
const ccsLayout: Record<string, string> = {
  "/w/ccs-backend/CentralizedConfigurationSystem.sln": "",
  "/w/ccs-backend/CentralizedConfigurationSystem.Api/Api.csproj": "",
  "/w/ccs-backend/mt4proxy/MT4Proxy.sln": "",
  "/w/ccs-backend/mt5-event-publisher/MT5EventPublisher.sln": "",
  "/w/ccs-backend/ccs-ui/README.md": "",
  "/w/ccs-backend/ccs-ui/ccs-src/ccs-app/package.json": nextPkg,
  "/w/ccs-backend/ccs-ui/ccs-src/ccs-app/node_modules/foo/x.sln": "",
};
const primary = "/w/ccs-backend/ccs-ui";

const fresh: PromptContext = { configuredProjects: [], repoConfigProjects: [], otherFolders: 0, alreadyAsked: false };

describe("hasFrontendDependency", () => {
  it("recognises next / react / vue / @angular/core / vite in any dependency field", () => {
    expect(hasFrontendDependency(nextPkg)).toBe(true);
    expect(hasFrontendDependency(JSON.stringify({ devDependencies: { vite: "5" } }))).toBe(true);
    expect(hasFrontendDependency(JSON.stringify({ peerDependencies: { "@angular/core": "18" } }))).toBe(true);
    expect(hasFrontendDependency(JSON.stringify({ dependencies: { vue: "3" } }))).toBe(true);
    expect(hasFrontendDependency(libPkg)).toBe(false);
    expect(hasFrontendDependency("{ not json")).toBe(false);
  });
});

describe("looksLikeFrontend / containsSolution", () => {
  it("finds package.json up to 3 levels down, skipping node_modules", async () => {
    const fs = fakeFs(ccsLayout);
    expect(await looksLikeFrontend(fs, primary, p)).toBe(true);
    // node_modules 里的 sln 不算主根自己的解决方案
    expect(await containsSolution(fs, primary, p)).toBe(false);
    expect(fs.reads.some((d) => d.includes("node_modules"))).toBe(false);
  });

  it("package.json deeper than 3 levels is not seen", async () => {
    const fs = fakeFs({ "/r/a/b/c/d/package.json": nextPkg });
    expect(await looksLikeFrontend(fs, "/r", p)).toBe(false);
    expect(await looksLikeFrontend(fakeFs({ "/r/a/b/c/package.json": nextPkg }), "/r", p)).toBe(true);
  });

  it("sln within 2 levels counts, deeper does not", async () => {
    expect(await containsSolution(fakeFs({ "/r/src/App/App.sln": "" }), "/r", p)).toBe(true);
    expect(await containsSolution(fakeFs({ "/r/src/App/x/App.slnx": "" }), "/r", p)).toBe(false);
  });
});

describe("findBackendCandidates", () => {
  it("parent with a solution at its root → only the parent (it already covers the siblings)", async () => {
    const cs = await findBackendCandidates(fakeFs(ccsLayout), primary, p);
    expect(cs).toEqual([{ dir: "/w/ccs-backend", name: "ccs-backend", sln: "CentralizedConfigurationSystem.sln", relative: "..", relation: "parent" }]);
    expect(suggestionMessage(cs[0]!)).toBe("检测到旁边的 .NET 后端 ccs-backend（CentralizedConfigurationSystem.sln）。一起索引后，项目地图能显示前端页面到后端控制器的完整调用链。是否一起索引？");
  });

  it("parent without a solution → siblings whose root has *.sln / *.slnx, at most 3, sorted", async () => {
    const fs = fakeFs({
      "/code/web/package.json": nextPkg,
      "/code/api/Api.slnx": "",
      "/code/billing/Billing.sln": "",
      "/code/docs/readme.md": "",
      "/code/deep/src/Deep.sln": "", // 只看同级目录根，不往下找
      "/code/zeta/Zeta.sln": "",
      "/code/zz-extra/Extra.sln": "",
      "/code/.hidden/H.sln": "",
    });
    const cs = await findBackendCandidates(fs, "/code/web", p);
    expect(cs.map((c) => [c.name, c.sln, c.relative, c.relation])).toEqual([
      ["api", "Api.slnx", "../api", "sibling"],
      ["billing", "Billing.sln", "../billing", "sibling"],
      ["zeta", "Zeta.sln", "../zeta", "sibling"],
    ]);
  });

  it("nothing around → empty", async () => {
    expect(await findBackendCandidates(fakeFs({ "/code/web/package.json": nextPkg, "/code/other/a.txt": "" }), "/code/web", p)).toEqual([]);
  });

  it("excluding the primary root itself: case-folded on win32 / darwin, exact on linux", async () => {
    // 主根 /code/web 旁边有个只差大小写的 Web/（Linux 上是另一个目录）
    const files = { "/code/web/package.json": nextPkg, "/code/web/Web.sln": "", "/code/Web/Web.sln": "" };
    expect((await findBackendCandidates(fakeFs(files), "/code/web", p, "linux")).map((c) => c.name)).toEqual(["Web"]);
    expect(await findBackendCandidates(fakeFs(files), "/code/web", p, "darwin")).toEqual([]);
    // Windows：fsPath 的大小写可能和磁盘上的目录名不同（D:\code\WEB 打开的是 web/），仍然认出是自己
    expect(await findBackendCandidates(fakeFs({ "/code/web/Web.sln": "" }), "/code/WEB", p, "win32")).toEqual([]);
  });
});

describe("detectBackendsToSuggest", () => {
  it("frontend next to a backend → suggests it", async () => {
    const cs = await detectBackendsToSuggest(fakeFs(ccsLayout), primary, fresh, p);
    expect(cs.map((c) => c.relative)).toEqual([".."]);
  });

  it("sibling backend → suggests the sibling", async () => {
    const fs = fakeFs({ "/code/web/package.json": nextPkg, "/code/api/Api.sln": "" });
    expect((await detectBackendsToSuggest(fs, "/code/web", fresh, p)).map((c) => c.relative)).toEqual(["../api"]);
  });

  it("primary root itself has a solution → no prompt", async () => {
    const fs = fakeFs({ ...ccsLayout, "/w/ccs-backend/ccs-ui/ccs-src/Ui.sln": "" });
    expect(await detectBackendsToSuggest(fs, primary, fresh, p)).toEqual([]);
  });

  it("not a frontend → no prompt", async () => {
    const fs = fakeFs({ "/code/tool/package.json": libPkg, "/code/api/Api.sln": "" });
    expect(await detectBackendsToSuggest(fs, "/code/tool", fresh, p)).toEqual([]);
  });

  it("already asked → no prompt, and no file system scan", async () => {
    const fs = fakeFs(ccsLayout);
    expect(await detectBackendsToSuggest(fs, primary, { ...fresh, alreadyAsked: true }, p)).toEqual([]);
    expect(fs.reads).toEqual([]);
  });

  it("codeatlas.projects / .codeatlas/config.json already set, or multi-root → no prompt", async () => {
    const fs = fakeFs(ccsLayout);
    expect(await detectBackendsToSuggest(fs, primary, { ...fresh, configuredProjects: [".."] }, p)).toEqual([]);
    expect(await detectBackendsToSuggest(fs, primary, { ...fresh, repoConfigProjects: ["../x"] }, p)).toEqual([]);
    expect(await detectBackendsToSuggest(fs, primary, { ...fresh, otherFolders: 1 }, p)).toEqual([]);
    // 只有空串不算配过
    expect((await detectBackendsToSuggest(fs, primary, { ...fresh, configuredProjects: ["  "] }, p)).length).toBe(1);
  });
});

describe("helpers", () => {
  it("readRepoConfigProjects reads .codeatlas/config.json projects", async () => {
    expect(await readRepoConfigProjects(fakeFs({ "/r/.codeatlas/config.json": '{"projects":["../api",""]}' }), "/r", p)).toEqual(["../api"]);
    expect(await readRepoConfigProjects(fakeFs({ "/r/.codeatlas/config.json": "oops" }), "/r", p)).toEqual([]);
    expect(await readRepoConfigProjects(fakeFs({}), "/r", p)).toEqual([]);
  });

  it("relativeProjectPath uses forward slashes, also on Windows", () => {
    expect(relativeProjectPath("/w/a/ui", "/w/a", p)).toBe("..");
    expect(relativeProjectPath("D:\\w\\ui", "D:\\w\\api", path.win32)).toBe("../api");
    expect(relativeProjectPath("D:\\w\\a\\ui", "D:\\w\\a", path.win32)).toBe("..");
  });

  it("projectsWithCandidate appends once", () => {
    const c = { dir: "/w", name: "w", sln: "w.sln", relative: "..", relation: "parent" as const };
    expect(projectsWithCandidate([], c)).toEqual([".."]);
    expect(projectsWithCandidate(["..", ""], c)).toEqual([".."]);
    expect(projectsWithCandidate(["../x"], c)).toEqual(["../x", ".."]);
  });
});
