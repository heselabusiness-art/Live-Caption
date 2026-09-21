# Builds a multi-resolution Windows .ico from a single source PNG.
# Sizes embedded:  16, 24, 32, 48, 64, 128, 256 (all PNG-compressed,
# which Windows 10/11 accept and which keeps the file small).
param(
    [Parameter(Mandatory = $true)] [string] $Source,
    [Parameter(Mandatory = $true)] [string] $Output
)

Add-Type -AssemblyName System.Drawing

$src   = [System.Drawing.Image]::FromFile($Source)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$png   = New-Object System.Collections.ArrayList

foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $sz, $sz)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [void]$png.Add(@{ Size = $sz; Bytes = $ms.ToArray() })
}
$src.Dispose()

# ── Compose ICO container ─────────────────────────────────────────────────────
$out    = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$writer.Write([UInt16]0)                       # reserved
$writer.Write([UInt16]1)                       # type = 1 (icon)
$writer.Write([UInt16]$png.Count)              # entry count

$offset = 6 + (16 * $png.Count)
foreach ($e in $png) {
    $sz = $e.Size
    $writer.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))   # width  (0 => 256)
    $writer.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))   # height (0 => 256)
    $writer.Write([byte]0)                                          # color count
    $writer.Write([byte]0)                                          # reserved
    $writer.Write([UInt16]1)                                        # color planes
    $writer.Write([UInt16]32)                                       # bits per pixel
    $writer.Write([UInt32]$e.Bytes.Length)                          # bytes in res
    $writer.Write([UInt32]$offset)                                  # offset to data
    $offset += $e.Bytes.Length
}
foreach ($e in $png) { $writer.Write($e.Bytes) }

$writer.Flush()
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
"Wrote $Output ($([System.IO.FileInfo]::new($Output).Length) bytes)"
