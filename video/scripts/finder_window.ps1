# A Finder window for the ad, rendered by OpenDock's own FinderWindow over sample folders (nothing personal),
# as a transparent PNG with Windows 11's rounded corners and window shadow.
#   powershell -STA -File finder_window.ps1 -out video\assets\finder_window.png
#   -tint "#FF375F"   as with Tinted icons in that colour and "One color everywhere" on: folders and accents in it
#   -kind settings   OpenDock's System Settings instead, on Desktop & Dock, scrolled to its Windows and Stage Manager rows
param([string]$out, [int]$W = 1100, [int]$H = 680, [double]$scale = 2, [string]$tint = "", [string]$kind = "finder")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
function Pump($ms) { $f = New-Object Windows.Threading.DispatcherFrame; $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval = [TimeSpan]::FromMilliseconds($ms); $t.Add_Tick({ $t.Stop(); $f.Continue = $false }.GetNewClosure()); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($f) }
$data = Join-Path $env:TEMP "opendock-video-finder"; if (Test-Path $data) { Remove-Item -Recurse -Force $data }; [void][IO.Directory]::CreateDirectory($data)
$S = T Settings
$S.GetField("StorageDirectory", $all).SetValue($null, $data)
(T Workspace).GetField("StorageDirectory", $all).SetValue($null, $data)
$S.GetField("Appearance").SetValue($null, 1)
if ($tint) { $S.GetField("IconStyle").SetValue($null, 1); $S.GetField("IconTint").SetValue($null, $tint); $S.GetField("OneColor").SetValue($null, $true) }
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"; (T Icons).GetMethod("Start").Invoke($null, $null)
(T Theme).GetMethod("Init").Invoke($null, [object[]]@($app.psobject.BaseObject))
if ($kind -eq "finder") {
# sample folders, some with a colour tag and a symbol
# in a neutral place, so the path bar reads "C: > Users > Public > Projects" (removed again at the end)
[string]$sample = "C:\Users\Public\Projects"
if (Test-Path $sample) { throw "$sample already exists; not touching it" }
[void][IO.Directory]::CreateDirectory($sample)
$FL = T FolderLook
$looks = @(@("Design", 4, [string][char]0xE790), @("Music", 0, [string][char]0xE8D6), @("Photos", 3, [string][char]0xEB9F), @("Travel", 1, [string][char]0xE709), @("Games", 5, [string][char]0xE7FC), @("Notes", 2, [string][char]0xE70B), @("Code", -1, [string][char]0xE943), @("Videos", -1, [string][char]0xE714), @("Books", 6, [string][char]0xE82D), @("Work", -1, [string][char]0xE821))
foreach ($l in $looks) {
  [string]$d = [IO.Path]::Combine($sample, $l[0]); [void][IO.Directory]::CreateDirectory($d)
  if ($l[1] -ge 0) { $FL.GetMethod("SetColor").Invoke($null, [object[]]@($d, [int]$l[1])) }
  if ($l[2] -ne "") { $FL.GetMethod("SetSymbol").Invoke($null, [object[]]@($d, [string]$l[2])) }
}
[Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext))
$fw = [Activator]::CreateInstance((T FinderWindow), [object[]]@([string]$sample, $null, $null))
Pump 1500
# nothing personal: the sidebar's home folder shows a sample name, the status line a sample disk
$side = (T FinderWindow).GetField("side", $all).GetValue($fw)
$me = [Environment]::UserName
$entries = @($side.ItemsSource); foreach ($e in $entries) { if ($e.Name -eq $me -or $e.Name -eq (Split-Path -Leaf $env:USERPROFILE)) { $e.Name = "Alex" } }
$side.ItemsSource = $null; $side.ItemsSource = $entries
function Walk($o) { if ($o -is [Windows.Controls.TextBlock] -and $o.Text -match "items?, .* available") { $o.Text = ($o.Text -replace ", .* available", ", 812 GB available") }; if ($o -is [Windows.DependencyObject]) { foreach ($ch in [Windows.LogicalTreeHelper]::GetChildren($o)) { Walk $ch } } }
$c = $fw.Content; $fw.Content = $null
} else {
  [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext))
  $fw = [Activator]::CreateInstance((T SettingsWindow), [object[]]@([string]"Desktop & Dock"))
  $fw.Width = $W; $fw.Height = $H
  Pump 800
  # nothing personal: the account card shows a sample name and initials
  $who = [Security.Principal.WindowsIdentity]::GetCurrent().Name; $who = $who.Substring($who.IndexOf('\') + 1)
  $initials = (($who -split ' ' | Where-Object { $_ } | Select-Object -First 2 | ForEach-Object { $_.Substring(0, 1).ToUpper() }) -join '')
  function Walk($o) {
    if ($o -is [Windows.Controls.TextBlock]) { if ($o.Text -eq $who -or $o.Text -eq [Environment]::UserName) { $o.Text = "Alex" } elseif ($o.Text -eq $initials) { $o.Text = "A" } }
    if ($o -is [Windows.DependencyObject]) { foreach ($ch in [Windows.LogicalTreeHelper]::GetChildren($o)) { Walk $ch } }
  }
  $c = $fw.Content; $fw.Content = $null
  $paneHost = (T SettingsWindow).GetField("paneHost", $all).GetValue($fw)
}
# the window: its background, Windows 11's 8 px rounded corners, and a soft window shadow around it
$M = 60
$frame = New-Object Windows.Controls.Border; $frame.Width = $W; $frame.Height = $H; $frame.Background = $app.Resources["WinBg"]; $frame.CornerRadius = 8; $frame.Child = $c
$frame.Clip = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new(0, 0, $W, $H)), 8, 8
$shadow = New-Object Windows.Controls.Border; $shadow.Width = $W; $shadow.Height = $H; $shadow.CornerRadius = 8; $shadow.Background = [Windows.Media.Brushes]::Black
$shadow.Effect = New-Object Windows.Media.Effects.DropShadowEffect -Property @{ BlurRadius = 48; ShadowDepth = 10; Direction = 270; Opacity = 0.32 }
$canvas = New-Object Windows.Controls.Canvas; $canvas.Width = $W + 2 * $M; $canvas.Height = $H + 2 * $M
foreach ($el in $shadow, $frame) { [Windows.Controls.Canvas]::SetLeft($el, $M); [Windows.Controls.Canvas]::SetTop($el, $M); [void]$canvas.Children.Add($el) }
$hairline = New-Object Windows.Controls.Border; $hairline.Width = $W; $hairline.Height = $H; $hairline.CornerRadius = 8; $hairline.BorderThickness = 1; $hairline.BorderBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(40, 0, 0, 0))
[Windows.Controls.Canvas]::SetLeft($hairline, $M); [Windows.Controls.Canvas]::SetTop($hairline, $M); [void]$canvas.Children.Add($hairline)
$cw = $W + 2 * $M; $ch = $H + 2 * $M
$canvas.Measure([Windows.Size]::new($cw, $ch)); $canvas.Arrange([Windows.Rect]::new(0, 0, $cw, $ch)); $canvas.UpdateLayout(); Pump 800; Walk $c; $canvas.UpdateLayout()
if ($kind -eq "settings") {
  # scroll the pane to its "Windows" heading: drag to tile, margins, Alt+`, then Stage Manager below
  $body = $paneHost.Content
  $head = $null; foreach ($kid in $body.Children) { if ($kid -is [Windows.Controls.TextBlock] -and $kid.Text -eq "Windows") { $head = $kid } }
  if ($head) { $y = $head.TransformToAncestor($body).Transform([Windows.Point]::new(0, 0)).Y; $paneHost.ScrollToVerticalOffset([Math]::Max(0, $y - 12)) }
  $canvas.UpdateLayout(); Pump 300; $canvas.UpdateLayout()
}
# the shadow box must not show through the window: draw it, then the window over it (the frame is opaque)
$rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]($cw * $scale)), ([int]($ch * $scale)), (96 * $scale), (96 * $scale), ([Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($canvas)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$fs = [IO.File]::Create($out); $enc.Save($fs); $fs.Close(); "ok margin $M"
if ($kind -eq "finder") {
  $fw.GetType().GetMethod("DisposePane", $all).Invoke($fw, $null) 2>$null
  foreach ($l in $looks) { [IO.Directory]::Delete([IO.Path]::Combine($sample, $l[0])) }   # only the empty sample folders made above
  [IO.Directory]::Delete($sample)
}
