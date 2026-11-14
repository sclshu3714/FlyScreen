# 将工具工程打成 .flytool，复制到各 App 输出目录的 Tools/Bundled/
param(
    [string]$Configuration = "Debug",
    [string]$OutRoot = "",
    [switch]$SkipAppCopy
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
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
    }
)

function Resolve-ProjectOutputDir([string]$projectFile, [string]$configuration, [string]$assemblyName) {
    $projDir = Split-Path -Parent $projectFile
    $candidates = @(
        (Join-Path $projDir "bin/$configuration/net10.0"),
        (Join-Path $projDir "bin/$configuration/net10.0-windows7.0"),
        (Join-Path $projDir "bin/$configuration/net10.0-windows10.0.19041.0"),
        (Join-Path $projDir "bin/$configuration/net10.0-android")
    )
    foreach ($dir in $candidates) {
        $dll = Join-Path $dir "$assemblyName.dll"
        if (Test-Path -LiteralPath $dll) { return $dir }
    }
    return $null
}

$stage = Join-Path $repo "artifacts/flytools-stage"
$bundledOut = if ($OutRoot) { $OutRoot } else { Join-Path $repo "artifacts/flytools" }
New-Item -ItemType Directory -Force -Path $bundledOut | Out-Null

$index = [ordered]@{ schemaVersion = 1; tools = @() }

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

    $packStage = Join-Path $stage $t.Id
    Remove-Item -Recurse -Force $packStage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $packStage | Out-Null
    Copy-Item $manifest (Join-Path $packStage "manifest.json") -Force
    Copy-Item (Join-Path $outDir "$asmName.dll") $packStage -Force

    # 平台 UI 卫星程序集（宿主提供 Core/ToolSdk）
    foreach ($uiRel in @($t.UiProjs)) {
        $uiProj = Join-Path $repo $uiRel
        if (-not (Test-Path -LiteralPath $uiProj)) { continue }
        Write-Host "  Building UI $uiRel..."
        dotnet build $uiProj -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed UI $uiRel" }
        $uiAsm = [IO.Path]::GetFileNameWithoutExtension($uiProj)
        $uiOut = Resolve-ProjectOutputDir $uiProj $Configuration $uiAsm
        if ($uiOut) {
            Copy-Item (Join-Path $uiOut "$uiAsm.dll") $packStage -Force
            Write-Host "  + $uiAsm.dll"
        }
        else {
            Write-Host "  ! skip missing UI dll $uiAsm"
        }
    }

    $fly = Join-Path $bundledOut "$($t.Id).flytool"
    if (Test-Path $fly) { Remove-Item $fly -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packStage, $fly)

    $hash = (Get-FileHash -Algorithm SHA256 $fly).Hash.ToLowerInvariant()
    $len = (Get-Item $fly).Length
    Write-Host "Packed $fly ($len bytes, sha256=$hash)"

    $mf = Get-Content $manifest -Raw | ConvertFrom-Json
    $index.tools += [ordered]@{
        id = $t.Id
        version = $mf.package.version
        fileName = "$($t.Id).flytool"
        sha256 = $hash
    }
}

$indexPath = Join-Path $bundledOut "tools-index.json"
($index | ConvertTo-Json -Depth 6) | Set-Content -Path $indexPath -Encoding UTF8

# 复制到常见 App 输出旁 Tools/Bundled（MSBuild 目标也会做；脚本可单独跑）
if (-not $SkipAppCopy) {
    $targets = @(
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows7.0",
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows",
        "src/FlyScreen.App.Windows/bin/$Configuration/net10.0-windows10.0.19041.0",
        "src/FlyScreen.App.Desktop/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Mac/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Linux/bin/$Configuration/net10.0",
        "src/FlyScreen.App.Maui/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64"
    )
    foreach ($rel in $targets) {
        $dir = Join-Path $repo $rel
        if (-not (Test-Path $dir)) { continue }
        $dest = Join-Path $dir "Tools/Bundled"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item (Join-Path $bundledOut "*.flytool") $dest -Force
        Copy-Item $indexPath $dest -Force
        Write-Host "Copied Bundled -> $dest"
    }
}

Write-Host "Done. Output: $bundledOut"
