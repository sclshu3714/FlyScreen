# 从发布输出目录挑出「单个」可执行/安装包，按 ROADMAP §4.2 复制到 artifacts/release：
#   FlyScreen-{version}-{rid}{ext}
#
# 用法：
#   powershell -File build\Finish-PublishToArtifacts.ps1 -SourceDir ... -Rid win-x64 -Kind Exe
#   powershell -File build\Finish-PublishToArtifacts.ps1 -SourceDir ... -Rid maui-android -Kind Apk
#   powershell -File build\Finish-PublishToArtifacts.ps1 -SourceDir ... -Rid linux-x64 -Kind UnixBin
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [ValidateSet('Exe', 'Apk', 'UnixBin')]
    [string]$Kind = 'Exe',

    [string]$RepoRoot = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$SourceDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDir)
if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "发布目录不存在: $SourceDir"
}

function Find-PackageFile {
    param([string]$Dir, [string]$Kind)

    $files = @(Get-ChildItem -LiteralPath $Dir -File -Recurse -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { throw "目录为空: $Dir" }

    switch ($Kind) {
        'Exe' {
            $exes = @($files | Where-Object { $_.Extension -eq '.exe' })
            if ($exes.Count -eq 0) { throw "未找到 .exe：$Dir" }
            $hit = $exes | Where-Object { $_.Name -match '^(FlyScreen|FlyScreen\.App\.Maui)' } |
                Sort-Object Length -Descending | Select-Object -First 1
            if (-not $hit) { $hit = $exes | Sort-Object Length -Descending | Select-Object -First 1 }
            return $hit.FullName
        }
        'Apk' {
            $apks = @($files | Where-Object { $_.Extension -eq '.apk' })
            if ($apks.Count -eq 0) { throw "未找到 .apk：$Dir" }
            # 优先已签名 / FlyScreen.apk
            $hit = $apks |
                Sort-Object @{
                    Expression = {
                        if ($_.Name -match 'Signed') { 0 }
                        elseif ($_.Name -eq 'FlyScreen.apk') { 1 }
                        else { 2 }
                    }
                }, Length -Descending |
                Select-Object -First 1
            return $hit.FullName
        }
        'UnixBin' {
            $bins = @($files | Where-Object {
                    $_.Extension -eq '' -and
                    $_.Name -notmatch '^(createdump|lib|Microsoft\.|System\.)' -and
                    $_.Length -gt 1MB
                })
            if ($bins.Count -eq 0) {
                # 兼容带扩展名的单文件
                $bins = @($files | Where-Object {
                        $_.Extension -in @('', '.dll') -and $_.Length -gt 1MB -and
                        $_.Name -match 'FlyScreen'
                    })
            }
            if ($bins.Count -eq 0) { throw "未找到单文件可执行：$Dir" }
            $hit = $bins | Where-Object { $_.Name -match 'FlyScreen' } |
                Sort-Object Length -Descending | Select-Object -First 1
            if (-not $hit) { $hit = $bins | Sort-Object Length -Descending | Select-Object -First 1 }
            return $hit.FullName
        }
    }
}

$sourceFile = Find-PackageFile -Dir $SourceDir -Kind $Kind
Write-Host "选用产物: $sourceFile"

$nameArgs = @{
    Source   = $sourceFile
    Rid      = $Rid
    RepoRoot = $RepoRoot
}
if ($OutDir) { $nameArgs.OutDir = $OutDir }

& (Join-Path $PSScriptRoot 'Name-FlyScreenPackage.ps1') @nameArgs
