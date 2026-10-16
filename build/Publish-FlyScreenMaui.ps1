# MAUI 双轨 beta：发布 Windows / Android / Mac Catalyst 自包含包，并按 ROADMAP §4.2 命名。
# 不替代 WPF win-x64 stable。
#
# Windows / Mac：PublishSingleFile 单文件可执行；Android：APK。
#
# 用法：
#   powershell -File build\Publish-FlyScreenMaui.ps1
#   powershell -File build\Publish-FlyScreenMaui.ps1 -Platforms Windows,Android
#   powershell -File build\Publish-FlyScreenMaui.ps1 -Platforms Mac -MacRid maccatalyst-arm64
#
# 说明：
#   - Windows / Android 可在 Windows 开发机发布（Android 需 SDK）
#   - Mac Catalyst 必须在 macOS 上发布（本脚本在非 macOS 上会跳过并提示）
param(
    [ValidateSet('All', 'Windows', 'Android', 'Mac')]
    [string[]]$Platforms = @('All'),

    [string]$Configuration = 'Release',
    [string]$MacRid = 'maccatalyst-arm64', # 或 maccatalyst-x64
    [string]$OutRoot = '',
    [string]$RepoRoot = '',
    [switch]$SkipName
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
if (-not $OutRoot) {
    $OutRoot = Join-Path $RepoRoot 'src\FlyScreen.App.Maui\bin\publish'
}

$proj = Join-Path $RepoRoot 'src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj'
$nameScript = Join-Path $PSScriptRoot 'Name-FlyScreenPackage.ps1'
$isMacHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::OSX)

$want = @{
    Windows = $Platforms -contains 'All' -or $Platforms -contains 'Windows'
    Android = $Platforms -contains 'All' -or $Platforms -contains 'Android'
    Mac     = $Platforms -contains 'All' -or $Platforms -contains 'Mac'
}

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$results = @()

function Invoke-DotNetPublish {
    param(
        [string[]]$PublishArgs,
        [string]$Label
    )
    Write-Host ""
    Write-Host "=== Publish $Label ===" -ForegroundColor Cyan
    Write-Host ("dotnet " + ($PublishArgs -join ' '))
    & dotnet @PublishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败: $Label (exit $LASTEXITCODE)"
    }
}

function Invoke-NamePackage {
    param(
        [string]$Source,
        [string]$Rid
    )
    if ($SkipName) { return $null }
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "命名失败，产物不存在: $Source"
    }
    & $nameScript -Source $Source -Rid $Rid -RepoRoot $RepoRoot
}

function Find-SingleFileBinary {
    param(
        [string]$Dir,
        [string[]]$PreferredNames,
        [string[]]$Extensions = @('.exe', '')
    )
    if (-not (Test-Path -LiteralPath $Dir)) {
        throw "发布目录不存在: $Dir"
    }

    foreach ($name in $PreferredNames) {
        foreach ($ext in $Extensions) {
            $candidate = Join-Path $Dir ($name + $ext)
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Get-Item -LiteralPath $candidate).FullName
            }
        }
    }

    $exes = @(Get-ChildItem -LiteralPath $Dir -File -Filter '*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^(createdump|hostfxr|corehost)' })
    if ($exes.Count -eq 1) { return $exes[0].FullName }
    if ($exes.Count -gt 1) {
        $hit = $exes | Where-Object { $_.Name -match 'FlyScreen|Maui' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $unixBins = @(Get-ChildItem -LiteralPath $Dir -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -eq '' -and
            $_.Name -notmatch '^(createdump|lib|Microsoft\.|System\.)' -and
            $_.Length -gt 1MB
        })
    if ($unixBins.Count -ge 1) {
        $hit = $unixBins | Where-Object { $_.Name -match 'FlyScreen|Maui' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
        return ($unixBins | Sort-Object Length -Descending | Select-Object -First 1).FullName
    }

    throw "未找到单文件可执行产物，目录: $Dir"
}

# ---------- Windows（自包含单文件 .exe）----------
if ($want.Windows) {
    $winOut = Join-Path $OutRoot 'maui-win-x64'
    if (Test-Path -LiteralPath $winOut) { Remove-Item -LiteralPath $winOut -Recurse -Force }
    # RuntimeIdentifierOverride：规避 WinAppSDK RID 冲突；勿仅用 -r（见 MS unpackaged CLI 文档）
    Invoke-DotNetPublish -Label 'MAUI-Windows (single-file)' -PublishArgs @(
        'publish', $proj,
        '-c', $Configuration,
        '-f', 'net10.0-windows10.0.19041.0',
        '-p:RuntimeIdentifierOverride=win-x64',
        '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-o', $winOut
    )
    $winExe = Find-SingleFileBinary -Dir $winOut -PreferredNames @(
        'FlyScreen.App.Maui', 'FlyScreen', '玄鸟'
    ) -Extensions @('.exe')
    $named = Invoke-NamePackage -Source $winExe -Rid 'maui-win-x64'
    $results += [pscustomobject]@{ Platform = 'Windows'; Path = $winExe; Package = $named.Path }
}

# ---------- Android（Release APK，自带运行时）----------
if ($want.Android) {
    $androidOut = Join-Path $OutRoot 'maui-android'
    if (Test-Path -LiteralPath $androidOut) { Remove-Item -LiteralPath $androidOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $androidOut | Out-Null
    Invoke-DotNetPublish -Label 'MAUI-Android' -PublishArgs @(
        'publish', $proj,
        '-c', $Configuration,
        '-f', 'net10.0-android',
        '-p:AndroidPackageFormat=apk',
        '-o', $androidOut
    )

    # SDK 签完常删 *-Signed.apk（只留 .idsig）；发布后再强制补回
    $ensureScript = Join-Path $RepoRoot 'build\Ensure-MauiSignedApk.ps1'
    $tfmApkDir = Join-Path $RepoRoot "src\FlyScreen.App.Maui\bin\$Configuration\net10.0-android"
    if (Test-Path -LiteralPath $ensureScript) {
        & $ensureScript -OutDir $tfmApkDir -PackageId 'com.xuanniao.flyscreen'
        & $ensureScript -OutDir $androidOut -PackageId 'com.xuanniao.flyscreen'
    }

    # 优先 *-Signed.apk（已签名可安装）；未签名的 .apk 会导致 packageInfo is null
    $candidates = @(Get-ChildItem -LiteralPath $androidOut -Filter '*.apk' -Recurse -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        $candidates = @(Get-ChildItem -Path (Join-Path $RepoRoot 'src\FlyScreen.App.Maui\bin') -Filter '*.apk' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Release|publish' })
    }
    $apk = $candidates |
        Sort-Object @{ Expression = { if ($_.Name -match 'Signed') { 0 } else { 1 } } }, LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $apk) {
        throw "未找到 Android APK，请检查 Android SDK / workload 是否已安装。"
    }
    Write-Host "使用 APK: $($apk.FullName)"

    $named = Invoke-NamePackage -Source $apk.FullName -Rid 'maui-android'
    $results += [pscustomobject]@{ Platform = 'Android'; Path = $apk.FullName; Package = $named.Path }
}

# ---------- Mac Catalyst（须在 macOS 上）----------
if ($want.Mac) {
    if (-not $isMacHost) {
        Write-Host ""
        Write-Host "跳过 Mac Catalyst：当前不是 macOS。请在 Mac 上执行：" -ForegroundColor Yellow
        Write-Host "  pwsh -File build/Publish-FlyScreenMaui.ps1 -Platforms Mac -MacRid $MacRid"
    }
    else {
        $macRidNorm = $MacRid.Trim().ToLowerInvariant()
        if ($macRidNorm -notmatch '^maccatalyst-(arm64|x64)$') {
            throw "MacRid 应为 maccatalyst-arm64 或 maccatalyst-x64，当前: $MacRid"
        }
        $pkgRid = if ($macRidNorm -eq 'maccatalyst-arm64') { 'maui-osx-arm64' } else { 'maui-osx-x64' }
        $macOut = Join-Path $OutRoot $pkgRid
        if (Test-Path -LiteralPath $macOut) { Remove-Item -LiteralPath $macOut -Recurse -Force }
        Invoke-DotNetPublish -Label "MAUI-MacCatalyst single-file ($macRidNorm)" -PublishArgs @(
            'publish', $proj,
            '-c', $Configuration,
            '-f', 'net10.0-maccatalyst',
            '-r', $macRidNorm,
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:CreatePackage=false',
            '-o', $macOut
        )

        $macBin = Find-SingleFileBinary -Dir $macOut -PreferredNames @(
            'FlyScreen.App.Maui', 'FlyScreen'
        ) -Extensions @('', '.dll')
        # Mac 单文件通常无扩展名；若误命中 dll 则再搜无扩展名大文件
        if ($macBin.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            $macBin = Find-SingleFileBinary -Dir $macOut -PreferredNames @(
                'FlyScreen.App.Maui', 'FlyScreen'
            ) -Extensions @('')
        }
        $named = Invoke-NamePackage -Source $macBin -Rid $pkgRid
        $results += [pscustomobject]@{ Platform = 'Mac'; Path = $macBin; Package = $named.Path }
    }
}

Write-Host ""
Write-Host "=== MAUI 发布完成 ===" -ForegroundColor Green
$results | Format-Table -AutoSize
if (-not $SkipName) {
    Write-Host "命名包目录: $(Join-Path $RepoRoot 'artifacts\release')"
}

$results
