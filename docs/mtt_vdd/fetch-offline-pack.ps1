#Requires -Version 5.1
<#
.SYNOPSIS
  拉取 MTT/VDD 基本离线静默包到共用 tools\VddOffline（WPF / Maui-Win 引用）。
  Mac/Linux 只复制其中 README。docs\mtt_vdd 仅保留本脚本与说明。
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dest = Join-Path $repoRoot 'tools\VddOffline'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$ua = 'FlyScreen-OfflinePack/1.0'

function Get-File([string]$Url, [string]$OutFile) {
    if ((Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 1000) {
        Write-Host "SKIP $(Split-Path $OutFile -Leaf) ($((Get-Item $OutFile).Length) bytes)"
        return
    }
    Write-Host "GET $(Split-Path $OutFile -Leaf)"
    & curl.exe -L --fail --retry 5 --retry-delay 2 --connect-timeout 30 --max-time 900 -A $ua -o $OutFile $Url
    if ($LASTEXITCODE -ne 0) { throw "curl failed exit=$LASTEXITCODE url=$Url" }
    Write-Host "OK $(Split-Path $OutFile -Leaf) ($((Get-Item $OutFile).Length) bytes)"
}

# 静默 setup + 控制台（25.5.2；25.7 已无 setup）
$b255 = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.5.2'
Get-File "$b255/Virtual.Display.Driver-v25.05.03-setup-x64.exe" "$dest\Virtual.Display.Driver-v25.05.03-setup-x64.exe"
Get-File "$b255/VDDControl-v25.05.03.exe" "$dest\VDDControl-v25.05.03.exe"

Write-Host ""
Write-Host "Done. Shared folder: $dest"
Get-ChildItem $dest -File | Sort-Object Name | Format-Table Name, Length -AutoSize
Write-Host "Driver.Only 不离线随包；setup/winget 失败时由程序按需下载。"
