# 发布 codeatlas.exe 与 codeatlas-roslyn.exe 到 dist/（framework-dependent，需本机 .NET 10 运行时）。
# 用法：powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 [-Rid win-x64] [-Out dist]
param(
    [string]$Rid = "win-x64",
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # 从 dist 里启动的服务会锁住要覆盖的文件，只结束这些；VS Code 扩展自带的 server/ 副本不受影响
    $outFull = (Resolve-Path -LiteralPath (Join-Path $root $Out) -ErrorAction SilentlyContinue).Path
    if ($outFull) {
        Get-Process codeatlas, codeatlas-roslyn -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($outFull, [System.StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }

    dotnet publish src/CodeAtlas.Cli -c Release -r $Rid --self-contained false -o $Out
    if ($LASTEXITCODE -ne 0) { throw "发布 CodeAtlas.Cli 失败" }
    dotnet publish src/CodeAtlas.Roslyn.Worker -c Release -r $Rid --self-contained false -o $Out
    if ($LASTEXITCODE -ne 0) { throw "发布 CodeAtlas.Roslyn.Worker 失败" }

    Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force
    Write-Host "已发布到 $root\$Out："
    Get-ChildItem $Out -Filter codeatlas*.exe | ForEach-Object { Write-Host "  $($_.Name)" }
}
finally {
    Pop-Location
}
