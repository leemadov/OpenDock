using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenDock
{
    partial class SettingsWindow
    {
        void WorkspacePane(StackPanel body)
        {
            body.Children.Add(Heading("Desktop Scenes"));
            var scenes=Group(body); var name=new TextBox { Text="My desktop",Width=180,Margin=new Thickness(4),Padding=new Thickness(6) };
            Row(scenes,name);
            Row(scenes,Pill("Save Current Desktop",()=>
            {
                try
                {
                    var scene=Workspace.Capture(name.Text,Windows.Enumerate());
                    var prior=Workspace.Data.Scenes.FirstOrDefault(s=>string.Equals(s.Name,scene.Name,StringComparison.OrdinalIgnoreCase));
                    if(prior!=null&&MessageBox.Show(this,"Replace the saved scene “"+scene.Name+"”?","Desktop Scenes",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
                    Workspace.Data.Scenes.RemoveAll(s=>string.Equals(s.Name,scene.Name,StringComparison.OrdinalIgnoreCase)); Workspace.Data.Scenes.Add(scene); Workspace.Save(); Show("Workspace",false);
                }
                catch(Exception ex) { MessageBox.Show(this,ex.Message,"Desktop Scenes"); }
            }));
            foreach(var value in Workspace.Data.Scenes.ToList())
            {
                var scene=value; var row=new WrapPanel(); row.Children.Add(Text(scene.Name+" · "+scene.Windows.Count+" windows",13,"Fg"));
                row.Children.Add(Pill("Restore",async()=> { await Workspace.Restore(scene); if(IsLoaded) MessageBox.Show(this,Workspace.Status,"Desktop Scenes"); }));
                row.Children.Add(Pill("Remove",()=> { Workspace.Data.Scenes.Remove(scene); Workspace.Save(); Show("Workspace",false); })); Row(scenes,row);
            }
            Note(body,"Scenes reopen available apps and reposition matching windows. Minimized apps are not captured; apps may require you to reopen additional windows. App documents, browser sessions and unsaved content are controlled by each app.");
            if(Workspace.Status.Length>0) Note(body,Workspace.Status);
            body.Children.Add(Heading("Shelf & Living Icons")); var shelf=Group(body);
            AddSwitch(shelf,"Show Shelf in Dock",Workspace.Data.ShowShelf,v=> { Workspace.Data.ShowShelf=v; Workspace.Save(); });
            Row(shelf,Pill("Open Shelf",ShelfWindow.Open)); Row(shelf,Pill("Collect Clipboard",ShelfWindow.AddClipboard));
            AddSwitch(shelf,"Live window counts, download activity and timer badges",Workspace.Data.LivingIcons,v=> { Workspace.Data.LivingIcons=v; Workspace.Save(); });
            var timers=new WrapPanel(); timers.Children.Add(Pill("5 minute timer",()=>Living.StartTimer(5))); timers.Children.Add(Pill("25 minute timer",()=>Living.StartTimer(25))); timers.Children.Add(Pill("Pause / Resume",Living.PauseTimer)); Row(shelf,timers);
            Note(body,"App badges show window counts, Downloads shows incomplete download files, and Launchpad shows your countdown. These are local status indicators, not unread-message counts.");
            body.Children.Add(Heading("Monitor Roles"));
            foreach(var screen in Sys.Screens())
            {
                var device=screen.Device; var group=Group(body); var current=Workspace.Data.Layouts.FirstOrDefault(l=>l.Device==device);
                InfoRow(group,device,current==null?"Shared pins":current.Role+" · independent pins");
                var role=new ComboBox { Width=190,ItemsSource=new[]{"Everyday","Communication","Creative tools","Media","Gaming"},SelectedIndex=0,Margin=new Thickness(4) }; Row(group,role);
                if(current!=null) role.SelectedItem=current.Role;
                Row(group,Pill("Use Independent Pins",()=>
                {
                    if(current==null) Workspace.Data.Layouts.Add(new MonitorLayout { Device=device,Role=(string)role.SelectedItem,Pins=Settings.Pins.ToList() });
                    else current.Role=(string)role.SelectedItem;
                    Workspace.Save(); foreach(var dock in App.Docks) dock.RefreshPins(); Show("Workspace",false);
                }));
                if(current!=null) Row(group,Pill("Use Shared Pins",()=> { Workspace.Data.Layouts.Remove(current); Workspace.Save(); foreach(var dock in App.Docks) dock.RefreshPins(); Show("Workspace",false); }));
            }
            Note(body,"Independent monitors keep their own pins when you pin, remove or reorder apps on that monitor. Other monitor layouts and shared pins remain intact.");
            body.Children.Add(Heading("Dock Studio")); Studio(body);
            body.Children.Add(Heading("Software Updates")); Row(Group(body),Pill("Open Software Update",()=>Show("Updates",true)));
        }
        void Studio(StackPanel body)
        {
            var draft=DockTheme.Capture("My theme"); var group=Group(body);
            var preview=new Border { CornerRadius=new CornerRadius(16),Padding=new Thickness(10),Margin=new Thickness(4,10,4,10),HorizontalAlignment=HorizontalAlignment.Center };
            var icons=new StackPanel { Orientation=Orientation.Horizontal }; preview.Child=icons;
            Action draw=()=>
            {
                preview.Background=Theme.B(draft.Appearance==1?"#BBE2E7EE":"#CC282C35"); icons.Children.Clear();
                foreach(var image in new ImageSource[]{Art.Finder,Art.Launchpad,Art.Settings,Art.Downloads,Art.Computer}) icons.Children.Add(new Image { Source=image,Width=draft.Size,Height=draft.Size,Margin=new Thickness(draft.Spacing/2.0,0,draft.Spacing/2.0,0) });
            };
            Row(group,preview); draw();
            AddSlider(group,"Icon size preview",36,96,draft.Size,v=> { draft.Size=(int)v; draw(); });
            AddSlider(group,"Spacing preview",0,16,draft.Spacing,v=> { draft.Spacing=(int)v; draw(); });
            var appearance=new ComboBox { ItemsSource=new[]{"Follow Windows","Light","Dark"},SelectedIndex=draft.Appearance,Width=180 };
            appearance.SelectionChanged+=delegate { draft.Appearance=appearance.SelectedIndex; draw(); }; Row(group,appearance);
            var position=new ComboBox { ItemsSource=new[]{"Bottom","Left","Right"},SelectedIndex=draft.Position,Width=180 }; position.SelectionChanged+=delegate { draft.Position=position.SelectedIndex; }; Row(group,position);
            AddSwitch(group,"Magnification",draft.Magnify,v=>draft.Magnify=v); AddSwitch(group,"Auto-hide",draft.AutoHide,v=>draft.AutoHide=v);
            AddSwitch(group,"Uniform icon tiles",draft.Tiles,v=>draft.Tiles=v);
            AddSwitch(group,"Original live glass (excluded from recordings)",draft.LiveGlass,v=>draft.LiveGlass=v);
            var title=new TextBox { Text="My theme",Width=190,Margin=new Thickness(4),Padding=new Thickness(6) }; Row(group,title);
            Row(group,Pill("Apply Preview",draft.Apply));
            Row(group,Pill("Save Theme",()=> { if(string.IsNullOrWhiteSpace(title.Text)) return; draft.Name=title.Text.Trim(); Workspace.Data.Themes.RemoveAll(t=>t.Name==draft.Name); Workspace.Data.Themes.Add(Workspace.Json.Deserialize<DockTheme>(Workspace.Json.Serialize(draft))); Workspace.Save(); Show("Workspace",false); }));
            foreach(var value in Workspace.Data.Themes.ToList())
            {
                var theme=value; var row=new WrapPanel(); row.Children.Add(Pill("Apply "+theme.Name,theme.Apply)); row.Children.Add(Pill("Remove",()=> { Workspace.Data.Themes.Remove(theme); Workspace.Save(); Show("Workspace",false); })); Row(group,row);
            }
            var choices=new List<KeyValuePair<string,string>> { new KeyValuePair<string,string>("Finder","fixed:finder"),new KeyValuePair<string,string>("Launchpad","fixed:launchpad"),new KeyValuePair<string,string>("Downloads","fixed:stack") };
            choices.AddRange(Settings.Pins.Where(p=>p.Contains("|")).Select(p=>new KeyValuePair<string,string>(p.Substring(0,p.IndexOf('|')),p.Substring(p.IndexOf('|')+1))));
            var apps=new ComboBox { ItemsSource=choices,DisplayMemberPath="Key",SelectedIndex=0,Width=200 }; Row(group,apps);
            Row(group,Pill("Choose Icon…",()=>
            {
                var pick=new Microsoft.Win32.OpenFileDialog { Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.ico" };
                if(pick.ShowDialog(this)==true) try { CustomIcons.Set(((KeyValuePair<string,string>)apps.SelectedItem).Value,pick.FileName); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Dock Icon"); }
            }));
            Row(group,Pill("Reset Icon",()=>CustomIcons.Reset(((KeyValuePair<string,string>)apps.SelectedItem).Value)));
            Note(body,"Preview changes stay in this panel until you choose Apply Preview. Themes save dock layout, size, spacing, material mode and icon-tile style; custom artwork is managed separately.");
        }
    }
}
