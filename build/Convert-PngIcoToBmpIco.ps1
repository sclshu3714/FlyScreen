# 把 PNG 压缩的 .ico 转成经典 BMP DIB .ico。
# .NET 单文件宿主只会把 BMP 帧写进 exe；PNG 帧会被丢掉，任务管理器就显示默认蓝块。
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$srcPath = (Resolve-Path -LiteralPath $Source).Path
$bytes = [IO.File]::ReadAllBytes($srcPath)
if ($bytes.Length -lt 6) { throw "不是 ico: $srcPath" }
$count = [BitConverter]::ToUInt16($bytes, 4)
if ($count -lt 1) { throw "ico 没有图像: $srcPath" }

function New-Dib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $absStride = [Math]::Abs($stride)
        $raw = New-Object byte[] ($absStride * $h)
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)

        $xor = New-Object byte[] ($w * $h * 4)
        for ($y = 0; $y -lt $h; $y++) {
            $srcY = if ($stride -ge 0) { $h - 1 - $y } else { $y }
            $src = $srcY * $absStride
            [Buffer]::BlockCopy($raw, $src, $xor, $y * $w * 4, $w * 4)
        }
    }
    finally {
        $bmp.UnlockBits($data)
    }

    $maskStride = [int]((($w + 31) -shr 5) * 4)
    $mask = New-Object byte[] ($maskStride * $h)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $a = $xor[($y * $w + $x) * 4 + 3]
            if ($a -lt 128) {
                $mask[$y * $maskStride + ($x -shr 3)] = $mask[$y * $maskStride + ($x -shr 3)] -bor (0x80 -shr ($x -band 7))
            }
        }
    }

    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($xor.Length + $mask.Length))
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write($xor)
    $bw.Write($mask)
    $bw.Flush()
    return $ms.ToArray()
}

$dibs = New-Object System.Collections.Generic.List[byte[]]
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + $i * 16
    $sz = [BitConverter]::ToUInt32($bytes, $o + 8)
    $off = [BitConverter]::ToUInt32($bytes, $o + 12)
    $chunk = New-Object byte[] $sz
    [Buffer]::BlockCopy($bytes, $off, $chunk, 0, $sz)

    $img = $null
    $bmp = $null
    try {
        $ms = New-Object IO.MemoryStream(,$chunk)
        $img = [System.Drawing.Image]::FromStream($ms)
        $bmp = New-Object System.Drawing.Bitmap $img.Width, $img.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($img, 0, 0, $img.Width, $img.Height)
        $g.Dispose()
        [void]$dibs.Add((New-Dib $bmp))
    }
    finally {
        if ($bmp) { $bmp.Dispose() }
        if ($img) { $img.Dispose() }
    }
}

$dir = Split-Path -Parent $Destination
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$dibs.Count)
$offset = 6 + (16 * $dibs.Count)
foreach ($dib in $dibs) {
    $headerW = [BitConverter]::ToInt32($dib, 4)
    $headerH = [BitConverter]::ToInt32($dib, 8) / 2
    $bw.Write([byte]($(if ($headerW -ge 256) { 0 } else { $headerW })))
    $bw.Write([byte]($(if ($headerH -ge 256) { 0 } else { $headerH })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$dib.Length)
    $bw.Write([uint32]$offset)
    $offset += $dib.Length
}
foreach ($dib in $dibs) { $bw.Write($dib) }
$bw.Flush()
[IO.File]::WriteAllBytes($Destination, $out.ToArray())
Write-Host "已写出 BMP 图标: $Destination ($($dibs.Count) 帧)"
