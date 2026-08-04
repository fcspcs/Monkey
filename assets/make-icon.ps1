#
# Erzeugt aus assets/monkey.png die Symboldatei assets/monkey.ico.
# Das Quellbild ist nicht quadratisch; es wird seitenverhaeltnistreu in eine
# quadratische, transparente Flaeche eingepasst.
#
# Wichtig: Alle Groessen bis 128 werden als klassisches BMP (BITMAPINFOHEADER)
# abgelegt, nur 256 als PNG. Grund: System.Drawing.Icon kann PNG-komprimierte
# Eintraege nicht dekodieren - ein reines PNG-Symbol ergaebe im Tray Bildmuell.
# Windows selbst kaeme mit PNG zurecht, .NET nicht.
#
# Muss nur neu laufen, wenn das Quellbild wechselt - die .ico liegt im Repo.
#
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot "monkey.png"),
    [string]$Target = (Join-Path $PSScriptRoot "monkey.ico")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-Square([System.Drawing.Image]$img, [int]$size) {
    $canvas = New-Object System.Drawing.Bitmap -ArgumentList $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = [Math]::Max(1, [int]($size * 0.04))
        $box = $size - 2 * $pad
        $scale = [Math]::Min($box / $img.Width, $box / $img.Height)
        $w = [int][Math]::Round($img.Width * $scale)
        $h = [int][Math]::Round($img.Height * $scale)
        $x = [int](($size - $w) / 2)
        $y = [int](($size - $h) / 2)
        $g.DrawImage($img, $x, $y, $w, $h)
    } finally { $g.Dispose() }
    return $canvas
}

# 32-Bit-BMP fuer ein Symbol: Kopf, Farbdaten von unten nach oben, dann die
# (bei 32 Bit unbenutzte, aber formal noetige) Maske.
function ConvertTo-IconBmp([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([UInt32]40)      # biSize
    $bw.Write([Int32]$w)       # biWidth
    $bw.Write([Int32]($h * 2)) # biHeight: Farbe + Maske
    $bw.Write([UInt16]1)       # biPlanes
    $bw.Write([UInt16]32)      # biBitCount
    $bw.Write([UInt32]0)       # biCompression = BI_RGB
    $bw.Write([UInt32]($w * $h * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buffer = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($buffer, $y * $stride, $w * 4)
        }
    } finally { $bmp.UnlockBits($data) }

    # AND-Maske: Zeilen auf 4 Byte aufgefuellt, komplett 0 (Alpha regelt alles).
    $maskRow = [int][Math]::Floor((($w + 31) / 32)) * 4
    $zero = New-Object byte[] ($maskRow * $h)
    $bw.Write($zero)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()

    # Komma davor: sonst entrollt PowerShell das Byte-Array beim Zurueckgeben,
    # und beim Aufrufer landet ein Object[] - dann greift beim Schreiben die
    # falsche Ueberladung und die Daten werden verstuemmelt.
    return , $bytes
}

$image = [System.Drawing.Image]::FromFile($Source)
try {
    $entries = @()
    foreach ($size in $sizes) {
        $canvas = New-Square $image $size
        if ($size -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $ms.ToArray()
            $ms.Dispose()
        } else {
            $bytes = ConvertTo-IconBmp $canvas
        }
        $canvas.Dispose()
        $entries += , @{ Size = $size; Bytes = $bytes }
    }

    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($out)
    $bw.Write([UInt16]0)               # reserviert
    $bw.Write([UInt16]1)               # Typ 1 = Symbol
    $bw.Write([UInt16]$entries.Count)

    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$e.Bytes.Length)
        $bw.Write([UInt32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes, 0, $e.Bytes.Length) }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Target, $out.ToArray())
    $bw.Dispose(); $out.Dispose()
}
finally { $image.Dispose() }

Write-Host "Erzeugt: $Target ($([int]((Get-Item $Target).Length / 1KB)) KB, Groessen: $($sizes -join ', '))"
