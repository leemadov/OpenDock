# OpenDock's Control Center for the ad, rendered by ControlCenterWindow itself, with a made-up song playing
# ("Glass Skies" by "OpenDock Sessions", original cover art). Nothing is read from this PC: the radios, Wi-Fi name
# and volume are only filled in when the panel opens for real, which this doesn't do.
#   powershell -STA -File cc_panel.ps1 -out video\assets\cc_panel.png
#   -backdrop screen.png   the screen behind it (1 px per DIP, 2560 x 1440): the glass is frosted and bent from it, as
#                          Frost() does with its capture; the panel sits where Place() puts it (top right, under the menu bar)
param([string]$out, [double]$scale = 2, [string]$backdrop = "")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
function Pump($ms) { $f = New-Object Windows.Threading.DispatcherFrame; $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval = [TimeSpan]::FromMilliseconds($ms); $t.Add_Tick({ $t.Stop(); $f.Continue = $false }.GetNewClosure()); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($f) }
$S = T Settings
$scratch = Join-Path $env:TEMP "opendock-video-settings"; [void][IO.Directory]::CreateDirectory($scratch)
$S.GetField("StorageDirectory", $all).SetValue($null, $scratch)
$S.GetField("Appearance").SetValue($null, 1)
$S.GetField("StageManager").SetValue($null, $true); $S.GetField("IconStyle").SetValue($null, 1); $S.GetField("IconTint").SetValue($null, "#FF375F"); $S.GetField("OneColor").SetValue($null, $true)
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
(T Icons).GetMethod("Start").Invoke($null, $null)
(T Theme).GetMethod("Init").Invoke($null, [object[]]@($app.psobject.BaseObject))
# the song
$NP = T NowPlaying
$cover = New-Object Windows.Media.Imaging.BitmapImage; $cover.BeginInit(); $cover.CacheOption = "OnLoad"; $cover.UriSource = [Uri]"$root\video\assets\cover_glass_skies.png"; $cover.DecodePixelWidth = 240; $cover.EndInit(); $cover.Freeze()
$NP.GetField("Title").SetValue($null, "Glass Skies"); $NP.GetField("Artist").SetValue($null, "OpenDock Sessions")
$NP.GetField("Playing").SetValue($null, $true); $NP.GetField("Art").SetValue($null, $cover)
$NP.GetField("Accent").SetValue($null, [Windows.Media.ColorConverter]::ConvertFromString("#FF6B9A"))
# NowPlaying.Active also wants a media session; give it a stand-in object of that type (never called)
$sf = $NP.GetField("session", $all)
try { $sf.SetValue($null, [Runtime.Serialization.FormatterServices]::GetUninitializedObject($sf.FieldType)) } catch { Write-Host "no stand-in session: $($_.Exception.Message)" }
"active: " + $NP.GetProperty("Active").GetValue($null)
$CCT = T ControlCenterWindow
$cc = [Activator]::CreateInstance((T ControlCenterWindow), $true)
foreach ($m in "ShowMusic", "ShowStage") { $mi = (T ControlCenterWindow).GetMethod($m, $all); if ($mi) { [void]$mi.Invoke($cc, $null) } }
# The music tile only shows a song with a live media session, so it's filled here the way ShowMusic fills it when
# something is playing, with the panel's own pieces (Text, Transport and the play/pause shapes).
$tile = $CCT.GetField("musicTile", $all).GetValue($cc)
$body = $tile.Body; $body.Children.Clear()
$TextM = $null; for ($t = $CCT; $t -and -not $TextM; $t = $t.BaseType) { $TextM = $t.GetMethods($all) | Where-Object { $_.Name -eq "Text" -and $_.GetParameters().Count -eq 4 } | Select-Object -First 1 }
$TransM = $null; for ($t = $CCT; $t -and -not $TransM; $t = $t.BaseType) { $TransM = $t.GetMethod("Transport", $all) }
function Shape($n) { for ($t = $CCT; $t; $t = $t.BaseType) { $fi = $t.GetField($n, $all); if ($fi) { return $fi.GetValue($null) } } }
$stack = New-Object Windows.Controls.StackPanel; $stack.Margin = "16,14,12,8"
$artB = New-Object Windows.Controls.Border; $artB.Width = 44; $artB.Height = 44; $artB.CornerRadius = 10; $artB.HorizontalAlignment = "Left"
$artB.Background = New-Object Windows.Media.ImageBrush $cover -Property @{ Stretch = "UniformToFill" }
[void]$stack.Children.Add($artB)
$title = $TextM.Invoke($null, [object[]]@("Glass Skies", [double]14, [Windows.FontWeights]::SemiBold, $false)); $title.Margin = "0,6,0,0"; [void]$stack.Children.Add($title)
[void]$stack.Children.Add($TextM.Invoke($null, [object[]]@("OpenDock Sessions", [double]12, [Windows.FontWeights]::Normal, $true)))
$controls = New-Object Windows.Controls.Grid; $controls.Margin = "0,2,0,0"; $controls.Height = 26
for ($i = 0; $i -lt 3; $i++) { [void]$controls.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition)) }
$shapes = @((Shape "PreviousShape"), (Shape "PauseShape"), (Shape "NextShape"))
for ($i = 0; $i -lt 3; $i++) { $b = $TransM.Invoke($null, [object[]]@($shapes[$i], [double]$(if ($i -eq 1) { 17 } else { 13 }), [Action]{ })); [Windows.Controls.Grid]::SetColumn($b, $i); [void]$controls.Children.Add($b) }
[void]$stack.Children.Add($controls)
[void]$body.Children.Add($stack)
$W = $cc.Width; $H = $cc.Height
if ($backdrop) {
  # Place(): right edge 8 DIP in from the screen's, top just under the 30 DIP menu bar; Pad = the shadow margin
  $pad = 30; $left = 2560 - 8 + $pad - $W; $top = 30 + 6 - $pad
  $bb = New-Object Windows.Media.Imaging.BitmapImage; $bb.BeginInit(); $bb.CacheOption = "OnLoad"; $bb.UriSource = [Uri]$backdrop; $bb.EndInit()
  # the area behind the panel; past the screen's right edge it's the screen mirrored there (Glass.MirrorOffscreen)
  $dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
  $dc.DrawImage($bb, [Windows.Rect]::new(-$left, -$top, 2560, 1440))
  $dc.PushTransform((New-Object Windows.Media.ScaleTransform -1, 1, (2560 - $left), 0)); $dc.DrawImage($bb, [Windows.Rect]::new(-$left, -$top, 2560, 1440)); $dc.Pop()
  $dc.PushTransform((New-Object Windows.Media.ScaleTransform 1, -1, 0, (-$top))); $dc.DrawImage($bb, [Windows.Rect]::new(-$left, -$top, 2560, 1440)); $dc.Pop()
  $dc.Close()
  $rtc = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$W), ([int]$H), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $rtc.Render($dv)
  $cv2 = New-Object Windows.Media.Imaging.FormatConvertedBitmap $rtc, ([Windows.Media.PixelFormats]::Bgr32), $null, 0
  $pw = $cv2.PixelWidth; $ph = $cv2.PixelHeight; [byte[]]$px = [byte[]]::new($pw * $ph * 4); $cv2.CopyPixels($px, $pw * 4, 0)
  (T Icons).GetMethod("BoxBlur").Invoke($null, [object[]]@($px, $pw, $ph, 2, 3))          # Glass.Behind's light frost
  $bright = $CCT.BaseType.GetMethod("Bright", $all).Invoke($null, [object[]]@(, $px))
  $pal = $null; for ($t = $CCT; $t -and -not $pal; $t = $t.BaseType) { $pal = $t.GetMethod("Palette", $all) }; [void]$pal.Invoke($cc, [object[]]@([bool]$bright))
  $tilesF = $null; for ($t = $CCT; $t -and -not $tilesF; $t = $t.BaseType) { $tilesF = $t.GetField("tiles", $all) }
  $lightF = $null; for ($t = $CCT; $t -and -not $lightF; $t = $t.BaseType) { $lightF = $t.GetField("light", $all) }
  $tint = $cc.Resources["CcTint"]; $L = T Liquid; $G = T Glass
  foreach ($tl in $tilesF.GetValue($cc)) {
    $r = $tl.At; $s = [object[]]@([double][Math]::Min($r.Width, $r.Height), [double]0, [double]0); [void]$L.GetMethod("Strength").Invoke($null, $s)
    $small = ($r.Width * $r.Height) -lt (140 * 140)
    $lens = $L.GetMethod("Lens").Invoke($null, [object[]]@($px, $pw, $ph, $r, [double]$tl.Radius, [double]$s[1], [double]$s[2], [bool]$small, [double]0, [double]1.5))
    $tl.Glass = $G.GetMethod("Slice").Invoke($null, [object[]]@($lens, [Windows.Rect]::new(0, 0, 1, 1), $tint))
    $tl.Paint([bool]$lightF.GetValue($cc))
  }
  "frosted from the backdrop at $left,$top (bright: $bright)"
}
$c = $cc.Content; $cc.Content = $null
$c.Width = $W; $c.Height = $H
$c.Measure([Windows.Size]::new($W, $H)); $c.Arrange([Windows.Rect]::new(0, 0, $W, $H)); $c.UpdateLayout(); Pump 500; $c.UpdateLayout()
$rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]($W * $scale)), ([int]($H * $scale)), (96 * $scale), (96 * $scale), ([Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($c)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$fs = [IO.File]::Create($out); $enc.Save($fs); $fs.Close(); "ok $W x $H"
