# The laptop's screen for the ad: an OpenDock desktop (wallpaper, menu bar, Dock) built from OpenDock's own code,
# so it looks like the real thing: the Dock uses DockWindow's geometry, the theme's Dock brushes, IconLook for the
# icon style and Liquid.Lens for the glass, with the wallpaper standing in for the live capture behind it.
#   powershell -STA -File screen_desktop.ps1 -out video\assets\screen_desktop.png [-style 3] [-wall drift-light.jpg]
#   -snapshot video\assets\dock-snapshot.png   uses OpenDock's own picture of its Dock (OpenDock.exe --snapshot-dock,
#   taken at 2x on a 3440x1440 screen with the same wallpaper) instead of rebuilding the Dock: the screen is then the
#   middle 16:9 of that desktop, so the Dock's glass lines up with the wallpaper around it.
param([string]$out, [string]$wall = "drift-light.jpg", [int]$style = 3, [int]$dark = 0, [string]$snapshot = "", [int]$realW = 3440, [int]$realH = 1440)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
function Pump($ms) { $f = New-Object Windows.Threading.DispatcherFrame; $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval = [TimeSpan]::FromMilliseconds($ms); $t.Add_Tick({ $t.Stop(); $f.Continue = $false }.GetNewClosure()); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($f) }

# Settings for a rebuilt Dock (kept apart from your real settings): light appearance, the chosen icon style
$S = T Settings
$scratch = Join-Path $env:TEMP "opendock-video-settings"; [void][IO.Directory]::CreateDirectory($scratch)
$S.GetField("StorageDirectory", $all).SetValue($null, $scratch)
$S.GetField("Appearance").SetValue($null, $(if ($dark) { 2 } else { 1 }))
$S.GetField("IconStyle").SetValue($null, $style)
# the Dock as you have it set up: size, spacing, no uniform tiles, the Liquid Glass slider
$S.GetField("IconSize").SetValue($null, 79); $S.GetField("DockSpacing").SetValue($null, 3)
$S.GetField("IconTiles").SetValue($null, $false); $S.GetField("GlassTint").SetValue($null, 45)
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
(T Icons).GetMethod("Start").Invoke($null, $null)
(T Theme).GetMethod("Init").Invoke($null, [object[]]@($app.psobject.BaseObject))
$art = T Art; $IC = T Icons; $IL = T IconLook; $L = T Liquid

# ---------- the Dock's items, in order (a separator before Downloads and the Trash, as in OpenDock) ----------
$apps = @(
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
  "shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
  "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe",
  "shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
  "$env:ProgramFiles\Blender Foundation\Blender 5.1\blender.exe",
  "${env:ProgramFiles(x86)}\Steam\Steam.exe",
  "$env:ProgramFiles\obs-studio\bin\64bit\obs64.exe")
$got = @{}
foreach ($p in $apps) { $key = $p; $cb = [Action[Windows.Media.ImageSource]]{ param($x) $got[$key] = $x }.GetNewClosure(); [void]$IC.GetMethod("Get").Invoke($null, [object[]]@($p, $true, $cb, 256, $true, $null)) }
Pump 4000
$items = New-Object System.Collections.ArrayList   # @{ icon; running } or "sep"
[void]$items.Add(@{ icon = $art.GetProperty("Finder").GetValue($null); running = $true })
[void]$items.Add(@{ icon = $art.GetProperty("Launchpad").GetValue($null); running = $false })
$runningApps = @(0, 2, 3)   # Chrome, VS Code, Spotify
for ($i = 0; $i -lt $apps.Count; $i++) { if ($got.ContainsKey($apps[$i])) { [void]$items.Add(@{ icon = $got[$apps[$i]]; running = ($runningApps -contains $i) }) } else { Write-Host "no icon: $($apps[$i])" } }
[void]$items.Add(@{ icon = $art.GetProperty("Settings").GetValue($null); running = $false })
[void]$items.Add("sep")
[void]$items.Add(@{ icon = $art.GetProperty("Downloads").GetValue($null); running = $false })
[void]$items.Add(@{ icon = $art.GetMethod("Trash").Invoke($null, [object[]]@($false)); running = $false })
# every icon through the icon style, at its place along the Dock (as DockWindow.LoadIcon does)
$n = ($items | Where-Object { $_ -ne "sep" }).Count; $k = 0
foreach ($it in $items) { if ($it -ne "sep") { $it.icon = $IL.GetMethod("Apply").Invoke($null, [object[]]@($it.icon, [double]($(if ($n -gt 1) { $k / ($n - 1) } else { 0.5 })))); $k++ } }

# ---------- the desktop ----------
$W = 1280; $H = 800; $SC = 2   # DIP, rendered at 2x (2560 x 1600)
if ($snapshot) { $H = $realH; $W = [int][Math]::Round($realH * 16 / 9); $SC = 3840 / $W }   # the middle 16:9 of the real screen, as 3840 x 2160
$canvas = New-Object Windows.Controls.Canvas; $canvas.Width = $W; $canvas.Height = $H; $canvas.ClipToBounds = $true
$bi = New-Object Windows.Media.Imaging.BitmapImage; $bi.BeginInit(); $bi.UriSource = [Uri]"$root\wallpapers\$wall"; $bi.DecodePixelWidth = $(if ($snapshot) { [int]($realW * $SC) } else { $W * $SC }); $bi.EndInit()
$wallImg = New-Object Windows.Controls.Image; $wallImg.Source = $bi; $wallImg.Stretch = "UniformToFill"; $wallImg.Width = $W; $wallImg.Height = $H
if ($snapshot) { $wallImg.Width = $realW; $wallImg.Height = $realH; [Windows.Controls.Canvas]::SetLeft($wallImg, -($realW - $W) / 2) }   # laid out as Windows fills the real screen
[void]$canvas.Children.Add($wallImg)
if ($snapshot) {
  $sb = New-Object Windows.Media.Imaging.BitmapImage; $sb.BeginInit(); $sb.CacheOption = "OnLoad"; $sb.UriSource = [Uri](Resolve-Path $snapshot).Path; $sb.EndInit()
  $snapImg = New-Object Windows.Controls.Image; $snapImg.Source = $sb; $snapImg.Stretch = "Fill"
  [Windows.Media.RenderOptions]::SetBitmapScalingMode($snapImg, "HighQuality")
  $snapImg.Width = $realW; $snapImg.Height = $sb.PixelHeight / 2   # the snapshot is 2x
  [Windows.Controls.Canvas]::SetLeft($snapImg, -($realW - $W) / 2); [Windows.Controls.Canvas]::SetTop($snapImg, $H - $snapImg.Height)
}
# the "capture" behind the Dock: the wallpaper as it's laid out, at 2 px per DIP (only for a rebuilt Dock)
if (-not $snapshot) {
$wv =New-Object Windows.Controls.Image; $wv.Source = $bi; $wv.Stretch = "UniformToFill"; $wv.Width = $W; $wv.Height = $H
$wv.Measure([Windows.Size]::new($W, $H)); $wv.Arrange([Windows.Rect]::new(0, 0, $W, $H))
$cap = New-Object Windows.Media.Imaging.RenderTargetBitmap ($W * $SC), ($H * $SC), (96 * $SC), (96 * $SC), ([Windows.Media.PixelFormats]::Pbgra32); $cap.Render($wv)
$capW = $W * $SC; $capH = $H * $SC; [byte[]]$capPx = [byte[]]::new($capW * $capH * 4); $cap.CopyPixels($capPx, $capW * 4, 0)
$blurPx = [byte[]]$capPx.Clone(); $IC.GetMethod("BoxBlur").Invoke($null, [object[]]@($blurPx, $capW, $capH, 2, 3))
$blurBmp = [Windows.Media.Imaging.BitmapSource]::Create($capW, $capH, 96 * $SC, 96 * $SC, [Windows.Media.PixelFormats]::Bgra32, $null, $blurPx, $capW * 4)
}

function Br($a, $r, $g, $b) { New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb($a, $r, $g, $b)) }
function Res($k) { $app.Resources[$k] }
function Place($el, $x, $y, $w, $h) { [Windows.Controls.Canvas]::SetLeft($el, $x); [Windows.Controls.Canvas]::SetTop($el, $y); $el.Width = $w; $el.Height = $h; [void]$canvas.Children.Add($el) }

# ---------- menu bar: as MenuBarWindow draws it (no background; near-black text with a white glow on a light wallpaper) ----------
$fg = $(if ($dark) { Br 255 255 255 255 } else { Br 230 0 0 0 })
function Txt($s, $size, $weight = "Normal") { $t = New-Object Windows.Controls.TextBlock; $t.Text = $s; $t.FontSize = $size; $t.FontWeight = $weight; $t.FontFamily = (T Theme).GetField("Font", $all).GetValue($null); $t.Foreground = $fg; $t.VerticalAlignment = "Center"; $t }
$dp = New-Object Windows.Controls.DockPanel
$dp.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 6; ShadowDepth = 0; Opacity = 0.35; Color = $(if ($dark) { [Windows.Media.Colors]::Black } else { [Windows.Media.Colors]::White }) }
$right = New-Object Windows.Controls.StackPanel; $right.Orientation = "Horizontal"; [Windows.Controls.DockPanel]::SetDock($right, "Right")
foreach ($g in [char]0xE701, [char]0xE995) { $t = Txt ([string]$g) 15; $t.FontFamily = "Segoe Fluent Icons"; $t.Margin = "0,0,14,0"; [void]$right.Children.Add($t) }
[void]$right.Children.Add((Txt "Sat Nov 7   9:41 AM" 13.5))
[void]$dp.Children.Add($right)
$left = New-Object Windows.Controls.StackPanel; $left.Orientation = "Horizontal"
$logo = Txt ([string][char]0xE7F4) 15; $logo.FontFamily = "Segoe Fluent Icons"; $logo.Margin = "0,0,16,0"; [void]$left.Children.Add($logo)
$first = $true
foreach ($m in "Finder", "File", "Edit", "View", "Go", "Window", "Help") { $t = Txt $m 13.5 $(if ($first) { "Bold" } else { "Normal" }); $t.Margin = "0,0,18,0"; [void]$left.Children.Add($t); $first = $false }
[void]$dp.Children.Add($left)
Place $dp 14 0 ($W - 28) 30

if ($snapshot) { [void]$canvas.Children.Add($snapImg); $items = @() }
# ---------- the Dock: DockWindow.Frame's layout at rest (no magnification, nothing hovered) ----------
if (-not $snapshot) {
$slot = [double]$S.GetField("IconSize").GetValue($null)        # 60
$gap = 6; $bgH = [Math]::Round($slot + 12); $bottom = $H - $gap; $iconBase = $bottom - 6; $pad = [Math]::Round($slot * 0.09)
$baseW = { param($it) if ($it -eq "sep") { $slot * 0.34 } else { $slot } }
$sp = [double]$S.GetField("DockSpacing").GetValue($null)
$total = 0.0; foreach ($it in $items) { $total += (& $baseW $it) + $sp }; $total -= $sp
$x = ($W - $total) / 2; $cxs = @()
foreach ($it in $items) { $bw = & $baseW $it; $cxs += $x + $bw / 2; $x += $bw + $sp }
$bgL = $cxs[0] - $slot / 2 - $pad; $bgR = $cxs[-1] + $slot / 2 + $pad; $bgTop = $bottom - $bgH; $radius = $bgH * 0.29
$pane = [Windows.Rect]::new($bgL, $bgTop, $bgR - $bgL, $bgH)
$clip = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new(0, 0, $pane.Width, $pane.Height)), $radius, $radius
# 1. the frosted backdrop and 2. the lens bending it at the rim (LensPane)
$wallPart = New-Object Windows.Controls.Image; $wallPart.Source = New-Object Windows.Media.Imaging.CroppedBitmap $blurBmp, ([Windows.Int32Rect]::new([int]($pane.X * $SC), [int]($pane.Y * $SC), [int]($pane.Width * $SC), [int]($pane.Height * $SC))); $wallPart.Stretch = "Fill"; $wallPart.Clip = $clip
Place $wallPart $pane.X $pane.Y $pane.Width $pane.Height
$a = [object[]]@([double]$pane.Height, [double]0, [double]0); [void]$L.GetMethod("Strength").Invoke($null, $a)
$lens = $L.GetMethod("Lens").Invoke($null, [object[]]@($capPx, $capW, $capH, [Windows.Rect]::new($pane.X * $SC, $pane.Y * $SC, $pane.Width * $SC, $pane.Height * $SC), [double]($radius * $SC), [double]($a[1] * $SC), [double]($a[2] * $SC), $false, [double]0, [double]1.5))
$lensImg = New-Object Windows.Controls.Image; $lensImg.Source = $lens; $lensImg.Stretch = "Fill"; $lensImg.Clip = $clip
Place $lensImg $pane.X $pane.Y $pane.Width $pane.Height
# 3. the hairline outline, 4. the tinted pane with its border and sheen
$outline = New-Object Windows.Controls.Border; $outline.BorderThickness = 1; $outline.BorderBrush = Res "DockOutline"; $outline.CornerRadius = $radius + 1
Place $outline ($pane.X - 1) ($pane.Y - 1) ($pane.Width + 2) ($pane.Height + 2)
$bgB = New-Object Windows.Controls.Border; $bgB.BorderThickness = 1; $bgB.Background = Res "DockTint"; $bgB.BorderBrush = Res "DockBorder"; $bgB.CornerRadius = $radius
$sheen = New-Object Windows.Controls.Border; $sheen.Background = Res "DockSheen"; $sheen.CornerRadius = [Math]::Max(0, $radius - 1); $bgB.Child = $sheen
Place $bgB $pane.X $pane.Y $pane.Width $pane.Height
# 5. the icons: each Liquid Glass icon over its own bent piece of backdrop (LensIcon), then 6. the running dots
$inset = $slot * 16 / 256; $tile = $slot - 2 * $inset
$squircle = $IL.GetMethod("Squircle").Invoke($null, [object[]]@([double]$tile))
$onGlass = $style -ge 2
for ($i = 0; $i -lt $items.Count; $i++) {
  $it = $items[$i]; $cx = $cxs[$i]
  if ($it -eq "sep") {
    $sepH = $slot * 0.62; $line = New-Object Windows.Shapes.Rectangle; $line.Fill = Res "DockBorder"
    Place $line ($cx - 0.5) ($bgTop + ($bgH - $sepH) / 2) 1 $sepH; continue
  }
  $ix = $cx - $slot / 2; $iy = $iconBase - $slot
  if ($onGlass) {
    $r = [Windows.Rect]::new($ix + $inset, $iy + $inset, $tile, $tile)
    $b = [object[]]@([double]$r.Width, [double]0, [double]0); [void]$L.GetMethod("Strength").Invoke($null, $b)
    $il = $L.GetMethod("Lens").Invoke($null, [object[]]@($capPx, $capW, $capH, [Windows.Rect]::new($r.X * $SC, $r.Y * $SC, $r.Width * $SC, $r.Height * $SC), [double]($r.Width * 0.26 * $SC), [double]($b[1] * $SC), [double]($b[2] * 1.15 * $SC), $true, [double]0, [double]1.5))
    $li = New-Object Windows.Controls.Image; $li.Source = $il; $li.Stretch = "Fill"; $li.Clip = $squircle
    Place $li $r.X $r.Y $tile $tile
  }
  $img = New-Object Windows.Controls.Image; $img.Source = $it.icon; $img.Stretch = "Fill"
  [Windows.Media.RenderOptions]::SetBitmapScalingMode($img, "HighQuality")
  Place $img $ix $iy $slot $slot
  if ($it.running) { $dot = New-Object Windows.Shapes.Ellipse; $dot.Fill = Res "Fg"; $dot.Opacity = 0.85; Place $dot ($cx - 2) ($bottom - 6.5) 4 4 }
}
}

$canvas.Measure([Windows.Size]::new($W, $H)); $canvas.Arrange([Windows.Rect]::new(0, 0, $W, $H)); $canvas.UpdateLayout(); Pump 300; $canvas.UpdateLayout()
$rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ($W * $SC), ($H * $SC), (96 * $SC), (96 * $SC), ([Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($canvas)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $out))
$fs = [IO.File]::Create($out); $enc.Save($fs); $fs.Close(); "ok " + $items.Count + " items"
