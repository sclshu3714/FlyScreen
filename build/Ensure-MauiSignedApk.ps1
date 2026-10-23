# 补齐 *-Signed.apk / FlyScreen.apk。绝不覆盖已签名的 Signed；缺失时用 keystore 对 plain 重新签名。
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir,

    [string]$PackageId = 'com.xuanniao.flyscreen',

    [string]$IntermediateDir = '',

    [string]$KeyStore = '',

    [string]$KeyStorePass = 'flyscreen',

    [string]$KeyAlias = 'flyscreen',

    [string]$KeyPass = 'flyscreen',

    # VS 存档在 MSBuild 返回后会把 *-Signed.apk 改名为包名。此开关在改名完成后把文件拷回。
    [switch]$RestoreAfterVisualStudio,

    [switch]$ApplyIdeRestore
)

$ErrorActionPreference = 'Stop'
$OutDir = $OutDir.Trim().Trim('"').TrimEnd('\', '/')
$PackageId = $PackageId.Trim().Trim('"')

function Start-RestoreAfterVisualStudio {
    if (-not $RestoreAfterVisualStudio) { return }
    $self = $PSCommandPath
    if (-not $self) { $self = $MyInvocation.ScriptName }
    $exe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    # Win32_Process 不在 MSBuild 的作业里，生成结束不会把补回进程一起杀掉。
    $cmd = "`"$exe`" -NoProfile -ExecutionPolicy Bypass -File `"$self`" -OutDir `"$OutDir`" -PackageId `"$PackageId`" -ApplyIdeRestore"
    $started = $false
    try {
        $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        $started = ($result.ReturnValue -eq 0)
    }
    catch { }
    if (-not $started) {
        Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $self,
            '-OutDir', $OutDir, '-PackageId', $PackageId, '-ApplyIdeRestore'
        ) | Out-Null
    }
}

if ($ApplyIdeRestore) {
    $plainWait = Join-Path $OutDir "$PackageId.apk"
    $signedWait = Join-Path $OutDir "$PackageId-Signed.apk"
    $stableWait = Join-Path $OutDir 'FlyScreen.apk'
    $archiveRoot = Join-Path $env:LOCALAPPDATA 'Xamarin\Mono for Android\Archives'
    for ($i = 0; $i -lt 80; $i++) {
        $hasPlain = Test-Path -LiteralPath $plainWait
        $hasSigned = Test-Path -LiteralPath $signedWait
        # IDE 会把 *-Signed.apk 改名为包名。包名还在、Signed 不在时，把同一份字节拷回去。
        if ($hasPlain -and -not $hasSigned) {
            $len1 = (Get-Item -LiteralPath $plainWait).Length
            Start-Sleep -Milliseconds 400
            if ((Test-Path -LiteralPath $signedWait) -or -not (Test-Path -LiteralPath $plainWait)) { continue }
            $len2 = (Get-Item -LiteralPath $plainWait).Length
            if ($len1 -ne $len2 -or $len2 -lt 1024) { continue }
            Copy-Item -LiteralPath $plainWait -Destination $signedWait -Force
            Copy-Item -LiteralPath $plainWait -Destination $stableWait -Force
            if (Test-Path -LiteralPath $archiveRoot) {
                $hit = Get-ChildItem -LiteralPath $archiveRoot -Recurse -Filter "$PackageId.apk" -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) -and $_.Length -eq $len2 } |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($hit) {
                    Copy-Item -LiteralPath $plainWait -Destination (Join-Path $hit.DirectoryName "$PackageId-Signed.apk") -Force
                    Copy-Item -LiteralPath $plainWait -Destination (Join-Path $hit.DirectoryName 'FlyScreen.apk') -Force
                }
            }
            exit 0
        }
        Start-Sleep -Milliseconds 250
    }
    exit 0
}
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
    Copy-Item -LiteralPath $signed -Destination $plain -Force
    Write-Host "Ensure-MauiSignedApk: Signed 已有效 → $signed"
    Start-RestoreAfterVisualStudio
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
    # VS 存档复制的是不带 -Signed 的包名；用已签名内容覆盖，安装才不会 NOT_APK
    Copy-Item -LiteralPath $signed -Destination $plain -Force
    Write-Host "Ensure-MauiSignedApk: 可安装 APK → $signed"
    Start-RestoreAfterVisualStudio
}
exit 0
