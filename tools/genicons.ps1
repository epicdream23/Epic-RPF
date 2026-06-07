# Generates modernized, color-coded folder icons (+ a .gfx file badge) that fit
# Epic RPF's flat dark design language. Output -> wwwroot/icons (source).
Add-Type -AssemblyName System.Drawing
$dir = "c:\Users\Joshua\Desktop\Epic RPF\src\App.UI\wwwroot\icons"
$S = 128

function RR([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r*2
  $p.AddArc($x,$y,$d,$d,180,90)
  $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90)
  $p.AddArc($x,$y+$h-$d,$d,$d,90,90)
  $p.CloseFigure()
  return $p
}
function C([string]$h) { return [System.Drawing.ColorTranslator]::FromHtml($h) }
function NewG {
  $bmp = New-Object System.Drawing.Bitmap $S,$S
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  return @($bmp,$g)
}
$White = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238,255,255,255))
function WPen([single]$w) { return New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(238,255,255,255)), $w }
function Poly($g,$pts) { $g.FillPolygon($White, [System.Drawing.PointF[]]$pts) }
function PF([single]$x,[single]$y) { return New-Object System.Drawing.PointF $x,$y }

function DrawFolder($g, $top, $bot) {
  $cb = New-Object System.Drawing.SolidBrush (C $bot)
  $ct = New-Object System.Drawing.SolidBrush (C $top)
  $tab = RR 22 30 52 24 9; $g.FillPath($cb, $tab)
  $back = RR 18 42 92 64 13; $g.FillPath($cb, $back)
  $front = RR 18 58 92 48 13; $g.FillPath($ct, $front)
  $tab.Dispose(); $back.Dispose(); $front.Dispose(); $cb.Dispose(); $ct.Dispose()
}

function Glyph($g, $kind, $frontHex) {
  switch ($kind) {
    'up' {
      Poly $g @((PF 64 69),(PF 49 85),(PF 79 85))
      $g.FillPath($White, (RR 58 83 12 17 2))
    }
    'data' {
      foreach ($y in 74,84,94) { $r = RR 48 $y 32 6 3; $g.FillPath($White, $r); $r.Dispose() }
    }
    'A' {
      $f = New-Object System.Drawing.Font "Segoe UI", 30, ([System.Drawing.FontStyle]::Bold)
      $sf = New-Object System.Drawing.StringFormat; $sf.Alignment=1; $sf.LineAlignment=1
      $g.DrawString("A", $f, $White, (New-Object System.Drawing.RectangleF 24,60,80,48), $sf); $f.Dispose()
    }
    'sound' {
      Poly $g @((PF 49 80),(PF 58 80),(PF 69 70),(PF 69 98),(PF 58 88),(PF 49 88))
      $p = WPen 3.5; $g.DrawArc($p, 71, 72, 16, 24, -55, 110); $p.Dispose()
    }
    'image' {
      $p = WPen 4; $fr = RR 46 70 36 28 5; $g.DrawPath($p, $fr); $fr.Dispose(); $p.Dispose()
      $g.FillEllipse($White, 52, 76, 9, 9)
      Poly $g @((PF 49 96),(PF 61 84),(PF 73 96))
    }
    'cube' {
      $pts = @(); foreach ($a in 90,150,210,270,330,30) {
        $rad = $a * [math]::PI/180; $pts += (PF (64 + 17*[math]::Cos($rad)) (84 - 17*[math]::Sin($rad)))
      }
      $p = WPen 4; $g.DrawPolygon($p, [System.Drawing.PointF[]]$pts)
      foreach ($a in 90,210,330) { $rad=$a*[math]::PI/180; $g.DrawLine($p, 64,84, (64+17*[math]::Cos($rad)), (84-17*[math]::Sin($rad))) }
      $p.Dispose()
    }
    'pin' {
      $g.FillEllipse($White, 53, 69, 22, 22)
      Poly $g @((PF 54 86),(PF 74 86),(PF 64 101))
      $hole = New-Object System.Drawing.SolidBrush (C $frontHex)
      $g.FillEllipse($hole, 59, 75, 10, 10); $hole.Dispose()
    }
    'play' { Poly $g @((PF 57 70),(PF 57 98),(PF 81 84)) }
    'clock' {
      $p = WPen 3.5; $g.DrawEllipse($p, 51, 71, 26, 26)
      $p.StartCap=2; $p.EndCap=2; $g.DrawLine($p, 64,84, 64,75); $g.DrawLine($p, 64,84, 72,87); $p.Dispose()
    }
    'user' {
      $g.FillEllipse($White, 56, 68, 16, 16)
      $b = RR 52 85 24 16 8; $g.FillPath($White, $b); $b.Dispose()
    }
    'gear' {
      for ($i=0; $i -lt 8; $i++) { $a=$i*45*[math]::PI/180; $g.FillEllipse($White, (64+15*[math]::Cos($a)-3.5), (84+15*[math]::Sin($a)-3.5), 7, 7) }
      $g.FillEllipse($White, 52, 72, 24, 24)
      $hole = New-Object System.Drawing.SolidBrush (C $frontHex)
      $g.FillEllipse($hole, 59, 79, 10, 10); $hole.Dispose()
    }
  }
}

# name -> top, bottom, glyph
$folders = @(
  @('folder',         '#8a99b5','#6c7c99',''),
  @('folder-update',  '#9d8cf2','#7b69dd','up'),
  @('folder-common',  '#4fd1c5','#36b3a8',''),
  @('folder-content', '#46c5e8','#2ba5cc',''),
  @('folder-data',    '#5fcf90','#3fae6d','data'),
  @('folder-models',  '#eaa64f','#d08732','cube'),
  @('folder-images',  '#ec7ab8','#d2589c','image'),
  @('folder-sounds',  '#f0c24f','#d9a82f','sound'),
  @('folder-text',    '#5f9ee8','#3f80cf','A'),
  @('folder-maps',    '#54c2aa','#38a88f','pin'),
  @('folder-anim',    '#ec7d7d','#d25a5a','play'),
  @('folder-temp',    '#9aa3af','#7e8794','clock'),
  @('folder-user',    '#8d7fe2','#6f60cf','user'),
  @('folder-config',  '#8a98ad','#6f7e95','gear')
)
foreach ($f in $folders) {
  $res = NewG; $bmp=$res[0]; $g=$res[1]
  DrawFolder $g $f[1] $f[2]
  if ($f[3] -ne '') { Glyph $g $f[3] $f[1] }
  $bmp.Save((Join-Path $dir ($f[0]+".png")), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

# .gfx file badge (matches the existing colored-letter file badges)
$res = NewG; $bmp=$res[0]; $g=$res[1]
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,10), (New-Object System.Drawing.Point 0,118), (C '#b56ee0'), (C '#9a52cf')
$badge = RR 12 12 104 104 22; $g.FillPath($grad, $badge); $badge.Dispose(); $grad.Dispose()
$f = New-Object System.Drawing.Font "Segoe UI", 30, ([System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment=1; $sf.LineAlignment=1
$g.DrawString("GFX", $f, $White, (New-Object System.Drawing.RectangleF 12,12,104,104), $sf)
$f.Dispose()
$bmp.Save((Join-Path $dir "gfx.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "generated $($folders.Count) folder icons + gfx.png"
