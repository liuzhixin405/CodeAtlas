import * as esbuild from "esbuild";
import { existsSync } from "node:fs";

const watch = process.argv.includes("--watch");

const webview = {
  entryPoints: ["webview/main.ts"],
  bundle: true,
  format: "iife",
  platform: "browser",
  target: "es2022",
  outfile: "media/panel.js",
  sourcemap: true,
  minify: !watch,
  logLevel: "info",
};

const extension = {
  entryPoints: ["src/extension.ts"],
  bundle: true,
  format: "cjs",
  platform: "node",
  target: "node20",
  external: ["vscode"],
  outfile: "dist/extension.js",
  sourcemap: true,
  logLevel: "info",
};

const configs = existsSync("src/extension.ts") ? [webview, extension] : [webview];

if (watch) {
  for (const c of configs) (await esbuild.context(c)).watch();
} else {
  await Promise.all(configs.map((c) => esbuild.build(c)));
}
