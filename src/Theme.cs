using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace OpenDock
{
    // macOS-like light/dark palette that follows the Windows app theme, plus control templates.
    static class Theme
    {
        public static bool Dark;
        public static event Action Changed;
        public static readonly FontFamily Font = new FontFamily("SF Pro Text, Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static void Init(Application app)
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
            app.Resources["UiFont"] = Font;
            Apply();
            SystemEvents.UserPreferenceChanged += delegate
            {
                if (IsDarkMode() == Dark) return;
                Apply();
                if (Changed != null) Changed();
            };
        }

        static bool IsDarkMode()
        {
            if (Settings.Appearance != 0) return Settings.Appearance == 2;
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return v is int && (int)v == 0;
        }

        public static void Refresh()
        {
            if (IsDarkMode() == Dark) return;
            Apply();
            if (Changed != null) Changed();
        }

        public static Brush B(string argb)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
            b.Freeze();
            return b;
        }

        public static Brush Get(string key) { return (Brush)Application.Current.Resources[key]; }

        static void Apply()
        {
            Dark = IsDarkMode();
            var r = Application.Current.Resources;
            string[] keys = { "MenuBg", "MenuBorder", "MenuFg", "MenuFgDim", "MenuSep", "Accent", "WinBg", "SideBg", "ToolBg", "Fg", "FgDim", "Hover", "SelBg", "Field", "Thumb", "Track", "DockTint", "DockBorder", "Bubble", "BubbleFg", "Glass" };
            string[] dark = { "#F72B2B2D", "#33FFFFFF", "#F2FFFFFF", "#73FFFFFF", "#1FFFFFFF", "#0A84FF", "#1E1E1E", "#2A2A2C", "#262628", "#EBFFFFFF", "#8CFFFFFF", "#14FFFFFF", "#29FFFFFF", "#1AFFFFFF", "#59FFFFFF", "#45FFFFFF", "#7A141418", "#38FFFFFF", "#E62A2A2C", "#F2FFFFFF", "#C72A2A2D" };
            string[] light = { "#F5F6F6F6", "#1F000000", "#E6000000", "#59000000", "#1A000000", "#007AFF", "#FFFFFF", "#ECECEE", "#F6F6F6", "#E6000000", "#80000000", "#0F000000", "#1A000000", "#0F000000", "#59000000", "#24000000", "#99F5F5F7", "#73FFFFFF", "#F2F4F4F4", "#E6000000", "#CCF4F4F6" };
            for (int i = 0; i < keys.Length; i++) r[keys[i]] = B(Dark ? dark[i] : light[i]);
        }

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <DropShadowEffect x:Key='MenuShadow' BlurRadius='22' ShadowDepth='6' Direction='270' Opacity='0.35'/>

  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='FontFamily' Value='{DynamicResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Margin='12,6,12,18' Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuBorder}' BorderThickness='1' CornerRadius='10' Padding='5' MinWidth='210' Effect='{StaticResource MenuShadow}'>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'><Border Height='1' Margin='10,5' Background='{DynamicResource MenuSep}'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource MenuFg}'/>
    <Setter Property='FontFamily' Value='{DynamicResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' CornerRadius='5' Padding='6,3,8,4' Background='Transparent' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='Auto' MinWidth='18'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='12'/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name='Check' Text='&#x2713;' Visibility='Hidden' FontSize='12' VerticalAlignment='Center'/>
              <ContentPresenter ContentSource='Icon' Margin='0,0,8,0' VerticalAlignment='Center'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
              <TextBlock x:Name='Gesture' Grid.Column='2' Text='{TemplateBinding InputGestureText}' Margin='28,0,0,0' Foreground='{DynamicResource MenuFgDim}' VerticalAlignment='Center'/>
              <Path x:Name='Arrow' Grid.Column='3' Visibility='Collapsed' Data='M0,0 L4,4 L0,8' Stroke='{TemplateBinding Foreground}' StrokeThickness='1.5' HorizontalAlignment='Right' VerticalAlignment='Center'/>
              <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='-6' VerticalOffset='-12' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'
                     IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
                <Border Margin='12,6,12,18' Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuBorder}' BorderThickness='1' CornerRadius='10' Padding='5' MinWidth='190' Effect='{StaticResource MenuShadow}'>
                  <StackPanel IsItemsHost='True'/>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='Role' Value='SubmenuHeader'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource Accent}'/>
              <Setter Property='Foreground' Value='White'/>
              <Setter TargetName='Gesture' Property='Foreground' Value='#D9FFFFFF'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource MenuFgDim}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Top-level item in the menu bar -->
  <Style x:Key='BarItem' TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Grid>
            <Border x:Name='Bd' CornerRadius='5' Padding='10,2' Margin='0,3' Background='Transparent'>
              <ContentPresenter ContentSource='Header' VerticalAlignment='Center' TextBlock.Foreground='{TemplateBinding Foreground}'/>
            </Border>
            <Popup x:Name='PART_Popup' Placement='Bottom' HorizontalOffset='-12' VerticalOffset='-2' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'
                   IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border Margin='12,6,12,18' Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuBorder}' BorderThickness='1' CornerRadius='10' Padding='5' MinWidth='230' Effect='{StaticResource MenuShadow}'>
                <StackPanel IsItemsHost='True'/>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Bd' Property='Background' Value='#33FFFFFF'/></Trigger>
            <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Bd' Property='Background' Value='#40FFFFFF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='{DynamicResource Bubble}' BorderBrush='{DynamicResource MenuBorder}' BorderThickness='1' CornerRadius='6' Padding='8,3' Margin='4'>
            <ContentPresenter TextBlock.Foreground='{DynamicResource BubbleFg}' TextBlock.FontFamily='{DynamicResource UiFont}' TextBlock.FontSize='12'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Thin overlay-style scrollbars -->
  <ControlTemplate x:Key='VScroll' TargetType='ScrollBar'>
    <Track x:Name='PART_Track' IsDirectionReversed='True'>
      <Track.Thumb><Thumb><Thumb.Template><ControlTemplate><Border CornerRadius='3' Margin='3,2' Background='{DynamicResource Thumb}'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    </Track>
  </ControlTemplate>
  <ControlTemplate x:Key='HScroll' TargetType='ScrollBar'>
    <Track x:Name='PART_Track'>
      <Track.Thumb><Thumb><Thumb.Template><ControlTemplate><Border CornerRadius='3' Margin='2,3' Background='{DynamicResource Thumb}'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    </Track>
  </ControlTemplate>
  <Style TargetType='ScrollBar'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Width' Value='11'/>
    <Setter Property='Template' Value='{StaticResource VScroll}'/>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='11'/>
        <Setter Property='Template' Value='{StaticResource HScroll}'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- macOS slider -->
  <Style x:Key='SliderFill' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='RepeatButton'><Border Height='4' CornerRadius='2' Background='{DynamicResource Accent}'/></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='SliderBlank' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Slider'>
          <Grid Height='24'>
            <Border Height='4' CornerRadius='2' Background='{DynamicResource Track}' VerticalAlignment='Center'/>
            <Track x:Name='PART_Track'>
              <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Style='{StaticResource SliderFill}'/></Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Style='{StaticResource SliderBlank}'/></Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb><Thumb.Template><ControlTemplate>
                  <Ellipse Width='20' Height='20' Fill='White' Stroke='#22000000' StrokeThickness='0.5'>
                    <Ellipse.Effect><DropShadowEffect BlurRadius='5' ShadowDepth='1' Direction='270' Opacity='0.35'/></Ellipse.Effect>
                  </Ellipse>
                </ControlTemplate></Thumb.Template></Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- macOS toggle switch -->
  <Style x:Key='Switch' TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{DynamicResource Fg}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
            <ContentPresenter VerticalAlignment='Center' TextBlock.Foreground='{TemplateBinding Foreground}'/>
            <Border x:Name='Track' Grid.Column='1' Width='38' Height='22' CornerRadius='11' Background='{DynamicResource Track}'>
              <Ellipse x:Name='Knob' Width='18' Height='18' Fill='White' HorizontalAlignment='Left' Margin='2,0'>
                <Ellipse.RenderTransform><TranslateTransform/></Ellipse.RenderTransform>
                <Ellipse.Effect><DropShadowEffect BlurRadius='4' ShadowDepth='1' Direction='270' Opacity='0.3'/></Ellipse.Effect>
              </Ellipse>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Track' Property='Background' Value='{DynamicResource Accent}'/>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='16' Duration='0:0:0.2'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='0' Duration='0:0:0.2'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Chromeless list items; templates draw their own selection -->
  <Style x:Key='Bare' TargetType='ListBoxItem'>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><ContentPresenter/></ControlTemplate></Setter.Value></Setter>
  </Style>

  <Style x:Key='Flat' TargetType='Button'>
    <Setter Property='Foreground' Value='{DynamicResource FgDim}'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='Bd' CornerRadius='6' Padding='{TemplateBinding Padding}' Background='{TemplateBinding Background}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextBlock.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource SelBg}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <DataTemplate x:Key='FinderTile'>
    <StackPanel Width='104' Margin='2,6' Background='Transparent'>
      <Border x:Name='IconBd' Width='80' Height='80' CornerRadius='8' Padding='6' HorizontalAlignment='Center'>
        <Image Source='{Binding Icon}' RenderOptions.BitmapScalingMode='HighQuality'/>
      </Border>
      <Border x:Name='NameBd' CornerRadius='4' Padding='5,1,5,2' Margin='0,4,0,0' HorizontalAlignment='Center'>
        <TextBlock x:Name='Label' Text='{Binding Name}' TextWrapping='Wrap' TextAlignment='Center' MaxHeight='34' TextTrimming='CharacterEllipsis' FontSize='12' Foreground='{DynamicResource Fg}'/>
      </Border>
    </StackPanel>
    <DataTemplate.Triggers>
      <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
        <Setter TargetName='IconBd' Property='Background' Value='{DynamicResource SelBg}'/>
        <Setter TargetName='NameBd' Property='Background' Value='{DynamicResource Accent}'/>
        <Setter TargetName='Label' Property='Foreground' Value='White'/>
      </DataTrigger>
    </DataTemplate.Triggers>
  </DataTemplate>

  <DataTemplate x:Key='SideRow'>
    <Border x:Name='Bd' CornerRadius='6' Padding='8,5' Margin='8,1' Background='Transparent'>
      <StackPanel Orientation='Horizontal'>
        <TextBlock Text='{Binding Glyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='15' Foreground='{DynamicResource Accent}' Width='24' VerticalAlignment='Center'/>
        <TextBlock x:Name='Label' Text='{Binding Name}' FontSize='13' Foreground='{DynamicResource Fg}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>
      </StackPanel>
    </Border>
    <DataTemplate.Triggers>
      <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
        <Setter TargetName='Bd' Property='Background' Value='{DynamicResource SelBg}'/>
      </DataTrigger>
      <DataTrigger Binding='{Binding IsHeader}' Value='True'>
        <Setter TargetName='Bd' Property='Margin' Value='0,14,8,2'/>
        <Setter TargetName='Bd' Property='IsHitTestVisible' Value='False'/>
        <Setter TargetName='Label' Property='FontSize' Value='11'/>
        <Setter TargetName='Label' Property='FontWeight' Value='SemiBold'/>
        <Setter TargetName='Label' Property='Foreground' Value='{DynamicResource FgDim}'/>
      </DataTrigger>
    </DataTemplate.Triggers>
  </DataTemplate>

  <DataTemplate x:Key='SpotRow'>
    <Border x:Name='Bd' CornerRadius='8' Padding='10,6' Margin='8,1' Background='Transparent'>
      <Grid>
        <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
        <Image Source='{Binding Icon}' Width='30' Height='30' Margin='0,0,12,0' RenderOptions.BitmapScalingMode='HighQuality'/>
        <TextBlock x:Name='Title' Grid.Column='1' Text='{Binding Name}' FontSize='15' Foreground='{DynamicResource Fg}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>
        <TextBlock x:Name='Sub' Grid.Column='2' Text='{Binding Sub}' FontSize='12' Foreground='{DynamicResource FgDim}' VerticalAlignment='Center' Margin='12,0,0,0' MaxWidth='300' TextTrimming='CharacterEllipsis'/>
      </Grid>
    </Border>
    <DataTemplate.Triggers>
      <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
        <Setter TargetName='Bd' Property='Background' Value='{DynamicResource Accent}'/>
        <Setter TargetName='Title' Property='Foreground' Value='White'/>
        <Setter TargetName='Sub' Property='Foreground' Value='#CCFFFFFF'/>
      </DataTrigger>
    </DataTemplate.Triggers>
  </DataTemplate>
</ResourceDictionary>";
    }
}
