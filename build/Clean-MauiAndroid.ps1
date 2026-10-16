# 缓解 XARDF7024（RemoveDirFixed: 目录不是空的）——强制删掉 Android 中间目录。
# 用法：
#   powershell -File build\Clean-MauiAndroid.ps1
#   powershell -File build\Clean-MauiAndroid.ps1 -Configuration Release -TargetFramework net10.0-android
param(
    [string]$Configuration = 'Release',
    [string]$TargetFramework = 'net10.0-android',
    [string]$RepoRoot = '',
    [switch]$All
)

$ErrorActionPreference = 'Continue'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$maui = Join-Path $RepoRoot 'src\FlyScreen.App.Maui'

$dirs = New-Object System.Collections.Generic.List[string]
if ($All) {
    foreach ($c in @('Debug', 'Release')) {
        $dirs.Add((Join-Path $maui "obj\$c\$TargetFramework"))
        $dirs.Add((Join-Path $maui "bin\$c\$TargetFramework"))
    }
} else {
    $base = Join-Path $maui "obj\$Configuration\$TargetFramework"
    foreach ($leaf in @('lp', 'android', 'library_projects', '__library_projects__', 'res', 'stamp', 'assets', 'java')) {
        $dirs.Add((Join-Path $base $leaf))
    }
    $dirs.Add($base)
}

function Remove-TreeForced([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $true }
    for ($i = 1; $i -le 8; $i++) {
        try {
            Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try { $_.Attributes = 'Normal' } catch { }
                }
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            Start-Sleep -Milliseconds (200 * $i)
            cmd /c "rd /s /q `"$path`"" | Out-Null
            if (-not (Test-Path -LiteralPath $path)) { return $true }
        }
    }
    Write-Warning "仍无法删除: $path"
    return $false
}

$ok = $true
foreach ($d in $dirs) {
    if (Remove-TreeForced $d) {
        if (Test-Path -LiteralPath $d) { } else { Write-Host "已清理: $d" }
    } else {
        $ok = $false
    }
}

if (-not $ok) { exit 1 }
