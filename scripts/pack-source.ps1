# Pack CodeAtlas source + plugin VSIX into one zip (no build output, packages or local index cache)
# 默认只带 Windows 的 VSIX（每个平台的包约 60 MB）；-AllVsix 带上全部平台
param([switch]$AllVsix, [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path, [string]$Zip = (Join-Path (Split-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -Parent) ('CodeAtlas-' + (Get-Date -Format 'yyyyMMdd') + '.zip')))
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$excludeDirs = @('bin', 'obj', 'node_modules', '.vs', '.git', '.codeatlas', '.superpowers', '.claude', 'TestResults')
$excludeTop = @('dist', 'dist-pkg', 'extension/server', 'extension/media', 'extension/dist')
$excludeExt = @('.user', '.suo', '.pdb')
$files = Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Where-Object {
    $rel = $_.FullName.Substring($Root.Length + 1).Replace([char]92, [char]47)
    $segs = $rel.Split([char]47)
    $dirs = if ($segs.Count -gt 1) { $segs[0..($segs.Count - 2)] } else { @() }
    -not ($dirs | Where-Object { $excludeDirs -contains $_ }) -and
    -not ($excludeTop | Where-Object { $rel.StartsWith($_ + '/') }) -and
    -not ($excludeExt -contains $_.Extension) -and
    -not ($_.Extension -eq '.vsix' -and -not ($rel -match '^extension/codeatlas-[a-z0-9-]+-\d+\.\d+\.\d+\.vsix$' -and ($AllVsix -or $rel -like 'extension/codeatlas-win32-x64-*')))
}
if (Test-Path -LiteralPath $Zip) { [System.IO.File]::Delete($Zip) }
$archive = [System.IO.Compression.ZipFile]::Open($Zip, 'Create')
try {
    foreach ($f in $files) {
        $entry = 'CodeAtlas/' + $f.FullName.Substring($Root.Length + 1).Replace([char]92, [char]47)
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
    }
}
finally { $archive.Dispose() }
$z = Get-Item -LiteralPath $Zip
'{0}  {1:N1} MB  {2} files' -f $z.FullName, ($z.Length / 1MB), $files.Count
