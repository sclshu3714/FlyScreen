# 从目录树剥离工具运行态，仅保留 Tools/Bundled（未安装）。
# 切勿用 Zip 改写 .apk：会破坏 APK Sig Block，Android 13+ 安装时报 PackageInfo null。
# Setup / Ensure-MauiSignedApk / 发布项目共用。
param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host "跳过工具安装态清理：目录不存在 $Root"
    exit 0
}

$stateFiles = @(
    'tools-install.json',
    'tools-shortcuts.json',
    'tools-local-programs.json',
    'tools-index.cache.json'
)

$removed = 0

function Strip-ToolsRoot([string]$toolsRoot) {
    foreach ($name in $stateFiles) {
        $f = Join-Path $toolsRoot $name
        if (Test-Path -LiteralPath $f) {
            Remove-Item -LiteralPath $f -Force
            $script:removed++
        }
    }

    Get-ChildItem -LiteralPath $toolsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -ieq 'Bundled') { return }
        try {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            $script:removed++
        }
        catch { }
    }
}

Get-ChildItem -LiteralPath $Root -Directory -Recurse -Filter 'Tools' -ErrorAction SilentlyContinue |
    ForEach-Object {
        try { Strip-ToolsRoot $_.FullName }
        catch {
            Write-Host "清理 Tools 失败: $($_.FullName) — $($_.Exception.Message)"
        }
    }

if ($removed -gt 0) {
    Write-Host "已剥离工具安装态（$removed 项），仅保留 Tools/Bundled。"
}
else {
    Write-Host "工具目录已是未安装态（仅 Bundled / 无安装记录）。"
}
