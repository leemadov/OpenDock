# Shot 7 (multitasking): drag to tile and Stage Manager, on the laptop's screen (2560 x 1440 DIP).
# Uses OpenDock's own renders: the desktop with the pink Dock (desk_pink.png), the Finder and System Settings
# windows at their open and half-screen sizes, and the pink Finder icon. The tile outline is drawn the way
# TilePreview draws itself, the tiles are Tiling.Area's (8 DIP margins, inside the work area the menu bar and the
# Dock leave), and the Stage Manager strip follows StageStrip's layout.
#   powershell -STA -File shot7.ps1 -outdir video\renders\shot7
#   -only N -still file.png   just frame N, to that file (e.g. -only 120 -outW 2560: shot 8's backdrop at 1 px per DIP)
param([string]$outdir, [int]$count = 120, [int]$outW = 1280, [int]$only = 0, [string]$still = "")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
$S = T Settings
$scratch = Join-Path $env:TEMP "opendock-video-settings"; [void][IO.Directory]::CreateDirectory($scratch)
$S.GetField("StorageDirectory", $all).SetValue($null, $scratch)
$S.GetField("Appearance").SetValue($null, 1)
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
(T Icons).GetMethod("Start").Invoke($null, $null)
(T Theme).GetMethod("Init").Invoke($null, [object[]]@($app.psobject.BaseObject))
$A = "$root\video\assets"
function Load($f) { $b = New-Object Windows.Media.Imaging.BitmapImage; $b.BeginInit(); $b.CacheOption = "OnLoad"; $b.UriSource = [Uri]$f; $b.EndInit(); $b.Freeze(); $b }
$desk = Load "$A\desk_pink.png"
# windows: picture, its shadow margin (DIP, at the picture's own scale) and the window's size inside it
$finderOpen = @{ img = (Load "$A\finder_seq\w_0090.png"); m = 60; w = 1100; h = 680 }
$finderTile = @{ img = (Load "$A\finder_tile_pink.png"); m = 60; w = 1268; h = 1293 }
$setOpen = @{ img = (Load "$A\settings_window_pink.png"); m = 60; w = 920; h = 660 }
$setTile = @{ img = (Load "$A\settings_tile_pink.png"); m = 60; w = 1268; h = 1293 }
$pink = [Windows.Media.ColorConverter]::ConvertFromString("#FF375F")
$finderIcon = (T IconLook).GetMethod("Style").Invoke($null, [object[]]@((T Art).GetProperty("Finder").GetValue($null), [int]1, $pink, $false))

$W = 2560; $H = 1440; $k = $outW / $W; $outH = [int]($H * $k)
if (-not $still) { [void][IO.Directory]::CreateDirectory($outdir) }
# Tiling.Area inside the work area: below the 30 DIP menu bar, above the Dock's reserved 101 DIP
$work = [Windows.Rect]::new(0, 30, $W, $H - 30 - 101); $gap = 8
$hw = ($work.Width - 2 * $gap - $gap) / 2
$tileL = [Windows.Rect]::new($work.X + $gap, $work.Y + $gap, $hw, $work.Height - 2 * $gap)
$tileR = [Windows.Rect]::new($work.X + $gap + $hw + $gap, $work.Y + $gap, $hw, $work.Height - 2 * $gap)
# where shot 6 leaves the Finder: 1375 x 850, centred at (1280, 690)
$finder0 = [Windows.Rect]::new(1280 - 687.5, 690 - 425, 1375, 850)
$set0 = [Windows.Rect]::new(1240, 330, 920, 660)

function Ease($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } elseif ($t -lt 0.5) { 4 * $t * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 3) / 2 } }
function Out3($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } else { 1 - [Math]::Pow(1 - $t, 3) } }
function Prog($f, $a, $b) { [Math]::Min(1.0, [Math]::Max(0.0, ($f - $a) / [double]($b - $a))) }
function LerpR($r1, $r2, $t) { [Windows.Rect]::new($r1.X + ($r2.X - $r1.X) * $t, $r1.Y + ($r2.Y - $r1.Y) * $t, $r1.Width + ($r2.Width - $r1.Width) * $t, $r1.Height + ($r2.Height - $r1.Height) * $t) }
function LerpP($p1, $p2, $t) { [Windows.Point]::new($p1.X + ($p2.X - $p1.X) * $t, $p1.Y + ($p2.Y - $p1.Y) * $t) }

# A window picture drawn so the window inside it fills rect $r (its shadow scales with it).
function DrawWin($cv, $pic, [Windows.Rect]$r, $opacity = 1.0) {
  if ($opacity -le 0.002) { return }
  $sx = $r.Width / $pic.w; $sy = $r.Height / $pic.h
  $im = New-Object Windows.Controls.Image; $im.Source = $pic.img; $im.Stretch = "Fill"; $im.Opacity = $opacity
  [Windows.Media.RenderOptions]::SetBitmapScalingMode($im, "HighQuality")
  [Windows.Controls.Canvas]::SetLeft($im, $r.X - $pic.m * $sx); [Windows.Controls.Canvas]::SetTop($im, $r.Y - $pic.m * $sy)
  $im.Width = ($pic.w + 2 * $pic.m) * $sx; $im.Height = ($pic.h + 2 * $pic.m) * $sy
  [void]$cv.Children.Add($im)
}
# A window changing size: its open picture and its tiled picture cross-fade as it moves.
function DrawMorph($cv, $a, $b, [Windows.Rect]$r, $mix) { DrawWin $cv $a $r (1 - $mix); DrawWin $cv $b $r $mix }

# TilePreview: accent-tinted glass with a white rim and a soft shadow, grown out of the pointer
function DrawPreview($cv, [Windows.Rect]$r, $opacity) {
  if ($opacity -le 0.002) { return }
  $c = $pink
  $g = New-Object Windows.Controls.Border; $g.CornerRadius = 14; $g.BorderThickness = 1.5; $g.Opacity = $opacity
  $g.BorderBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(200, 255, 255, 255))
  $g.Background = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.Color]::FromArgb(105, [byte]([Math]::Floor($c.R / 2) + 128), [byte]([Math]::Floor($c.G / 2) + 128), [byte]([Math]::Floor($c.B / 2) + 128))), ([Windows.Media.Color]::FromArgb(70, $c.R, $c.G, $c.B)), 90
  $g.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 22; ShadowDepth = 3; Direction = 270; Opacity = 0.3 }
  [Windows.Controls.Canvas]::SetLeft($g, $r.X); [Windows.Controls.Canvas]::SetTop($g, $r.Y); $g.Width = $r.Width; $g.Height = $r.Height
  [void]$cv.Children.Add($g)
}
function PreviewRect($tile, $ptr, $t) { LerpR ([Windows.Rect]::new($ptr.X - 40, $ptr.Y - 30, 80, 60)) $tile (Out3 $t) }

# the pointer: Windows' arrow, black with a white edge, at 1.6x for a 1440p screen
$arrow = [Windows.Media.Geometry]::Parse("M 0,0 L 0,17.5 L 4.1,13.6 L 6.9,20.2 L 9.6,19.1 L 6.9,12.6 L 12.5,12.6 Z")
function DrawPointer($cv, $p, $opacity) {
  if ($opacity -le 0.002) { return }
  $path = New-Object Windows.Shapes.Path; $path.Data = $arrow; $path.Fill = [Windows.Media.Brushes]::Black; $path.Stroke = [Windows.Media.Brushes]::White; $path.StrokeThickness = 1.2
  $path.RenderTransform = New-Object Windows.Media.ScaleTransform 1.9, 1.9; $path.Opacity = $opacity
  $path.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 4; ShadowDepth = 1.5; Direction = 300; Opacity = 0.45 }
  [Windows.Controls.Canvas]::SetLeft($path, $p.X); [Windows.Controls.Canvas]::SetTop($path, $p.Y); [void]$cv.Children.Add($path)
}

# StageStrip: a slot for the Finder, its window as a picture (fit to the slot) with the app's icon beside it
$slot = [Windows.Rect]::new(10, $work.Y + 26, 176, 120)
$thumbArea = [Windows.Rect]::new($slot.X + 38, $slot.Y + 14, $slot.Width - 44, $slot.Height - 20)
$tk = [Math]::Min($thumbArea.Width / $finderTile.w, $thumbArea.Height / $finderTile.h)
$thumb = [Windows.Rect]::new($thumbArea.X + ($thumbArea.Width - $finderTile.w * $tk) / 2, $thumbArea.Bottom - $finderTile.h * $tk, $finderTile.w * $tk, $finderTile.h * $tk)

# the pointer's path and the windows' rects, by frame
$grabF = [Windows.Point]::new(1000, $finder0.Y + 30)          # the Finder's toolbar, left of Settings
$grabS = [Windows.Point]::new($set0.X + 520, $set0.Y + 26)     # Settings' title bar
$leftEdge = [Windows.Point]::new(1, 640); $rightEdge = [Windows.Point]::new($W - 2, 600)
for ($f = 0; $f -lt $count; $f++) {
  if ($only -and $f -ne $only - 1) { continue }
  $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
  $bg = New-Object Windows.Controls.Image; $bg.Source = $desk; $bg.Width = $W; $bg.Height = $H; $bg.Stretch = "Fill"; [void]$cv.Children.Add($bg)

  # Settings opens (0-12)
  $so = Out3 (Prog $f 0 12); $setScale = 0.9 + 0.1 * $so
  # the Finder: dragged (24-44) from its spot to the left edge, released at 46, settles into the left tile (46-56)
  $dragF = Ease (Prog $f 24 44); $ptrF = LerpP $grabF $leftEdge $dragF
  $finderAt = [Windows.Rect]::new($finder0.X + ($ptrF.X - $grabF.X), $finder0.Y + ($ptrF.Y - $grabF.Y), $finder0.Width, $finder0.Height)
  $snapF = Out3 (Prog $f 46 56)
  # Settings: dragged (68-84) to the right edge, released at 86, settles into the right tile (86-96)
  $dragS = Ease (Prog $f 68 84); $ptrS = LerpP $grabS $rightEdge $dragS
  $setAt = [Windows.Rect]::new($set0.X + ($ptrS.X - $grabS.X), $set0.Y + ($ptrS.Y - $grabS.Y), $set0.Width, $set0.Height)
  $snapS = Out3 (Prog $f 86 96)
  # Stage Manager (100-116): the Finder leaves the stage for the strip
  $stage = Ease (Prog $f 100 116)

  $finderRect = $(if ($f -lt 46) { $finderAt } else { LerpR $finderAt $tileL $snapF })
  if ($f -ge 46) { $finderAt = [Windows.Rect]::new($finder0.X + ($leftEdge.X - $grabF.X), $finder0.Y + ($leftEdge.Y - $grabF.Y), $finder0.Width, $finder0.Height); $finderRect = LerpR $finderAt $tileL $snapF }
  $setRect = $(if ($f -lt 86) { $setAt } else { LerpR ([Windows.Rect]::new($set0.X + ($rightEdge.X - $grabS.X), $set0.Y + ($rightEdge.Y - $grabS.Y), $set0.Width, $set0.Height)) $tileR $snapS })
  # (Settings opens from slightly smaller, about its centre)
  if ($f -lt 12) { $c = [Windows.Point]::new($set0.X + $set0.Width / 2, $set0.Y + $set0.Height / 2); $setRect = [Windows.Rect]::new($c.X - $set0.Width * $setScale / 2, $c.Y - $set0.Height * $setScale / 2, $set0.Width * $setScale, $set0.Height * $setScale) }

  # the tile outlines: they grow from the pointer while it rests at the edge, and go when the window lets go
  $prevL = $(if ($f -ge 41 -and $f -lt 50) { [Math]::Min(1.0, ($f - 41) / 4.0) * (1 - (Prog $f 46 50)) } else { 0 })
  $prevR = $(if ($f -ge 81 -and $f -lt 90) { [Math]::Min(1.0, ($f - 81) / 4.0) * (1 - (Prog $f 86 90)) } else { 0 })

  # draw, back to front: the window behind, the outline under the dragged window, the front window
  $finderFront = $f -ge 22 -and $f -lt 58
  $finderPic = { param($r) if ($snapF -gt 0) { DrawMorph $cv $finderOpen $finderTile $r $snapF } else { DrawWin $cv $finderOpen $r } }
  $setPic = { param($r) if ($snapS -gt 0) { DrawMorph $cv $setOpen $setTile $r $snapS } else { DrawWin $cv $setOpen $r $so } }
  if ($stage -gt 0) {
    # the Finder shrinks into its strip slot; Settings, the app in use, stays on stage
    $r = LerpR $tileL $thumb $stage
    DrawWin $cv $finderTile $r (1 - 0.15 * $stage)
    if ($stage -gt 0.6) {
      $ic = New-Object Windows.Controls.Image; $ic.Source = $finderIcon; $ic.Width = 34; $ic.Height = 34; $ic.Opacity = ($stage - 0.6) / 0.4
      $ic.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 6; ShadowDepth = 1; Opacity = 0.35 }
      [Windows.Controls.Canvas]::SetLeft($ic, $slot.X + 2); [Windows.Controls.Canvas]::SetTop($ic, $slot.Bottom - 38); [void]$cv.Children.Add($ic)
    }
    & $setPic $setRect
  } elseif ($finderFront) {
    & $setPic $setRect
    DrawPreview $cv (PreviewRect $tileL $leftEdge (Prog $f 41 45)) $prevL
    & $finderPic $finderRect
  } else {
    & $finderPic $finderRect
    DrawPreview $cv (PreviewRect $tileR $rightEdge (Prog $f 81 85)) $prevR
    & $setPic $setRect
  }

  # the pointer: in from the lower right, to the Finder, drags it; then to Settings, drags it; then away
  $p0 = [Windows.Point]::new(1900, 1250)
  if ($f -lt 22) { $ptr = LerpP $p0 $grabF (Ease (Prog $f 6 22)) }
  elseif ($f -lt 46) { $ptr = $ptrF }
  elseif ($f -lt 68) { $ptr = LerpP ([Windows.Point]::new($leftEdge.X + 30, $leftEdge.Y)) $grabS (Ease (Prog $f 50 68)) }
  elseif ($f -lt 86) { $ptr = $ptrS }
  else { $ptr = LerpP ([Windows.Point]::new($rightEdge.X - 40, $rightEdge.Y)) ([Windows.Point]::new(1900, 900)) (Ease (Prog $f 88 104)) }
  $pOpacity = [Math]::Min(1.0, $f / 6.0) * (1 - (Prog $f 104 112))
  DrawPointer $cv $ptr $pOpacity

  $cv.RenderTransform = New-Object Windows.Media.ScaleTransform $k, $k
  $view = New-Object Windows.Controls.Canvas; $view.Width = $outW; $view.Height = $outH; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
  $view.Measure([Windows.Size]::new($outW, $outH)); $view.Arrange([Windows.Rect]::new(0, 0, $outW, $outH)); $view.UpdateLayout()
  $rt = New-Object Windows.Media.Imaging.RenderTargetBitmap $outW, $outH, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $rt.Render($view)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rt))
  $fs = [IO.File]::Create($(if ($still) { $still } else { [IO.Path]::Combine($outdir, ("f_{0:D4}.png" -f ($f + 1))) })); $enc.Save($fs); $fs.Close()
}
"done"
