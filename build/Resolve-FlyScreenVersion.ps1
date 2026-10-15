# 与 build/FlyScreen.Version.targets 同一规则：
# Version = 最新 vX.Y.Z 标签的 patch + 1；无标签则 1.0.0
# VersionCode = major*10000 + minor*100 + patch（单调递增）
# 覆盖：-Version 1.1.0
param(
    [string]$Version = "",
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

function ConvertTo-VersionCode([Version]$v) {
    return ($v.Major * 10000) + ($v.Minor * 100) + [Math]::Max(0, $v.Build)
}

$latestTag = ""
$resolved = $null

if ($Version) {
    $resolved = [Version]$Version
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
        $resolved = [Version]::new($tagVer.Major, $tagVer.Minor, $tagVer.Build + 1)
    }
    else {
        $resolved = [Version]"1.0.0"
    }
}

$versionName = "{0}.{1}.{2}" -f $resolved.Major, $resolved.Minor, [Math]::Max(0, $resolved.Build)
$versionCode = ConvertTo-VersionCode $resolved

[pscustomobject]@{
    Version     = $versionName
    VersionCode = [int]$versionCode
    LatestTag   = $latestTag
}
