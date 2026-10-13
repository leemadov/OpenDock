using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // Built-in icons drawn as vectors (no bundled image files), rasterised once with the same shadow as app tiles.
    static class Art
    {
        static Brush Grad(string top, string bottom)
        {
            return new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        }

        static Brush Solid(string c) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); }

        static readonly Rect Tile = new Rect(16, 16, 224, 224);
        const double R = 50;

        static BitmapSource Bake(DrawingGroup g, bool shadow = true)
        {
            g.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 256, 256))));
            var img = new Image { Source = new DrawingImage(g), Width = 256, Height = 256 };
            if (shadow) img.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.3 };
            var host = new Grid { Width = 256, Height = 256 };
            host.Children.Add(img);
            return Icons.Render(host, 256, 256);
        }

        static BitmapSource finder, launchpad, downloads, trash, trashFull;

        public static BitmapSource Finder
        {
            get
            {
                if (finder != null) return finder;
                var g = new DrawingGroup();
                var tile = new RectangleGeometry(Tile, R, R);
                g.Children.Add(new GeometryDrawing(Grad("#F7F9FC", "#D5DCE6"), null, tile));
                // left half: sky blue, split by a soft wave
                var left = Geometry.Combine(tile, Geometry.Parse("M0,0 L134,0 C120,70 150,110 128,150 C112,180 136,215 126,256 L0,256 Z"), GeometryCombineMode.Intersect, null);
                g.Children.Add(new GeometryDrawing(Grad("#86D8FF", "#1E8BEF"), null, left));
                var ink = new Pen(Solid("#1B2A3C"), 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                g.Children.Add(new GeometryDrawing(null, ink, Geometry.Parse("M88,82 L88,112 M170,82 L170,112")));
                g.Children.Add(new GeometryDrawing(null, ink, Geometry.Parse("M70,162 Q128,206 188,160")));
                g.Children.Add(new GeometryDrawing(null, new Pen(Solid("#33FFFFFF"), 2), new RectangleGeometry(new Rect(17, 17, 222, 222), R, R)));
                return finder = Bake(g);
            }
        }

        public static BitmapSource Launchpad
        {
            get
            {
                if (launchpad != null) return launchpad;
                var g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(Grad("#6A6A74", "#2A2A30"), null, new RectangleGeometry(Tile, R, R)));
                string[] colors = { "#FF5F57", "#FF9F0A", "#FFD60A", "#32D74B", "#64D2FF", "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F" };
                for (int i = 0; i < 9; i++)
                {
                    double x = 56 + (i % 3) * 52, y = 56 + (i / 3) * 52;
                    var c = (Color)ColorConverter.ConvertFromString(colors[i]);
                    var lighter = Color.FromRgb((byte)(c.R + (255 - c.R) / 3), (byte)(c.G + (255 - c.G) / 3), (byte)(c.B + (255 - c.B) / 3));
                    g.Children.Add(new GeometryDrawing(new LinearGradientBrush(lighter, c, 90), null, new RectangleGeometry(new Rect(x, y, 40, 40), 11, 11)));
                }
                g.Children.Add(new GeometryDrawing(null, new Pen(Solid("#2EFFFFFF"), 2), new RectangleGeometry(new Rect(17, 17, 222, 222), R, R)));
                return launchpad = Bake(g);
            }
        }

        public static BitmapSource Downloads
        {
            get
            {
                if (downloads != null) return downloads;
                var g = new DrawingGroup();
                g.Children.Add(new GeometryDrawing(Grad("#4AA8F0", "#2479D6"), null, Geometry.Parse("M28,70 Q28,52 46,52 L100,52 Q110,52 116,60 L124,70 L210,70 Q228,70 228,88 L228,200 L28,200 Z")));
                g.Children.Add(new GeometryDrawing(Grad("#7CCBFF", "#3B9CF0"), null, new RectangleGeometry(new Rect(28, 86, 200, 128), 16, 16)));
                g.Children.Add(new GeometryDrawing(null, new Pen(Solid("#40FFFFFF"), 2), Geometry.Parse("M36,88 L220,88")));
                var arrow = new Pen(Solid("#1D5FAE"), 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                g.Children.Add(new GeometryDrawing(null, new Pen(Solid("#1D5FAE"), 8), new EllipseGeometry(new Point(128, 150), 38, 38)));
                g.Children.Add(new GeometryDrawing(null, arrow, Geometry.Parse("M128,128 L128,170 M110,154 L128,172 L146,154")));
                return downloads = Bake(g);
            }
        }

        public static BitmapSource Trash(bool full)
        {
            if (full && trashFull != null) return trashFull;
            if (!full && trash != null) return trash;
            var g = new DrawingGroup();
            if (full)
            {
                g.Children.Add(new GeometryDrawing(Solid("#FAFAFA"), new Pen(Solid("#C9CDD3"), 2), Geometry.Parse("M70,62 L96,30 L128,48 L150,22 L176,56 L190,44 L184,76 Z")));
                g.Children.Add(new GeometryDrawing(Solid("#EEF2F7"), new Pen(Solid("#C2C8D0"), 2), Geometry.Parse("M100,70 L118,40 L150,60 L168,36 L180,72 Z")));
            }
            var body = Geometry.Parse("M52,64 L204,64 L190,224 Q188,236 176,236 L80,236 Q68,236 66,224 Z");
            g.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromArgb(235, 236, 239, 243), Color.FromArgb(235, 176, 182, 192), 0), new Pen(Solid("#8C9099"), 2), body));
            var rib = new Pen(Solid("#668A909A"), 3);
            for (int i = 0; i < 7; i++)
            {
                double t = (i + 1) / 8.0, top = 52 + 152 * t, bottom = 66 + 124 * t;
                g.Children.Add(new GeometryDrawing(null, rib, new LineGeometry(new Point(top, 78), new Point(bottom, 228))));
            }
            g.Children.Add(new GeometryDrawing(Grad("#D9DDE3", "#A9AFB9"), new Pen(Solid("#80868F"), 2), new RectangleGeometry(new Rect(46, 56, 164, 16), 8, 8)));
            return full ? (trashFull = Bake(g)) : (trash = Bake(g));
        }
    }
}
