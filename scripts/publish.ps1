# 发布 codeatlas（服务 + 命令行）与 codeatlas-roslyn 到 dist/。
# codeatlas 自带 .NET 运行时（self-contained）：只看前端 / Go / Python / Java 的用户什么都不用装。
# codeatlas-roslyn 放在 roslyn/ 子目录，依赖本机 .NET 10（它要用 SDK 里的 MSBuild 读 C# 项目；本来就只有 C# 用户用得上）。
# 两者不能放同一目录：自带运行时的 hostfxr 会被 worker 的启动器误用。
# 用法：powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 [-Rid win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64] [-Out dist]
param(
    [string]$Rid = "win-x64",
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $outFull = Join-Path $root $Out
    if (Test-Path -LiteralPath $outFull) {
        # 从 dist 里启动的服务会锁住要覆盖的文件，只结束这些；VS Code 扩展自带的 server/ 副本不受影响
        Get-Process codeatlas, codeatlas-roslyn -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($outFull, [System.StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        # 整个清掉：旧布局（依赖框架、worker 与服务同目录）残留的文件混进自带运行时的目录会出问题
        Remove-Item -LiteralPath $outFull -Recurse -Force
    }

    dotnet publish src/CodeAtlas.Cli -c Release -r $Rid --self-contained true -o $Out
    if ($LASTEXITCODE -ne 0) { throw "发布 CodeAtlas.Cli 失败" }
    dotnet publish src/CodeAtlas.Roslyn.Worker -c Release -r $Rid --self-contained false -o (Join-Path $Out "roslyn")
    if ($LASTEXITCODE -ne 0) { throw "发布 CodeAtlas.Roslyn.Worker 失败" }

    Get-ChildItem $Out -Filter *.pdb -Recurse | Remove-Item -Force
    # TreeSitter.DotNet 带了 30 种语法的原生库（约 60 MB），只留适配器用到的（LanguageAdapters.Create 里那几门语言）；
    # 加一门语言时在这里加上它的语法库名
    $grammars = @('c-sharp', 'typescript', 'tsx', 'javascript', 'go', 'python', 'java')
    $libs = Get-ChildItem $Out -File | Where-Object { $_.Name -match '^(lib)?tree-sitter-(.+?)\.(dll|so|dylib)$' }
    foreach ($g in $grammars) {
        if (-not ($libs | Where-Object { $_.Name -match "^(lib)?tree-sitter-$([regex]::Escape($g))\." })) { throw "发布目录里缺 tree-sitter-$g 语法库" }
    }
    $libs | Where-Object { $null = $_.Name -match '^(lib)?tree-sitter-(.+?)\.(dll|so|dylib)$'; $grammars -notcontains $Matches[2] } | Remove-Item -Force
    $size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("已发布 {0} 到 {1}（{2:N0} MB）：" -f $Rid, $outFull, $size)
    Get-ChildItem $Out -Recurse -File | Where-Object { $_.BaseName -in 'codeatlas', 'codeatlas-roslyn' -and $_.Extension -in '', '.exe' } |
        ForEach-Object { Write-Host "  $($_.FullName.Substring($outFull.Length + 1))" }
}
finally {
    Pop-Location
}
