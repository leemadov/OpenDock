using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    static class AuditChecks
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static object Get(object value,string name) { return value.GetType().GetField(name,Private).GetValue(value); }
        static object Call(object value,string name,params object[] args) { return value.GetType().GetMethod(name,Private).Invoke(value,args); }
        static void Check(bool condition,string message) { if(!condition) throw new Exception("audit: "+message); }
        public static void Run()
        {
            string directory=Path.Combine(Path.GetTempPath(),"OpenDock-audit-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var prior=Workspace.Data; string storage=Workspace.StorageDirectory,status=Workspace.Status,settings=Settings.StorageDirectory;
            var pins=Settings.Pins.ToList(); var hidden=Settings.Hidden.ToList(); var walls=Settings.Wallpapers.ToList();
            try
            {
                Workspace.StorageDirectory=directory; Workspace.Data=new WorkspaceData(); Settings.StorageDirectory=directory;
                Settings.Pins=new List<string>{"Fixture|file.exe"}; Settings.Hidden=new List<string>{"fixture"}; Settings.Wallpapers=new List<string>{"fixture.jpg"};
                Settings.Save(); Settings.Load(); Settings.Load();
                Check(Settings.Pins.Count==1&&Settings.Hidden.Count==1&&Settings.Wallpapers.Count==1,"loading preferences duplicated collection entries");
                string photosA=Path.Combine(directory,"photos-a"),photosB=Path.Combine(directory,"photos-b"); Directory.CreateDirectory(photosA); Directory.CreateDirectory(photosB);
                string photoA=Path.Combine(photosA,"same.jpg"),photoB=Path.Combine(photosB,"same.jpg"); File.WriteAllText(photoA,"aaaa"); File.WriteAllText(photoB,"bbbb");
                string copyA=Sys.AddToLibrary(photoA),copyB=Sys.AddToLibrary(photoB);
                Check(copyA!=copyB&&File.ReadAllText(copyA)=="aaaa"&&File.ReadAllText(copyB)=="bbbb"&&Sys.AddToLibrary(photoB)==copyB,"same-name/same-size wallpapers were confused or duplicated");
                string personal=Settings.WallpaperDir+"-personal"; Directory.CreateDirectory(personal); string personalPhoto=Path.Combine(personal,"original.jpg"); File.WriteAllText(personalPhoto,"keep original");
                Settings.Wallpapers.Add(personalPhoto); Sys.RemoveFromLibrary(personalPhoto);
                Check(File.Exists(personalPhoto)&&!Sys.Within(personalPhoto,Settings.WallpaperDir),"wallpaper removal deleted a personal file in a similarly named directory");
                string original=Path.Combine(directory,"original.txt"); File.WriteAllText(original,"preserve");
                Shelf.Add("file",original); Shelf.Add("file",original.ToUpperInvariant()); Check(Workspace.Data.Shelf.Count==1,"Shelf duplicated a differently cased Windows path");
                Check(!string.IsNullOrEmpty(new ShelfEntry { Kind="file",Value=Path.GetPathRoot(directory) }.Label),"Shelf drive-root entry had no display label");
                string saved=File.ReadAllText(Workspace.FilePath); var before=Workspace.Data.Shelf;
                using(var held=File.Open(Workspace.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    bool failed=false; try { Shelf.Add("text","unsaved"); } catch(IOException) { failed=true; }
                    Check(failed&&ReferenceEquals(before,Workspace.Data.Shelf),"failed Shelf add changed in-memory collection");
                    failed=false; try { Shelf.Remove(before.ToArray()); } catch(IOException) { failed=true; }
                    Check(failed&&ReferenceEquals(before,Workspace.Data.Shelf),"failed Shelf removal changed in-memory collection");
                }
                Check(File.ReadAllText(Workspace.FilePath)==saved&&Directory.GetFiles(directory,"*.tmp-*").Length==0,"failed atomic save damaged data or leaked staging files");
                Shelf.Add("text","second"); File.WriteAllText(Workspace.FilePath,"{ unreadable"); Workspace.Load();
                Check(Workspace.Data.Shelf.Count==1,"valid previous workspace was not recovered"); Workspace.Save();
                Check(Directory.GetFiles(directory,"workspace.json.unreadable-*").Any(p=>File.ReadAllText(p)=="{ unreadable"),"unreadable original was not retained");
                bool notified=false; Action broken=()=> { throw new Exception("Expected audit notification failure"); }; Action healthy=()=>notified=true;
                Workspace.Changed+=broken; Workspace.Changed+=healthy;
                try { Shelf.Add("text","notification fixture"); Check(notified,"one failed observer blocked other observers"); }
                finally { Workspace.Changed-=broken; Workspace.Changed-=healthy; }
                File.WriteAllText(Workspace.FilePath,"broken current"); File.WriteAllText(Workspace.FilePath+".previous","broken previous"); Workspace.Load();
                bool refused=false; try { Shelf.Add("text","must not overwrite"); } catch(InvalidDataException) { refused=true; }
                Check(refused&&File.ReadAllText(Workspace.FilePath)=="broken current"&&Workspace.Data.Shelf.Count==0,"unreadable workspace was silently overwritten");

                var both=DragDropEffects.Copy|DragDropEffects.Move;
                Check(FinderWindow.TransferEffect(new[]{@"C:\source\file.txt"},@"C:\target",DragDropKeyStates.None,DragDropEffects.Copy)==DragDropEffects.Copy,"copy-only drag source could be moved");
                Check(FinderWindow.TransferEffect(new[]{@"C:\source\file.txt"},@"D:\target",DragDropKeyStates.None,both)==DragDropEffects.Copy,"cross-drive default was not copy");
                Check(FinderWindow.TransferEffect(new[]{@"C:\source\file.txt"},@"D:\target",DragDropKeyStates.ShiftKey,both)==DragDropEffects.Move,"Shift did not request move");
                Check(FinderWindow.TransferEffect(new[]{@"C:\source\file.txt"},@"C:\target",DragDropKeyStates.ControlKey,DragDropEffects.Move)==DragDropEffects.None,"unsupported modifier was ignored");
                string parent=Path.Combine(directory,"folder"), child=Path.Combine(parent,"child"); Directory.CreateDirectory(child);
                bool rejected=false; try { FinderWindow.TransferDestination(parent,child); } catch(IOException) { rejected=true; }
                Check(rejected,"recursive folder transfer allowed");
                string target=Path.Combine(directory,"OpenDock.exe");
                var now=DateTime.UtcNow;
                string older=target+".backup-old-"+now.AddHours(-2).ToString("yyyyMMdd-HHmmss"),newer=target+".backup-new-"+now.AddHours(-1).ToString("yyyyMMdd-HHmmss");
                File.WriteAllText(older,"old installation"); File.WriteAllText(newer,"new installation"); File.SetLastWriteTimeUtc(older,now.AddDays(1)); File.SetLastWriteTimeUtc(newer,now.AddDays(-20));
                Check(Updater.Previous(target)==newer,"rollback selected compiler timestamp rather than latest installation");
                File.WriteAllText(target+".previous","native update backup"); File.SetLastWriteTimeUtc(target+".previous",now);
                Check(Updater.Previous(target)==target+".previous","latest native update backup ignored");
                Check(Updater.CompareVersions("v1.0.0-preview.1000000000000000000000000","v1.0.0-preview.999999999999999999999999")>0,"large numeric preview identifiers were sorted as text");
                foreach(string tag in new[]{"v01.0.0","v1.0.0-preview.01","v1.0.0+build..invalid"})
                {
                    rejected=false; try { Updater.CompareVersions(tag,"v1.0.0"); } catch(ArgumentException) { rejected=true; } Check(rejected,"invalid release version accepted");
                }
                Workspace.StorageDirectory=Path.Combine(directory,"scene"); Workspace.Data=new WorkspaceData(); Workspace.Load();
                var restoring=Workspace.Restore(new Scene { Name="Concurrent pins" });
                Workspace.Data.Layouts.Add(new MonitorLayout { Device="changed-while-restoring",Role="Fixture" }); restoring.GetAwaiter().GetResult();
                Check(Workspace.Data.Layouts.Any(d=>d.Device=="changed-while-restoring"),"scene completion discarded a monitor layout changed during restore");
                var watch=Stopwatch.StartNew(); string output=Sys.Run(Assembly.GetExecutingAssembly().Location,"--hang-fixture",150);
                Check(output==""&&watch.ElapsedMilliseconds<4000,"settings helper timeout did not bound stdout reads");
                Check(Sys.Run(Assembly.GetExecutingAssembly().Location,"--output-fixture").Length==65536,"settings helper lost piped output");
                Check(Sys.Run(Assembly.GetExecutingAssembly().Location,"--echo-fixture "+Launcher.Quote(new[]{@"C:\",@"C:\folder with spaces\"}))==@"C:\|C:\folder with spaces\","quoted trailing directory separators were parsed incorrectly");
                Console.WriteLine("audit: safe drag effects, recursive-transfer rejection, transactional Shelf saves, corrupt-workspace recovery, atomic cleanup, preference reload, wallpaper identity/deletion boundaries, rollback chronology, version sorting and helper timeouts passed");
            }
            finally { Workspace.Data=prior; Workspace.StorageDirectory=storage; Workspace.Status=status; Settings.StorageDirectory=settings; Settings.Pins=pins; Settings.Hidden=hidden; Settings.Wallpapers=walls; Directory.Delete(directory,true); }
        }
        static void Settle(int milliseconds=250)
        {
            var frame=new DispatcherFrame(); var clock=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) }; clock.Tick+=delegate { clock.Stop(); frame.Continue=false; }; clock.Start(); Dispatcher.PushFrame(frame);
        }
        static void SavePng(BitmapSource image,string path)
        {
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(path)) encoder.Save(file);
        }
        public static void Desktop(string fixture)
        {
            fixture=Path.Combine(fixture,"audit-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(fixture,"Documents"));
            var finder=new FinderWindow(fixture); finder.Show(); Settle();
            string source=Path.Combine(fixture,"Audit source.txt"),folder=Path.Combine(fixture,"Audit destination"); Directory.CreateDirectory(folder); File.WriteAllText(source,"original");
            try
            {
                Settle(800);
                var files=(ListView)Get(finder,"files");
                var address=(TextBox)Get(finder,"address"); address.Text="."; Call(finder,"NavigateAddress"); Check(address.Text==fixture,"same-folder navigation did not restore the complete address");
                var payload=new DataObject(DataFormats.FileDrop,new[]{source});
                var drop=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{payload,DragDropKeyStates.None,DragDropEffects.Copy,finder,new Point(0,0)},null);
                drop.RoutedEvent=UIElement.DropEvent;
                Call(finder,"DropInto",new[]{source},folder,drop);
                Check(File.Exists(source)&&File.ReadAllText(Path.Combine(folder,Path.GetFileName(source)))=="original"&&drop.Effects==DragDropEffects.Copy,"copy-only drop moved or damaged its original");
                Check((string)Get(finder,"path")==fixture,"drop changed navigation destination");
                Call(finder,"Go",folder,true); Check(files.Items.Count==0&&files.SelectedItems.Count==0,"old directory remained actionable while loading the next one"); Settle();
                Call(finder,"Go",fixture,true); Settle();
                var selected=files.Items.Cast<Entry>().Single(e=>e.Path==source); files.SelectedItem=selected;
                File.AppendAllText(source,new string('x',2048)); Settle(1100);
                var updated=files.Items.Cast<Entry>().Single(e=>e.Path==source);
                Check(updated.SizeText!=selected.SizeText&&files.SelectedItems.Cast<Entry>().Any(e=>e.Path==source),"file metadata refresh was stale or discarded selection");
                string vanished=Path.Combine(fixture,"vanished"); Directory.CreateDirectory(vanished); Call(finder,"Go",vanished,true); Call(finder,"Go",fixture,true); Directory.Delete(vanished); Call(finder,"Back");
                Check((string)Get(finder,"path")!=vanished&&Directory.Exists((string)Get(finder,"path")),"history navigated into a removed folder"); Settle();
                Call(finder,"NewTab",Path.Combine(fixture,"Documents")); Call(finder,"NewTab",folder); Settle();
                var tabs=(StackPanel)Get(finder,"tabStrip"); ((Button)((StackPanel)tabs.Children[0]).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                IntPtr originalMonitor=Native.MonitorFromWindow(new WindowInteropHelper(finder).Handle,2); var previousDock=App.Dock; App.Dock=App.Docks.Last();
                Call(finder,"ToggleSplit"); Check(Native.MonitorFromWindow(new WindowInteropHelper(finder).Handle,2)==originalMonitor,"split view moved Finder to the last-used dock's monitor"); App.Dock=previousDock;
                var split=(FinderWindow)Get(finder,"splitPane"); Call(split,"PaneFocused"); Call(finder,"RefreshTabs");
                var panes=(Grid)Get(finder,"panes"); Check(ReferenceEquals(panes.Children[0],Get(finder,"Layout")),"right-pane focus changed the selected left tab");
                finder.WindowState=WindowState.Maximized; Settle();
                Check(((FrameworkElement)finder.Content).Margin==new Thickness(7)&&((FrameworkElement)Get(finder,"Layout")).Margin==new Thickness(0),"maximize padding was applied to one pane rather than the workspace"); finder.WindowState=WindowState.Normal; Settle();
                var allTabs=(List<FinderWindow>)Get(finder,"tabs"); var background=allTabs[1]; Call(finder,"ClosePane",background);
                Check(ReferenceEquals(Get(finder,"activePane"),split)&&ReferenceEquals(panes.Children[0],Get(finder,"Layout")),"closing a background tab changed the active pane");
                Call(finder,"ClosePane",finder); Check(Get(finder,"watcher")==null&&!(bool)((DispatcherTimer)Get(finder,"reloadSoon")).IsEnabled,"closed original tab retained its watcher");
                Call(split,"CloseFromChrome"); Check(!finder.IsVisible,"embedded pane close button did not close the containing window");
                var fallback=new FinderWindow(Path.Combine(fixture,"not-there"));
                try { Check((string)Get(fallback,"path")==Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"missing start folder opened a blank Finder"); }
                finally { fallback.Close(); }
                var active=new FinderWindow(fixture); active.Show(); Settle();
                try { Call(App.MenuBar,"OnForeground",new WindowInteropHelper(active).Handle); Check(MenuBarWindow.TargetHandle()==new WindowInteropHelper(active).Handle,"menu commands targeted the previous external app instead of Finder"); }
                finally { active.Close(); }

                string oldIcon=Path.Combine(fixture,"Audit old icon.png"),newIcon=Path.Combine(fixture,"Audit new icon.png"); SavePng(Art.Finder,oldIcon); SavePng(Art.Settings,newIcon); Icons.ClearCache();
                bool notified=false; Icons.Get(oldIcon,false,image=> { throw new Exception("Expected audit icon subscriber failure"); }); Icons.Get(oldIcon,false,image=>notified=true);
                int attempts=0; while(!notified&&attempts++<30) Settle(); Check(notified,"one failed icon callback blocked the others");
                ImageSource cached=null; Icons.Get(newIcon,true,image=>cached=image); attempts=0; while(cached==null&&attempts++<30) Settle(); Check(cached!=null,"fixture icon not loaded");
                using(var gate=new ManualResetEventSlim(false))
                {
                    Icons.Run(()=>gate.Wait(4000));
                    var item=new DockItem { IconPath=oldIcon,Img=new Image() }; var dock=App.Docks.First(); Call(dock,"LoadIcon",item); item.IconPath=newIcon; Call(dock,"LoadIcon",item); gate.Set(); Settle(500);
                    Check(ReferenceEquals(item.Img.Source,cached),"old asynchronous icon request overwrote the latest artwork");
                }
                Console.WriteLine("audit UI: copy-only file drop, navigation clearing, live metadata/selection, split focus, background-tab close, watcher disposal, close-button ownership, menu targeting and asynchronous icon delivery passed");
            }
            finally { finder.Close(); Directory.Delete(fixture,true); }
        }
        public static void DesktopOnly()
        {
            var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            using(var graphics=System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) App.Dpi=graphics.DpiX/96.0;
            Settings.HideTaskbar=false; Settings.FirstRun=false; Settings.AutoHide=true; Settings.LiveGlass=false;
            Theme.Init(app); Glass.Init(); Icons.Start(); Wallpaper.Load();
            string fixture=Path.Combine(Settings.DataDir,"audit-fixture"); Directory.CreateDirectory(Path.Combine(fixture,"Documents"));
            Exception failure=null;
            Action finish=()=> { foreach(var dock in App.Docks.ToList()) dock.Close(); App.Docks.Clear(); if(App.MenuBar!=null) App.MenuBar.Close(); app.Shutdown(); };
            app.DispatcherUnhandledException+=(s,e)=> { failure=e.Exception; e.Handled=true; finish(); };
            app.Startup+=delegate
            {
                App.MenuBar=new MenuBarWindow(); App.MenuBar.Show(); App.ReconcileDocks();
                app.Dispatcher.BeginInvoke(new Action(()=> { try { Desktop(fixture); } catch(Exception ex) { failure=ex; } finally { finish(); } }));
            };
            app.Run(); if(failure!=null) throw new Exception("Audit UI failed",failure);
        }
    }
}
