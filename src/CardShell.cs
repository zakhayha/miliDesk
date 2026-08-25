using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DeskMonitor
{
    /// <summary>
    /// A card drawn as stacked layers so the fill opacity, frosted backdrop and
    /// grain overlay can each be changed without disturbing the others.
    /// </summary>
    internal sealed class CardShell : Border
    {
        private readonly Grid _layers = new Grid();
        private readonly Border _back = new Border();
        private readonly Border _tint = new Border();
        private readonly Border _sheen = new Border();
        private readonly Border _grain = new Border();
        private readonly Border _content = new Border();

        public CardShell()
        {
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            _back.IsHitTestVisible = false;
            _tint.IsHitTestVisible = false;
            _sheen.IsHitTestVisible = false;
            _grain.IsHitTestVisible = false;
            _layers.Children.Add(_back);
            _layers.Children.Add(_tint);
            _layers.Children.Add(_sheen);
            _layers.Children.Add(_grain);
            _layers.Children.Add(_content);
            Child = _layers;
        }

        public UIElement Inner
        {
            get { return _content.Child; }
            set { _content.Child = value; }
        }

        public Brush Backdrop
        {
            get { return _back.Background; }
            set { _back.Background = value; }
        }

        public void Dress(double radius, Thickness padding, Brush tint, Brush sheen, double grain)
        {
            var outer = new CornerRadius(radius);
            var inner = new CornerRadius(Math.Max(0, radius - 1));
            CornerRadius = outer;
            _back.CornerRadius = inner;
            _tint.CornerRadius = inner;
            _sheen.CornerRadius = inner;
            _grain.CornerRadius = inner;
            _content.Padding = padding;
            _tint.Background = tint;
            _sheen.Background = sheen;
            _grain.Background = grain > 0.001 ? Noise.Brush : null;
            _grain.Opacity = grain;
        }

        public void Bare(Thickness padding)
        {
            CornerRadius = new CornerRadius(0);
            _content.Padding = padding;
            _back.Background = null;
            _tint.Background = null;
            _sheen.Background = null;
            _grain.Background = null;
        }
    }

    /// <summary>
    /// Tileable monochrome noise used for the grain overlay.
    /// </summary>
    internal static class Noise
    {
        private const int Tile = 96;
        private static Brush _brush;

        public static Brush Brush
        {
            get
            {
                if (_brush == null) _brush = Build();
                return _brush;
            }
        }

        private static Brush Build()
        {
            var pixels = new byte[Tile * Tile * 4];
            var rnd = new Random(20260825);
            for (int i = 0; i < Tile * Tile; i++)
            {
                // Two samples averaged keeps the noise from looking like static.
                int v = (rnd.Next(0, 256) + rnd.Next(0, 256)) / 2;
                pixels[i * 4 + 0] = (byte)v;
                pixels[i * 4 + 1] = (byte)v;
                pixels[i * 4 + 2] = (byte)v;
                pixels[i * 4 + 3] = 255;
            }
            var bmp = BitmapSource.Create(Tile, Tile, 96, 96, PixelFormats.Bgra32, null, pixels, Tile * 4);
            bmp.Freeze();
            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, Tile, Tile),
                Stretch = Stretch.None
            };
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Builds frosted-glass fills from the desktop wallpaper. Live window content
    /// cannot be sampled from a layered window, so the wallpaper behind the card is
    /// blurred and mapped to the card's position on screen.
    /// </summary>
    internal static class Frost
    {
        private static BitmapSource _blurred;
        private static string _source;
        private static DateTime _checkedAt;

        public static BitmapSource Wallpaper
        {
            get
            {
                string path = CurrentPath();
                if (string.IsNullOrEmpty(path)) return null;
                if (_blurred != null && string.Equals(path, _source, StringComparison.OrdinalIgnoreCase))
                {
                    return _blurred;
                }
                _blurred = Blur(path);
                _source = path;
                return _blurred;
            }
        }

        public static Brush For(Rect card, Rect monitor)
        {
            var image = Wallpaper;
            if (image == null || monitor.Width < 1 || monitor.Height < 1) return null;

            double u = (card.X - monitor.X) / monitor.Width;
            double v = (card.Y - monitor.Y) / monitor.Height;
            double du = card.Width / monitor.Width;
            double dv = card.Height / monitor.Height;
            u = Clamp(u, -0.5, 1.5);
            v = Clamp(v, -0.5, 1.5);

            var brush = new ImageBrush(image)
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(u, v, Math.Max(0.01, du), Math.Max(0.01, dv)),
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };
            brush.Freeze();
            return brush;
        }

        private static string CurrentPath()
        {
            // The shell rewrites the wallpaper file in place, so re-reading every few
            // seconds is enough to notice a change without hammering the API.
            if (_source != null && DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(10)) return _source;
            _checkedAt = DateTime.UtcNow;
            try
            {
                var sb = new StringBuilder(520);
                if (!SystemParametersInfo(SpiGetDeskWallpaper, (uint)sb.Capacity, sb, 0)) return _source;
                string path = sb.ToString();
                return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) ? null : path;
            }
            catch
            {
                return _source;
            }
        }

        private static BitmapSource Blur(string path)
        {
            try
            {
                var small = new BitmapImage();
                small.BeginInit();
                small.UriSource = new Uri(path, UriKind.Absolute);
                small.CacheOption = BitmapCacheOption.OnLoad;
                small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                small.DecodePixelWidth = 360;
                small.EndInit();
                small.Freeze();

                int w = small.PixelWidth;
                int h = small.PixelHeight;
                if (w < 4 || h < 4) return null;

                // Draw oversized so the blur kernel samples real pixels at the edges
                // instead of fading into transparency.
                double bleed = 24;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(small, new Rect(-bleed, -bleed, w + bleed * 2, h + bleed * 2));
                }
                visual.Effect = new BlurEffect
                {
                    Radius = 18,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };

                var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();
                return target;
            }
            catch
            {
                return null;
            }
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private const uint SpiGetDeskWallpaper = 0x0073;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
    }
}
