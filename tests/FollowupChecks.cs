using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenDock
{
    static class FollowupChecks
    {
        const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
        static object Get(object target,string name) { return target.GetType().GetField(name,Private).GetValue(target); }
        static void Set(object target,string name,object value) { target.GetType().GetField(name,Private).SetValue(target,value); }
        static void Check(bool condition,string text) { if(!condition) throw new Exception("follow-up: "+text); }
        static void Settle(int milliseconds=250)
        {
            var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick+=delegate { timer.Stop(); frame.Continue=false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {
            if(parent is T) yield return (T)parent;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(parent,i))) yield return child;
        }
        static Slider SliderNamed(Window window,string label)
        {
            return Descendants<Grid>(window).Single(g=>g.Children.OfType<TextBlock>().Any(t=>t.Text==label)&&g.Children.OfType<Slider>().Any()).Children.OfType<Slider>().Single();
        }
        public static void Run()
        {
            var data=Workspace.Data; string workspace=Workspace.StorageDirectory,settings=Settings.StorageDirectory;
            var pins=Settings.Pins; string root=Path.Combine(Path.GetTempPath(),"OpenDock-followup-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                Workspace.StorageDirectory=Settings.StorageDirectory=root; Workspace.Data=new WorkspaceData();
                Settings.Pins=new List<string>{"Saved shared|saved.exe"}; Settings.Save(); var shared=Settings.Pins;
                using(var held=File.Open(Path.Combine(root,"settings.txt"),FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    bool failed=false; try { Workspace.SetPins(null,new List<string>{"Unsaved|new.exe"}); } catch(IOException) { failed=true; }
                    Check(failed&&ReferenceEquals(Settings.Pins,shared),"failed shared pin save changed saved state");
                }
                var layout=new MonitorLayout { Device="fixture",Pins=new List<string>{"Saved local|saved.exe"} }; Workspace.Data.Layouts.Add(layout); Workspace.Save(); var local=layout.Pins;
                using(var held=File.Open(Workspace.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    bool failed=false; try { Workspace.SetPins("fixture",new List<string>{"Unsaved|new.exe"}); } catch(IOException) { failed=true; }
                    Check(failed&&ReferenceEquals(layout.Pins,local),"failed monitor pin save changed saved state");
                    failed=false; try { Workspace.SetShelfShown(false); } catch(IOException) { failed=true; }
                    Check(failed&&Workspace.Data.ShowShelf,"failed Shelf visibility save changed state");
                }
                string exe=Path.Combine(root,"OpenDock.exe"); File.WriteAllText(exe,"current"); File.WriteAllText(exe+".previous","current");
                string older=exe+".backup-working-"+DateTime.UtcNow.AddHours(-1).ToString("yyyyMMdd-HHmmss"); File.WriteAllText(older,"older");
                Check(Updater.Previous(exe)==older,"rollback offered an identical copy of the running build"); File.WriteAllText(older,"current"); Check(Updater.Previous(exe)==null,"rollback enabled with only identical copies");
                string atomic=Path.Combine(root,"unchanged.txt"); Workspace.AtomicWrite(atomic,"first"); Workspace.AtomicWrite(atomic,"second"); Workspace.AtomicWrite(atomic,"second");
                Check(File.ReadAllText(atomic+".previous")=="first","unchanged save replaced the useful previous version");
                using(var held=File.Open(atomic,FileMode.Open,FileAccess.Read,FileShare.Read)) Workspace.AtomicWrite(atomic,"second");
                Console.WriteLine("follow-up: failed pin/visibility saves preserve state and rollback skips identical builds");
            }
            finally { Workspace.Data=data; Workspace.StorageDirectory=workspace; Settings.StorageDirectory=settings; Settings.Pins=pins; Directory.Delete(root,true); }
        }
        public static void Desktop()
        {
            var prior=Workspace.Data; string storage=Workspace.StorageDirectory; var original=DockTheme.Capture("restore checks");
            string root=Path.Combine(Settings.DataDir,"followup-ui"); Directory.CreateDirectory(root); Workspace.StorageDirectory=root; Workspace.Data=new WorkspaceData(); Workspace.Save();
            try
            {
                string collision=Path.Combine(root,"untitled folder"); File.WriteAllText(collision,"preserve this file"); var finder=new FinderWindow(root); finder.Show(); Settle();
                try { typeof(FinderWindow).GetMethod("NewFolder",Private).Invoke(finder,null); Check(Directory.Exists(Path.Combine(root,"untitled folder 2"))&&File.ReadAllText(collision)=="preserve this file","new-folder action failed on a colliding filename"); }
                finally { finder.Close(); }
                int applied=0; Action changed=()=>applied++; Settings.Changed+=changed;
                try
                {
                    var studio=new SettingsWindow("Workspace"); studio.Show(); Settle();
                    try
                    {
                        var slider=SliderNamed(studio,"Icon size preview"); int before=Settings.IconSize; slider.Value=slider.Value<95?slider.Value+1:slider.Value-1; int expected=(int)slider.Value; Settle(300);
                        Check(Settings.IconSize==before&&applied==0&&!((DispatcherTimer)Get(studio,"apply")).IsEnabled,"preview controls refreshed the live dock");
                        var apply=Descendants<Button>(studio).Single(b=>b.Content is TextBlock&&((TextBlock)b.Content).Text=="Apply Preview"); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(Settings.IconSize==expected&&applied>0,"Apply Preview did not commit the draft");
                    }
                    finally { studio.Close(); }
                    var settings=new SettingsWindow("Desktop & Dock"); settings.Show(); Settle(); var size=SliderNamed(settings,"Size"); size.Value=size.Value<95?size.Value+1:size.Value-1; int saved=(int)size.Value;
                    Check(((DispatcherTimer)Get(settings,"apply")).IsEnabled,"fixture did not queue its settings save"); settings.Close();
                    Check(!((DispatcherTimer)Get(settings,"apply")).IsEnabled&&File.ReadAllLines(Path.Combine(Settings.DataDir,"settings.txt")).Contains("IconSize="+saved),"closing settings lost the pending edit");
                }
                finally { Settings.Changed-=changed; original.Apply(); }

                ShelfWindow.Open(); Settle(); var shelf=Application.Current.Windows.OfType<ShelfWindow>().Single(w=>w.IsVisible);
                try
                {
                    var actions=Descendants<Button>(shelf).Where(b=>new[]{"Copy Selected","Open","Remove"}.Contains(b.Content as string)).ToList();
                    Check(actions.Count==3&&actions.All(b=>!b.IsEnabled),"empty Shelf actions could clear the clipboard"); Shelf.Add("text","selection fixture"); ((ListBox)Get(shelf,"list")).SelectedIndex=0;
                    Check(actions.All(b=>b.IsEnabled),"selecting an item did not enable Shelf actions");
                }
                finally { shelf.Close(); }
                var launchpad=new LaunchpadWindow(); var image=new Image { Opacity=0.6 };
                Set(launchpad,"pressAt",new Point(20,20)); Set(launchpad,"swiping",true); Set(launchpad,"pressedImg",image); Set(launchpad,"pressedApp",new AppEntry { Name="fixture" }); launchpad.IsOpen=true;
                Set(launchpad,"W",1000.0); Set(launchpad,"pages",3); Set(launchpad,"page",1); var slide=(TranslateTransform)Get(launchpad,"slide"); slide.X=-900;
                typeof(LaunchpadWindow).GetMethod("ResetGesture",Private).Invoke(launchpad,null); Settle(500);
                Check(slide.X==-1000&&Get(launchpad,"pressAt")==null&&image.Opacity==1,"canceled swipe left Launchpad between pages");
                Set(launchpad,"pressAt",new Point(20,20)); Set(launchpad,"swiping",true); Set(launchpad,"pressedImg",image); image.Opacity=0.6;
                launchpad.Close2(null,false); Check(Get(launchpad,"pressAt")==null&&Get(launchpad,"pressedApp")==null&&!(bool)Get(launchpad,"swiping")&&image.Opacity==1,"Launchpad retained an interrupted gesture"); launchpad.Close(); Settle(300);
                var spotlight=new SpotlightWindow(); spotlight.Show(); spotlight.Close2(false); spotlight.Close(); Settle(200);
                Previews();
                Console.WriteLine("follow-up UI: folder collision, preview/apply isolation, close-time save, Shelf selection, canceled gestures, fade disposal and thumbnail cleanup passed");
            }
            finally { Workspace.Data=prior; Workspace.StorageDirectory=storage; Workspace.Notify(); }
        }
        static Button CloseButton(PreviewWindow preview)
        {
            var row=(StackPanel)Get(preview,"row"); var card=row.Children.OfType<Border>().First(c=>c.Visibility==Visibility.Visible); return ((DockPanel)((StackPanel)card.Child).Children[0]).Children.OfType<Button>().Single();
        }
        static void Previews()
        {
            var windows=Enumerable.Range(0,3).Select(i=>new Window { Title="Preview cleanup fixture "+i,Width=220,Height=150,Content=new TextBlock { Text="Thumbnail fixture" } }).ToArray();
            var preview=new PreviewWindow();
            try
            {
                foreach(var window in windows) window.Show(); Settle(); var handles=windows.Select(w=>new WindowInteropHelper(w).Handle).ToList();
                var area=DisplayLayout.ActiveWorkArea; var anchor=new Point(area.Left+area.Width/2,area.Bottom-80); var item=new DockItem { Kind="app",Name="fixture" };
                preview.ShowFor(item,handles.Take(2).ToList(),Art.Settings,anchor,0,area); Settle();
                Check(((List<IntPtr>)Get(preview,"thumbs")).Count==2,"native thumbnail fixture could not attach");
                CloseButton(preview).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle();
                Check(((List<IntPtr>)Get(preview,"thumbs")).Count==1,"closing a preview leaked or duplicated DWM thumbnails");
                preview.ShowFor(item,handles.Skip(1).ToList(),Art.Settings,anchor,0,area); CloseButton(preview).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); preview.Close2(); Settle();
                Check(((List<IntPtr>)Get(preview,"thumbs")).Count==0&&!preview.IsVisible,"queued preview rebuild survived dismissal");
            }
            finally { preview.Close(); foreach(var window in windows) window.Close(); }
        }
    }
}
