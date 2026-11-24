# Generates assets/DiskSweep.ico (multi-size, PNG-compressed) with System.Drawing.
# Run once: powershell -ExecutionPolicy Bypass -File assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [math]::Max(3, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,0), (New-Object System.Drawing.Point $s,$s), ([System.Drawing.Color]::FromArgb(43,136,216)), ([System.Drawing.Color]::FromArgb(15,95,168))
    $g.FillPath($brush, $path)
    $fontName = 'Segoe Fluent Icons'
    if (-not ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains $fontName)) { $fontName = 'Segoe MDL2 Assets' }
    $font = New-Object System.Drawing.Font $fontName, ($s * 0.50), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xEDA2, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s*0.04), $s, $s), $sf)
    if ($s -ge 32) {
        $f2 = New-Object System.Drawing.Font $fontName, ($s * 0.26), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString([string][char]0xE735, $f2, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,224,102))), (New-Object System.Drawing.RectangleF ($s*0.52), ($s*0.02), ($s*0.46), ($s*0.46)), $sf)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,$ms.ToArray(); $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'DiskSweep.ico'), $out.ToArray())
# Also a 256px PNG for the README
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\docs\icon.png'), $pngs[-1])
'icon written'
