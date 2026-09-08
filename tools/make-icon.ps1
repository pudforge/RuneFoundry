# Builds the application .ico from the sprite strip the icon is drawn in.
#
# The strip holds the 32x32 at the left edge and the 16x16 beside it. Both are hand-drawn
# at their own size: shrinking a 32 to 16 turns two-pixel detail into mush, which is why
# the small one is authored rather than generated.
#
# Run by each program on build whenever its .png is newer than its .ico, so an updated
# drawing cannot be left out of the exe. The launcher and the editor have a drawing each,
# because they ship in the same folder and identical icons would be no help to anyone.
# Can also be run by hand:
#
#     powershell -File tools\make-icon.ps1 assets\app-icon-src-editor.png assets\RuneFoundry.Editor.ico
#
# System.Drawing can only save a single-image icon, so the container is written by hand:
# an ICONDIR, one ICONDIRENTRY per size, and each image as a bottom-up 32-bit DIB followed
# by the 1-bit AND mask Windows still expects. Sizes are ascending, as icon editors write
# them. There is no 48 or 256 here yet; when there is a larger drawing, add it to $images
# and give the 256 a PNG-compressed entry rather than a raw DIB.

param(
    [Parameter(Mandatory = $true)] [string] $Source,
    [Parameter(Mandatory = $true)] [string] $Destination
)

Add-Type -AssemblyName System.Drawing

$sheet = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))

# The 16x16 is found rather than assumed: it sits next to the 32, but not always flush
# against a particular column.
function Find-Art([int]$from, [int]$to, [int]$size) {
    for ($x = $from; $x -le ($to - $size); $x++) {
        for ($y = 0; $y -le ($sheet.Height - $size); $y++) {
            for ($i = 0; $i -lt $size; $i++) {
                if ($sheet.GetPixel($x, $y + $i).A -gt 0) { return @($x, $y) }
            }
        }
    }
    throw "No $($size)x$size artwork found between x=$from and x=$to in $Source."
}

$small = Find-Art 32 $sheet.Width 16

$images = @(
    @{ Size = 16; X = $small[0]; Y = $small[1] },
    @{ Size = 32; X = 0;         Y = 0 }
)

$blobs = @()
foreach ($img in $images) {
    $size = $img.Size
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)

    $maskRow = [int][Math]::Floor(($size + 31) / 32) * 4     # 1bpp rows pad to 4 bytes
    $colourBytes = $size * $size * 4
    $maskBytes = $maskRow * $size

    $w.Write([int]40)                        # BITMAPINFOHEADER size
    $w.Write([int]$size)                     # width
    $w.Write([int]($size * 2))               # height: colour rows plus mask rows
    $w.Write([int16]1)                       # planes
    $w.Write([int16]32)                      # bits per pixel
    $w.Write([int]0)                         # BI_RGB
    $w.Write([int]($colourBytes + $maskBytes))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)

    for ($row = $size - 1; $row -ge 0; $row--) {
        for ($col = 0; $col -lt $size; $col++) {
            $p = $sheet.GetPixel($img.X + $col, $img.Y + $row)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
        }
    }

    # Ignored for a 32-bit icon, but it has to be present and the right size. Zero means
    # "the alpha channel decides".
    for ($i = 0; $i -lt $maskBytes; $i++) { $w.Write([byte]0) }

    $w.Flush()
    $blobs += ,$ms.ToArray()
    $w.Dispose(); $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)

$offset = 6 + 16 * $images.Count
for ($i = 0; $i -lt $images.Count; $i++) {
    $size = $images[$i].Size
    $w.Write([byte]$size); $w.Write([byte]$size)
    $w.Write([byte]0); $w.Write([byte]0)         # palette entries, reserved
    $w.Write([int16]1); $w.Write([int16]32)      # planes, bits per pixel
    $w.Write([int]$blobs[$i].Length)
    $w.Write([int]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $w.Write($b) }
$w.Flush()

$full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Destination))
[System.IO.File]::WriteAllBytes($full, $out.ToArray())
$w.Dispose(); $out.Dispose(); $sheet.Dispose()

# Loading it back is the cheapest proof the container is well formed.
$check = New-Object System.Drawing.Icon($full)
Write-Output "make-icon: wrote $full ($((Get-Item $full).Length) bytes, $($images.Count) sizes)"
$check.Dispose()
