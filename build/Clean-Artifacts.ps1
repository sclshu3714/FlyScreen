# 清理 artifacts 下中间产物，保留最终交付物。
#
# 默认保留：
#   artifacts/release   — 命名后的安装包 / 独立应用 / zip
#   artifacts/flytools  — 已组好的 .flytool + tools-index.json
#
# 会删除：
#   setup-work / setup-staging / flytools-stage
#   各 RID 发布暂存（linux-x64、win-x64、maui-* 等）
#   临时检查目录、根目录日志与其它非保留项
#
# 用法：
#   powershell -File build\Clean-Artifacts.ps1
#   powershell -File build\Clean-Artifacts.ps1 -AlsoFlyTools
#   powershell -File build\Clean-Artifacts.ps1 -AlsoRelease
#   powershell -File build\Clean-Artifacts.ps1 -WhatIf
param(
    [string]$RepoRoot = '',
    [switch]$AlsoRelease,
    [switch]$AlsoFlyTools,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$artifacts = Join-Path $RepoRoot 'artifacts'
if (-not (Test-Path -LiteralPath $artifacts)) {
    Write-Host "无 artifacts 目录，跳过。"
    exit 0
}

$keep = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$keep.Add('release')
[void]$keep.Add('flytools')
if ($AlsoRelease) { [void]$keep.Remove('release') }
if ($AlsoFlyTools) { [void]$keep.Remove('flytools') }

function Remove-TreeSafe([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    if ($WhatIf) {
        Write-Host "[WhatIf] 删除: $Path"
        return $true
    }
    for ($i = 0; $i -lt 6; $i++) {
        try {
            Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try { $_.Attributes = 'Normal' } catch { }
                }
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            Start-Sleep -Milliseconds (200 * ($i + 1))
        }
    }
    return -not (Test-Path -LiteralPath $Path)
}

$removed = 0
$failed = 0

Get-ChildItem -LiteralPath $artifacts -Force | ForEach-Object {
    if ($_.PSIsContainer) {
        if ($keep.Contains($_.Name)) {
            Write-Host "保留: $($_.FullName)"
            return
        }
        if (Remove-TreeSafe $_.FullName) {
            Write-Host "已删除目录: $($_.FullName)"
            $script:removed++
        }
        else {
            Write-Host "删除失败: $($_.FullName)" -ForegroundColor Yellow
            $script:failed++
        }
    }
    else {
        if (Remove-TreeSafe $_.FullName) {
            Write-Host "已删除文件: $($_.FullName)"
            $script:removed++
        }
        else {
            Write-Host "删除失败: $($_.FullName)" -ForegroundColor Yellow
            $script:failed++
        }
    }
}

Write-Host "Clean-Artifacts 完成：删除 $removed 项，失败 $failed 项。"
if ($failed -gt 0) { exit 1 }
exit 0
