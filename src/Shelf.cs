using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    static class Shelf
    {
        public static void Add(string kind,string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if(kind!="file"&&kind!="text") throw new ArgumentException("Unsupported Shelf item type.");
            if(value.Length>16000) throw new ArgumentException("Shelf text entries support up to 16,000 characters.");
            if (kind=="file") { value=Path.GetFullPath(value); if (!File.Exists(value)&&!Directory.Exists(value)) return; }
            if (Workspace.Data.Shelf.Any(x=>x.Kind==kind&&string.Equals(x.Value,value,kind=="file"?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))) return;
            Change(items=>
            {
                items.Add(new ShelfEntry { Kind=kind, Value=value });
                while(items.Count>200||items.Sum(e=>e.Value.Length)>100000) items.RemoveAt(0);
            });
        }
        static void Change(Action<System.Collections.Generic.List<ShelfEntry>> edit)
        {
            var previous=Workspace.Data.Shelf; var next=previous.ToList(); edit(next); Workspace.Data.Shelf=next;
            try { Workspace.Save(); } catch { Workspace.Data.Shelf=previous; throw; }
        }
        public static void Remove(ShelfEntry[] entries) { if(entries.Length>0) Change(items=>items.RemoveAll(entries.Contains)); }
        public static DataObject Payload(ShelfEntry[] entries)
        {
            var data=new DataObject(); var paths=new StringCollection();
            paths.AddRange(entries.Where(e=>e.Kind=="file"&&(File.Exists(e.Value)||Directory.Exists(e.Value))).Select(e=>e.Value).ToArray());
            if(paths.Count>0) data.SetFileDropList(paths);
            string text=string.Join(Environment.NewLine,entries.Where(e=>e.Kind!="file").Select(e=>e.Value));
            if(text.Length>0) data.SetText(text);
            return data;
        }
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
        public static bool Deliver(IntPtr window,string[] files)
        {
            if(!Native.IsWindow(window)) return false;
            Native.Activate(window);
            if((Native.GetWindowLong(window,Native.GWL_EXSTYLE)&0x10)==0) return false;
            var text=System.Text.Encoding.Unicode.GetBytes(string.Join("\0",files)+"\0\0");
            var memory=GlobalAlloc(0x42,new UIntPtr((uint)(20+text.Length))); if(memory==IntPtr.Zero) return false;
            var pointer=GlobalLock(memory); if(pointer==IntPtr.Zero) { GlobalFree(memory); return false; }
            Marshal.WriteInt32(pointer,0,20); Marshal.WriteInt32(pointer,16,1); Marshal.Copy(text,0,IntPtr.Add(pointer,20),text.Length); GlobalUnlock(memory);
            if(Native.PostMessage(window,0x233,memory,IntPtr.Zero)) return true;
            GlobalFree(memory); return false;
        }
    }
    class ShelfWindow : MacWindow
    {
        static ShelfWindow open;
        readonly ListBox list=new ListBox { SelectionMode=SelectionMode.Extended,DisplayMemberPath="Label",Margin=new Thickness(16) };
        public static void Open()
        {
            if(open==null||!open.IsLoaded) open=new ShelfWindow();
            if(open.WindowState==WindowState.Minimized) open.WindowState=WindowState.Normal;
            open.Show(); open.Activate();
        }
        public static void AddClipboard()
        {
            try
            {
                if(Clipboard.ContainsFileDropList()) foreach(string path in Clipboard.GetFileDropList()) Shelf.Add("file",path);
                else if(Clipboard.ContainsImage())
                {
                    string dir=Path.Combine(Workspace.DirectoryPath,"Shelf Images"); Directory.CreateDirectory(dir);
                    string path=Path.Combine(dir,Guid.NewGuid().ToString("N")+".png");
                    var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Clipboard.GetImage())); using(var file=File.Create(path)) png.Save(file);
                    Shelf.Add("file",path);
                }
                else if(Clipboard.ContainsText()) Shelf.Add("text",Clipboard.GetText());
                Open();
            }
            catch(Exception ex) { MessageBox.Show(ex.Message,"Shelf"); }
        }
        ShelfWindow()
        {
            Title="Shelf"; Icon=Art.Shelf; Width=560; Height=460; MinWidth=420; MinHeight=300;
            var root=new DockPanel(); var head=new DockPanel { Height=52 }; head.Children.Add(TrafficLights());
            var title=new TextBlock { Text="Shelf",Margin=new Thickness(110,0,0,0),VerticalAlignment=VerticalAlignment.Center,FontSize=16 }; title.SetResourceReference(ForegroundProperty,"Fg"); head.Children.Add(title);
            DockPanel.SetDock(head,Dock.Top); root.Children.Add(head);
            var buttons=new WrapPanel { Margin=new Thickness(12,4,12,4) };
            Func<string,Action,Button> add=(name,action)=> { var b=new Button { Content=name,Margin=new Thickness(3),Padding=new Thickness(10,5,10,5) }; b.SetResourceReference(FrameworkElement.StyleProperty,"Flat"); b.SetResourceReference(Control.ForegroundProperty,"Fg"); b.Click+=delegate { try { action(); } catch(Exception ex) { MessageBox.Show(ex.Message,"Shelf"); } }; buttons.Children.Add(b); return b; };
            add("Add Clipboard",AddClipboard);
            var copy=add("Copy Selected",()=> { var payload=Shelf.Payload(list.SelectedItems.Cast<ShelfEntry>().ToArray()); if(payload.GetFormats().Length>0) Clipboard.SetDataObject(payload,true); });
            var openSelected=add("Open",()=> { foreach(var e in list.SelectedItems.Cast<ShelfEntry>()) { if(e.Kind=="file") Launcher.Open(e.Value); else { Uri uri; if(Uri.TryCreate(e.Value,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="http")) Launcher.Shell(uri.AbsoluteUri); } } });
            var remove=add("Remove",()=>Shelf.Remove(list.SelectedItems.Cast<ShelfEntry>().ToArray()));
            Action selection=()=> { foreach(var button in new[]{copy,openSelected,remove}) button.IsEnabled=list.SelectedItems.Count>0; };
            list.SelectionChanged+=delegate { selection(); }; selection();
            DockPanel.SetDock(buttons,Dock.Top); root.Children.Add(buttons);
            var tip=new TextBlock { Text="Drop files or text here. Drag selected items into another app. Removing an item keeps its original file.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(16,4,16,8) }; tip.SetResourceReference(ForegroundProperty,"FgDim"); DockPanel.SetDock(tip,Dock.Bottom); root.Children.Add(tip);
            list.SetResourceReference(BackgroundProperty,"Field"); list.SetResourceReference(ForegroundProperty,"Fg"); root.Children.Add(list); Layout.Children.Add(root);
            AllowDrop=true; Drop+=(s,e)=> { try { if(e.Data.GetDataPresent(DataFormats.FileDrop)) foreach(string path in (string[])e.Data.GetData(DataFormats.FileDrop)) Shelf.Add("file",path); else if(e.Data.GetDataPresent(DataFormats.UnicodeText)) Shelf.Add("text",(string)e.Data.GetData(DataFormats.UnicodeText)); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Shelf"); } e.Handled=true; };
            Point? press=null;
            list.PreviewMouseLeftButtonDown+=(s,e)=>press=e.GetPosition(list);
            list.PreviewMouseMove+=(s,e)=> { if(press==null||e.LeftButton!=MouseButtonState.Pressed||(e.GetPosition(list)-press.Value).Length<8) return; press=null; var selected=list.SelectedItems.Cast<ShelfEntry>().ToArray(); if(selected.Length>0) DragDrop.DoDragDrop(list,Shelf.Payload(selected),DragDropEffects.Copy); };
            Workspace.Changed+=Refresh; Closed+=delegate { Workspace.Changed-=Refresh; }; Refresh();
        }
        void Refresh() { var selected=list.SelectedItems.Cast<ShelfEntry>().ToList(); list.ItemsSource=Workspace.Data.Shelf.ToList(); foreach(var item in selected) if(Workspace.Data.Shelf.Contains(item)) list.SelectedItems.Add(item); }
    }
    static class Living
    {
        static System.Windows.Threading.DispatcherTimer clock;
        static bool scanning;
        static DateTime scanAfter;
        public static void Start()
        {
            if(clock!=null) return;
            clock=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(1) }; clock.Tick+=delegate { Tick(); }; clock.Start();
        }
        static DateTime? end; static TimeSpan paused;
        public static int Downloads;
        public static string TimerText { get { var t=end.HasValue ? end.Value-DateTime.UtcNow : paused; return t>TimeSpan.Zero ? ((int)t.TotalMinutes)+":"+t.Seconds.ToString("00") : ""; } }
        public static void StartTimer(int minutes) { paused=TimeSpan.Zero; end=DateTime.UtcNow.AddMinutes(Math.Max(1,Math.Min(1440,minutes))); Workspace.Notify(); }
        public static void PauseTimer() { if(end.HasValue) { paused=end.Value-DateTime.UtcNow; end=null; } else if(paused>TimeSpan.Zero) { end=DateTime.UtcNow+paused; paused=TimeSpan.Zero; } Workspace.Notify(); }
        public static void Tick()
        {
            if(end.HasValue&&end.Value<=DateTime.UtcNow) { end=null; paused=TimeSpan.Zero; System.Media.SystemSounds.Asterisk.Play(); Workspace.Status="Timer finished."; }
            if(!scanning&&DateTime.UtcNow>=scanAfter) ScanDownloads();
            foreach(var dock in App.Docks) dock.RefreshBadges();
        }
        static async void ScanDownloads()
        {
            scanning=true; string folder=Launcher.Downloads;
            Downloads=await System.Threading.Tasks.Task.Run(()=>
            {
                try { return Directory.EnumerateFiles(folder).Count(f=>new[]{".crdownload",".part",".download",".partial"}.Contains(Path.GetExtension(f).ToLowerInvariant())); }
                catch { return 0; }
            });
            scanAfter=DateTime.UtcNow.AddSeconds(5); scanning=false;
        }
    }
}
