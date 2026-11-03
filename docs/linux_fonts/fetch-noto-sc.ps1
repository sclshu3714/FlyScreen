# 拉取可再分发的 Noto Sans SC Regular（SIL OFL）到 tools/linux-fonts/
# 供 Linux 发布包 Fonts/ 使用。勿放入微软雅黑等专有字体。
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$destDir = Join-Path $repoRoot 'tools\linux-fonts'
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

# notofonts/noto-cjk SubsetOTF（简体子集，约 8MB；OFL）
$fontUrl = 'https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/SubsetOTF/SC/NotoSansSC-Regular.otf'
$licUrl = 'https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/LICENSE'
$outFont = Join-Path $destDir 'NotoSansSC-Regular.otf'
$outLic = Join-Path $destDir 'OFL.txt'

function Get-File([string]$Url, [string]$OutPath) {
    Write-Host "Downloading $Url"
    & curl.exe -L --fail --connect-timeout 30 --max-time 600 -o $OutPath $Url
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $OutPath) -or (Get-Item $OutPath).Length -lt 1000) {
        throw "Download failed: $Url"
    }
}

Get-File $fontUrl $outFont
Get-File $licUrl $outLic

Write-Host "OK font=$((Get-Item $outFont).Length) bytes -> $outFont"
Write-Host "Publish Linux 时将复制到输出 Fonts/（见 FlyScreen.App.Linux.csproj）。"
Write-Host "License: SIL Open Font License — $outLic"
