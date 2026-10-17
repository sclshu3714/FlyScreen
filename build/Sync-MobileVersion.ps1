# 将 Android / 鸿蒙版本与 Win 端 Git 标签规则对齐。
# 用法（仓库根目录）：
#   powershell -File build\Sync-MobileVersion.ps1
#   powershell -File build\Sync-MobileVersion.ps1 -Version 1.1.0
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$info = & "$PSScriptRoot\Resolve-FlyScreenVersion.ps1" -Version $Version -RepoRoot $repoRoot
$ver = $info.Version
$code = [int]$info.VersionCode

Write-Host "同步移动端版本: $ver (code=$code) 来自标签 $($info.LatestTag)"

# --- build/version.json（各端可读）---
$jsonPath = Join-Path $PSScriptRoot "version.json"
@{
    version     = $ver
    versionCode = $code
    latestTag   = $info.LatestTag
} | ConvertTo-Json | Set-Content -Path $jsonPath -Encoding utf8

# --- 鸿蒙 AppScope ---
$appJson = Join-Path $repoRoot "mobile\harmony\AppScope\app.json5"
$appText = Get-Content $appJson -Raw -Encoding utf8
$appText = [regex]::Replace($appText, '"versionCode"\s*:\s*\d+', "`"versionCode`": $code")
$appText = [regex]::Replace($appText, '"versionName"\s*:\s*"[^"]*"', "`"versionName`": `"$ver`"")
Set-Content -Path $appJson -Value $appText.TrimEnd() -Encoding utf8 -NoNewline
Add-Content -Path $appJson -Value "`n" -Encoding utf8

# --- 鸿蒙 common oh-package ---
$ohPkg = Join-Path $repoRoot "mobile\harmony\common\oh-package.json5"
if (Test-Path $ohPkg) {
    $ohText = Get-Content $ohPkg -Raw -Encoding utf8
    $ohText = [regex]::Replace($ohText, '"version"\s*:\s*"[^"]*"', "`"version`": `"$ver`"", 1)
    Set-Content -Path $ohPkg -Value $ohText.TrimEnd() -Encoding utf8 -NoNewline
    Add-Content -Path $ohPkg -Value "`n" -Encoding utf8
}

# --- 鸿蒙握手常量（构建/DevEco 打开前应已同步）---
$etsPath = Join-Path $repoRoot "mobile\harmony\common\src\main\ets\FlyScreenVersion.ets"
@"
/** 由 build/Sync-MobileVersion.ps1 生成，勿手改。与 Win Git 标签版本规则一致。 */
export const FLYSCREEN_VERSION: string = '$ver';
export const FLYSCREEN_VERSION_CODE: number = $code;
"@ | Set-Content -Path $etsPath -Encoding utf8

Write-Host "已更新: AppScope/app.json5, common/oh-package.json5, FlyScreenVersion.ets, build/version.json"
Write-Host "Android 在 Gradle 配置阶段自动解析，无需改源码。"
