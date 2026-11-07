# Shot 8 (Control Center and the Dynamic Island), on the laptop's screen (2560 x 1440 DIP), continuing from shot 7's
# last frame (shot7_end.png). The Island is built the way IslandWindow builds it: an idle pill H tall and 2.6 H wide
# just inside the menu bar, the compact view (cover + waveform), then the media view (372 x 128, radius 32) with a
# BackEase morph; the song is made up. Control Center is OpenDock's own panel (cc_panel.png, frosted from the screen).
#   powershell -STA -File shot8.ps1 -outdir video\renders\shot8 [-only N -still file.png]
param([string]$outdir, [int]$count = 90, [int]$outW = 1280, [int]$only = 0, [string]$still = "")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
$A = "$root\video\assets"
function Load($f, $w = 0) { $b = New-Object Windows.Media.Imaging.BitmapImage; $b.BeginInit(); $b.CacheOption = "OnLoad"; $b.UriSource = [Uri]$f; if ($w) { $b.DecodePixelWidth = $w }; $b.EndInit(); $b.Freeze(); $b }
$screen = Load "$A\shot7_end.png"
$cc = Load "$A\cc_panel.png"
$cover = Load "$A\cover_glass_skies.png" 240
$font = New-Object Windows.Media.FontFamily "Segoe UI Variable Text, Segoe UI"
$glyphs = New-Object Windows.Media.FontFamily "Segoe Fluent Icons, Segoe MDL2 Assets"
$W = 2560; $H = 1440; $k0 = $outW / $W; $outH = [int]($H * $k0)
if (-not $still) { [void][IO.Directory]::CreateDirectory($outdir) }

function Prog($f, $a, $b) { [Math]::Min(1.0, [Math]::Max(0.0, ($f - $a) / [double]($b - $a))) }
function Ease($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } elseif ($t -lt 0.5) { 4 * $t * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 3) / 2 } }
function Out3($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } else { 1 - [Math]::Pow(1 - $t, 3) } }
# WPF's BackEase (EaseOut, amplitude 0.28), as the Island's Morph uses
function Back($t) { if ($t -le 0) { return 0.0 }; if ($t -ge 1) { return 1.0 }; $u = 1 - $t; $a = 0.28; return 1 - ($u * $u * $u - $u * $a * [Math]::Sin($u * [Math]::PI)) }

# the Island's sizes (MenuBarWindow is 30 tall: H = 26)
$IH = 26; $idleW = [Math]::Round($IH * 2.6)
$idle = @{ w = $idleW; h = $IH; r = $IH / 2 }
$compact = @{ w = $idleW + 2 * ($IH + 2); h = $IH; r = $IH / 2 }
$media = @{ w = 372; h = 128; r = 32 }
$islandTop = (30 - $IH) / 2
function Bars($cv, $x, $y, $height, $t, $color) {
  $rest = 0.35, 0.6, 0.45, 0.3; $period = 0.42, 0.55, 0.37, 0.5
  for ($i = 0; $i -lt 4; $i++) {
    $ph = ($t % (2 * $period[$i])) / $period[$i]; if ($ph -gt 1) { $ph = 2 - $ph }
    $s = 0.2 + 0.8 * (0.5 - 0.5 * [Math]::Cos([Math]::PI * $ph))      # SineEase between 0.2 and 1, back and forth
    $bh = $height * $s
    $bar = New-Object Windows.Shapes.Rectangle; $bar.Width = 3; $bar.Height = $bh; $bar.RadiusX = 1.5; $bar.RadiusY = 1.5; $bar.Fill = $color
    [Windows.Controls.Canvas]::SetLeft($bar, $x + $i * 5.4 + 1.2); [Windows.Controls.Canvas]::SetTop($bar, $y + ($height - $bh) / 2); [void]$cv.Children.Add($bar)
  }
}
$waveColor = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString("#FF6B9A"))
function Txt($cv, $s, $size, $weight, $color, $x, $y, $maxW = 400) {
  $t = New-Object Windows.Controls.TextBlock; $t.Text = $s; $t.FontSize = $size; $t.FontWeight = $weight; $t.FontFamily = $font; $t.Foreground = $color; $t.MaxWidth = $maxW; $t.TextTrimming = "CharacterEllipsis"
  [Windows.Controls.Canvas]::SetLeft($t, $x); [Windows.Controls.Canvas]::SetTop($t, $y); [void]$cv.Children.Add($t)
}
function Glyph($cv, $g, $size, $x, $y) {
  $t = New-Object Windows.Controls.TextBlock; $t.Text = [string][char]$g; $t.FontSize = $size; $t.FontFamily = $glyphs; $t.Foreground = [Windows.Media.Brushes]::White
  $t.Width = $size + 14; $t.TextAlignment = "Center"
  [Windows.Controls.Canvas]::SetLeft($t, $x); [Windows.Controls.Canvas]::SetTop($t, $y); [void]$cv.Children.Add($t)
}
# the Island at size $s (w, h, r), with the compact view ($cA) and the media view ($mA) faded in
function Island($cv, $s, $cA, $mA, $time) {
  $x = ($W - $s.w) / 2; $y = $islandTop
  $pill = New-Object Windows.Shapes.Rectangle; $pill.Width = $s.w; $pill.Height = $s.h; $pill.RadiusX = $s.r; $pill.RadiusY = $s.r; $pill.Fill = [Windows.Media.Brushes]::Black
  $pill.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 18; ShadowDepth = 3; Opacity = 0.35; Color = [Windows.Media.Colors]::Black }
  [Windows.Controls.Canvas]::SetLeft($pill, $x); [Windows.Controls.Canvas]::SetTop($pill, $y); [void]$cv.Children.Add($pill)
  $clip = New-Object Windows.Controls.Canvas; $clip.Width = $s.w; $clip.Height = $s.h; $clip.ClipToBounds = $true
  [Windows.Controls.Canvas]::SetLeft($clip, $x); [Windows.Controls.Canvas]::SetTop($clip, $y); [void]$cv.Children.Add($clip)
  if ($cA -gt 0.01) {
    # CompactView: the cover on the left (H - 8, radius 6), the waveform on the right
    $g = New-Object Windows.Controls.Canvas; $g.Opacity = $cA; $g.Width = $s.w; $g.Height = $s.h; [void]$clip.Children.Add($g)
    $cb = New-Object Windows.Controls.Border; $cb.Width = $IH - 8; $cb.Height = $IH - 8; $cb.CornerRadius = 6; $cb.Background = New-Object Windows.Media.ImageBrush $cover
    [Windows.Controls.Canvas]::SetLeft($cb, 7); [Windows.Controls.Canvas]::SetTop($cb, 4); [void]$g.Children.Add($cb)
    Bars $g ($s.w - 12 - 22) 6 ($IH - 12) $time $waveColor
  }
  if ($mA -gt 0.01) {
    # MediaView: 52 px cover (radius 12), title and artist, the waveform, then previous / pause / next
    $g = New-Object Windows.Controls.Canvas; $g.Opacity = $mA; $g.Width = $media.w; $g.Height = $media.h; [void]$clip.Children.Add($g)
    $cb = New-Object Windows.Controls.Border; $cb.Width = 52; $cb.Height = 52; $cb.CornerRadius = 12; $cb.Background = New-Object Windows.Media.ImageBrush $cover
    [Windows.Controls.Canvas]::SetLeft($cb, 18); [Windows.Controls.Canvas]::SetTop($cb, 14); [void]$g.Children.Add($cb)
    Txt $g "Glass Skies" 15 "SemiBold" ([Windows.Media.Brushes]::White) 82 18 220
    Txt $g "OpenDock Sessions" 13 "Normal" (New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(0x99, 255, 255, 255))) 82 40 220
    Bars $g ($media.w - 18 - 26) 30 20 $time $waveColor
    $cx = $media.w / 2
    Glyph $g 0xE892 18 ($cx - 32 - 3 - 32 - 4) 84; Glyph $g 0xE769 24 ($cx - 19) 80; Glyph $g 0xE893 18 ($cx + 22 + 7) 84
  }
}
$arrow = [Windows.Media.Geometry]::Parse("M 0,0 L 0,17.5 L 4.1,13.6 L 6.9,20.2 L 9.6,19.1 L 6.9,12.6 L 12.5,12.6 Z")
function Pointer($cv, $p, $op) {
  if ($op -le 0.002) { return }
  $path = New-Object Windows.Shapes.Path; $path.Data = $arrow; $path.Fill = [Windows.Media.Brushes]::Black; $path.Stroke = [Windows.Media.Brushes]::White; $path.StrokeThickness = 1.2
  $path.RenderTransform = New-Object Windows.Media.ScaleTransform 1.9, 1.9; $path.Opacity = $op
  $path.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 4; ShadowDepth = 1.5; Direction = 300; Opacity = 0.45 }
  [Windows.Controls.Canvas]::SetLeft($path, $p.X); [Windows.Controls.Canvas]::SetTop($path, $p.Y); [void]$cv.Children.Add($path)
}
function LerpS($a, $b, $t) { @{ w = $a.w + ($b.w - $a.w) * $t; h = $a.h + ($b.h - $a.h) * $t; r = $a.r + ($b.r - $a.r) * $t } }
# Control Center where Place() puts it (its picture includes a 30 DIP shadow margin)
$ccRect = [Windows.Rect]::new(2560 - 8 + 30 - 352, 30 + 6 - 30, 352, 622)

for ($f = 0; $f -lt $count; $f++) {
  if ($only -and $f -ne $only - 1) { continue }
  $time = $f / 30.0
  $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
  $bg = New-Object Windows.Controls.Image; $bg.Source = $screen; $bg.Width = $W; $bg.Height = $H; $bg.Stretch = "Fill"; [void]$cv.Children.Add($bg)
  # the Island: idle, a song starts (compact, 8-20), it opens (24-38), stays, closes back to compact (66-78)
  $toCompact = Back (Prog $f 8 22); $toMedia = Back (Prog $f 24 38); $back = Back (Prog $f 66 80)
  $s = LerpS $idle $compact $toCompact
  if ($f -ge 24) { $s = LerpS $compact $media $toMedia }
  if ($f -ge 66) { $s = LerpS $media $compact $back }
  $cA = $(if ($f -lt 24) { Prog $f 12 20 } elseif ($f -lt 66) { 1 - (Prog $f 24 28) } else { Prog $f 72 80 })
  $mA = $(if ($f -lt 24) { 0 } elseif ($f -lt 66) { Prog $f 28 36 } else { 1 - (Prog $f 66 70) })
  # Control Center: the pointer clicks the menu bar's status area (40), the panel drops open (42-52), closes (70-78)
  $open = Out3 (Prog $f 42 52); $close = Prog $f 70 78
  $ccA = $open * (1 - $close)
  if ($ccA -gt 0.002) {
    $sc = 0.96 + 0.04 * $open
    $im = New-Object Windows.Controls.Image; $im.Source = $cc; $im.Stretch = "Fill"; $im.Opacity = $ccA
    $im.Width = $ccRect.Width; $im.Height = $ccRect.Height
    $im.RenderTransform = New-Object Windows.Media.ScaleTransform $sc, $sc, ($ccRect.Width - 30), 30
    [Windows.Controls.Canvas]::SetLeft($im, $ccRect.X); [Windows.Controls.Canvas]::SetTop($im, $ccRect.Y + 6 * (1 - $open)); [void]$cv.Children.Add($im)
  }
  Island $cv $s $cA $mA $time
  $p = $(if ($f -lt 40) { $a = [Windows.Point]::new(1700, 520); $b = [Windows.Point]::new(2380, 8); $e = Ease (Prog $f 18 40); [Windows.Point]::new($a.X + ($b.X - $a.X) * $e, $a.Y + ($b.Y - $a.Y) * $e) } else { $a = [Windows.Point]::new(2380, 8); $b = [Windows.Point]::new(2300, 260); $e = Ease (Prog $f 56 70); [Windows.Point]::new($a.X + ($b.X - $a.X) * $e, $a.Y + ($b.Y - $a.Y) * $e) })
  Pointer $cv $p ((Prog $f 14 20) * (1 - (Prog $f 70 78)))
  # the camera: up to the top right (0-18), back out to the whole screen (72-89)
  $zin = Ease (Prog $f 0 18); $zout = Ease (Prog $f 72 89)
  $z = 1 + 0.6 * $zin * (1 - $zout)
  $vw = $W / $z; $vh = $H / $z
  $vx = ($W - $vw); $vy = 0                                   # the view's left-top: the right edge and the top stay put
  $k = $outW / $vw
  if ($still) { $k = $outW / $W; $vx = 0; $vy = 0 }
  $cv.RenderTransform = New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($k, 0, 0, $k, -$vx * $k, -$vy * $k))
  $view = New-Object Windows.Controls.Canvas; $view.Width = $outW; $view.Height = $outH; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
  $view.Measure([Windows.Size]::new($outW, $outH)); $view.Arrange([Windows.Rect]::new(0, 0, $outW, $outH)); $view.UpdateLayout()
  $rt = New-Object Windows.Media.Imaging.RenderTargetBitmap $outW, $outH, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $rt.Render($view)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rt))
  $fs = [IO.File]::Create($(if ($still) { $still } else { [IO.Path]::Combine($outdir, ("f_{0:D4}.png" -f ($f + 1))) })); $enc.Save($fs); $fs.Close()
}
"done"
