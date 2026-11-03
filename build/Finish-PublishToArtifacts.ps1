# 从发布输出目录挑出可执行/安装包，按 ROADMAP §4.2 复制到 artifacts/release：
#   FlyScreen-{version}-{rid}{ext}
#
# UnixBin：若旁路存在 Fonts/（Linux 中文 OFL 字体），则打成含可执行文件 + Fonts/ + Licenses/ 的 .zip；
#          否则仍发布单个无扩展名二进制（如 Mac）。
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

function New-UnixBundleWithFonts {
    param(
        [string]$BinaryPath,
        [string]$PublishDir,
        [string]$RepoRoot
    )

    $fontsSrc = Join-Path $PublishDir 'Fonts'
    if (-not (Test-Path -LiteralPath $fontsSrc)) {
        return $null
    }

    $stage = Join-Path $env:TEMP ("flyscreen-unixbin-{0}" -f [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath $BinaryPath -Destination (Join-Path $stage (Split-Path $BinaryPath -Leaf)) -Force
    Copy-Item -LiteralPath $fontsSrc -Destination (Join-Path $stage 'Fonts') -Recurse -Force

    $licDir = Join-Path $stage 'Licenses'
    New-Item -ItemType Directory -Force -Path $licDir | Out-Null

    $tp = Join-Path $RepoRoot 'docs\THIRD_PARTY.md'
    if (Test-Path -LiteralPath $tp) {
        Copy-Item -LiteralPath $tp -Destination (Join-Path $licDir 'THIRD_PARTY.md') -Force
    }
    $notice = Join-Path $RepoRoot 'NOTICE.txt'
    if (Test-Path -LiteralPath $notice) {
        Copy-Item -LiteralPath $notice -Destination (Join-Path $licDir 'NOTICE.txt') -Force
    }
    $licensesRoot = Join-Path $RepoRoot 'third_party\licenses'
    if (Test-Path -LiteralPath $licensesRoot) {
        Get-ChildItem -LiteralPath $licensesRoot -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licDir $_.Name) -Force
        }
    }
    $ofl = Join-Path $fontsSrc 'OFL.txt'
    if (Test-Path -LiteralPath $ofl) {
        Copy-Item -LiteralPath $ofl -Destination (Join-Path $licDir 'OFL-NotoSansSC.txt') -Force
    }

    Write-Host "UnixBin 含 Fonts/ → 打成目录包: $stage"
    return $stage
}

$sourceFile = Find-PackageFile -Dir $SourceDir -Kind $Kind
Write-Host "选用产物: $sourceFile"

$stageToClean = $null
$packageSource = $sourceFile

if ($Kind -eq 'UnixBin') {
    $bundle = New-UnixBundleWithFonts -BinaryPath $sourceFile -PublishDir $SourceDir -RepoRoot $RepoRoot
    if ($bundle) {
        $packageSource = $bundle
        $stageToClean = $bundle
    }
}

try {
    $nameArgs = @{
        Source   = $packageSource
        Rid      = $Rid
        RepoRoot = $RepoRoot
    }
    if ($OutDir) { $nameArgs.OutDir = $OutDir }

    & (Join-Path $PSScriptRoot 'Name-FlyScreenPackage.ps1') @nameArgs
}
finally {
    if ($stageToClean -and (Test-Path -LiteralPath $stageToClean)) {
        Remove-Item -LiteralPath $stageToClean -Recurse -Force -ErrorAction SilentlyContinue
    }
}
