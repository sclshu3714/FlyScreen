# 与 build/FlyScreen.Version.targets / Android Gradle 同一规则：
# 自动叠加：最新 vX.Y.Z 标签 +1，且各段 >9 向高位进位（1.0.9→1.1.0，1.9.9→2.0.0）。
# 无标签则 1.0.0。VersionCode = major*10000 + minor*100 + patch。
# 覆盖：-Version 1.1.0（显式指定时不做进位改写）
# MSBuild：-WriteVersionJson path -MachineReadable
param(
    [string]$Version = "",
    [string]$RepoRoot = "",
    [string]$WriteVersionJson = "",
    [switch]$MachineReadable
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

function ConvertTo-VersionCode([int]$Major, [int]$Minor, [int]$Patch) {
    return ($Major * 10000) + ($Minor * 100) + $Patch
}

# 检查 major / minor / patch：minor、patch 大于 9 时向高位进位；major 可超过 9。
function Get-NormalizedVersionParts {
    param(
        [int]$Major,
        [int]$Minor,
        [int]$Patch
    )

    if ($Major -lt 0) { $Major = 0 }
    if ($Minor -lt 0) { $Minor = 0 }
    if ($Patch -lt 0) { $Patch = 0 }

    while ($Patch -gt 9) {
        $Patch -= 10
        $Minor++
    }
    while ($Minor -gt 9) {
        $Minor -= 10
        $Major++
    }

    return @([int]$Major, [int]$Minor, [int]$Patch)
}

function Get-BumpedVersionParts {
    param(
        [int]$Major,
        [int]$Minor,
        [int]$Patch
    )

    return Get-NormalizedVersionParts -Major $Major -Minor $Minor -Patch ($Patch + 1)
}

$latestTag = ""
$major = 1
$minor = 0
$patch = 0

if ($Version) {
    $parsed = [Version]$Version
    $major = $parsed.Major
    $minor = $parsed.Minor
    $patch = [Math]::Max(0, $parsed.Build)
}
else {
    Push-Location $RepoRoot
    try {
        $tags = @(git tag -l "v*.*.*" --sort=-v:refname 2>$null)
    }
    finally {
        Pop-Location
    }

    $latestBody = $null
    foreach ($t in $tags) {
        if ($t -match '^v(\d+\.\d+\.\d+)$') {
            $latestTag = $t
            $latestBody = $Matches[1]
            break
        }
    }

    if ($latestBody) {
        $tagVer = [Version]$latestBody
        $parts = Get-BumpedVersionParts `
            -Major $tagVer.Major `
            -Minor $tagVer.Minor `
            -Patch ([Math]::Max(0, $tagVer.Build))
        $major = $parts[0]
        $minor = $parts[1]
        $patch = $parts[2]
    }
}

$versionName = "{0}.{1}.{2}" -f $major, $minor, $patch
$versionCode = [int](ConvertTo-VersionCode -Major $major -Minor $minor -Patch $patch)

if ($WriteVersionJson) {
    $dir = Split-Path -Parent $WriteVersionJson
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    @(
        '{'
        "  `"version`": `"$versionName`","
        "  `"versionCode`": $versionCode,"
        "  `"latestTag`": `"$latestTag`""
        '}'
    ) | Set-Content -Path $WriteVersionJson -Encoding utf8
}

if ($MachineReadable) {
    Write-Output ("{0}|{1}|{2}" -f $versionName, $versionCode, $latestTag)
    return
}

[pscustomobject]@{
    Version     = $versionName
    VersionCode = $versionCode
    LatestTag   = $latestTag
}
