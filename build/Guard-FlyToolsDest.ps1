# Refuse .flytool destinations under AppData or source-tree (non bin/obj).
# Allow: artifacts\, ...\bin\..., ...\obj\...
# Called from MSBuild Exec — NEVER pass a path ending with '\' inside quotes
# (trailing \" escapes the closing quote and breaks -Path).
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = "Continue"
if ([string]::IsNullOrWhiteSpace($Path)) {
    Write-Host "FlyScreen: refuse empty flytool dest"
    exit 1
}

$raw = $Path.Replace('/', '\').Trim().TrimEnd('\')

# Relative Tools\Bundled without bin/obj is always source pollution
if (-not [IO.Path]::IsPathRooted($raw)) {
    if ($raw -notmatch '(?i)(^|\\)(bin|obj)\\') {
        Write-Host "FlyScreen: refuse relative (non-bin) flytool dest: $Path"
        exit 1
    }
}

try {
    $n = [IO.Path]::GetFullPath($raw).Replace('/', '\').ToLowerInvariant()
}
catch {
    Write-Host "FlyScreen: refuse unresolvable flytool dest: $Path"
    exit 1
}

if ($n -match '\\appdata\\(roaming|local)\\flyscreen(\\|$)') {
    Write-Host "FlyScreen: refuse AppData flytool dest: $Path"
    exit 1
}

# Under repo src\: only bin/obj output trees are allowed
if ($n -match '\\src\\') {
    $ok = ($n -match '\\bin\\') -or ($n -match '\\obj\\')
    if (-not $ok) {
        Write-Host "FlyScreen: refuse source-tree flytool dest: $Path"
        exit 1
    }
}

# Explicit ban: App project Tools without bin (defense in depth)
if ($n -match '\\src\\flyscreen\.app\.[^\\]+\\tools(\\|$)' -and $n -notmatch '\\bin\\' -and $n -notmatch '\\obj\\') {
    Write-Host "FlyScreen: refuse App project Tools dest: $Path"
    exit 1
}

exit 0
