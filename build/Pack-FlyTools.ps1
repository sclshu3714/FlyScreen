# Pack tool projects into .flytool and copy ONLY to App bin output Tools/Bundled.
# NEVER write under src/**/Tools/Bundled (source tree). NEVER write AppData.
# Output primary: artifacts/flytools ; optional copy: <App>/bin/<cfg>/<tfm>/Tools/Bundled
#
# Maui 多 TFM / 多宿主并行构建时会同时调用本脚本；用命名互斥串行化，避免抢写 .flytool。
param(
    [string]$Configuration = "Debug",
    [string]$OutRoot = "",
    [switch]$SkipAppCopy,
    # Force: rebuild zip even if dll/manifest not newer than existing .flytool
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
# 组包暂存目录：最终产物在 artifacts/flytools（或 -OutRoot），结束后必须删除
$stage = Join-Path $repo "artifacts/flytools-stage"

# 串行化：VS 多 TFM 并行时否则会同时 Zip / 删 stage 导致 exit 1
$mutex = $null
$mutexAcquired = $false
try {
    $mutex = New-Object System.Threading.Mutex($false, "Local\FlyScreen.PackFlyTools.v1")
    Write-Host "Pack-FlyTools: waiting for lock..."
    $mutexAcquired = $mutex.WaitOne([TimeSpan]::FromMinutes(45))
    if (-not $mutexAcquired) {
        throw "Pack-FlyTools: timed out waiting for pack lock (another build stuck?)"
    }
    Write-Host "Pack-FlyTools: lock acquired"
}
catch {
    Write-Error $_
    exit 1
}

try {
$tools = @(
    @{
        Id = "color-picker"
        Proj = "src/Tools/ColorPicker/FlyScreen.Tool.ColorPicker/FlyScreen.Tool.ColorPicker.csproj"
        Manifest = "src/Tools/ColorPicker/FlyScreen.Tool.ColorPicker/manifest.json"
        UiProjs = @(
            "src/Tools/ColorPicker/FlyScreen.Tool.ColorPicker.Ui.Win/FlyScreen.Tool.ColorPicker.Ui.Win.csproj",
            "src/Tools/ColorPicker/FlyScreen.Tool.ColorPicker.Ui.Desktop/FlyScreen.Tool.ColorPicker.Ui.Desktop.csproj",
            "src/Tools/ColorPicker/FlyScreen.Tool.ColorPicker.Ui.Maui/FlyScreen.Tool.ColorPicker.Ui.Maui.csproj"
        )
    },
    @{
        Id = "shared-devices"
        Proj = "src/Tools/SharedDevices/FlyScreen.Tool.SharedDevices/FlyScreen.Tool.SharedDevices.csproj"
        Manifest = "src/Tools/SharedDevices/FlyScreen.Tool.SharedDevices/manifest.json"
        UiProjs = @(
            "src/Tools/SharedDevices/FlyScreen.Tool.SharedDevices.Ui.Win/FlyScreen.Tool.SharedDevices.Ui.Win.csproj",
            "src/Tools/SharedDevices/FlyScreen.Tool.SharedDevices.Ui.Desktop/FlyScreen.Tool.SharedDevices.Ui.Desktop.csproj",
            "src/Tools/SharedDevices/FlyScreen.Tool.SharedDevices.Ui.Maui/FlyScreen.Tool.SharedDevices.Ui.Maui.csproj"
        )
    },
    @{
        Id = "port-proxy"
        Proj = "src/Tools/PortProxy/FlyScreen.Tool.PortProxy/FlyScreen.Tool.PortProxy.csproj"
        Manifest = "src/Tools/PortProxy/FlyScreen.Tool.PortProxy/manifest.json"
        UiProjs = @(
            "src/Tools/PortProxy/FlyScreen.Tool.PortProxy.Ui.Win/FlyScreen.Tool.PortProxy.Ui.Win.csproj",
            "src/Tools/PortProxy/FlyScreen.Tool.PortProxy.Ui.Maui/FlyScreen.Tool.PortProxy.Ui.Maui.csproj"
        )
    },
    @{
        Id = "video-wall"
        Proj = "src/Tools/VideoWall/FlyScreen.Tool.VideoWall/FlyScreen.Tool.VideoWall.csproj"
        Manifest = "src/Tools/VideoWall/FlyScreen.Tool.VideoWall/manifest.json"
        UiProjs = @(
            "src/Tools/VideoWall/FlyScreen.Tool.VideoWall.Ui.Win/FlyScreen.Tool.VideoWall.Ui.Win.csproj",
            "src/Tools/VideoWall/FlyScreen.Tool.VideoWall.Ui.Desktop/FlyScreen.Tool.VideoWall.Ui.Desktop.csproj",
            "src/Tools/VideoWall/FlyScreen.Tool.VideoWall.Ui.Maui/FlyScreen.Tool.VideoWall.Ui.Maui.csproj"
        )
    },
    @{
        Id = "developer-tools"
        Proj = "src/Tools/DeveloperTools/FlyScreen.Tool.DeveloperTools/FlyScreen.Tool.DeveloperTools.csproj"
        Manifest = "src/Tools/DeveloperTools/FlyScreen.Tool.DeveloperTools/manifest.json"
        UiProjs = @(
            "src/Tools/DeveloperTools/FlyScreen.Tool.DeveloperTools.Ui.Win/FlyScreen.Tool.DeveloperTools.Ui.Win.csproj",
            "src/Tools/DeveloperTools/FlyScreen.Tool.DeveloperTools.Ui.Desktop/FlyScreen.Tool.DeveloperTools.Ui.Desktop.csproj",
            "src/Tools/DeveloperTools/FlyScreen.Tool.DeveloperTools.Ui.Maui/FlyScreen.Tool.DeveloperTools.Ui.Maui.csproj"
        )
    }
)

function Test-AllowedFlyToolDest([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    $full = [IO.Path]::GetFullPath($path.Trim().TrimEnd('\', '/'))
    $norm = $full.Replace('/', '\').ToLowerInvariant()

    if ($norm -match '\\appdata\\(roaming|local)\\flyscreen(\\|$)') { return $false }

    if ($norm -match '\\src\\') {
        if ($norm -notmatch '\\bin\\' -and $norm -notmatch '\\obj\\') { return $false }
    }

    return $true
}

function Resolve-ProjectOutputDir([string]$projectFile, [string]$configuration, [string]$assemblyName, [switch]$PreferWindows) {
    $projDir = Split-Path -Parent $projectFile
    $candidates = if ($PreferWindows) {
        @(
            (Join-Path $projDir "bin/$configuration/net10.0-windows10.0.19041.0"),
            (Join-Path $projDir "bin/$configuration/net10.0-windows7.0"),
            (Join-Path $projDir "bin/$configuration/net10.0"),
            (Join-Path $projDir "bin/$configuration/net10.0-android")
        )
    }
    else {
        @(
            (Join-Path $projDir "bin/$configuration/net10.0"),
            (Join-Path $projDir "bin/$configuration/net10.0-windows7.0"),
            (Join-Path $projDir "bin/$configuration/net10.0-windows10.0.19041.0"),
            (Join-Path $projDir "bin/$configuration/net10.0-android")
        )
    }
    foreach ($dir in $candidates) {
        $dll = Join-Path $dir "$assemblyName.dll"
        if (Test-Path -LiteralPath $dll) { return $dir }
    }
    return $null
}

function Get-NewestUtc([string[]]$paths) {
    $max = [datetime]::MinValue
    foreach ($p in $paths) {
        if ([string]::IsNullOrWhiteSpace($p) -or -not (Test-Path -LiteralPath $p)) { continue }
        $t = (Get-Item -LiteralPath $p).LastWriteTimeUtc
        if ($t -gt $max) { $max = $t }
    }
    return $max
}

function Get-Sha256Hex([string]$path) {
    # 不用 Get-FileHash：MSBuild 调起的 powershell -NoProfile 偶发未加载 Utility 模块
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($path)
        try {
            $bytes = $sha.ComputeHash($fs)
            return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
        }
        finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Add-IndexEntry([string]$id, [string]$manifestPath, [string]$flyPath) {
    $hash = Get-Sha256Hex $flyPath
    $mf = Get-Content $manifestPath -Raw | ConvertFrom-Json
    return [ordered]@{
        id = $id
        version = $mf.package.version
        fileName = [IO.Path]::GetFileName($flyPath)
        sha256 = $hash
    }
}

function Write-FlyToolZip([string]$packStage, [string]$flyPath) {
    $tmp = "$flyPath.packing"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $flyPath) { Remove-Item -LiteralPath $flyPath -Force -ErrorAction SilentlyContinue }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packStage, $tmp)
    $retries = 0
    while ($true) {
        try {
            Move-Item -LiteralPath $tmp -Destination $flyPath -Force
            break
        }
        catch {
            $retries++
            if ($retries -ge 8) { throw }
            Start-Sleep -Milliseconds (200 * $retries)
        }
    }
}

$bundledOut = if ($OutRoot) { $OutRoot } else { Join-Path $repo "artifacts/flytools" }
$bundledOut = [IO.Path]::GetFullPath($bundledOut)
if (-not (Test-AllowedFlyToolDest $bundledOut)) {
    throw "Refusing OutRoot (forbidden path): $bundledOut"
}
New-Item -ItemType Directory -Force -Path $bundledOut | Out-Null
# 每次组包前清空 stage，避免残留污染本次 zip
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$index = [ordered]@{ schemaVersion = 1; tools = @() }

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($t in $tools) {
    $proj = Join-Path $repo $t.Proj
    if (-not (Test-Path -LiteralPath $proj)) { throw "missing project: $($t.Proj)" }
    $manifest = Join-Path $repo $t.Manifest
    if (-not (Test-Path -LiteralPath $manifest)) { throw "missing manifest: $($t.Manifest)" }

    Write-Host "Building $($t.Id)..."
    dotnet build $proj -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed $($t.Id)" }

    $asmName = [IO.Path]::GetFileNameWithoutExtension($proj)
    $outDir = Resolve-ProjectOutputDir $proj $Configuration $asmName
    if (-not $outDir) { throw "missing output dll for $asmName under $(Split-Path $proj -Parent)/bin/$Configuration" }

    # 用 List 避免 PS「+= 单个 hashtable 退化成非数组」陷阱
    $uiDlls = New-Object 'System.Collections.Generic.List[string]'
    $uiPackItems = New-Object 'System.Collections.Generic.List[object]'
    foreach ($uiRel in @($t.UiProjs)) {
        $uiProj = Join-Path $repo $uiRel
        if (-not (Test-Path -LiteralPath $uiProj)) { continue }
        Write-Host "  Building UI $uiRel..."
        dotnet build $uiProj -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed UI $uiRel" }
        $uiAsm = [IO.Path]::GetFileNameWithoutExtension($uiProj)
        $preferWin = $uiAsm -like "*.Ui.Maui"
        $uiOut = Resolve-ProjectOutputDir $uiProj $Configuration $uiAsm -PreferWindows:$preferWin
        if ($uiOut) {
            $uiDll = Join-Path $uiOut "$uiAsm.dll"
            [void]$uiDlls.Add($uiDll)
            [void]$uiPackItems.Add([pscustomobject]@{ Asm = $uiAsm; OutDir = $uiOut })
            Write-Host "  + $uiAsm.dll  <- $uiOut"
        }
        else {
            Write-Host "  ! skip missing UI dll $uiAsm"
        }
    }

    $mainDll = Join-Path $outDir "$asmName.dll"
    $fly = Join-Path $bundledOut "$($t.Id).flytool"
    $inputs = @($manifest, $mainDll) + $uiDlls.ToArray()
    $newestInput = Get-NewestUtc $inputs
    $flyTime = if (Test-Path -LiteralPath $fly) { (Get-Item -LiteralPath $fly).LastWriteTimeUtc } else { [datetime]::MinValue }

    if (-not $Force -and (Test-Path -LiteralPath $fly) -and $newestInput -le $flyTime) {
        Write-Host "Up-to-date $($t.Id).flytool (inputs <= pack $($flyTime.ToLocalTime().ToString('HH:mm:ss')))"
        $index.tools += Add-IndexEntry $t.Id $manifest $fly
        continue
    }

    Write-Host "Packing $($t.Id) (inputs newer than pack)..."
    $packStage = Join-Path $stage $t.Id
    Remove-Item -Recurse -Force $packStage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $packStage | Out-Null
    Copy-Item $manifest (Join-Path $packStage "manifest.json") -Force
    Copy-Item $mainDll $packStage -Force

    if ($t.Id -eq "video-wall") {
        foreach ($dep in @(
            "LibVLCSharp.dll",
            "System.Security.Cryptography.ProtectedData.dll"
        )) {
            $depPath = Join-Path $outDir $dep
            if (Test-Path -LiteralPath $depPath) {
                Copy-Item $depPath $packStage -Force
                Write-Host "  + $dep"
            }
            else {
                Write-Host "  ! missing $dep under $outDir"
            }
        }
        $libVlc = Join-Path $outDir "libvlc/win-x64"
        if (Test-Path -LiteralPath $libVlc) {
            $nativeDest = Join-Path $packStage "runtimes/win-x64/native"
            New-Item -ItemType Directory -Force -Path $nativeDest | Out-Null
            Copy-Item (Join-Path $libVlc "*") $nativeDest -Recurse -Force
            $libVlcDest = Join-Path $packStage "libvlc/win-x64"
            New-Item -ItemType Directory -Force -Path $libVlcDest | Out-Null
            Copy-Item (Join-Path $libVlc "*") $libVlcDest -Recurse -Force
            Write-Host "  + libvlc/win-x64 (native)"
        }
        else {
            Write-Host "  ! missing libvlc/win-x64 under $outDir"
        }
    }

    foreach ($item in $uiPackItems) {
        $uiAsm = $item.Asm
        $uiOut = $item.OutDir
        Copy-Item (Join-Path $uiOut "$uiAsm.dll") $packStage -Force
        if ($t.Id -eq "video-wall" -and $uiAsm -like "*.Ui.Win") {
            foreach ($dep in @("LibVLCSharp.WPF.dll", "LibVLCSharp.dll")) {
                $depPath = Join-Path $uiOut $dep
                if (Test-Path -LiteralPath $depPath) {
                    Copy-Item $depPath $packStage -Force
                    Write-Host "  + $dep"
                }
            }
        }
        # Ui.Maui：仅 HWND + Controls，不打包 LibVLCSharp.MAUI（宿主零依赖；内核 DLL 已在主包）
    }

    Write-FlyToolZip $packStage $fly

    $len = (Get-Item $fly).Length
    $entry = Add-IndexEntry $t.Id $manifest $fly
    Write-Host "Packed $fly ($len bytes, sha256=$($entry.sha256))"
    $index.tools += $entry
}

$indexPath = Join-Path $bundledOut "tools-index.json"
($index | ConvertTo-Json -Depth 6) | Set-Content -Path $indexPath -Encoding UTF8

if (-not $SkipAppCopy) {
    $targets = @(
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows7.0",
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows",
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows10.0.19041.0",
        "src/FlyScreen.App.Desktop/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Mac/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Linux/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Maui/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64",
        "src/FlyScreen.App.Maui/bin/$Configuration/net10.0-android"
    )
    foreach ($rel in $targets) {
        $dir = Join-Path $repo $rel
        if (-not (Test-Path $dir)) { continue }
        $dest = Join-Path $dir "Tools/Bundled"
        if (-not (Test-AllowedFlyToolDest $dest)) {
            Write-Host "SKIP forbidden dest: $dest"
            continue
        }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item (Join-Path $bundledOut "*.flytool") $dest -Force
        Copy-Item $indexPath $dest -Force
        Write-Host "Copied Bundled -> $dest"
    }
}

$polluted = @(
    "src/FlyScreen.App.Windows/Tools/Bundled",
    "src/FlyScreen.App.Maui/Tools/Bundled",
    "src/FlyScreen.App.Desktop/Tools/Bundled",
    "src/FlyScreen.App.Mac/Tools/Bundled",
    "src/FlyScreen.App.Linux/Tools/Bundled"
)
foreach ($rel in $polluted) {
    $dir = Join-Path $repo $rel
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in ".flytool", ".json" } |
        ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "Removed source pollution: $($_.FullName)"
        }
}

Write-Host "Done. Output: $bundledOut"
exit 0
}
catch {
    Write-Host "Pack-FlyTools FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace }
    exit 1
}
finally {
    # 无论成功失败都清掉 stage，只保留 artifacts/flytools（或 -OutRoot）最终产物
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        try {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop
            Write-Host "Cleaned stage: $stage"
        }
        catch {
            Write-Host "Clean stage failed (可稍后手动删除): $stage — $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if ($mutexAcquired -and $mutex) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    if ($mutex) { $mutex.Dispose() }
}
