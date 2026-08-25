using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskMonitor
{
    internal static class HardwareIcons
    {
        public static Viewbox Create(string name)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            if (name == "GPU") DrawGpu(canvas);
            else if (name == "RAM") DrawRam(canvas);
            else if (name == "ETH") DrawEth(canvas);
            else DrawCpu(canvas);

            return new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private static void DrawCpu(Canvas canvas)
        {
            var body = Rounded(6, 6, 12, 12, 2.2);
            canvas.Children.Add(body);
            canvas.Children.Add(Rounded(9, 9, 6, 6, 1.1));
            for (int i = 0; i < 3; i++)
            {
                double y = 8 + i * 4;
                canvas.Children.Add(Pin(3.2, y, 2.4, 1.5));
                canvas.Children.Add(Pin(18.4, y, 2.4, 1.5));
                double x = 8 + i * 4;
                canvas.Children.Add(Pin(x, 3.2, 1.5, 2.4));
                canvas.Children.Add(Pin(x, 18.4, 1.5, 2.4));
            }
        }

        private static void DrawGpu(Canvas canvas)
        {
            canvas.Children.Add(Rounded(3, 6, 18, 12, 2));
            var fan = new Ellipse
            {
                Width = 7.5,
                Height = 7.5,
                Stroke = Theme.Value,
                StrokeThickness = 1.4,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(fan, 6.2);
            Canvas.SetTop(fan, 8.2);
            canvas.Children.Add(fan);
            canvas.Children.Add(Rounded(15.2, 8.5, 3.6, 7, 0.8));
            canvas.Children.Add(Pin(4.5, 18.2, 15, 2.2));
        }

        private static void DrawRam(Canvas canvas)
        {
            canvas.Children.Add(Rounded(3, 7, 18, 9, 1.6));
            for (int i = 0; i < 6; i++)
            {
                canvas.Children.Add(Pin(5.2 + i * 2.4, 16.2, 1.5, 3.2));
            }
            canvas.Children.Add(Rounded(6, 9, 4.2, 5, 0.8));
            canvas.Children.Add(Rounded(13.6, 9, 4.2, 5, 0.8));
        }

        private static void DrawEth(Canvas canvas)
        {
            canvas.Children.Add(Rounded(8.5, 3.2, 7, 5.5, 1.1));
            canvas.Children.Add(Rounded(4.5, 8, 15, 11, 1.8));
            for (int i = 0; i < 4; i++)
            {
                canvas.Children.Add(Pin(7.2 + i * 2.6, 15.6, 1.6, 2.4));
            }
        }

        private static Rectangle Rounded(double x, double y, double w, double h, double radius)
        {
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                RadiusX = radius,
                RadiusY = radius,
                Stroke = Theme.Value,
                StrokeThickness = 1.4,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            return rect;
        }

        private static Rectangle Pin(double x, double y, double w, double h)
        {
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = Theme.Value,
                RadiusX = 0.4,
                RadiusY = 0.4
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            return rect;
        }
    }

    internal static class ChromeIcons
    {
        public static Path Gear(double size)
        {
            return new Path
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = Theme.Value,
                Data = Geometry.Parse(
                    "M10.2,1.6 L13.8,1.6 L14.4,4.4 C15.1,4.6 15.8,5 16.4,5.4 L19,4.2 L21.2,6.4 L20,9 C20.4,9.6 20.8,10.3 21,11 L23.8,11.6 L23.8,15.2 L21,15.8 C20.8,16.5 20.4,17.2 20,17.8 L21.2,20.4 L19,22.6 L16.4,21.4 C15.8,21.8 15.1,22.2 14.4,22.4 L13.8,25.2 L10.2,25.2 L9.6,22.4 C8.9,22.2 8.2,21.8 7.6,21.4 L5,22.6 L2.8,20.4 L4,17.8 C3.6,17.2 3.2,16.5 3,15.8 L0.2,15.2 L0.2,11.6 L3,11 C3.2,10.3 3.6,9.6 4,9 L2.8,6.4 L5,4.2 L7.6,5.4 C8.2,5 8.9,4.6 9.6,4.4 Z M12,8.2 A4.4,4.4 0 1 0 12,17 A4.4,4.4 0 1 0 12,8.2 Z")
            };
        }

        public static Path Grip(double size)
        {
            return new Path
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Stroke = Theme.Value,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent,
                Data = Geometry.Parse("M6,18 L18,6 M10,18 L18,10 M14,18 L18,14")
            };
        }
    }
}
