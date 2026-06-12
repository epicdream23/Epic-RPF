# Generates the .epic file icon and the "epic" folder icon (teal-diamond brand),
# matching the 128x128 PNG icon set in wwwroot/icons. Re-run to tweak.
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot "..\src\App.UI\wwwroot\icons"
$S = 128

function New-Bmp { $b = New-Object System.Drawing.Bitmap($S, $S); $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.Clear([System.Drawing.Color]::Transparent); return @($b, $g) }
function Round([System.Drawing.Graphics]$g, $x, $y, $w, $h, $r, $brush, $pen) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = [float]($r * 2)
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure()
  if ($brush) { $g.FillPath($brush, $p) }; if ($pen) { $g.DrawPath($pen, $p) }; $p.Dispose() }
function Diamond([System.Drawing.Graphics]$g, $cx, $cy, $r, $brush) {
  $pts = @((New-Object Drawing.PointF([float]$cx, [float]($cy - $r))), (New-Object Drawing.PointF([float]($cx + $r), [float]$cy)),
           (New-Object Drawing.PointF([float]$cx, [float]($cy + $r))), (New-Object Drawing.PointF([float]($cx - $r), [float]$cy)))
  $g.FillPolygon($brush, $pts) }

$teal  = [System.Drawing.Color]::FromArgb(255, 79, 209, 197)
$tealD = [System.Drawing.Color]::FromArgb(255, 45, 158, 148)
$white = [System.Drawing.Color]::FromArgb(255, 235, 252, 250)

# --- epic.png (file): dark rounded tile + teal gem ---
$r = New-Bmp; $b = $r[0]; $g = $r[1]
$rect = New-Object System.Drawing.Rectangle(8, 8, 112, 112)
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255,19,24,32), [System.Drawing.Color]::FromArgb(255,9,12,16), 90)
Round $g 8 8 112 112 26 $grad $null
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,30,40,54), 1.5); Round $g 8 8 112 112 26 $null $pen; $pen.Dispose()
$bt = New-Object System.Drawing.SolidBrush($teal); $bd = New-Object System.Drawing.SolidBrush($tealD)
Diamond $g 64 64 38 $bt
$cen = New-Object Drawing.PointF(64,64)
$g.FillPolygon($bd, @($cen, (New-Object Drawing.PointF(102,64)), (New-Object Drawing.PointF(64,102))))
$g.FillPolygon($bd, @($cen, (New-Object Drawing.PointF(64,102)), (New-Object Drawing.PointF(26,64))))
$penH = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235,190,245,240), 2.0)
$g.DrawLine($penH, (New-Object Drawing.PointF(26,64)), (New-Object Drawing.PointF(64,26))); $g.DrawLine($penH, (New-Object Drawing.PointF(64,26)), (New-Object Drawing.PointF(102,64))); $penH.Dispose()
$grad.Dispose(); $bt.Dispose(); $bd.Dispose()
$b.Save((Join-Path $dir "epic.png"), [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose()

# --- folder-epic.png: teal folder + white diamond glyph ---
$r = New-Bmp; $b = $r[0]; $g = $r[1]
$tab = New-Object System.Drawing.Drawing2D.GraphicsPath
$tab.AddArc(14, 30, 16, 16, 180, 90); $tab.AddLine(50, 30, 60, 42); $tab.AddLine(108, 42, 108, 50); $tab.AddLine(22, 50, 22, 38); $tab.CloseFigure()
$bTabBrush = New-Object System.Drawing.SolidBrush($tealD); $g.FillPath($bTabBrush, $tab); $tab.Dispose(); $bTabBrush.Dispose()
$body = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(14,42,100,68)), $teal, $tealD, 90)
Round $g 14 42 100 66 12 $body $null; $body.Dispose()
$bw = New-Object System.Drawing.SolidBrush($white); Diamond $g 64 78 22 $bw
$bi = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 16, 78, 73)); Diamond $g 64 78 10 $bi
$bw.Dispose(); $bi.Dispose()
$b.Save((Join-Path $dir "folder-epic.png"), [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose()

Write-Host "wrote epic.png + folder-epic.png to $dir"
