# 按 docs/ROADMAP.md §4.2 命名发布物：
#   FlyScreen-{version}-{rid}{实际后缀}
# 后缀以 Source 为准（.exe/.apk/无扩展名可执行文件/.tar.gz）。
# 目录仅用于安装包，打成 .tar.gz；独立程序必须传入单个可执行文件。
#
# 用法：
#   powershell -File build\Name-FlyScreenPackage.ps1 -Source path\to\FlyScreen.exe -Rid win-x64
#   powershell -File build\Name-FlyScreenPackage.ps1 -Source artifacts\linux-x64 -Rid linux-x64
#   powershell -File build\Name-FlyScreenPackage.ps1 -Source app-phone-release.apk -Rid android
#
# 参数：
#   -Source   已生成的文件，或安装包目录（目录打成 .tar.gz）
#   -Rid      平台标识：win-x64 / osx-arm64 / osx-x64 / linux-x64 / linux-arm64 /
#             android / android-tv / harmony /
#             maui-win-x64 / maui-android / maui-osx-arm64 / maui-osx-x64 等
#   -Version  可选；默认走 Resolve-FlyScreenVersion.ps1
#   -OutDir   输出目录，默认 artifacts/release
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [string]$Version = "",
    [string]$OutDir = "",
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$Source = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Source)
if (-not (Test-Path -LiteralPath $Source)) {
    throw "Source 不存在: $Source"
}

if (-not $OutDir) {
    $OutDir = Join-Path $RepoRoot "artifacts\release"
}
elseif (-not [IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $RepoRoot $OutDir
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$verInfo = & (Join-Path $PSScriptRoot "Resolve-FlyScreenVersion.ps1") -Version $Version -RepoRoot $RepoRoot
$ver = $verInfo.Version
$rid = $Rid.Trim().ToLowerInvariant()

$isDir = (Get-Item -LiteralPath $Source).PSIsContainer
$ext = ""
$staged = $null

if ($isDir) {
    # 安装包目录 → tar.gz（独立程序不应走到这里）
    $ext = ".tar.gz"
    $staged = Join-Path $env:TEMP ("flyscreen-pkg-{0}-{1}.tar.gz" -f $ver, $rid)
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    if (Get-Command tar -ErrorAction SilentlyContinue) {
        & tar -czf $staged -C $Source .
        if ($LASTEXITCODE -ne 0) { throw "tar 打包失败: $Source" }
    }
    else {
        throw "未找到 tar，无法将安装包目录打成 .tar.gz: $Source"
    }
}
else {
    $ext = [IO.Path]::GetExtension($Source)
    if ([string]::IsNullOrWhiteSpace($ext)) {
        $ext = "" # 无扩展名（如 linux 单文件可执行）
    }
    $staged = $Source
}

$destName = "FlyScreen-{0}-{1}{2}" -f $ver, $rid, $ext
$destPath = Join-Path $OutDir $destName

if (Test-Path -LiteralPath $destPath) {
    Remove-Item -LiteralPath $destPath -Force
}

Copy-Item -LiteralPath $staged -Destination $destPath -Force

if ($isDir -and (Test-Path -LiteralPath $staged)) {
    Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
}

Write-Host "已命名发布包: $destPath"
Write-Host "  version=$ver  rid=$rid  ext=$ext"

[pscustomobject]@{
    Version  = $ver
    Rid      = $rid
    Ext      = $ext
    Path     = $destPath
    FileName = $destName
}
