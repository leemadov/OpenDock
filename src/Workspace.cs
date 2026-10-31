using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;

namespace OpenDock
{
    class WorkspaceData
    {
        public List<Scene> Scenes = new List<Scene>();
        public List<ShelfEntry> Shelf = new List<ShelfEntry>();
        public List<MonitorLayout> Layouts = new List<MonitorLayout>();
        public List<DockTheme> Themes = new List<DockTheme>();
        public bool LivingIcons = true;
        public bool ShowShelf = true;
    }
    class Scene
    {
        public string Name;
        public DockTheme Theme;
        public List<string> Pins = new List<string>();
        public List<SceneWindow> Windows = new List<SceneWindow>();
        public List<string> Folders = new List<string>();
        public List<MonitorLayout> Layouts = new List<MonitorLayout>();
    }
    class SceneWindow
    {
        public string Key, Target, Title, Monitor;
        public double X, Y, Width, Height;
        public bool Maximized;
    }
    class MonitorLayout
    {
        public string Device, Role;
        public List<string> Pins = new List<string>();
    }
    class ShelfEntry
    {
        public string Kind, Value;
        public string Label { get { if(Kind=="file") { string name=Path.GetFileName(Value.TrimEnd('\\')); return string.IsNullOrEmpty(name)?Value:name; } return Value.Length>100?Value.Substring(0,100)+"…":Value; } }
    }
    class DockTheme
    {
        public string Name;
        public int Size, Magnified, Spacing, Position, Appearance;
        public bool Magnify, AutoHide, Tiles, LiveGlass;
        public static DockTheme Capture(string name)
        {
            return new DockTheme { Name = name, Size = Settings.IconSize, Magnified = Settings.MagnifySize, Spacing = Settings.DockSpacing, Position = Settings.DockPosition,
                Appearance = Settings.Appearance, Magnify = Settings.Magnify, AutoHide = Settings.AutoHide, Tiles = Settings.IconTiles, LiveGlass = Settings.LiveGlass };
        }
        public void Apply()
        {
            Settings.IconSize = Math.Max(36, Math.Min(96, Size)); Settings.MagnifySize = Math.Max(48, Math.Min(160, Magnified));
            Settings.DockSpacing = Math.Max(0, Math.Min(16, Spacing)); Settings.DockPosition = Math.Max(0, Math.Min(2, Position));
            Settings.Appearance = Math.Max(0, Math.Min(2, Appearance)); Settings.Magnify = Magnify; Settings.AutoHide = AutoHide;
            Settings.IconTiles = Tiles; Settings.LiveGlass = LiveGlass; Theme.Refresh(); Settings.Apply();
        }
    }
    static class Workspace
    {
        public static WorkspaceData Data = new WorkspaceData();
        internal static string StorageDirectory=Settings.DataDir;
        public static string DirectoryPath { get { return StorageDirectory; } }
        public static string FilePath { get { return Path.Combine(DirectoryPath, "workspace.json"); } }
        public static event Action Changed;
        public static bool Restoring;
        public static IntPtr CommandWindow;
        public static string Status = "";
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        static string unreadableFile, preserveBeforeSave;
        static WorkspaceData Read(string path)
        {
            if(new FileInfo(path).Length>2*1024*1024) throw new InvalidDataException("Workspace file is too large.");
            var data=Json.Deserialize<WorkspaceData>(File.ReadAllText(path)); Validate(data); return data;
        }
        public static void Load()
        {
            unreadableFile=preserveBeforeSave=null;
            if (!File.Exists(FilePath)) { Data=new WorkspaceData(); return; }
            try { Data=Read(FilePath); }
            catch (Exception ex)
            {
                App.Log(ex);
                try { Data=Read(FilePath+".previous"); preserveBeforeSave=FilePath; Status="Workspace recovered from its previous copy. The unreadable original will be archived before saving."; }
                catch { Data=new WorkspaceData(); unreadableFile=FilePath; Status="Workspace data is unreadable. Restore a valid backup before saving; existing files have been retained."; }
            }
        }
        internal static void Validate(WorkspaceData data)
        {
            Func<List<MonitorLayout>,bool> layouts=list=>list!=null&&list.All(l=>l!=null&&!string.IsNullOrEmpty(l.Device)&&l.Pins!=null&&l.Pins.All(p=>p!=null));
            if(data==null||data.Scenes==null||data.Shelf==null||data.Themes==null||!layouts(data.Layouts)||
                data.Themes.Any(t=>t==null||string.IsNullOrWhiteSpace(t.Name))||
                data.Shelf.Any(e=>e==null||(e.Kind!="file"&&e.Kind!="text")||string.IsNullOrEmpty(e.Value))||
                data.Scenes.Any(s=>s==null||string.IsNullOrWhiteSpace(s.Name)||s.Pins==null||s.Pins.Any(p=>p==null)||!layouts(s.Layouts)||s.Folders==null||s.Folders.Any(f=>f==null)||s.Windows==null||
                    s.Windows.Any(w=>w==null||string.IsNullOrEmpty(w.Target)||string.IsNullOrEmpty(w.Key)||new[]{w.X,w.Y,w.Width,w.Height}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)))))
                throw new InvalidDataException("Invalid workspace data.");
            foreach(var entry in data.Shelf.Where(e=>e.Kind=="file")) Path.GetFullPath(entry.Value);
        }
        public static void Save()
        {
            if(unreadableFile==FilePath) throw new InvalidDataException(Status);
            Validate(Data);
            Directory.CreateDirectory(DirectoryPath);
            string json=Json.Serialize(Data);
            if(System.Text.Encoding.UTF8.GetByteCount(json)>2*1024*1024) throw new InvalidDataException("Workspace storage is full. Remove unused scenes or Shelf items before saving.");
            if(preserveBeforeSave==FilePath&&File.Exists(FilePath)) File.Copy(FilePath,FilePath+".unreadable-"+Guid.NewGuid().ToString("N"));
            AtomicWrite(FilePath, json); preserveBeforeSave=null; Notify();
        }
        public static void AtomicWrite(string path, string text)
        {
            if(File.Exists(path)&&File.ReadAllText(path)==text) return;
            string stage = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(stage, text, new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(stage, path, path + ".previous"); else File.Move(stage, path);
            }
            finally { try { if(File.Exists(stage)) File.Delete(stage); } catch { } }
        }
        public static void Notify()
        {
            if(Changed!=null) foreach(Action changed in Changed.GetInvocationList()) try { changed(); } catch(Exception ex) { App.Log(ex); }
            foreach(var dock in App.Docks.ToList()) try { dock.RefreshShelf(); dock.Sync(App.Wins); } catch(Exception ex) { App.Log(ex); }
        }
        public static List<string> Pins(string device)
        {
            var layout = Data.Layouts.FirstOrDefault(l => l.Device == device);
            return layout == null ? Settings.Pins : layout.Pins;
        }
        public static void SetPins(string device, List<string> pins)
        {
            var layout = Data.Layouts.FirstOrDefault(l => l.Device == device);
            var previous=layout==null?Settings.Pins:layout.Pins;
            try { if (layout == null) { Settings.Pins = pins; Settings.Save(); } else { layout.Pins = pins; Save(); } }
            catch { if(layout==null) Settings.Pins=previous; else layout.Pins=previous; throw; }
        }
        public static void SetShelfShown(bool value)
        {
            bool previous=Data.ShowShelf; Data.ShowShelf=value;
            try { Save(); } catch { Data.ShowShelf=previous; throw; }
        }
        public static Scene Capture(string name, IEnumerable<WinInfo> windows)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Enter a scene name of 1–80 characters.");
            var scene = new Scene { Name = name.Trim(),Theme=DockTheme.Capture(name.Trim()),Pins=Settings.Pins.ToList() };
            var screens = Sys.Screens();
            foreach (var w in windows.Where(w => w.InternalKind == null && w.Key != null && !Native.IsIconic(w.Hwnd)))
            {
                Native.RECT r; if (!Native.GetWindowRect(w.Hwnd, out r)) continue;
                var screen = screens.FirstOrDefault(s => s.Monitor == Native.MonitorFromWindow(w.Hwnd, 2)) ?? screens.First(); var b = screen.Work;
                string target = w.Aumid != null && (w.IsFrameHost || (w.Exe ?? "").IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) ? "app:" + w.Aumid : w.Exe;
                if (string.IsNullOrEmpty(target)) continue;
                scene.Windows.Add(new SceneWindow { Key = target.StartsWith("app:")?target:w.Key, Target = target, Title = w.Title, Monitor = screen.Device, Maximized = Native.IsZoomed(w.Hwnd),
                    X = (r.Left-b.Left)/(double)(b.Right-b.Left), Y = (r.Top-b.Top)/(double)(b.Bottom-b.Top), Width = (r.Right-r.Left)/(double)(b.Right-b.Left), Height = (r.Bottom-r.Top)/(double)(b.Bottom-b.Top) });
            }
            scene.Folders = App.Finders.SelectMany(f => f.OpenPaths()).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            scene.Layouts = Json.Deserialize<List<MonitorLayout>>(Json.Serialize(Data.Layouts));
            return scene;
        }
        public static Native.RECT Bounds(SceneWindow window, Native.RECT work)
        {
            double width = work.Right-work.Left, height = work.Bottom-work.Top;
            int w = (int)Math.Max(Math.Min(200, width), Math.Min(width, width * window.Width));
            int h = (int)Math.Max(Math.Min(120, height), Math.Min(height, height * window.Height));
            int x = (int)Math.Max(work.Left, Math.Min(work.Right-w, work.Left+width*window.X));
            int y = (int)Math.Max(work.Top, Math.Min(work.Bottom-h, work.Top+height*window.Y));
            return new Native.RECT { Left=x, Top=y, Right=x+w, Bottom=y+h };
        }
        public static async Task Restore(Scene scene)
        {
            if (Restoring) return; Restoring = true; Status = "Restoring " + scene.Name + "…"; Notify();
            try
            {
                Data.Layouts = Json.Deserialize<List<MonitorLayout>>(Json.Serialize(scene.Layouts));
                if(scene.Theme!=null) { Settings.Pins=scene.Pins.ToList(); scene.Theme.Apply(); }
                foreach(var dock in App.Docks) dock.RefreshPins();
                await Task.Delay(250);
                var launched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var used = new HashSet<IntPtr>();
                var remaining = scene.Windows.ToList(); int restored = 0;
                for (int attempt=0; attempt<24 && remaining.Count>0; attempt++)
                {
                    var live = Windows.Enumerate();
                    foreach (var saved in remaining.ToList())
                    {
                        var window = live.Where(w => !used.Contains(w.Hwnd) && (string.Equals(w.Key,saved.Key,StringComparison.OrdinalIgnoreCase)||saved.Target.StartsWith("app:")&&string.Equals(w.Aumid,saved.Target.Substring(4),StringComparison.OrdinalIgnoreCase))).OrderByDescending(w => w.Title == saved.Title).FirstOrDefault();
                        if (window == null)
                        {
                            if (launched.Add(saved.Target) && (saved.Target.StartsWith("app:") || File.Exists(saved.Target))) Launcher.Open(saved.Target);
                            continue;
                        }
                        var screens = Sys.Screens(); var screen = screens.FirstOrDefault(s => s.Device == saved.Monitor) ?? screens.FirstOrDefault(s => s.Primary) ?? screens.First();
                        var r = Bounds(saved, screen.Work); Native.ShowWindow(window.Hwnd, Native.SW_RESTORE);
                        if(!Native.SetWindowPos(window.Hwnd, IntPtr.Zero, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, 4 | Native.SWP_NOACTIVATE | 0x4000 /* ASYNCWINDOWPOS */)) continue;
                        if (saved.Maximized) Native.ShowWindow(window.Hwnd, Native.SW_MAXIMIZE);
                        used.Add(window.Hwnd); remaining.Remove(saved); restored++;
                    }
                    if (remaining.Count>0) await Task.Delay(400);
                }
                Save(); foreach(var dock in App.Docks) dock.RefreshPins();
                foreach(var folder in scene.Folders.Where(Directory.Exists)) if (!App.Finders.Any(f=>f.OpenPaths().Contains(folder))) App.NewFinder(folder);
                Status = "Restored " + restored + " of " + scene.Windows.Count + " windows" + (remaining.Count>0 ? "; some apps were unavailable or need another window opened." : ".");
            }
            catch(Exception ex) { Status = "Scene restore: " + ex.Message; App.Log(ex); }
            finally { Restoring=false; Notify(); }
        }
        public static void MoveWindow(IntPtr window, bool next, bool leftHalf)
        {
            if (window == IntPtr.Zero || !Native.IsWindow(window)) return;
            var screens=Sys.Screens(); if(screens.Count==0) return;
            int current=screens.FindIndex(s=>s.Monitor==Native.MonitorFromWindow(window,2));
            var b=screens[next ? (Math.Max(0,current)+1)%screens.Count : Math.Max(0,current)].Work;
            int width=next ? b.Right-b.Left : (b.Right-b.Left)/2;
            Native.ShowWindow(window,Native.SW_RESTORE);
            Native.SetWindowPos(window,IntPtr.Zero,next || leftHalf ? b.Left : b.Left+width,b.Top,width,b.Bottom-b.Top,4|Native.SWP_NOACTIVATE|0x4000);
        }
        public static IEnumerable<Entry> Commands(string query)
        {
            Func<IntPtr> target=()=>Native.IsWindow(CommandWindow)?CommandWindow:App.LastForeground;
            var commands = new List<Entry> {
                new Entry { Name="Open Shelf", Sub="Desktop command", Open=ShelfWindow.Open },
                new Entry { Name="Desktop Scenes", Sub="Desktop command", Open=()=>App.OpenSettings("Workspace") },
                new Entry { Name="Move window to next monitor", Sub="Desktop command", Open=()=>MoveWindow(target(),true,false) },
                new Entry { Name="Tile window left", Sub="Desktop command", Open=()=>MoveWindow(target(),false,true) },
                new Entry { Name="Tile window right", Sub="Desktop command", Open=()=>MoveWindow(target(),false,false) },
                new Entry { Name="Dock Studio", Sub="Desktop command", Open=()=>App.OpenSettings("Workspace") },
                new Entry { Name="Copy clipboard to Shelf", Sub="Desktop command", Open=ShelfWindow.AddClipboard },
                new Entry { Name="Start 5 minute timer", Sub="Desktop command", Open=()=>Living.StartTimer(5) }
            };
            commands.AddRange(Data.Scenes.Select(s=> { var scene=s; return new Entry { Name="Scene: "+s.Name, Sub="Restore desktop scene", Open=async ()=>await Restore(scene) }; }));
            string q=query.TrimStart('>',' ');
            return commands.Where(c=>c.Name.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0).Take(8);
        }
    }
}
