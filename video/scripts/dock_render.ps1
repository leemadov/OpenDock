# Renders OpenDock's Dock the way DockWindow draws it, over any backdrop, so it can move (shot 4 onwards).
# It follows DockWindow step by step: the same items, geometry (Frame at rest), the frosted half-resolution capture
# (Glass.Behind: scale 2, box blur 1 x 3), Liquid.Lens on that capture for the pane (LensPane) and each icon
# (LensIcon), the theme's Dock brushes, IconLook for the icon style. Settings are the ones the real Dock uses.
#
#   -mode compare   render the Dock over the wallpaper on the 3440x1440 screen and compare it with OpenDock's own
#                   snapshot (video\assets\dock-snapshot.png): writes compare.png and prints the difference
#   -mode frames    shot 4: render -count frames of the macro shot to -outdir
param([string]$mode = "compare", [string]$wall = "drift-light.jpg", [string]$outdir = "", [int]$count = 90, [int]$outWidth = 1280)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
function Pump($ms) { $f = New-Object Windows.Threading.DispatcherFrame; $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval = [TimeSpan]::FromMilliseconds($ms); $t.Add_Tick({ $t.Stop(); $f.Continue = $false }.GetNewClosure()); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($f) }

# ---------- the real Dock's settings (read from OpenDock's settings.txt) ----------
$S = T Settings
$scratch = Join-Path $env:TEMP "opendock-video-settings"; [void][IO.Directory]::CreateDirectory($scratch)
$S.GetField("StorageDirectory", $all).SetValue($null, $scratch)
foreach ($kv in @{ Appearance = 1; IconStyle = 3; IconSize = 79; DockSpacing = 3; GlassTint = 45 }.GetEnumerator()) { $S.GetField($kv.Key).SetValue($null, $kv.Value) }
$S.GetField("IconTiles").SetValue($null, $false)
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
(T Icons).GetMethod("Start").Invoke($null, $null)
(T Theme).GetMethod("Init").Invoke($null, [object[]]@($app.psobject.BaseObject))
$art = T Art; $IC = T Icons; $IL = T IconLook; $L = T Liquid
# the app list first: Store apps get their own large light-theme logo from it (Apps.PackageLogo), as in the Dock
(T Apps).GetMethod("Refresh").Invoke($null, [object[]]@($true)); $wait = 0
while (((T Apps).GetField("All").GetValue($null)).Count -eq 0 -and $wait -lt 60) { Pump 250; $wait++ }

# ---------- the items: Finder, Launchpad, the pins, running apps that aren't pinned, | Downloads, Trash ----------
$entries = @(
  @{ art = "Finder" }, @{ art = "Launchpad" },
  @{ path = "shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" },
  @{ path = "shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" },
  @{ path = "shell:AppsFolder\Microsoft.VisualStudioCode" },
  @{ path = "shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" },
  @{ path = "shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude"; run = $true },
  @{ path = "shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App"; run = $true },
  @{ path = "$env:LOCALAPPDATA\Programs\Opera\opera.exe"; run = $true },
  @{ path = "${env:ProgramFiles(x86)}\Steam\steamapps\common\War Thunder\launcher.exe" },
  @{ path = "$env:ProgramFiles\Blender Foundation\Blender 5.1\blender.exe"; run = $true },
  @{ sep = $true }, @{ art = "Downloads" }, @{ art = "Trash" })
$got = @{}
foreach ($e in $entries) { if ($e.path) { $key = $e.path; $cb = [Action[Windows.Media.ImageSource]]{ param($x) $got[$key] = $x }.GetNewClosure(); [void]$IC.GetMethod("Get").Invoke($null, [object[]]@($e.path, $true, $cb, 256, $true, $null)) } }
Pump 5000
$items = @(); $n = ($entries | Where-Object { -not $_.sep }).Count; $k = 0
foreach ($e in $entries) {
  if ($e.sep) { $items += @{ sep = $true }; continue }
  $src = $null
  if ($e.art -eq "Trash") { $src = $art.GetMethod("Trash").Invoke($null, [object[]]@($false)) } elseif ($e.art) { $src = $art.GetProperty($e.art).GetValue($null) } elseif ($got.ContainsKey($e.path)) { $src = $got[$e.path] } else { Write-Host "no icon: $($e.path)"; continue }
  $img = $IL.GetMethod("Apply").Invoke($null, [object[]]@($src, [double]($k / [Math]::Max(1, $n - 1)))); $k++
  $items += @{ icon = $img; src = $src; run = [bool]$e.run }
}

# ---------- DockWindow.Frame at rest, on a W x H screen (DIP) ----------
function Layout($W, $H) {
  $slot = 79.0; $sp = 3.0
  $bw = @(); foreach ($it in $items) { $bw += $(if ($it.sep) { $slot * 0.34 } else { $slot }) }
  $total = ($bw | Measure-Object -Sum).Sum + $sp * ($items.Count - 1)
  $x = ($W - $total) / 2; $cx = @()
  for ($i = 0; $i -lt $items.Count; $i++) { $cx += $x + $bw[$i] / 2; $x += $bw[$i] + $sp }
  $bgH = [Math]::Round($slot + 12); $bottom = $H - 6; $pad = [Math]::Round($slot * 0.09)
  $L = $cx[0] - $bw[0] / 2; $R = $cx[-1] + $bw[-1] / 2
  @{ slot = $slot; cx = $cx; bgH = $bgH; bottom = $bottom; iconBase = $bottom - 6; bgL = $L - $pad; bgR = $R + $pad; bgTop = $bottom - $bgH; radius = $bgH * 0.29; magSlot = 121.0 }
}

# The Dock's capture of what's behind it: the area around the pane, at half resolution, box-blurred (Glass.Behind).
# $backdrop: a function giving the screen as a bitmap at 1 px per DIP for a rect.
function Capture($bmp, [Windows.Rect]$area) {
  $sw = [Math]::Max(1, [int]($area.Width / 2)); $sh = [Math]::Max(1, [int]($area.Height / 2))
  $crop = New-Object Windows.Media.Imaging.CroppedBitmap $bmp, ([Windows.Int32Rect]::new([int]$area.X, [int]$area.Y, [int]$area.Width, [int]$area.Height))
  $dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen(); $dc.DrawImage($crop, [Windows.Rect]::new(0, 0, $sw, $sh)); $dc.Close()
  [Windows.Media.RenderOptions]::SetBitmapScalingMode($dv, "HighQuality")
  $rt = New-Object Windows.Media.Imaging.RenderTargetBitmap $sw, $sh, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $rt.Render($dv)
  [byte[]]$px = [byte[]]::new($sw * $sh * 4); $rt.CopyPixels($px, $sw * 4, 0)
  $IC.GetMethod("BoxBlur").Invoke($null, [object[]]@($px, $sw, $sh, 1, 3))
  $b = [Windows.Media.Imaging.BitmapSource]::Create($sw, $sh, 96, 96, [Windows.Media.PixelFormats]::Bgr32, $null, $px, $sw * 4); $b.Freeze()
  @{ px = $px; w = $sw; h = $sh; bmp = $b; area = $area }
}

# Draws the Dock (pane, glass, icons, dots) for layout $g over capture $cap into a Canvas (DIP coordinates).
# $states (optional, one per item): @{ a; b = two pictures of the icon; mix = 0..1 from a to b; lensA, lensB = whether
# each style sits on glass; scale } -- shot 5's style changes. Without it, each item's own icon.
function DrawDock($canvas, $g, $cap, $offX = 0.0, $states = $null) {
  function Put($el, $x, $y, $w, $h) { [Windows.Controls.Canvas]::SetLeft($el, $x); [Windows.Controls.Canvas]::SetTop($el, $y); $el.Width = $w; $el.Height = $h; [void]$canvas.Children.Add($el) }
  $pane = [Windows.Rect]::new($g.bgL + $offX, $g.bgTop, $g.bgR - $g.bgL, $g.bgH); $rad = $g.radius
  $clip = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new(0, 0, $pane.Width, $pane.Height)), $rad, $rad
  $a = $cap.area; $sx = $cap.w / $a.Width; $sy = $cap.h / $a.Height
  [byte[]]$pxb = $cap.px   # (PowerShell hands it back as object[]; Liquid.Lens wants bytes)
  # the frosted capture, positioned where it was taken and clipped to the pane
  $wall = New-Object Windows.Controls.Image; $wall.Source = $cap.bmp; $wall.Stretch = "Fill"
  $wall.Clip = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new($pane.X - $a.X, $pane.Y - $a.Y, $pane.Width, $pane.Height)), $rad, $rad
  Put $wall $a.X $a.Y $a.Width $a.Height
  # the pane's lens
  $s = [object[]]@([double]$pane.Height, [double]0, [double]0); [void]$L.GetMethod("Strength").Invoke($null, $s)
  $r = [Windows.Rect]::new(($pane.X - $a.X) * $sx, ($pane.Y - $a.Y) * $sy, $pane.Width * $sx, $pane.Height * $sy)
  $lens = New-Object Windows.Controls.Image; $lens.Source = $L.GetMethod("Lens").Invoke($null, [object[]]@($pxb, $cap.w, $cap.h, $r, [double]($rad * $sy), [double]($s[1] * $sy), [double]($s[2] * $sy), $false, [double]0, [double]1.5)); $lens.Stretch = "Fill"; $lens.Clip = $clip
  Put $lens $pane.X $pane.Y $pane.Width $pane.Height
  # outline, then the tinted pane with its border and sheen
  $o = New-Object Windows.Controls.Border; $o.BorderThickness = 1; $o.BorderBrush = $app.Resources["DockOutline"]; $o.CornerRadius = $rad + 1
  Put $o ($pane.X - 1) ($pane.Y - 1) ($pane.Width + 2) ($pane.Height + 2)
  $bg = New-Object Windows.Controls.Border; $bg.BorderThickness = 1; $bg.Background = $app.Resources["DockTint"]; $bg.BorderBrush = $app.Resources["DockBorder"]; $bg.CornerRadius = $rad
  $sh2 = New-Object Windows.Controls.Border; $sh2.Background = $app.Resources["DockSheen"]; $sh2.CornerRadius = [Math]::Max(0, $rad - 1); $bg.Child = $sh2
  Put $bg $pane.X $pane.Y $pane.Width $pane.Height
  # icons, each over its own lens (LensIcon), and the running dots
  $slot = $g.slot; $inset = $slot * 16 / 256; $tile = $slot - 2 * $inset
  $sq = $IL.GetMethod("Squircle").Invoke($null, [object[]]@([double]$tile))
  for ($i = 0; $i -lt $items.Count; $i++) {
    $it = $items[$i]; $cx = $g.cx[$i] + $offX
    if ($it.sep) { $sepH = $slot * 0.62; $line = New-Object Windows.Shapes.Rectangle; $line.Fill = $app.Resources["DockBorder"]; Put $line ($cx - 0.5) ($g.bgTop + ($g.bgH - $sepH) / 2) 1 $sepH; continue }
    $ix = $cx - $slot / 2; $iy = $g.iconBase - $slot
    $ir = [Windows.Rect]::new($ix + $inset, $iy + $inset, $tile, $tile)
    $s2 = [object[]]@([double]$ir.Width, [double]0, [double]0); [void]$L.GetMethod("Strength").Invoke($null, $s2)
    $cr = [Windows.Rect]::new(($ir.X - $a.X) * $sx, ($ir.Y - $a.Y) * $sy, [Math]::Max(2, $ir.Width * $sx), [Math]::Max(2, $ir.Height * $sy))
    $st = $(if ($states) { $states[$i] } else { $null })
    $lensOn = $(if ($st) { [double]$st.lensA * (1 - $st.mix) + [double]$st.lensB * $st.mix } else { 1.0 })
    $sc = $(if ($st) { $st.scale } else { 1.0 })
    $pop = New-Object Windows.Media.ScaleTransform $sc, $sc, ($slot / 2), ($slot / 2)
    if ($lensOn -gt 0.01) {
      $li = New-Object Windows.Controls.Image; $li.Source = $L.GetMethod("Lens").Invoke($null, [object[]]@($pxb, $cap.w, $cap.h, $cr, [double]($ir.Width * 0.26 * $sx), [double]($s2[1] * $sx), [double]($s2[2] * 1.15 * $sx), $true, [double]0, [double]1.5)); $li.Stretch = "Fill"; $li.Clip = $sq
      $li.Opacity = $lensOn; $li.RenderTransform = New-Object Windows.Media.ScaleTransform $sc, $sc, ($tile / 2), ($tile / 2)
      Put $li $ir.X $ir.Y $tile $tile
    }
    if ($st) { $pics = @(@($st.a, (1 - $st.mix)), @($st.b, $st.mix)) } else { $pics = @(, @($it.icon, 1.0)) }   # (not $(...): it would flatten the pairs)
    foreach ($pic in $pics) {
      if ($pic[1] -le 0.005) { continue }
      $img = New-Object Windows.Controls.Image; $img.Source = $pic[0]; $img.Stretch = "Fill"; $img.Opacity = $pic[1]; $img.RenderTransform = $pop
      [Windows.Media.RenderOptions]::SetBitmapScalingMode($img, "HighQuality")
      Put $img $ix $iy $slot $slot
    }
    if ($it.run) { $d = New-Object Windows.Shapes.Ellipse; $d.Fill = $app.Resources["Fg"]; $d.Opacity = 0.85; Put $d ($cx - 2) ($g.bottom - 6.5) 4 4 }
  }
}

function Wallpaper($W, $H, $scale) {
  $bi = New-Object Windows.Media.Imaging.BitmapImage; $bi.BeginInit(); $bi.UriSource = [Uri]"$root\wallpapers\$wall"; $bi.DecodePixelWidth = [int]($W * $scale); $bi.CacheOption = "OnLoad"; $bi.EndInit(); $bi.Freeze(); $bi
}
function Snap($visual, $w, $h, $scale) {
  $rt = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]($w * $scale)), ([int]($h * $scale)), (96 * $scale), (96 * $scale), ([Windows.Media.PixelFormats]::Pbgra32)
  $visual.Measure([Windows.Size]::new($w, $h)); $visual.Arrange([Windows.Rect]::new(0, 0, $w, $h)); $visual.UpdateLayout(); $rt.Render($visual); $rt
}
function SavePng($bmp, $file) { $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp)); $fs = [IO.File]::Create($file); $enc.Save($fs); $fs.Close() }

if ($mode -eq "compare") {
  $W = 3440; $H = 1440
  $wp = Wallpaper $W $H 1
  $screen = New-Object Windows.Controls.Image; $screen.Source = $wp; $screen.Stretch = "UniformToFill"
  $shot = Snap $screen $W $H 1                       # the screen as the Dock's capture sees it (1 px per DIP)
  $g = Layout $W $H
  $area = [Windows.Rect]::new([Math]::Max(0, [Math]::Floor($g.bgL - $g.magSlot)), [Math]::Max(0, [Math]::Floor($g.bgTop - 8)), 0, 0)
  $area.Width = [Math]::Min($W, [Math]::Ceiling($g.bgR + $g.magSlot)) - $area.X; $area.Height = $H - $area.Y
  $cap = Capture $shot $area
  $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
  $bgimg = New-Object Windows.Controls.Image; $bgimg.Source = (Wallpaper $W $H 2); $bgimg.Stretch = "UniformToFill"; $bgimg.Width = $W; $bgimg.Height = $H; [void]$cv.Children.Add($bgimg)
  DrawDock $cv $g $cap
  $mine = Snap $cv $W $H 2
  # the real one: the same wallpaper with OpenDock's snapshot on top (the Dock window spans the screen's bottom)
  $cv2 = New-Object Windows.Controls.Canvas; $cv2.Width = $W; $cv2.Height = $H
  $bg2 = New-Object Windows.Controls.Image; $bg2.Source = (Wallpaper $W $H 2); $bg2.Stretch = "UniformToFill"; $bg2.Width = $W; $bg2.Height = $H; [void]$cv2.Children.Add($bg2)
  $sb = New-Object Windows.Media.Imaging.BitmapImage; $sb.BeginInit(); $sb.CacheOption = "OnLoad"; $sb.UriSource = [Uri]"$root\video\assets\dock-snapshot.png"; $sb.EndInit()
  $si = New-Object Windows.Controls.Image; $si.Source = $sb; $si.Stretch = "Fill"; $si.Width = $W; $si.Height = $sb.PixelHeight / 2
  [Windows.Controls.Canvas]::SetTop($si, $H - $si.Height); [void]$cv2.Children.Add($si)
  $real = Snap $cv2 $W $H 2
  $box = [Windows.Int32Rect]::new([int](($g.bgL - 20) * 2), [int](($g.bgTop - 20) * 2), [int](($g.bgR - $g.bgL + 40) * 2), [int](($H - $g.bgTop + 20) * 2))
  $a1 = New-Object Windows.Media.Imaging.CroppedBitmap $mine, $box; $a2 = New-Object Windows.Media.Imaging.CroppedBitmap $real, $box
  [byte[]]$p1 = [byte[]]::new($box.Width * $box.Height * 4); [byte[]]$p2 = [byte[]]::new($p1.Length); $a1.CopyPixels($p1, $box.Width * 4, 0); $a2.CopyPixels($p2, $box.Width * 4, 0)
  $sum = 0.0; for ($i = 0; $i -lt $p1.Length; $i += 4) { $sum += [Math]::Abs($p1[$i] - $p2[$i]) + [Math]::Abs($p1[$i + 1] - $p2[$i + 1]) + [Math]::Abs($p1[$i + 2] - $p2[$i + 2]) }
  "mean difference per channel: {0:N2} (0 = identical, 255 = opposite)" -f ($sum / ($p1.Length / 4 * 3))
  $stack = New-Object Windows.Controls.StackPanel
  foreach ($bm in $a2, $a1) { $im = New-Object Windows.Controls.Image; $im.Source = $bm; $im.Width = $box.Width / 2; $im.Height = $box.Height / 2; [void]$stack.Children.Add($im) }
  SavePng (Snap $stack ($box.Width / 2) ($box.Height) 2) "$root\video\renders\compare.png"
  "top: real snapshot, bottom: rebuilt -> video\renders\compare.png"
}

if ($mode -eq "frames") {
  # Shot 4 (Liquid Glass): starts on the laptop screen exactly as shot 3 ends (the middle 16:9 of the 3440x1440
  # desktop), zooms into the Dock while a Finder window drifts down behind it; the glass bends it frame by frame.
  $W = 3440; $H = 1440; $outW = $outWidth; $outH = [int]($outWidth * 9 / 16)
  [void][IO.Directory]::CreateDirectory($outdir)
  $g = Layout $W $H
  $wp = Wallpaper $W $H 1
  $fwimg = New-Object Windows.Media.Imaging.BitmapImage; $fwimg.BeginInit(); $fwimg.CacheOption = "OnLoad"; $fwimg.UriSource = [Uri]"$root\video\assets\finder_window.png"; $fwimg.EndInit(); $fwimg.Freeze()
  $fwW = $fwimg.PixelWidth / 2; $fwH = $fwimg.PixelHeight / 2; $fwM = 60
  $crop = [Windows.Rect]::new(($W - [Math]::Round($H * 16 / 9)) / 2, 0, [Math]::Round($H * 16 / 9), $H)
  # the menu bar, as on the laptop's screen (spanning the screen, not the whole desktop)
  function MenuBar($cv) {
    $fg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(230, 0, 0, 0))
    $font = (T Theme).GetField("Font", $all).GetValue($null)
    function Txt($s, $size, $weight = "Normal") { $t = New-Object Windows.Controls.TextBlock; $t.Text = $s; $t.FontSize = $size; $t.FontWeight = $weight; $t.FontFamily = $font; $t.Foreground = $fg; $t.VerticalAlignment = "Center"; $t }
    $dp = New-Object Windows.Controls.DockPanel; $dp.Height = 30; $dp.Width = $crop.Width - 28
    $dp.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 6; ShadowDepth = 0; Opacity = 0.35; Color = [Windows.Media.Colors]::White }
    $right = New-Object Windows.Controls.StackPanel; $right.Orientation = "Horizontal"; [Windows.Controls.DockPanel]::SetDock($right, "Right")
    foreach ($gl in [char]0xE701, [char]0xE995) { $t = Txt ([string]$gl) 15; $t.FontFamily = "Segoe Fluent Icons"; $t.Margin = "0,0,14,0"; [void]$right.Children.Add($t) }
    [void]$right.Children.Add((Txt "Sat Nov 7   9:41 AM" 13.5)); [void]$dp.Children.Add($right)
    $left = New-Object Windows.Controls.StackPanel; $left.Orientation = "Horizontal"
    $logo = Txt ([string][char]0xE7F4) 15; $logo.FontFamily = "Segoe Fluent Icons"; $logo.Margin = "0,0,16,0"; [void]$left.Children.Add($logo)
    $first = $true; foreach ($m in "Finder", "File", "Edit", "View", "Go", "Window", "Help") { $t = Txt $m 13.5 $(if ($first) { "Bold" } else { "Normal" }); $t.Margin = "0,0,18,0"; [void]$left.Children.Add($t); $first = $false }
    [void]$dp.Children.Add($left)
    [Windows.Controls.Canvas]::SetLeft($dp, $crop.X + 14); [Windows.Controls.Canvas]::SetTop($dp, 0); [void]$cv.Children.Add($dp)
  }
  function Ease($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } elseif ($t -lt 0.5) { 4 * $t * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 3) / 2 } }
  # the window: centred under the left half of the Dock, its folders sweeping down through the glass
  $winX = $g.bgL + 60 - 205
  $area = [Windows.Rect]::new([Math]::Floor($g.bgL - $g.magSlot), [Math]::Floor($g.bgTop - 8), 0, 0)
  $area.Width = [Math]::Ceiling($g.bgR + $g.magSlot) - $area.X; $area.Height = $H - $area.Y
  $focus = [Windows.Point]::new($g.bgL + 400, $g.bgTop - 175)   # keeps the rounded left end of the glass in view
  for ($f = 0; $f -lt $count; $f++) {
    $t = $f / [Math]::Max(1, $count - 1)
    $z = 1 + 1.45 * (Ease ($t / 0.8))                     # zoom over the first 80%, then hold
    $winTop = 300 + (1305 - 300) * (Ease (($t - 0.05) / 0.95))
    # the screen without the Dock: wallpaper, the Finder window, the menu bar
    $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
    $bgimg = New-Object Windows.Controls.Image; $bgimg.Source = $wp; $bgimg.Stretch = "UniformToFill"; $bgimg.Width = $W; $bgimg.Height = $H; [void]$cv.Children.Add($bgimg)
    $wi = New-Object Windows.Controls.Image; $wi.Source = $fwimg; $wi.Stretch = "Fill"; $wi.Width = $fwW; $wi.Height = $fwH
    [Windows.Controls.Canvas]::SetLeft($wi, $winX - $fwM); [Windows.Controls.Canvas]::SetTop($wi, $winTop - $fwM); [void]$cv.Children.Add($wi)
    # it opens as a Mac window does: a quick zoom up from slightly smaller while it fades in
    $open = [Math]::Min(1.0, $f / 12.0); $os = 0.9 + 0.1 * (1 - [Math]::Pow(1 - $open, 3))
    $wi.Opacity = $open; $wi.RenderTransform = New-Object Windows.Media.ScaleTransform $os, $os, ($fwW / 2), ($fwH / 2)
    MenuBar $cv
    # what the Dock sees behind it, then the Dock over it
    $shot = Snap $cv $W $H 1
    $cap = Capture $shot $area
    DrawDock $cv $g $cap
    # the camera: from the whole laptop screen to a close-up of the Dock's left end
    $vw = $crop.Width / $z; $vh = $crop.Height / $z
    $cx = $crop.X + $crop.Width / 2 + ($focus.X - ($crop.X + $crop.Width / 2)) * (($z - 1) / 1.45)
    $cy = $H - $vh / 2                                    # the bottom of the view stays on the screen's bottom: the Dock never leaves the frame
    $k = $outW / $vw
    $cv.RenderTransform = New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($k, 0, 0, $k, -($cx - $vw / 2) * $k, -($cy - $vh / 2) * $k))
    $view = New-Object Windows.Controls.Canvas; $view.Width = $outW; $view.Height = $outH; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
    $frame = Snap $view $outW $outH 1
    SavePng $frame ([IO.Path]::Combine($outdir, ("f_{0:D4}.png" -f ($f + 1))))
    if ($f % 15 -eq 0) { Write-Host "frame $($f + 1) / $count" }
  }
  "done"
}


if ($mode -eq "styles") {
  # Shot 5 (icon styles): picks up where shot 4 ends (close on the Dock, the Finder window behind it), eases out to
  # the whole Dock while the window closes, and three waves roll along the Dock switching every icon's style:
  # Liquid Glass (colors) -> Default -> Clear -> Tinted pink. Each style drawn by IconLook, as the Dock does.
  $W = 3440; $H = 1440; $outW = $outWidth; $outH = [int]($outWidth * 9 / 16)
  [void][IO.Directory]::CreateDirectory($outdir)
  $g = Layout $W $H
  $wp = Wallpaper $W $H 1
  $fwimg = New-Object Windows.Media.Imaging.BitmapImage; $fwimg.BeginInit(); $fwimg.CacheOption = "OnLoad"; $fwimg.UriSource = [Uri]"$root\video\assets\finder_window.png"; $fwimg.EndInit(); $fwimg.Freeze()
  $fwW = $fwimg.PixelWidth / 2; $fwH = $fwimg.PixelHeight / 2; $fwM = 60
  $crop = [Windows.Rect]::new(($W - [Math]::Round($H * 16 / 9)) / 2, 0, [Math]::Round($H * 16 / 9), $H)
  $winX = $g.bgL + 60 - 205; $winTop = 1305
  $area = [Windows.Rect]::new([Math]::Floor($g.bgL - $g.magSlot), [Math]::Floor($g.bgTop - 8), 0, 0)
  $area.Width = [Math]::Ceiling($g.bgR + $g.magSlot) - $area.X; $area.Height = $H - $area.Y
  function Ease($t) { if ($t -le 0) { 0.0 } elseif ($t -ge 1) { 1.0 } elseif ($t -lt 0.5) { 4 * $t * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 3) / 2 } }
  # every icon in every style (Default = the icon itself, as IconLook.Apply leaves it)
  $pink = [Windows.Media.ColorConverter]::ConvertFromString("#FF375F")
  $looks = @{}
  for ($i = 0; $i -lt $items.Count; $i++) {
    $it = $items[$i]; if ($it.sep) { continue }
    $raw = $IC.GetMethod("RawOf").Invoke($null, [object[]]@($it.src)); if ($raw -eq $null) { $raw = $it.src }
    $looks[$i] = @{ 0 = $it.src; 3 = $it.icon
      2 = $IL.GetMethod("Style").Invoke($null, [object[]]@($raw, [int]2, $pink, $false))
      1 = $IL.GetMethod("Style").Invoke($null, [object[]]@($raw, [int]1, $pink, $false)) }
  }
  $seq = @(3, 0, 2, 1); $waveAt = @(16, 40, 62); $sweep = 16.0; $flip = 9.0
  $glass = @{ 0 = 0; 1 = 0; 2 = 1; 3 = 1 }
  # the camera: from shot 4's last view (close on the Dock's left end) out to the whole Dock
  $focus = [Windows.Point]::new($g.bgL + 400, $g.bgTop - 175); $z0 = 2.45
  $dockW = $g.bgR - $g.bgL; $z1 = $crop.Width / ($dockW * 1.32); $c1 = ($g.bgL + $g.bgR) / 2
  for ($f = 0; $f -lt $count; $f++) {
    $e = Ease ($f / 36.0)
    $z = $z0 + ($z1 - $z0) * $e
    $vw = $crop.Width / $z; $vh = $crop.Height / $z
    $cx = $focus.X + ($c1 - $focus.X) * $e; $cy = $H - $vh / 2
    # the icons' states at this frame
    $states = @()
    for ($i = 0; $i -lt $items.Count; $i++) {
      if ($items[$i].sep) { $states += $null; continue }
      $u = ($g.cx[$i] - $g.bgL) / ($g.bgR - $g.bgL)
      $from = $seq[0]; $to = $seq[0]; $mix = 0.0; $scale = 1.0
      for ($wave = 0; $wave -lt $waveAt.Count; $wave++) {
        $start = $waveAt[$wave] + $sweep * $u; $p = ($f - $start) / $flip
        if ($p -ge 1) { $from = $seq[$wave + 1]; $to = $seq[$wave + 1]; $mix = 0.0 }
        elseif ($p -gt 0) { $from = $seq[$wave]; $to = $seq[$wave + 1]; $mix = Ease $p; $scale = 1 - 0.16 * [Math]::Sin([Math]::PI * $p) }
      }
      $states += @{ a = $looks[$i][$from]; b = $looks[$i][$to]; mix = $mix; lensA = $glass[$from]; lensB = $glass[$to]; scale = $scale }
    }
    $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
    $bgimg = New-Object Windows.Controls.Image; $bgimg.Source = $wp; $bgimg.Stretch = "UniformToFill"; $bgimg.Width = $W; $bgimg.Height = $H; [void]$cv.Children.Add($bgimg)
    # the Finder window closes: shrinks a little as it fades
    $close = [Math]::Min(1.0, $f / 10.0)
    if ($close -lt 1) {
      $wi = New-Object Windows.Controls.Image; $wi.Source = $fwimg; $wi.Stretch = "Fill"; $wi.Width = $fwW; $wi.Height = $fwH; $wi.Opacity = 1 - $close
      $cs = 1 - 0.08 * $close; $wi.RenderTransform = New-Object Windows.Media.ScaleTransform $cs, $cs, ($fwW / 2), ($fwH / 2)
      [Windows.Controls.Canvas]::SetLeft($wi, $winX - $fwM); [Windows.Controls.Canvas]::SetTop($wi, $winTop - $fwM); [void]$cv.Children.Add($wi)
    }
    $shot = Snap $cv $W $H 1
    $cap = Capture $shot $area
    DrawDock $cv $g $cap 0.0 $states
    $k = $outW / $vw
    $cv.RenderTransform = New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($k, 0, 0, $k, -($cx - $vw / 2) * $k, -($cy - $vh / 2) * $k))
    $view = New-Object Windows.Controls.Canvas; $view.Width = $outW; $view.Height = $outH; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
    SavePng (Snap $view $outW $outH 1) ([IO.Path]::Combine($outdir, ("f_{0:D4}.png" -f ($f + 1))))
    if ($f % 15 -eq 0) { Write-Host "frame $($f + 1) / $count" }
  }
  "done"
}


if ($mode -eq "desk") {
  # Shot 6's backdrop: the laptop's screen (the middle 16:9 of the desktop) with the menu bar and the Dock in pink
  # Tinted, exactly as shot 5 leaves it, as one 3840 x 2160 picture for Blender. Also writes desk_camera.txt: shot
  # 5's last view (centre and width in screen DIPs), where shot 6's camera starts.
  $W = 3440; $H = 1440; $outW = 3840; $outH = 2160
  $g = Layout $W $H
  $wp = Wallpaper $W $H 2
  $crop = [Windows.Rect]::new(($W - [Math]::Round($H * 16 / 9)) / 2, 0, [Math]::Round($H * 16 / 9), $H)
  $area = [Windows.Rect]::new([Math]::Floor($g.bgL - $g.magSlot), [Math]::Floor($g.bgTop - 8), 0, 0)
  $area.Width = [Math]::Ceiling($g.bgR + $g.magSlot) - $area.X; $area.Height = $H - $area.Y
  $pink = [Windows.Media.ColorConverter]::ConvertFromString("#FF375F")
  $states = @()
  for ($i = 0; $i -lt $items.Count; $i++) {
    $it = $items[$i]; if ($it.sep) { $states += $null; continue }
    $raw = $IC.GetMethod("RawOf").Invoke($null, [object[]]@($it.src)); if ($raw -eq $null) { $raw = $it.src }
    $p1 = $IL.GetMethod("Style").Invoke($null, [object[]]@($raw, [int]1, $pink, $false))
    $states += @{ a = $p1; b = $p1; mix = 0.0; lensA = 0; lensB = 0; scale = 1.0 }
  }
  $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
  $bgimg = New-Object Windows.Controls.Image; $bgimg.Source = $wp; $bgimg.Stretch = "UniformToFill"; $bgimg.Width = $W; $bgimg.Height = $H; [void]$cv.Children.Add($bgimg)
  $shot = Snap $cv $W $H 1
  $cap = Capture $shot $area
  # the menu bar across the laptop's screen
  $fg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(230, 0, 0, 0))
  $font = (T Theme).GetField("Font", $all).GetValue($null)
  function Txt($s, $size, $weight = "Normal") { $t = New-Object Windows.Controls.TextBlock; $t.Text = $s; $t.FontSize = $size; $t.FontWeight = $weight; $t.FontFamily = $font; $t.Foreground = $fg; $t.VerticalAlignment = "Center"; $t }
  $dp = New-Object Windows.Controls.DockPanel; $dp.Height = 30; $dp.Width = $crop.Width - 28
  $dp.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 6; ShadowDepth = 0; Opacity = 0.35; Color = [Windows.Media.Colors]::White }
  $right = New-Object Windows.Controls.StackPanel; $right.Orientation = "Horizontal"; [Windows.Controls.DockPanel]::SetDock($right, "Right")
  foreach ($gl in [char]0xE701, [char]0xE995) { $t = Txt ([string]$gl) 15; $t.FontFamily = "Segoe Fluent Icons"; $t.Margin = "0,0,14,0"; [void]$right.Children.Add($t) }
  [void]$right.Children.Add((Txt "Sat Nov 7   9:41 AM" 13.5)); [void]$dp.Children.Add($right)
  $left = New-Object Windows.Controls.StackPanel; $left.Orientation = "Horizontal"
  $logo = Txt ([string][char]0xE7F4) 15; $logo.FontFamily = "Segoe Fluent Icons"; $logo.Margin = "0,0,16,0"; [void]$left.Children.Add($logo)
  $first = $true; foreach ($m in "Finder", "File", "Edit", "View", "Go", "Window", "Help") { $t = Txt $m 13.5 $(if ($first) { "Bold" } else { "Normal" }); $t.Margin = "0,0,18,0"; [void]$left.Children.Add($t); $first = $false }
  [void]$dp.Children.Add($left)
  [Windows.Controls.Canvas]::SetLeft($dp, $crop.X + 14); [Windows.Controls.Canvas]::SetTop($dp, 0); [void]$cv.Children.Add($dp)
  DrawDock $cv $g $cap 0.0 $states
  $k = $outW / $crop.Width
  $cv.RenderTransform = New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($k, 0, 0, $k, -$crop.X * $k, 0))
  $view = New-Object Windows.Controls.Canvas; $view.Width = $outW; $view.Height = $outH; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
  SavePng (Snap $view $outW $outH 1) "$root\video\assets\desk_pink.png"
  # shot 5's last view, in screen DIPs (0..2560 x 0..1440 of the laptop's screen)
  $dockW = $g.bgR - $g.bgL; $z1 = $crop.Width / ($dockW * 1.32); $vw = $crop.Width / $z1; $vh = $crop.Height / $z1
  $c1x = ($g.bgL + $g.bgR) / 2 - $crop.X; $c1y = $H - $vh / 2
  Set-Content -Encoding ascii "$root\video\assets\desk_camera.txt" ("{0} {1} {2} {3}" -f $c1x, $c1y, $vw, $vh)
  "desk ok: view $c1x $c1y $vw x $vh"
}


if ($mode -eq "icons") {
  # For the 3D Dock: every icon on its own (Liquid Glass (colors), as the Dock shows it, 256 px with its shadow) in
  # Dock order, the layout in screen DIPs (icons.txt: index, centre x, separator?), and the laptop's screen without
  # the Dock (desk_nodock.png: wallpaper and menu bar, 3840 x 2160).
  $W = 3440; $H = 1440
  $dir = "$root\video\assets\icons"; [void][IO.Directory]::CreateDirectory($dir)
  $g = Layout $W $H
  $crop = [Windows.Rect]::new(($W - [Math]::Round($H * 16 / 9)) / 2, 0, [Math]::Round($H * 16 / 9), $H)
  $lines = @("# i cx_in_screen sep  (screen = the laptop's 2560 x 1440 DIP; pane: " + ($g.bgL - $crop.X) + " " + $g.bgTop + " " + ($g.bgR - $g.bgL) + " " + $g.bgH + ")")
  for ($i = 0; $i -lt $items.Count; $i++) {
    $it = $items[$i]
    if (-not $it.sep) {
      $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($it.icon))
      $fs = [IO.File]::Create(("$dir\icon_{0:D2}.png" -f $i)); $enc.Save($fs); $fs.Close()
    }
    $lines += ("{0} {1} {2}" -f $i, ($g.cx[$i] - $crop.X), [int][bool]$it.sep)
  }
  Set-Content -Encoding ascii "$dir\icons.txt" $lines
  # the screen without the Dock
  $cv = New-Object Windows.Controls.Canvas; $cv.Width = $W; $cv.Height = $H; $cv.ClipToBounds = $true
  $bgimg = New-Object Windows.Controls.Image; $bgimg.Source = (Wallpaper $W $H 2); $bgimg.Stretch = "UniformToFill"; $bgimg.Width = $W; $bgimg.Height = $H; [void]$cv.Children.Add($bgimg)
  $fg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(230, 0, 0, 0)); $font = (T Theme).GetField("Font", $all).GetValue($null)
  function Txt($s, $size, $weight = "Normal") { $t = New-Object Windows.Controls.TextBlock; $t.Text = $s; $t.FontSize = $size; $t.FontWeight = $weight; $t.FontFamily = $font; $t.Foreground = $fg; $t.VerticalAlignment = "Center"; $t }
  $dp = New-Object Windows.Controls.DockPanel; $dp.Height = 30; $dp.Width = $crop.Width - 28
  $dp.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 6; ShadowDepth = 0; Opacity = 0.35; Color = [Windows.Media.Colors]::White }
  $right = New-Object Windows.Controls.StackPanel; $right.Orientation = "Horizontal"; [Windows.Controls.DockPanel]::SetDock($right, "Right")
  foreach ($gl in [char]0xE701, [char]0xE995) { $t = Txt ([string]$gl) 15; $t.FontFamily = "Segoe Fluent Icons"; $t.Margin = "0,0,14,0"; [void]$right.Children.Add($t) }
  [void]$right.Children.Add((Txt "Sat Nov 7   9:41 AM" 13.5)); [void]$dp.Children.Add($right)
  $left = New-Object Windows.Controls.StackPanel; $left.Orientation = "Horizontal"
  $logo = Txt ([string][char]0xE7F4) 15; $logo.FontFamily = "Segoe Fluent Icons"; $logo.Margin = "0,0,16,0"; [void]$left.Children.Add($logo)
  $first = $true; foreach ($m in "Finder", "File", "Edit", "View", "Go", "Window", "Help") { $t = Txt $m 13.5 $(if ($first) { "Bold" } else { "Normal" }); $t.Margin = "0,0,18,0"; [void]$left.Children.Add($t); $first = $false }
  [void]$dp.Children.Add($left)
  [Windows.Controls.Canvas]::SetLeft($dp, $crop.X + 14); [Windows.Controls.Canvas]::SetTop($dp, 0); [void]$cv.Children.Add($dp)
  $k = 3840 / $crop.Width
  $cv.RenderTransform = New-Object Windows.Media.MatrixTransform ([Windows.Media.Matrix]::new($k, 0, 0, $k, -$crop.X * $k, 0))
  $view = New-Object Windows.Controls.Canvas; $view.Width = 3840; $view.Height = 2160; $view.ClipToBounds = $true; [void]$view.Children.Add($cv)
  SavePng (Snap $view 3840 2160 1) "$root\video\assets\desk_nodock.png"
  "icons ok: $($items.Count) items"
}
