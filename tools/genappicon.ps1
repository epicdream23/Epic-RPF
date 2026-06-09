# Generates the application icon: a multi-resolution .ico (for the EXE + taskbar)
# and a matching app.png (for the in-app toolbar/window). Brand = teal diamond on a
# dark rounded tile, matching the "◆ Epic RPF" wordmark. Re-run after tweaking.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs  = @{}

function New-IconBitmap([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.Clear([System.Drawing.Color]::Transparent)

  # rounded tile
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
  $top = [System.Drawing.Color]::FromArgb(255, 19, 24, 32)
  $bot = [System.Drawing.Color]::FromArgb(255, 9, 12, 16)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
  $g.FillPath($grad, $path)
  if ($s -ge 32) {
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 30, 40, 54), [float]([Math]::Max($s/128.0, 1)))
    $g.DrawPath($pen, $path); $pen.Dispose()
  }

  # teal diamond
  $cx = $s / 2.0; $cy = $s / 2.0; $r = $s * 0.29
  $top2 = New-Object System.Drawing.PointF([float]$cx, [float]($cy - $r))
  $rgt  = New-Object System.Drawing.PointF([float]($cx + $r), [float]$cy)
  $bot2 = New-Object System.Drawing.PointF([float]$cx, [float]($cy + $r))
  $lft  = New-Object System.Drawing.PointF([float]($cx - $r), [float]$cy)
  $teal = [System.Drawing.Color]::FromArgb(255, 79, 209, 197)
  $tealD = [System.Drawing.Color]::FromArgb(255, 45, 158, 148)
  $cen = New-Object System.Drawing.PointF([float]$cx, [float]$cy)

  $brTeal = New-Object System.Drawing.SolidBrush($teal)
  $brDark = New-Object System.Drawing.SolidBrush($tealD)
  $g.FillPolygon($brTeal, @($top2, $rgt, $bot2, $lft))
  if ($s -ge 32) {
    # darker right/bottom facets for a gem look
    $g.FillPolygon($brDark, @($cen, $rgt, $bot2))
    $g.FillPolygon($brDark, @($cen, $bot2, $lft))
    # bright top-left highlight edge
    $penH = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 190, 245, 240), [float]([Math]::Max($s/110.0, 1)))
    $g.DrawLine($penH, $lft, $top2); $g.DrawLine($penH, $top2, $rgt); $penH.Dispose()
  }
  $brTeal.Dispose(); $brDark.Dispose(); $grad.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

foreach ($s in $sizes) {
  $bmp = New-IconBitmap $s
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs[$s] = $ms.ToArray()
  if ($s -eq 256) {
    $appPng = Join-Path $PSScriptRoot "..\src\App.UI\wwwroot\icons\app.png"
    [System.IO.File]::WriteAllBytes((Resolve-Path $appPng).Path, $pngs[$s])
    Write-Host "wrote app.png ($($pngs[$s].Length) bytes)"
  }
  $bmp.Dispose()
}

# assemble the .ico (PNG-compressed entries; Vista+ supports this)
$icoPath = Join-Path $PSScriptRoot "..\src\App.UI\icon.ico"
$icoPath = [System.IO.Path]::GetFullPath($icoPath)
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
  $bytes = $pngs[$s]
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))   # width  (0 = 256)
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))   # height
  $bw.Write([Byte]0)                                        # palette
  $bw.Write([Byte]0)                                        # reserved
  $bw.Write([UInt16]1)                                      # planes
  $bw.Write([UInt16]32)                                     # bpp
  $bw.Write([UInt32]$bytes.Length)                          # bytes in resource
  $bw.Write([UInt32]$offset)                                # image offset
  $offset += $bytes.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host "wrote $icoPath"
