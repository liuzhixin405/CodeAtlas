# CodeTrail Graph：发布到 VS Code Marketplace

## 1. 名称、身份和图标

- 市场显示名：`CodeTrail Graph`。
- 扩展包名：`codetrail-graph`。
- 当前配置的发布者 ID：`victor-liu`，最终扩展 ID 为 `victor-liu.codetrail-graph`。**这只是仓库配置，不代表该发布者已经注册或归你所有**。
- 同名显示名不等于扩展 ID 冲突；身份由 `<publisher>.<name>` 决定。名称可用性及商标问题需在正式提交前确认，仓库配置不能预留名称。
- 市场图标：[`extension/images/icon.png`](../extension/images/icon.png)，256 × 256 PNG；可编辑源稿为同目录的 `icon.svg`。Marketplace 图标使用 PNG，活动栏单色图标单独保留在 `extension/webview/icon.svg`。
- 后端仍叫 CodeAtlas；已有的 `codeatlas.*` 命令 / 设置、SecretStorage 键和 `.codeatlas` 索引目录不变。

正式发布后不要随意改 `publisher` 或 `name`，否则会成为另一个扩展，不会自动给原用户升级。旧的本地 CodeAtlas 扩展应先禁用或卸载，避免相同命令和视图冲突。

## 2. 注册发布者与授权

1. 登录 [Marketplace 发布者管理页](https://marketplace.visualstudio.com/manage)，创建你拥有的 publisher，记录 **ID**（不是显示名）。
2. 如果 ID 不是 `victor-liu`，修改 `extension/package.json` 的 `publisher` 后重新打包。
3. 按 [VS Code 官方发布指南](https://code.visualstudio.com/api/working-with-extensions/publishing-extension) 创建 Azure DevOps Personal Access Token（PAT）：组织选择 **All accessible organizations**，授权范围仅选择 **Marketplace → Manage**，并设置合理的有效期。账号必须有该 publisher 的发布权限。
4. 在 `extension` 目录运行 `npx vsce login <你的发布者ID>`，在交互提示中输入 PAT。不要把 PAT 写入仓库、文档、命令行参数或截图。也可以使用管理页手动上传 VSIX，不需要在本机配置 PAT。

`private: true` 只是防止误发布到 npm，不会阻止 VSIX 打包或 Marketplace 发布。无需为了 VS Code 市场发布移除它。

## 3. 本地验证和打包

构建机器需要 Node.js 22（满足当前锁定依赖的要求）、npm 和 .NET 10 SDK；最终用户无需安装 .NET 运行时。以下命令从仓库根目录运行，任一步失败都应停止，修复后再继续：

```powershell
Set-Location .\extension
npm ci
npm run typecheck
npm test
npm run build
Set-Location ..

# 五个平台：Windows x64、Linux x64 / arm64、macOS x64 / arm64
powershell -ExecutionPolicy Bypass -File .\scripts\package-all.ps1
```

首次发布建议先验证 Windows 包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Rid win-x64 -Out dist
Set-Location .\extension
npm run package -- --target win32-x64 --dist dist
code --install-extension .\codetrail-graph-win32-x64-0.1.0.vsix --force
```

完全退出并重启 VS Code，验证活动栏图标、命令、首次索引、调用图、服务启动 / 停止；其他平台也应在对应系统上实际验证，交叉打包成功不代表运行验证通过。

打包前检查 `package.json` 的版本、publisher、仓库链接、图标和 README。检查打包日志中的文件清单：应包含 `dist/extension.js`、`media/panel.js`、样式、活动栏图标、`images/icon.png`、服务程序和 `server/target.json`。每个平台都需要真实的自带运行时服务，不要只运行 `vsce package` 生成缺后端的包。

## 4. 正式上传

### 方式 A：网页上传（首次发布最直观）

进入 [发布者管理页](https://marketplace.visualstudio.com/manage)，选择自己的 publisher → **New extension → Visual Studio Code**，上传生成的 VSIX。随后在该扩展下上传其他平台的同版本 VSIX；各平台包的 `publisher`、`name`、`version` 必须一致。

### 方式 B：命令行上传已验证的包

在 `extension` 目录，完成 `vsce login` 后执行：

```powershell
npx vsce publish --packagePath .\codetrail-graph-win32-x64-0.1.0.vsix .\codetrail-graph-linux-x64-0.1.0.vsix .\codetrail-graph-linux-arm64-0.1.0.vsix .\codetrail-graph-darwin-x64-0.1.0.vsix .\codetrail-graph-darwin-arm64-0.1.0.vsix
```

仅发布 Windows 时，只保留 Windows 的路径。上传预构建的 VSIX，不要直接运行不带 `--packagePath` 的 `vsce publish`：本项目的打包脚本负责把对应平台的后端放入扩展，循环构建后工作区的 `server` 只保留最后一个平台。

上传后在管理页检查验证 / 扫描结果；通过后再在干净的 VS Code 环境中从 Marketplace 安装，确认安装到本机平台的版本。市场页面和搜索可能不会立即更新。

## 5. 后续升级

1. 在 `extension` 目录执行 `npm version patch --no-git-tag-version`（例如 `0.1.0` → `0.1.1`），同步 manifest 与 lockfile。
2. 更新扩展 README 的版本示例与相关发布说明。
3. 重新测试、构建、打包所有要维护的平台，并用新版本文件名上传。
4. 不要覆盖旧版 VSIX 内容后仍沿用已发布的相同版本。不同平台的同一次发布使用相同版本号。

仓库链接仍指向 CodeAtlas 项目；更改市场品牌不需要改 GitHub 仓库名称。
