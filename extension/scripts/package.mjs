// 把 ../dist（dotnet publish 产物）拷进 server/，然后用 vsce 打包成 VSIX。
import { cpSync, existsSync, rmSync } from "node:fs";
import { execSync } from "node:child_process";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionDir = path.resolve(here, "..");
const distDir = path.resolve(extensionDir, "..", "dist");
const serverDir = path.join(extensionDir, "server");

if (!existsSync(path.join(distDir, "codeatlas.exe"))) {
  console.error(`没有找到 ${distDir}\\codeatlas.exe，先运行：dotnet publish src/CodeAtlas.Cli -c Release -r win-x64 --self-contained false -o dist`);
  process.exit(1);
}

if (!existsSync(path.join(distDir, "codeatlas-roslyn.exe"))) {
  console.warn(`提示：${distDir} 里没有 codeatlas-roslyn.exe，打出来的扩展不会有 Roslyn 精确语义。完整发布请运行：powershell -File scripts/publish.ps1`);
}

rmSync(serverDir, { recursive: true, force: true });
cpSync(distDir, serverDir, { recursive: true, filter: (src) => !src.endsWith(".pdb") });
console.log(`已复制服务到 ${serverDir}`);

execSync("npm run build", { cwd: extensionDir, stdio: "inherit" });
execSync("npx @vscode/vsce package --no-dependencies --allow-missing-repository", { cwd: extensionDir, stdio: "inherit" });
