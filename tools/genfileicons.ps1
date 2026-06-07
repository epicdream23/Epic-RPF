# Real, distinctive per-extension file icons grouped by family (matching colour +
# related glyph). Output -> wwwroot/icons. Run after genicons.ps1 (folders).
Add-Type -AssemblyName System.Drawing
$dir = "c:\Users\Joshua\Desktop\Epic RPF\src\App.UI\wwwroot\icons"
$S = 128
function C([string]$h) { [System.Drawing.ColorTranslator]::FromHtml($h) }
$White = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(242,255,255,255))
function WPen([single]$w) { $p = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(242,255,255,255)), $w; $p.StartCap=2;$p.EndCap=2;$p.LineJoin=2; $p }
function APen($hex,[single]$w) { $p = New-Object System.Drawing.Pen ((C $hex)), $w; $p.StartCap=2;$p.EndCap=2;$p.LineJoin=2; $p }
function PF([single]$x,[single]$y) { New-Object System.Drawing.PointF $x,$y }
function Pts($a) { [System.Drawing.PointF[]]$a }
function NewG { $b = New-Object System.Drawing.Bitmap $S,$S; $g=[System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias; $g.TextRenderingHint=[System.Drawing.Text.TextRenderingHint]::AntiAlias; $g.Clear([System.Drawing.Color]::Transparent); @($b,$g) }
function RR([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) { $p=New-Object System.Drawing.Drawing2D.GraphicsPath; $d=$r*2; $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90); $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90); $p.CloseFigure(); $p }
function Tile($g,$top,$bot) { $gr=New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,6),(New-Object System.Drawing.Point 0,122),(C $top),(C $bot); $t=RR 8 8 112 112 24; $g.FillPath($gr,$t); $t.Dispose(); $gr.Dispose() }

function Cube($g,$cx,$cy,$r,$acc) {
  $pts=@(); foreach($a in 90,30,-30,-90,-150,150){ $rad=$a*[Math]::PI/180; $pts+=PF ($cx+$r*[Math]::Cos($rad)) ($cy-$r*[Math]::Sin($rad)) }
  $g.FillPolygon($White,(Pts $pts))
  $p=APen $acc 3.2
  foreach($a in 90,-150,-30){ $rad=$a*[Math]::PI/180; $g.DrawLine($p,[single]$cx,[single]$cy,[single]($cx+$r*[Math]::Cos($rad)),[single]($cy-$r*[Math]::Sin($rad))) }
  $p.Dispose()
}
function Doc($g,$acc) {
  $p=New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddLines((Pts @((PF 40 30),(PF 74 30),(PF 90 46),(PF 90 98),(PF 40 98)))); $p.CloseFigure()
  $g.FillPath($White,$p); $p.Dispose()
  $g.FillPolygon((New-Object System.Drawing.SolidBrush (C $acc)),(Pts @((PF 74 30),(PF 90 46),(PF 74 46))))
}

# ext -> @(topColour, bottomColour, glyphKind)
$icons = @(
  @('ydr','#4fd1c5','#37b3a8','cube'), @('ydd','#4fd1c5','#37b3a8','cubes'), @('yft','#4fd1c5','#37b3a8','cubecrack'),
  @('ytd','#ec7ab8','#d2589c','picture'),
  @('ypt','#eaa64f','#d08732','spark'),
  @('ybn','#5cc98b','#3fae6d','wirecube'),
  @('ynv','#54c2aa','#38a890','mesh'),
  @('ynd','#57b6c9','#3f9aad','nodes'),
  @('ycd','#8d7fe2','#6f60cf','film'), @('yed','#8d7fe2','#6f60cf','curve'), @('yfd','#8d7fe2','#6f60cf','funnel'),
  @('ypdb','#8d7fe2','#6f60cf','bone'), @('mrf','#8d7fe2','#6f60cf','flow'), @('yld','#8d7fe2','#6f60cf','cloth'),
  @('ymap','#5fb38f','#439872','pin'),
  @('meta','#5f9ee8','#3f80cf','doclines'), @('ymt','#5f9ee8','#3f80cf','doclines'),
  @('ymf','#5f9ee8','#3f80cf','doccheck'), @('ytyp','#5f9ee8','#3f80cf','doct'),
  @('pso','#5f9ee8','#3f80cf','docdots'), @('xml','#5f9ee8','#3f80cf','doccode'),
  @('awc','#f0c24f','#d9a82f','speaker'), @('rel','#f0c24f','#d9a82f','wave'),
  @('cut','#ec7d7d','#d25a5a','clap'),
  @('txt','#9aa3af','#7e8794','doclines'), @('dat','#9aa3af','#7e8794','cyl'), @('nametable','#9aa3af','#7e8794','list'),
  @('ini','#8a98ad','#6f7e95','gear'), @('cfg','#8a98ad','#6f7e95','gear'),
  @('lua','#7fb069','#639150','moon'),
  @('gxt2','#e0a14f','#c6863a','globe'),
  @('archive','#9b7fe0','#7d60cf','box'),
  @('gfx','#b56ee0','#9a52cf','play'),
  @('file','#8a93a6','#6e7888','doc')
)

foreach ($ic in $icons) {
  $res = NewG; $b=$res[0]; $g=$res[1]
  Tile $g $ic[1] $ic[2]
  $acc = $ic[1]; $ab = New-Object System.Drawing.SolidBrush (C $acc)
  switch ($ic[3]) {
    'cube'      { Cube $g 64 64 32 $acc }
    'cubes'     { Cube $g 50 52 19 $acc; Cube $g 80 56 19 $acc; Cube $g 64 82 21 $acc }
    'cubecrack' { Cube $g 64 64 32 $acc; $p=APen $acc 4; $g.DrawLines($p,(Pts @((PF 64 33),(PF 55 58),(PF 72 70),(PF 62 95)))); $p.Dispose() }
    'picture'   { $pg=RR 38 44 52 40 6; $g.FillPath($White,$pg); $pg.Dispose(); $g.FillPolygon($ab,(Pts @((PF 44 80),(PF 60 58),(PF 73 80)))); $g.FillPolygon($ab,(Pts @((PF 64 80),(PF 76 64),(PF 86 80)))); $g.FillEllipse($ab,73,50,9,9) }
    'spark'     { $g.FillPolygon($White,(Pts @((PF 64 28),(PF 71 64),(PF 64 100),(PF 57 64)))); $g.FillPolygon($White,(Pts @((PF 28 64),(PF 64 57),(PF 100 64),(PF 64 71)))); $g.FillEllipse($White,38,40,7,7); $g.FillEllipse($White,84,82,6,6) }
    'wirecube'  { $pts=@(); foreach($a in 90,30,-30,-90,-150,150){ $rad=$a*[Math]::PI/180; $pts+=PF (64+32*[Math]::Cos($rad)) (64-32*[Math]::Sin($rad)) }; $p=WPen 3.2; $g.DrawPolygon($p,(Pts $pts)); foreach($a in 90,-150,-30){ $rad=$a*[Math]::PI/180; $g.DrawLine($p,64,64,[single](64+32*[Math]::Cos($rad)),[single](64-32*[Math]::Sin($rad))) }; $p.Dispose() }
    'mesh'      { $p=WPen 3; $g.DrawPolygon($p,(Pts @((PF 64 32),(PF 96 64),(PF 64 96),(PF 32 64)))); $g.DrawLine($p,64,32,64,96); $g.DrawLine($p,32,64,96,64); $g.DrawLine($p,32,64,64,96); $p.Dispose() }
    'nodes'     { $p=WPen 3; $g.DrawLine($p,44,48,84,44); $g.DrawLine($p,84,44,64,88); $g.DrawLine($p,44,48,64,88); $p.Dispose(); foreach($pt in @((44,48),(84,44),(64,88))){ $g.FillEllipse($White,$pt[0]-8,$pt[1]-8,16,16) } }
    'film'      { $pg=RR 40 40 48 48 6; $g.FillPath($White,$pg); $pg.Dispose(); foreach($y in 46,60,74){ $g.FillRectangle($ab,43,$y,6,6); $g.FillRectangle($ab,79,$y,6,6) }; $g.FillPolygon($ab,(Pts @((PF 58 52),(PF 58 76),(PF 80 64)))) }
    'curve'     { $p=WPen 4; $pp=@(); for($x=34;$x -le 94;$x+=3){ $pp+=PF $x ([single](64-18*[Math]::Sin(($x-34)/60.0*2*[Math]::PI))) }; $g.DrawLines($p,(Pts $pp)); $p.Dispose() }
    'funnel'    { $g.FillPolygon($White,(Pts @((PF 36 40),(PF 92 40),(PF 70 66),(PF 70 94),(PF 58 94),(PF 58 66)))) }
    'bone'      { $g.FillEllipse($White,38,50,17,17); $g.FillEllipse($White,38,61,17,17); $g.FillEllipse($White,73,50,17,17); $g.FillEllipse($White,73,61,17,17); $bar=RR 46 59 36 10 5; $g.FillPath($White,$bar); $bar.Dispose() }
    'flow'      { foreach($bx in @((34,48),(74,48),(54,80))){ $r=RR $bx[0] $bx[1] 22 20 5; $g.FillPath($White,$r); $r.Dispose() }; $p=WPen 3; $g.DrawLine($p,56,58,74,58); $g.DrawLine($p,64,68,64,80); $p.Dispose() }
    'cloth'     { $p=WPen 4; for($o=0;$o -le 32;$o+=16){ $pp=@(); for($x=34;$x -le 94;$x+=3){ $pp+=PF $x ([single](50+$o+7*[Math]::Sin(($x-34)/30.0*[Math]::PI))) }; $g.DrawLines($p,(Pts $pp)) }; $p.Dispose() }
    'pin'       { $g.FillEllipse($White,49,42,30,30); $g.FillPolygon($White,(Pts @((PF 52 64),(PF 76 64),(PF 64 96)))); $g.FillEllipse($ab,57,50,14,14) }
    'doclines'  { Doc $g $acc; $p=APen $acc 3.5; foreach($y in 54,64,74,84){ $g.DrawLine($p,49,$y,81,$y) }; $p.Dispose() }
    'doccheck'  { Doc $g $acc; $p=APen $acc 3; foreach($y in 56,70,84){ $g.DrawLines($p,(Pts @((PF 49 $y),(PF 53 ($y+4)),(PF 60 ($y-4))))); $g.DrawLine($p,66,$y,82,$y) }; $p.Dispose() }
    'doct'      { Doc $g $acc; $f=New-Object System.Drawing.Font 'Segoe UI',26,([System.Drawing.FontStyle]::Bold); $sf=New-Object System.Drawing.StringFormat; $sf.Alignment=1;$sf.LineAlignment=1; $g.DrawString('T',$f,$ab,(New-Object System.Drawing.RectangleF 40,46,50,50),$sf); $f.Dispose() }
    'docdots'   { Doc $g $acc; for($yy=0;$yy -lt 3;$yy++){ for($xx=0;$xx -lt 4;$xx++){ $g.FillEllipse($ab,(50+$xx*9),(56+$yy*11),5,5) } } }
    'doccode'   { Doc $g $acc; $f=New-Object System.Drawing.Font 'Consolas',17,([System.Drawing.FontStyle]::Bold); $sf=New-Object System.Drawing.StringFormat; $sf.Alignment=1;$sf.LineAlignment=1; $g.DrawString('</>',$f,$ab,(New-Object System.Drawing.RectangleF 38,48,54,46),$sf); $f.Dispose() }
    'speaker'   { $g.FillPolygon($White,(Pts @((PF 38 54),(PF 52 54),(PF 66 42),(PF 66 86),(PF 52 74),(PF 38 74)))); $p=WPen 3.5; $g.DrawArc($p,64,46,18,36,-52,104); $g.DrawArc($p,64,38,30,52,-52,104); $p.Dispose() }
    'wave'      { $hs=@(20,40,58,34,52,26,44,16); for($i=0;$i -lt $hs.Count;$i++){ $h=$hs[$i]; $g.FillRectangle($White,(36+$i*8),(64-$h/2),5,$h) } }
    'clap'      { $bd=RR 36 56 56 34 5; $g.FillPath($White,$bd); $bd.Dispose(); $top=New-Object System.Drawing.Drawing2D.GraphicsPath; $top.AddLines((Pts @((PF 36 54),(PF 88 44),(PF 92 56),(PF 40 66)))); $top.CloseFigure(); $g.FillPath($White,$top); $top.Dispose(); $p=APen $acc 3; for($i=0;$i -lt 4;$i++){ $g.DrawLine($p,(44+$i*13),50,(40+$i*13),64) }; $p.Dispose() }
    'cyl'       { $g.FillEllipse($White,40,42,48,15); $g.FillRectangle($White,40,49,48,34); $g.FillEllipse($White,40,75,48,15); $p=APen $acc 2.5; $g.DrawEllipse($p,40,42,48,15); $g.DrawArc($p,40,57,48,15,0,180); $p.Dispose() }
    'list'      { Doc $g $acc; $p=APen $acc 3.5; foreach($y in 54,66,78){ $g.FillEllipse($ab,48,($y-2),5,5); $g.DrawLine($p,58,$y,82,$y) }; $p.Dispose() }
    'gear'      { for($i=0;$i -lt 8;$i++){ $a=$i*45*[Math]::PI/180; $g.FillEllipse($White,[single](64+18*[Math]::Cos($a)-4),[single](64+18*[Math]::Sin($a)-4),8,8) }; $g.FillEllipse($White,48,48,32,32); $g.FillEllipse($ab,57,57,14,14) }
    'moon'      { $g.FillEllipse($White,38,38,50,50); $cut=New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,6),(New-Object System.Drawing.Point 0,122),(C $ic[1]),(C $ic[2]); $g.FillEllipse($cut,52,32,46,46); $cut.Dispose(); $g.FillEllipse($White,86,46,9,9) }
    'globe'     { $p=WPen 3; $g.DrawEllipse($p,40,40,48,48); $g.DrawLine($p,40,64,88,64); $g.DrawEllipse($p,56,40,16,48); $p.Dispose() }
    'box'       { $g.FillPolygon($White,(Pts @((PF 64 34),(PF 94 50),(PF 64 66),(PF 34 50)))); $g.FillPolygon($White,(Pts @((PF 34 50),(PF 64 66),(PF 64 98),(PF 34 82)))); $g.FillPolygon($White,(Pts @((PF 94 50),(PF 64 66),(PF 64 98),(PF 94 82)))); $p=APen $acc 2.6; $g.DrawLine($p,64,66,64,98); $g.DrawLine($p,34,50,64,66); $g.DrawLine($p,94,50,64,66); $p.Dispose() }
    'play'      { $p=WPen 4; $fr=RR 40 40 48 48 10; $g.DrawPath($p,$fr); $fr.Dispose(); $p.Dispose(); $g.FillPolygon($White,(Pts @((PF 58 52),(PF 58 76),(PF 80 64)))) }
    'doc'       { Doc $g $acc }
  }
  $ab.Dispose()
  $b.Save((Join-Path $dir ($ic[0]+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $b.Dispose()
}
Write-Output "generated $($icons.Count) file icons"
