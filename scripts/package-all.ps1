# 每个平台发布一次服务、打一个 VSIX：extension\codetrail-graph-<平台>-<版本>.vsix。
# 用法：powershell -ExecutionPolicy Bypass -File scripts/package-all.ps1 [-Targets win32-x64,linux-x64,...]
param(
    [string[]]$Targets = @('win32-x64', 'linux-x64', 'linux-arm64', 'darwin-x64', 'darwin-arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# vsce 的平台名 → .NET 的 RID
$Rids = @{ 'win32-x64' = 'win-x64'; 'win32-arm64' = 'win-arm64'; 'linux-x64' = 'linux-x64'; 'linux-arm64' = 'linux-arm64'; 'darwin-x64' = 'osx-x64'; 'darwin-arm64' = 'osx-arm64' }
foreach ($t in $Targets) {
    $rid = $Rids[$t]
    if (-not $rid) { throw "不认识的平台 $t（可选：$($Rids.Keys -join ', ')）" }
    $out = "dist-pkg\$rid"
    & (Join-Path $PSScriptRoot 'publish.ps1') -Rid $rid -Out $out
    Push-Location (Join-Path $root 'extension')
    try {
        node scripts/package.mjs --target $t --dist $out
        if ($LASTEXITCODE -ne 0) { throw "打包 $t 失败" }
    }
    finally { Pop-Location }
}
$extensionName = (Get-Content (Join-Path $root 'extension\package.json') -Raw | ConvertFrom-Json).name
Get-ChildItem (Join-Path $root 'extension') -Filter "$extensionName-*.vsix" | Sort-Object Name |
    ForEach-Object { Write-Host ("{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
