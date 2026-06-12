# Generates the .epic FILE icon — visually distinct from the app icon so a .epic on the
# desktop reads as "an Epic extension package", not the app itself. The app icon is a teal
# gem on a DARK tile; the .epic file flips that: a white gem on a bright TEAL tile, with a
# small "+" extension badge. Writes a multi-res icons/epic.ico (for the shell association)
# and a matching icons/epic.png (for the in-app file list). Re-run after tweaking.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs  = @{}

function New-EpicBitmap([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.Clear([System.Drawing.Color]::Transparent)

  # rounded tile (bright teal gradient)
  $inset = [Math]::Max([int]($s * 0.06), 1)
  $rad = [double]($s * 0.22)
  $x = $inset; $y = $inset; $w = $s - 2 * $inset; $h = $s - 2 * $inset
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [float]($rad * 2)
  $path.AddArc($x, $y, $d, $d, 180, 90)
  $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $path.CloseFigure()

  $rect = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
  $top = [System.Drawing.Color]::FromArgb(255, 88, 222, 210)
  $bot = [System.Drawing.Color]::FromArgb(255, 38, 150, 140)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
  $g.FillPath($grad, $path)
  if ($s -ge 32) {
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 24, 110, 103), [float]([Math]::Max($s/128.0, 1)))
    $g.DrawPath($pen, $path); $pen.Dispose()
  }

  # white diamond gem (with a faint teal core + facets)
  $cx = $s / 2.0; $cy = $s / 2.0; $r = $s * 0.30
  $top2 = New-Object System.Drawing.PointF([float]$cx, [float]($cy - $r))
  $rgt  = New-Object System.Drawing.PointF([float]($cx + $r), [float]$cy)
  $bot2 = New-Object System.Drawing.PointF([float]$cx, [float]($cy + $r))
  $lft  = New-Object System.Drawing.PointF([float]($cx - $r), [float]$cy)
  $cen  = New-Object System.Drawing.PointF([float]$cx, [float]$cy)
  $white  = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
  $shade  = [System.Drawing.Color]::FromArgb(255, 206, 240, 236)   # cool grey-teal for lower facets

  $brWhite = New-Object System.Drawing.SolidBrush($white)
  $brShade = New-Object System.Drawing.SolidBrush($shade)
  $g.FillPolygon($brWhite, @($top2, $rgt, $bot2, $lft))
  if ($s -ge 32) {
    # lower half a touch darker for a gem look — horizontal split only (no centre seam)
    $g.FillPolygon($brShade, @($lft, $rgt, $bot2))
  }

  # small extension "+" badge, bottom-right (only when large enough to read)
  if ($s -ge 48) {
    $bs = $s * 0.30
    $bx = $x + $w - $bs * 0.92; $by = $y + $h - $bs * 0.92
    $brBadge = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 17, 23, 31))
    $g.FillEllipse($brBadge, [float]$bx, [float]$by, [float]$bs, [float]$bs)
    $penP = New-Object System.Drawing.Pen($white, [float]([Math]::Max($s/64.0, 1.5)))
    $mx = $bx + $bs / 2.0; $my = $by + $bs / 2.0; $arm = $bs * 0.26
    $g.DrawLine($penP, [float]($mx - $arm), [float]$my, [float]($mx + $arm), [float]$my)
    $g.DrawLine($penP, [float]$mx, [float]($my - $arm), [float]$mx, [float]($my + $arm))
    $penP.Dispose(); $brBadge.Dispose()
  }

  $brWhite.Dispose(); $brShade.Dispose(); $grad.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

$iconsDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\App.UI\wwwroot\icons"))

foreach ($s in $sizes) {
  $bmp = New-EpicBitmap $s
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs[$s] = $ms.ToArray()
  if ($s -eq 256) {
    [System.IO.File]::WriteAllBytes((Join-Path $iconsDir "epic.png"), $pngs[$s])
    Write-Host "wrote epic.png ($($pngs[$s].Length) bytes)"
  }
  $bmp.Dispose()
}

# assemble epic.ico (PNG-compressed entries; Vista+ supports this)
$icoPath = Join-Path $iconsDir "epic.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
  $bytes = $pngs[$s]
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$bytes.Length)
  $bw.Write([UInt32]$offset)
  $offset += $bytes.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host "wrote $icoPath"
