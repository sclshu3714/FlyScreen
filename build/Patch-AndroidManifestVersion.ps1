# 强制改写 AndroidManifest 中的 versionName / versionCode
# Files: 分号分隔的绝对路径列表
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$VersionCode,
    [Parameter(Mandatory = $true)][string]$Files
)

$ErrorActionPreference = 'Continue'
$code = 0
foreach ($f in ($Files -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    if (-not (Test-Path -LiteralPath $f)) {
        Write-Host "skip missing $f"
        continue
    }
    try {
        $t = [IO.File]::ReadAllText($f)
        if ($t -notmatch 'android:versionName=') {
            Write-Host "skip no versionName $f"
            continue
        }
        $n = [regex]::Replace($t, 'android:versionName="[^"]*"', "android:versionName=`"$Version`"")
        $n = [regex]::Replace($n, 'android:versionCode="[^"]*"', "android:versionCode=`"$VersionCode`"")
        if ($n -ne $t) {
            [IO.File]::WriteAllText($f, $n)
            Write-Host "patched $f -> versionName=$Version versionCode=$VersionCode"
        }
        else {
            Write-Host "ok $f (already $Version / $VersionCode)"
        }
    }
    catch {
        Write-Host "fail $f : $($_.Exception.Message)"
        $code = 1
    }
}
exit $code
