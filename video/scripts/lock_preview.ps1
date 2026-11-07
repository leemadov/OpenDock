# A still of OpenDock's lock screen (LockWindow.Preview) with a sample name and time, on an OpenDock wallpaper.
#   powershell -STA -File lock_preview.ps1 -out video\assets\lock_screen.png
param([string]$out, [int]$W = 2560, [int]$H = 1440, [string]$wall = "drift-light.jpg")
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\checks.exe")
$all = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function T($n) { $asm.GetType("OpenDock.$n") }
$app = New-Object Windows.Application; $app.ShutdownMode = "OnExplicitShutdown"
$bi = New-Object Windows.Media.Imaging.BitmapImage; $bi.BeginInit(); $bi.CacheOption = "OnLoad"; $bi.UriSource = [Uri]"$root\wallpapers\$wall"; $bi.DecodePixelWidth = $W; $bi.EndInit(); $bi.Freeze()
$c = (T LockWindow).GetMethod("Preview").Invoke($null, [object[]]@([double]$W, [double]$H, "Alex", [DateTime]::new(2026, 11, 7, 9, 41, 0), $bi.psobject.BaseObject))
# no account picture in a preview: initials only
$c.Measure([Windows.Size]::new($W, $H)); $c.Arrange([Windows.Rect]::new(0, 0, $W, $H)); $c.UpdateLayout()
$rt = New-Object Windows.Media.Imaging.RenderTargetBitmap $W, $H, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32); $rt.Render($c)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rt))
$fs = [IO.File]::Create($out); $enc.Save($fs); $fs.Close(); "ok"
