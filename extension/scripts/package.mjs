// 把 dotnet publish 产物（默认 ../dist）拷进 server/，然后用 vsce 打成指定平台的 VSIX。
// 用法：node scripts/package.mjs [--target win32-x64|linux-x64|linux-arm64|darwin-x64|darwin-arm64] [--dist <目录>]
// 服务是自带运行时发布的，每个平台一个包；插件市场按安装者的系统发对应的包。
import { cpSync, existsSync, rmSync, writeFileSync } from "node:fs";
import { execSync } from "node:child_process";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionDir = path.resolve(here, "..");
const args = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const target = arg("--target", "win32-x64");
const distDir = path.resolve(extensionDir, "..", arg("--dist", "dist"));
const serverDir = path.join(extensionDir, "server");
const exe = target.startsWith("win32-") ? "codeatlas.exe" : "codeatlas";
const worker = target.startsWith("win32-") ? "codeatlas-roslyn.exe" : "codeatlas-roslyn";

if (!existsSync(path.join(distDir, exe))) {
  console.error(`没有找到 ${path.join(distDir, exe)}，先运行：powershell -File scripts/publish.ps1 -Rid <rid> -Out <目录>`);
  process.exit(1);
}
// 自带运行时发布才有 hostfxr；没有说明是旧的依赖框架发布，用户机器上得装 .NET
const hostfxr = target.startsWith("win32-") ? "hostfxr.dll" : target.startsWith("darwin-") ? "libhostfxr.dylib" : "libhostfxr.so";
if (!existsSync(path.join(distDir, hostfxr))) {
  console.error(`${distDir} 不是自带运行时的发布（缺 ${hostfxr}），请用 scripts/publish.ps1 重新发布。`);
  process.exit(1);
}
if (!existsSync(path.join(distDir, "roslyn", worker))) {
  console.warn(`提示：${path.join(distDir, "roslyn")} 里没有 ${worker}，打出来的扩展不会有 Roslyn 精确语义。`);
}

rmSync(serverDir, { recursive: true, force: true });
cpSync(distDir, serverDir, { recursive: true, filter: (src) => !src.endsWith(".pdb") });
// 扩展启动时拿它和当前系统比对：装错平台的包（Windows 上装了 darwin-x64）直接说该装哪个，而不是报"找不到路径"
writeFileSync(path.join(serverDir, "target.json"), JSON.stringify({ target }) + "\n");
console.log(`已复制服务（${target}）到 ${serverDir}`);

execSync("npm run build", { cwd: extensionDir, stdio: "inherit" });
execSync(`npx @vscode/vsce package --no-dependencies --target ${target}`, { cwd: extensionDir, stdio: "inherit" });
