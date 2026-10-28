using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace OpenDock
{
    partial class FinderWindow
    {
        FinderWindow host, activePane, splitPane;
        readonly List<FinderWindow> tabs=new List<FinderWindow>();
        readonly StackPanel tabStrip=new StackPanel { Orientation=Orientation.Horizontal };
        readonly Grid panes=new Grid();
        bool workspaceClosing;
        protected override void CloseFromChrome() { if(host!=null) host.ClosePane(this); else Close(); }
        protected override void MinimizeFromChrome() { (host??this).WindowState=WindowState.Minimized; }
        protected override void ZoomFromChrome() { var owner=host??this; owner.WindowState=owner.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized; }
        public IEnumerable<string> OpenPaths()
        {
            var owner=host??this;
            return owner.tabs.Concat(owner.splitPane==null?new FinderWindow[0]:new[]{owner.splitPane}).Select(f=>f.path).Where(p=>p!=null&&p!=AppsLocation).Distinct().ToList();
        }
        void InitWorkspace()
        {
            tabs.Add(this); activePane=this;
            var root=new DockPanel();
            var bar=new DockPanel { Height=32,Margin=new Thickness(8,0,8,0) };
            Action<string,Action> button=(label,action)=> { var b=new Button { Content=label,Margin=new Thickness(2,1,2,1),Padding=new Thickness(8,1,8,1) }; b.SetResourceReference(FrameworkElement.StyleProperty,"Flat"); b.Click+=delegate { action(); }; WindowChrome.SetIsHitTestVisibleInChrome(b,true); DockPanel.SetDock(b,Dock.Right); bar.Children.Add(b); };
            button("Split",ToggleSplit); button("+",()=>NewTab((activePane??this).path));
            var scroll=new ScrollViewer { Content=tabStrip,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled }; WindowChrome.SetIsHitTestVisibleInChrome(scroll,true); bar.Children.Add(scroll);
            DockPanel.SetDock(bar,Dock.Top); root.Children.Add(bar); root.Children.Add(panes);
            Content=null; Content=root;
            PreviewKeyDown+=(s,e)=>
            {
                if(e.Key==Key.T&&Keyboard.Modifiers==ModifierKeys.Control) { NewTab((activePane??this).path); e.Handled=true; }
                else if(e.Key==Key.W&&Keyboard.Modifiers==ModifierKeys.Control) { ClosePane(activePane??this); e.Handled=true; }
                else if(e.Key==Key.S&&Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)) { ToggleSplit(); e.Handled=true; }
            };
            Closed+=delegate { workspaceClosing=true; foreach(var child in tabs.Concat(splitPane==null?new FinderWindow[0]:new[]{splitPane}).Where(f=>f!=this).ToList()) child.Close(); };
            RefreshTabs();
        }
        void NewTab(string folder)
        {
            var pane=new FinderWindow(folder,null,this); pane.Content=null;
            tabs.Add(pane); activePane=pane; RefreshTabs();
        }
        void ToggleSplit()
        {
            if(splitPane!=null) { var old=splitPane; splitPane=null; if(activePane==old) activePane=tabs.Last(); old.Close(); }
            else
            {
                splitPane=new FinderWindow((activePane??this).path,null,this); splitPane.Content=null;
                var area=DisplayLayout.ActiveWorkArea; Width=Math.Min(area.Width,Math.Max(1280,Width)); Left=Math.Max(area.Left,Math.Min(area.Right-Width,Left));
            }
            RefreshTabs();
        }
        void ClosePane(FinderWindow pane)
        {
            if(pane==splitPane) { ToggleSplit(); return; }
            if(tabs.Count==1) { Close(); return; }
            tabs.Remove(pane); activePane=tabs.Last();
            // The original Window owns the workspace; retain its backing pane until the workspace closes.
            if(pane!=this) pane.Close();
            RefreshTabs();
        }
        void RefreshTabs()
        {
            var owner=host??this;
            if(owner!=this) { owner.RefreshTabs(); return; }
            if(workspaceClosing||tabs.Count==0) return;
            if(activePane==null||(!tabs.Contains(activePane)&&activePane!=splitPane)) activePane=tabs[0];
            tabStrip.Children.Clear();
            foreach(var p in tabs)
            {
                var pane=p; var row=new StackPanel { Orientation=Orientation.Horizontal };
                var b=new Button { Content=p.title.Text.Length>0?p.title.Text:"Folder",Padding=new Thickness(10,1,10,1),FontWeight=activePane==p?FontWeights.Bold:FontWeights.Normal };
                b.SetResourceReference(FrameworkElement.StyleProperty,"Flat"); b.SetResourceReference(Control.ForegroundProperty,"Fg");
                b.Click+=delegate { activePane=pane; RefreshTabs(); }; WindowChrome.SetIsHitTestVisibleInChrome(b,true); row.Children.Add(b);
                var close=new Button { Content="×",Padding=new Thickness(4,1,4,1),ToolTip="Close tab" }; close.SetResourceReference(FrameworkElement.StyleProperty,"Flat"); close.SetResourceReference(Control.ForegroundProperty,"Fg"); close.Click+=delegate { ClosePane(pane); }; WindowChrome.SetIsHitTestVisibleInChrome(close,true); row.Children.Add(close); tabStrip.Children.Add(row);
            }
            panes.Children.Clear(); panes.ColumnDefinitions.Clear();
            var primary=activePane==splitPane?tabs.Last():activePane;
            panes.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(primary.Layout,0); panes.Children.Add(primary.Layout);
            if(splitPane!=null)
            {
                panes.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(5) }); panes.ColumnDefinitions.Add(new ColumnDefinition());
                var divider=new GridSplitter { Width=5,HorizontalAlignment=HorizontalAlignment.Stretch }; Grid.SetColumn(divider,1); panes.Children.Add(divider);
                Grid.SetColumn(splitPane.Layout,2); panes.Children.Add(splitPane.Layout);
            }
            Title=primary.title.Text;
        }
        void PaneFocused()
        {
            if(host!=null) host.activePane=this;
            else if(tabs.Count>0) activePane=this;
        }
    }
}
