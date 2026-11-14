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
        Proj = "src/FlyScreen.Tool.ColorPicker/FlyScreen.Tool.ColorPicker.csproj"
        Manifest = "src/FlyScreen.Tool.ColorPicker/manifest.json"
        UiProjs = @(
            "src/FlyScreen.Tool.ColorPicker.Ui.Win/FlyScreen.Tool.ColorPicker.Ui.Win.csproj",
            "src/FlyScreen.Tool.ColorPicker.Ui.Desktop/FlyScreen.Tool.ColorPicker.Ui.Desktop.csproj",
            "src/FlyScreen.Tool.ColorPicker.Ui.Maui/FlyScreen.Tool.ColorPicker.Ui.Maui.csproj"
        )
    },
    @{
        Id = "shared-devices"
        Proj = "src/FlyScreen.Tool.SharedDevices/FlyScreen.Tool.SharedDevices.csproj"
        Manifest = "src/FlyScreen.Tool.SharedDevices/manifest.json"
        UiProjs = @(
            "src/FlyScreen.Tool.SharedDevices.Ui.Win/FlyScreen.Tool.SharedDevices.Ui.Win.csproj",
            "src/FlyScreen.Tool.SharedDevices.Ui.Desktop/FlyScreen.Tool.SharedDevices.Ui.Desktop.csproj",
            "src/FlyScreen.Tool.SharedDevices.Ui.Maui/FlyScreen.Tool.SharedDevices.Ui.Maui.csproj"
        )
    }
)

$stage = Join-Path $repo "artifacts/flytools-stage"
$bundledOut = if ($OutRoot) { $OutRoot } else { Join-Path $repo "artifacts/flytools" }
New-Item -ItemType Directory -Force -Path $bundledOut | Out-Null

$index = [ordered]@{ schemaVersion = 1; tools = @() }

foreach ($t in $tools) {
    $proj = Join-Path $repo $t.Proj
    Write-Host "Building $($t.Id)..."
    dotnet build $proj -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed $($t.Id)" }

    $outDir = Join-Path $repo ("src/" + [IO.Path]::GetFileNameWithoutExtension($t.Proj) + "/bin/$Configuration/net10.0")
    # 实际路径
    $asmName = [IO.Path]::GetFileNameWithoutExtension($t.Proj)
    $outDir = Join-Path $repo "src/$asmName/bin/$Configuration/net10.0"
    if (-not (Test-Path $outDir)) { throw "missing $outDir" }

    $packStage = Join-Path $stage $t.Id
    Remove-Item -Recurse -Force $packStage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $packStage | Out-Null
    Copy-Item (Join-Path $repo $t.Manifest) (Join-Path $packStage "manifest.json") -Force
    Copy-Item (Join-Path $outDir "$asmName.dll") $packStage -Force
    # 平台 UI 卫星程序集（宿主提供 Core/ToolSdk）
    foreach ($uiRel in @($t.UiProjs)) {
        $uiProj = Join-Path $repo $uiRel
        if (-not (Test-Path $uiProj)) { continue }
        Write-Host "  Building UI $uiRel..."
        dotnet build $uiProj -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "build failed UI $uiRel" }
        $uiAsm = [IO.Path]::GetFileNameWithoutExtension($uiRel)
        $uiOutRoots = @(
            (Join-Path $repo "src/$uiAsm/bin/$Configuration/net10.0"),
            (Join-Path $repo "src/$uiAsm/bin/$Configuration/net10.0-windows7.0"),
            (Join-Path $repo "src/$uiAsm/bin/$Configuration/net10.0-windows10.0.19041.0"),
            (Join-Path $repo "src/$uiAsm/bin/$Configuration/net10.0-android")
        )
        foreach ($uiOut in $uiOutRoots) {
            $dll = Join-Path $uiOut "$uiAsm.dll"
            if (Test-Path $dll) {
                Copy-Item $dll $packStage -Force
                Write-Host "  + $uiAsm.dll"
                break
            }
        }
    }

    $fly = Join-Path $bundledOut "$($t.Id).flytool"
    if (Test-Path $fly) { Remove-Item $fly -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packStage, $fly)

    $hash = (Get-FileHash -Algorithm SHA256 $fly).Hash.ToLowerInvariant()
    $len = (Get-Item $fly).Length
    Write-Host "Packed $fly ($len bytes, sha256=$hash)"

    $mf = Get-Content (Join-Path $repo $t.Manifest) -Raw | ConvertFrom-Json
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
