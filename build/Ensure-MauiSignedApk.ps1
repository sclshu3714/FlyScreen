# 补齐 *-Signed.apk / FlyScreen.apk。绝不覆盖已签名的 Signed；缺失时用 keystore 对 plain 重新签名。
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir,

    [string]$PackageId = 'com.xuanniao.flyscreen',

    [string]$IntermediateDir = '',

    [string]$KeyStore = '',

    [string]$KeyStorePass = 'flyscreen',

    [string]$KeyAlias = 'flyscreen',

    [string]$KeyPass = 'flyscreen'
)

$ErrorActionPreference = 'Stop'
$OutDir = $OutDir.Trim().Trim('"').TrimEnd('\', '/')
$PackageId = $PackageId.Trim().Trim('"')
$IntermediateDir = $IntermediateDir.Trim().Trim('"').TrimEnd('\', '/')
$KeyStore = $KeyStore.Trim().Trim('"')

function Test-ApkHasV2Signature {
    param([string]$ApkPath)
    if (-not (Test-Path -LiteralPath $ApkPath)) { return $false }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $z = [IO.Compression.ZipFile]::OpenRead($ApkPath)
        try {
            # JAR 签名（v1）或仅 androidx 元数据时可能没有 CERT；用文件尾 APK Sig Block 更可靠
            $names = @($z.Entries | ForEach-Object { $_.FullName })
            $hasV1 = $names | Where-Object { $_ -match '^META-INF/.*\.(RSA|DSA|EC)$' }
            if ($hasV1) { return $true }
        }
        finally { $z.Dispose() }
    } catch { }

    # APK Signature Scheme v2/v3：文件末尾有 APK Sig Block（"APK Sig Block 42"）
    $fs = [IO.File]::OpenRead($ApkPath)
    try {
        if ($fs.Length -lt 64) { return $false }
        $fs.Seek(-4096, [IO.SeekOrigin]::End) | Out-Null
        $len = [int][Math]::Min(4096, $fs.Length)
        $buf = New-Object byte[] $len
        [void]$fs.Read($buf, 0, $len)
        $text = [Text.Encoding]::ASCII.GetString($buf)
        return $text.Contains('APK Sig Block 42')
    }
    finally { $fs.Close() }
}

function Find-ApkSigner {
    $roots = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        "$env:LOCALAPPDATA\Android\Sdk",
        "${env:ProgramFiles(x86)}\Android\android-sdk",
        'C:\Android\android-sdk'
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $bt = Join-Path $root 'build-tools'
        if (-not (Test-Path $bt)) { continue }
        $ver = Get-ChildItem -LiteralPath $bt -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $ver) { continue }
        $bat = Join-Path $ver.FullName 'apksigner.bat'
        if (Test-Path -LiteralPath $bat) { return $bat }
    }
    return $null
}

function Invoke-SignApk {
    param([string]$ApkPath, [string]$Store, [string]$StorePass, [string]$Alias, [string]$Pass)
    $apksigner = Find-ApkSigner
    if (-not $apksigner) {
        Write-Warning 'Ensure-MauiSignedApk: 未找到 apksigner，无法重新签名'
        return $false
    }
    if (-not (Test-Path -LiteralPath $Store)) {
        Write-Warning "Ensure-MauiSignedApk: keystore 不存在: $Store"
        return $false
    }
    Write-Host "Ensure-MauiSignedApk: apksigner sign → $ApkPath"
    & $apksigner sign --ks $Store --ks-key-alias $Alias --ks-pass "pass:$StorePass" --key-pass "pass:$Pass" $ApkPath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Ensure-MauiSignedApk: apksigner sign 失败 (exit $LASTEXITCODE)"
        return $false
    }
    & $apksigner verify $ApkPath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Ensure-MauiSignedApk: apksigner verify 失败'
        return $false
    }
    return $true
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    Write-Warning 'Ensure-MauiSignedApk: OutDir 为空，跳过'
    exit 0
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$plain = Join-Path $OutDir "$PackageId.apk"
$signed = Join-Path $OutDir "$PackageId-Signed.apk"
$stable = Join-Path $OutDir 'FlyScreen.apk'
$idsig = Join-Path $OutDir "$PackageId-Signed.apk.idsig"

if ([string]::IsNullOrWhiteSpace($KeyStore)) {
    $KeyStore = Join-Path $PSScriptRoot 'android\flyscreen-sideload.keystore'
}

# 从 intermediate 补 plain（仅 plain 缺失时）
if (-not (Test-Path -LiteralPath $plain) -and $IntermediateDir -ne '' -and (Test-Path -LiteralPath $IntermediateDir)) {
    $interPlain = Join-Path $IntermediateDir "$PackageId.apk"
    if (Test-Path -LiteralPath $interPlain) {
        Copy-Item -LiteralPath $interPlain -Destination $plain -Force
        Write-Host "Ensure-MauiSignedApk: 已从 intermediate 复制 plain → $plain"
    }
}

# Signed 已存在且带签名：不要覆盖
if ((Test-Path -LiteralPath $signed) -and (Test-ApkHasV2Signature $signed)) {
    Copy-Item -LiteralPath $signed -Destination $stable -Force
    Write-Host "Ensure-MauiSignedApk: Signed 已有效 → $stable"
    exit 0
}

# Signed 缺失或无效：以 plain 为底稿重新签名（勿直接把未签名 plain 当 Signed 分发）
$source = $null
if (Test-Path -LiteralPath $plain) { $source = $plain }
elseif ((Test-Path -LiteralPath $signed)) { $source = $signed }

if (-not $source) {
    Write-Host "Ensure-MauiSignedApk: 尚未找到 APK，跳过（$OutDir）"
    exit 0
}

# 若 plain 本身已签名，直接复制为 Signed
if ((Test-ApkHasV2Signature $source) -and ($source -eq $plain)) {
    Copy-Item -LiteralPath $plain -Destination $signed -Force
    Write-Host "Ensure-MauiSignedApk: plain 已签名，复制为 Signed"
}
else {
    Copy-Item -LiteralPath $source -Destination $signed -Force
    # 删除陈旧 idsig，避免与新签内容不匹配
    if (Test-Path -LiteralPath $idsig) { Remove-Item -LiteralPath $idsig -Force }
    if (-not (Invoke-SignApk -ApkPath $signed -Store $KeyStore -StorePass $KeyStorePass -Alias $KeyAlias -Pass $KeyPass)) {
        Write-Warning 'Ensure-MauiSignedApk: 重新签名失败；请检查 Android SDK build-tools / keystore'
        # 失败则删掉无效 Signed，避免安装时报 NOT_APK
        if (Test-Path -LiteralPath $signed) { Remove-Item -LiteralPath $signed -Force -ErrorAction SilentlyContinue }
        exit 0
    }
}

if (Test-Path -LiteralPath $signed) {
    Copy-Item -LiteralPath $signed -Destination $stable -Force
    Write-Host "Ensure-MauiSignedApk: 可安装 APK → $stable"
}
exit 0
