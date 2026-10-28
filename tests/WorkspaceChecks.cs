using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;
using System.Windows.Interop;

namespace OpenDock
{
    static class WorkspaceChecks
    {
        const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
        public static void Run()
        {
            var before=Workspace.Data; string prior=Workspace.StorageDirectory;
            string dir=Path.Combine(Path.GetTempPath(),"OpenDock-workspace-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                Workspace.StorageDirectory=dir; Workspace.Data=new WorkspaceData();
                string file=Path.Combine(dir,"preserve.txt"); File.WriteAllText(file,"original contents");
                Shelf.Add("file",file); Shelf.Add("file",file); Shelf.Add("text","collected note");
                if(Workspace.Data.Shelf.Count!=2) throw new Exception("Shelf duplicate collection");
                var payload=Shelf.Payload(Workspace.Data.Shelf.ToArray());
                if(payload.GetFileDropList().Count!=1||payload.GetText()!="collected note") throw new Exception("Shelf drag/copy payload");
                Workspace.Data.Layouts.Add(new MonitorLayout { Device="fixture",Role="Creative tools",Pins=new System.Collections.Generic.List<string>{"Editor|fixture.exe"} });
                Workspace.Data.Themes.Add(DockTheme.Capture("Test"));
                Workspace.Data.Scenes.Add(new Scene { Name="Fixture" }); Workspace.Data.ShowShelf=false; Workspace.Save(); Workspace.Load();
                if(Workspace.Data.Scenes.Single().Name!="Fixture"||Workspace.Pins("fixture").Single()!="Editor|fixture.exe"||Workspace.Data.Themes.Single().Name!="Test") throw new Exception("Workspace persistence round trip");
                if(Workspace.Data.ShowShelf||Workspace.Data.Shelf.Count!=2) throw new Exception("Shelf visibility preference or collected items did not persist");
                var bounds=Workspace.Bounds(new SceneWindow { X=9,Y=-2,Width=10,Height=2 },new Native.RECT { Left=-1920,Top=-1080,Right=0,Bottom=0 });
                if(bounds.Left< -1920||bounds.Right>0||bounds.Top< -1080||bounds.Bottom>0) throw new Exception("Scene bounds escaped disconnected-monitor fallback");
                if(!Workspace.Commands(">tile window").Any()||!Workspace.Commands("Scene:").Any()) throw new Exception("Desktop command matching");
                Workspace.Data.Shelf.Clear(); Workspace.Save(); if(File.ReadAllText(file)!="original contents") throw new Exception("Removing Shelf data touched original file");
                if(!File.Exists(Workspace.FilePath+".previous")) throw new Exception("Workspace atomic backup missing");
                bool rejected=false;
                try { Workspace.Validate(Workspace.Json.Deserialize<WorkspaceData>("{\"Scenes\":[{\"Name\":\"Broken\",\"Windows\":null}]}")); } catch(InvalidDataException) { rejected=true; }
                if(!rejected) throw new Exception("Malformed nested workspace data was accepted");
                Console.WriteLine("workspace: Shelf payload/preservation, atomic storage, monitor pins, themes, scene bounds and command matching passed");
            }
            finally { Workspace.Data=before; Workspace.StorageDirectory=prior; Directory.Delete(dir,true); }
        }
        static void Settle()
        {
            var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(300) }; timer.Tick+=delegate { timer.Stop(); frame.Continue=false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        static void Capture(Window window,string name)
        {
            window.UpdateLayout(); var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qa",name+".png"))) encoder.Save(file);
        }
        public static void Desktop(string fixture)
        {
            var data=Workspace.Data; string storage=Workspace.StorageDirectory;
            string isolated=Path.Combine(Path.GetTempPath(),"OpenDock-workspace-ui-"+Guid.NewGuid().ToString("N"));
            Workspace.Data=new WorkspaceData(); Workspace.StorageDirectory=isolated;
            var finder=new FinderWindow(fixture); finder.Show(); Settle();
            try
            {
                typeof(FinderWindow).GetMethod("NewTab",Private).Invoke(finder,new object[]{Path.Combine(fixture,"Documents")}); Settle();
                var paths=finder.OpenPaths().ToList(); if(paths.Count!=2) throw new Exception("Finder tabs lost folder state");
                typeof(FinderWindow).GetMethod("ToggleSplit",Private).Invoke(finder,null); Settle();
                var split=(FinderWindow)typeof(FinderWindow).GetField("splitPane",Private).GetValue(finder);
                if(split==null) throw new Exception("Finder split view didn't create its independent pane");
                Capture(finder,"finder-tabs-split");
                typeof(FinderWindow).GetMethod("ToggleSplit",Private).Invoke(finder,null); Settle();
                if(typeof(FinderWindow).GetField("splitPane",Private).GetValue(finder)!=null) throw new Exception("Finder split view didn't close");
                var panel=new SettingsWindow("Workspace"); panel.Show(); Settle(); Capture(panel,"workspace-studio"); panel.Close();
                ShelfWindow.Open(); Settle();
                var shelf=Application.Current.Windows.OfType<ShelfWindow>().Single(w=>w.IsVisible); Capture(shelf,"shelf");
                var dock=App.Docks.First();
                Func<DockWindow,System.Collections.Generic.List<DockItem>> items=d=>(System.Collections.Generic.List<DockItem>)typeof(DockWindow).GetField("items",Private).GetValue(d);
                Shelf.Add("text","Keep this collected note");
                var tile=items(dock).Single(i=>i.Kind=="shelf");
                typeof(DockWindow).GetMethod("ShowMenu",Private).Invoke(dock,new object[]{tile});
                var menu=(ContextMenu)typeof(DockWindow).GetField("openMenu",Private).GetValue(dock);
                menu.Items.OfType<MenuItem>().Single(i=>(string)i.Header=="Remove from Dock").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Settle();
                var currentWindows=Windows.Enumerate(); foreach(var display in App.Docks) display.Sync(currentWindows);
                if(App.Docks.Any(d=>items(d).Any(i=>i.Kind=="shelf"||i.RunKey=="opendock:shelf"))) throw new Exception("Hidden Shelf returned while its window was open");
                Workspace.Load(); if(Workspace.Data.ShowShelf||Workspace.Data.Shelf.Single().Value!="Keep this collected note") throw new Exception("Removing Shelf did not preserve its saved visibility/data");
                var restarted=new DockWindow(dock.Device);
                try { restarted.Sync(currentWindows); if(items(restarted).Any(i=>i.Kind=="shelf"||i.RunKey=="opendock:shelf")) throw new Exception("Shelf returned on a new dock"); }
                finally { restarted.Close(); }
                Workspace.Data.ShowShelf=true; Workspace.Save();
                if(App.Docks.Any(d=>items(d).Count(i=>i.Kind=="shelf")!=1||items(d)[2].Kind!="shelf")) throw new Exception("Restoring Shelf created a duplicate or wrong position");
                tile=items(dock).Single(i=>i.Kind=="shelf");
                typeof(DockWindow).GetMethod("BeginDrag",Private).Invoke(dock,new object[]{tile});
                double top=(double)typeof(DockWindow).GetField("bgTop",Private).GetValue(dock),slot=(double)typeof(DockWindow).GetField("slot",Private).GetValue(dock);
                var outside=new Point(tile.CX,top-slot*2);
                typeof(DockWindow).GetMethod("DragMove",Private).Invoke(dock,new object[]{outside});
                typeof(DockWindow).GetMethod("EndDrag",Private).Invoke(dock,new object[]{outside}); Settle();
                if(Workspace.Data.ShowShelf||App.Docks.Any(d=>items(d).Any(i=>i.Kind=="shelf"))) throw new Exception("Dragging Shelf off did not hide it on all monitors");
                Workspace.Data.ShowShelf=true; Workspace.Save(); shelf.Close();
                Console.WriteLine("Shelf: context-menu removal, drag-off removal, restart persistence, all-monitor restore and collected-data preservation passed");
                using(var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--focus-fixture") { UseShellExecute=false,CreateNoWindow=true }))
                {
                    WinInfo live=null; int tries=0;
                    while(live==null&&!child.HasExited&&tries++<15) { Settle(); live=Windows.Enumerate().FirstOrDefault(w=>w.Pid==(uint)child.Id); }
                    if(live==null) throw new Exception("Scene fixture did not open");
                    var scene=Workspace.Capture("Scene placement",new[]{live}); scene.Theme=null; scene.Folders.Clear(); scene.Windows[0].Target="unavailable-fixture.exe";
                    Native.RECT original; Native.GetWindowRect(live.Hwnd,out original);
                    Native.SetWindowPos(live.Hwnd,IntPtr.Zero,original.Left+120,original.Top+100,280,140,4|Native.SWP_NOACTIVATE); Settle();
                    var restore=Workspace.Restore(scene); while(!restore.IsCompleted) Settle();
                    Native.RECT restored; Native.GetWindowRect(live.Hwnd,out restored);
                    if(Math.Abs(restored.Left-original.Left)>4||Math.Abs(restored.Top-original.Top)>4||Math.Abs((restored.Right-restored.Left)-(original.Right-original.Left))>4) throw new Exception("Scene did not restore the saved window geometry");
                    Workspace.CommandWindow=live.Hwnd; Workspace.Commands(">tile window left").Single().Open(); Settle();
                    Native.RECT tiled; Native.GetWindowRect(live.Hwnd,out tiled);
                    var area=Sys.Screens().First(s=>s.Monitor==Native.MonitorFromWindow(live.Hwnd,2)).Work;
                    if(Math.Abs((tiled.Right-tiled.Left)-(area.Right-area.Left)/2)>3) throw new Exception("Desktop tile command did not resize the target window");
                    while(!child.HasExited) Settle();
                }
                Console.WriteLine("workspace UI: independent Finder tabs/split panes, Dock Studio and Shelf opened successfully");
                Console.WriteLine("workspace actions: saved external-window geometry restored and desktop tile command verified");
            }
            finally { finder.Close(); Workspace.Data=data; Workspace.StorageDirectory=storage; Workspace.Notify(); if(Directory.Exists(isolated)) Directory.Delete(isolated,true); }
        }
    }
}
