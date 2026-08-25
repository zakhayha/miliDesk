using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskMonitor
{
    internal sealed class TrayFlyout : Window
    {
        private readonly HeatGauge _cpu = new HeatGauge("CPU");
        private readonly HeatGauge _gpu = new HeatGauge("GPU");
        private readonly HeatGauge _ram = new HeatGauge("RAM");
        private readonly HeatGauge _net = new HeatGauge("ETH");
        private readonly CardShell _cpuWrap = new CardShell();
        private readonly CardShell _gpuWrap = new CardShell();
        private readonly CardShell _ramWrap = new CardShell();
        private readonly CardShell _netWrap = new CardShell();
        private readonly StackPanel _row = new StackPanel();
        private readonly OverlayWindow _host;

        public TrayFlyout(OverlayWindow host)
        {
            _host = host;
            Title = "DeskMonitor";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _row.Orientation = Orientation.Horizontal;
            _row.VerticalAlignment = VerticalAlignment.Top;
            _cpuWrap.Inner = _cpu.Root;
            _gpuWrap.Inner = _gpu.Root;
            _ramWrap.Inner = _ram.Root;
            _netWrap.Inner = _net.Root;
            _cpuWrap.VerticalAlignment = VerticalAlignment.Top;
            _gpuWrap.VerticalAlignment = VerticalAlignment.Top;
            _ramWrap.VerticalAlignment = VerticalAlignment.Top;
            _netWrap.VerticalAlignment = VerticalAlignment.Top;
            _row.Children.Add(_cpuWrap);
            _row.Children.Add(_gpuWrap);
            _row.Children.Add(_ramWrap);
            _row.Children.Add(_netWrap);
            BindHover(_cpuWrap, _cpu);
            BindHover(_gpuWrap, _gpu);
            BindHover(_ramWrap, _ram);
            BindHover(_netWrap, _net);

            Content = new Border
            {
                Child = _row,
                Padding = Theme.ShadowRoom(),
                Background = Brushes.Transparent
            };

            Deactivated += delegate { Hide(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
            };
            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var ex = Native.GetWindowLong(hwnd, -20);
                Native.SetWindowLong(hwnd, -20, ex | 0x00000080);
            };
        }

        public void ApplyLayout(SettingsStore s)
        {
            double scale = SettingsStore.Clamp(s.Scale * 0.92, 0.7, 1.4);
            _cpu.ApplyScale(scale, s);
            _gpu.ApplyScale(scale, s);
            _ram.ApplyScale(scale, s);
            _net.ApplyScale(scale, s);

            bool[] show = { s.ShowCpu, s.ShowGpu, s.ShowRam, s.ShowNet };
            CardShell[] wraps = { _cpuWrap, _gpuWrap, _ramWrap, _netWrap };
            string style = SettingsStore.NormalizeCardStyle(s.CardStyle);
            double gap = 10 * scale;
            double card = Math.Max(_cpu.RingSize, Math.Max(_gpu.RingSize, Math.Max(_ram.RingSize, _net.RingSize))) * 1.16 + 24 * scale;
            for (int i = 0; i < wraps.Length; i++)
            {
                wraps[i].Visibility = show[i] ? Visibility.Visible : Visibility.Collapsed;
                bool after = false;
                for (int j = i + 1; j < show.Length; j++) if (show[j]) after = true;
                wraps[i].Margin = new Thickness(0, 0, (show[i] && after) ? gap : 0, 0);
                wraps[i].Dress(22, new Thickness(10 * scale, 16 * scale, 10 * scale, 10 * scale),
                    Theme.CardFill(s.CardOpacity, style), Theme.Sheen(style), s.Grain);
                wraps[i].BorderBrush = Theme.CardEdge(style);
                wraps[i].BorderThickness = new Thickness(1);
                wraps[i].Effect = Theme.CardShadow();
                if (show[i])
                {
                    wraps[i].Width = card;
                    wraps[i].MinWidth = card;
                    wraps[i].MaxWidth = card;
                }
            }
            Dispatcher.BeginInvoke(new Action(PaintBackdrop), DispatcherPriority.Loaded);
        }

        private void PaintBackdrop()
        {
            if (!IsLoaded) return;
            CardShell[] wraps = { _cpuWrap, _gpuWrap, _ramWrap, _netWrap };
            string style = SettingsStore.NormalizeCardStyle(_host.Settings.CardStyle);
            if (style == "solid")
            {
                foreach (var wrap in wraps) wrap.Backdrop = null;
                return;
            }

            var src = PresentationSource.FromVisual(this);
            Matrix m = src != null && src.CompositionTarget != null
                ? src.CompositionTarget.TransformToDevice
                : Matrix.Identity;
            var probe = new Drawing.Point((int)(Left * m.M11) + 4, (int)(Top * m.M22) + 4);
            var bounds = Forms.Screen.FromPoint(probe).Bounds;
            var monitor = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

            foreach (var wrap in wraps)
            {
                if (wrap.Visibility != Visibility.Visible || wrap.ActualWidth < 4)
                {
                    wrap.Backdrop = null;
                    continue;
                }
                Point offset = wrap.TranslatePoint(new Point(0, 0), this);
                var rect = new Rect(
                    (Left + offset.X) * m.M11,
                    (Top + offset.Y) * m.M22,
                    wrap.ActualWidth * m.M11,
                    wrap.ActualHeight * m.M22);
                wrap.Backdrop = Frost.For(rect, monitor);
            }
        }

        public void Paint(Snapshot snap, OverlayWindow host)
        {
            host.PaintGauges(_cpu, _gpu, _ram, _net, snap);
        }

        public void Toggle()
        {
            if (IsVisible)
            {
                Hide();
                return;
            }
            ApplyLayout(_host.Settings);
            if (_host.LastSnapshot != null) Paint(_host.LastSnapshot, _host);
            Show();
            PlaceAboveTray();
            Activate();
        }

        private void PlaceAboveTray()
        {
            UpdateLayout();
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            double w = ActualWidth > 8 ? ActualWidth : 520;
            double h = ActualHeight > 8 ? ActualHeight : 180;
            Left = area.Right - w - 8;
            Top = area.Bottom - h - 8;
            if (Left < area.Left + 8) Left = area.Left + 8;
            if (Top < area.Top + 8) Top = area.Top + 8;
        }

        private void BindHover(Border wrap, HeatGauge gauge)
        {
            wrap.MouseEnter += delegate
            {
                _cpu.ShowTip(false);
                _gpu.ShowTip(false);
                _ram.ShowTip(false);
                _net.ShowTip(false);
                gauge.ShowTip(true);
            };
            wrap.MouseLeave += delegate { gauge.ShowTip(false); };
        }
    }

    internal sealed class TrayHub : IDisposable
    {
        private readonly Forms.NotifyIcon[] _icons;
        private readonly Drawing.Icon[] _glyphs;
        private readonly OverlayWindow _host;
        private readonly TrayFlyout _flyout;
        private readonly Forms.ContextMenu _menu;

        public Forms.MenuItem TopmostItem;
        public Forms.MenuItem StartupItem;

        public TrayHub(OverlayWindow host, TrayFlyout flyout)
        {
            _host = host;
            _flyout = flyout;
            _menu = BuildMenu();
            string[] names = { "CPU", "GPU", "RAM", "ETH" };
            _glyphs = new Drawing.Icon[names.Length];
            _icons = new Forms.NotifyIcon[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                _glyphs[i] = TrayGlyphs.Create(names[i]);
                var icon = new Forms.NotifyIcon
                {
                    Text = names[i],
                    Icon = _glyphs[i],
                    Visible = false,
                    ContextMenu = _menu
                };
                icon.MouseUp += OnIconMouseUp;
                _icons[i] = icon;
            }
            var promote = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            promote.Tick += delegate
            {
                promote.Stop();
                TrayPromote.TryShowOnTaskbar();
                PlaceIconsVisible();
            };
            promote.Start();
        }

        private void PlaceIconsVisible()
        {
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i] == null || !_icons[i].Visible) continue;
                _icons[i].Visible = false;
                _icons[i].Visible = true;
            }
            TrayPromote.TryShowOnTaskbar();
        }

        public void Apply(SettingsStore s)
        {
            bool[] show = { s.ShowCpu, s.ShowGpu, s.ShowRam, s.ShowNet };
            for (int i = 0; i < _icons.Length; i++) _icons[i].Visible = show[i];
            TrayPromote.TryShowOnTaskbar();
        }

        public void SetTips(string cpu, string gpu, string ram, string net)
        {
            if (_icons[0] != null) _icons[0].Text = cpu;
            if (_icons[1] != null) _icons[1].Text = gpu;
            if (_icons[2] != null) _icons[2].Text = ram;
            if (_icons[3] != null) _icons[3].Text = net;
        }

        private void OnIconMouseUp(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left) _flyout.Toggle();
        }

        private Forms.ContextMenu BuildMenu()
        {
            var menu = new Forms.ContextMenu();
            var customize = new Forms.MenuItem("Customize...");
            customize.Click += delegate { _host.OpenCustomize(); };

            TopmostItem = new Forms.MenuItem("Always on top") { Checked = _host.Topmost };
            TopmostItem.Click += delegate
            {
                _host.Topmost = !TopmostItem.Checked;
                TopmostItem.Checked = _host.Topmost;
                _host.Settings.Topmost = _host.Topmost;
                _host.Settings.Save();
            };

            StartupItem = new Forms.MenuItem("Start with Windows") { Checked = Startup.IsEnabled() };
            StartupItem.Click += delegate
            {
                var next = !StartupItem.Checked;
                Startup.SetEnabled(next);
                StartupItem.Checked = next;
            };

            var admin = new Forms.MenuItem("Restart as administrator");
            admin.Click += delegate { _host.RestartElevated(); };

            var exit = new Forms.MenuItem("Exit");
            exit.Click += delegate
            {
                _host.Settings.Save();
                Application.Current.Shutdown();
            };

            menu.MenuItems.Add(customize);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(TopmostItem);
            menu.MenuItems.Add(StartupItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(admin);
            menu.MenuItems.Add(exit);
            return menu;
        }

        public void Dispose()
        {
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i] == null) continue;
                _icons[i].Visible = false;
                _icons[i].Dispose();
            }
            for (int i = 0; i < _glyphs.Length; i++)
            {
                if (_glyphs[i] != null) _glyphs[i].Dispose();
            }
        }
    }

    internal static class TrayPromote
    {
        public static void TryShowOnTaskbar()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return;
                    string[] ids = root.GetSubKeyNames();
                    for (int i = 0; i < ids.Length; i++)
                    {
                        using (var key = root.OpenSubKey(ids[i], true))
                        {
                            if (key == null) continue;
                            var path = key.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            if (path.IndexOf("DeskMonitor", StringComparison.OrdinalIgnoreCase) < 0 &&
                                (exe == null || path.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0))
                            {
                                continue;
                            }
                            key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class TaskbarDock : Window
    {
        private readonly OverlayWindow _host;
        private readonly TrayFlyout _flyout;
        private readonly Button[] _buttons = new Button[4];
        private readonly TextBlock[] _values = new TextBlock[4];
        private readonly DispatcherTimer _timer;
        private static readonly string[] Names = { "CPU", "GPU", "RAM", "ETH" };

        public TaskbarDock(OverlayWindow host, TrayFlyout flyout)
        {
            _host = host;
            _flyout = flyout;
            Title = "DeskMonitor Tray";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            SizeToContent = SizeToContent.Width;
            Height = 40;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent
            };
            for (int i = 0; i < Names.Length; i++)
            {
                _buttons[i] = MakeButton(Names[i], i);
                row.Children.Add(_buttons[i]);
            }
            Content = row;
            MouseRightButtonUp += delegate
            {
                if (_host.ContextMenu != null)
                {
                    _host.ContextMenu.PlacementTarget = this;
                    _host.ContextMenu.IsOpen = true;
                }
            };
            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, -20);
                Native.SetWindowLong(hwnd, -20, ex | 0x00000080);
            };
            Loaded += delegate { Place(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += delegate { Place(); };
            Show();
            _timer.Start();
        }

        public void Apply(SettingsStore s)
        {
            bool[] show = { s.ShowCpu, s.ShowGpu, s.ShowRam, s.ShowNet };
            for (int i = 0; i < _buttons.Length; i++)
                _buttons[i].Visibility = show[i] ? Visibility.Visible : Visibility.Collapsed;
            Place();
        }

        public void Dispose()
        {
            _timer.Stop();
            Close();
        }

        public void SetValues(Snapshot snap, OverlayWindow host)
        {
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] == null) continue;
                _values[i].Text = host.DockText(i, snap);
            }
            Place();
        }

        private Button MakeButton(string name, int index)
        {
            var icon = HardwareIcons.Create(name);
            icon.Width = 15;
            icon.Height = 15;
            icon.VerticalAlignment = VerticalAlignment.Center;

            _values[index] = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 26
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            row.Children.Add(_values[index]);

            var b = new Button
            {
                Content = row,
                Height = 28,
                Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = name,
                Padding = new Thickness(6, 2, 6, 2)
            };
            b.Click += delegate { _flyout.Toggle(); };
            b.Template = IconButtonTemplate();
            return b;
        }

        private static ControlTemplate IconButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Shell");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Freeze(38, 255, 255, 255), "Shell"));
            template.Triggers.Add(hover);
            return template;
        }

        private void Place()
        {
            if (!IsLoaded) return;
            UpdateLayout();
            double w = ActualWidth > 8 ? ActualWidth : 120;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT band = new Native.RECT();
            bool ok = false;
            if (tray != IntPtr.Zero)
            {
                IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out band) && band.Right > band.Left + 8)
                {
                    ok = true;
                }
                else if (Native.GetWindowRect(tray, out band))
                {
                    ok = true;
                    band.Left = band.Right - 240;
                }
            }
            if (ok)
            {
                Point a = ToDip(band.Left, band.Top);
                Point b = ToDip(band.Right, band.Bottom);
                double h = Math.Max(28, b.Y - a.Y);
                Height = h;
                foreach (var btn in _buttons) btn.Height = Math.Max(22, h - 4);
                Left = a.X - w - 6;
                Top = a.Y;
            }
            else
            {
                var screen = Forms.Screen.PrimaryScreen;
                double h = Math.Max(32, screen.Bounds.Bottom - screen.WorkingArea.Bottom);
                if (h < 8) h = 40;
                Height = h;
                Left = screen.WorkingArea.Right - w - 12;
                Top = screen.WorkingArea.Bottom;
            }
            if (Left < 0) Left = 8;
        }

        private Point ToDip(int x, int y)
        {
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
                return src.CompositionTarget.TransformFromDevice.Transform(new Point(x, y));
            return new Point(x, y);
        }
    }

    internal static class TrayGlyphs
    {
        public static Drawing.Icon Create(string name)
        {
            var bmp = new Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                var pen = new Drawing.Pen(Drawing.Color.FromArgb(248, 248, 250), 1.4f);
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(248, 248, 250));
                if (name == "GPU") DrawGpu(g, pen, fill);
                else if (name == "RAM") DrawRam(g, pen, fill);
                else if (name == "ETH") DrawEth(g, pen, fill);
                else DrawCpu(g, pen, fill);
                pen.Dispose();
                fill.Dispose();
            }
            IntPtr handle = bmp.GetHicon();
            var icon = Drawing.Icon.FromHandle(handle);
            var clone = (Drawing.Icon)icon.Clone();
            Native.DestroyIcon(handle);
            icon.Dispose();
            bmp.Dispose();
            return clone;
        }

        private static void DrawCpu(Drawing.Graphics g, Drawing.Pen pen, Drawing.Brush fill)
        {
            g.DrawRectangle(pen, 4, 4, 8, 8);
            g.DrawRectangle(pen, 6, 6, 4, 4);
            g.FillRectangle(fill, 2, 6, 2, 1);
            g.FillRectangle(fill, 12, 6, 2, 1);
            g.FillRectangle(fill, 2, 9, 2, 1);
            g.FillRectangle(fill, 12, 9, 2, 1);
            g.FillRectangle(fill, 6, 2, 1, 2);
            g.FillRectangle(fill, 9, 2, 1, 2);
            g.FillRectangle(fill, 6, 12, 1, 2);
            g.FillRectangle(fill, 9, 12, 1, 2);
        }

        private static void DrawGpu(Drawing.Graphics g, Drawing.Pen pen, Drawing.Brush fill)
        {
            g.DrawRectangle(pen, 2, 4, 12, 7);
            g.DrawEllipse(pen, 4, 5, 5, 5);
            g.FillRectangle(fill, 11, 6, 2, 4);
            g.FillRectangle(fill, 3, 12, 10, 1.5f);
        }

        private static void DrawRam(Drawing.Graphics g, Drawing.Pen pen, Drawing.Brush fill)
        {
            g.DrawRectangle(pen, 2, 5, 12, 5);
            for (int i = 0; i < 5; i++) g.FillRectangle(fill, 3.5f + i * 2.2f, 11, 1.2f, 2.4f);
            g.DrawRectangle(pen, 4, 6.2f, 3, 2.6f);
            g.DrawRectangle(pen, 9, 6.2f, 3, 2.6f);
        }

        private static void DrawEth(Drawing.Graphics g, Drawing.Pen pen, Drawing.Brush fill)
        {
            g.DrawRectangle(pen, 5, 2, 6, 3);
            g.DrawRectangle(pen, 3, 5, 10, 7);
            for (int i = 0; i < 4; i++) g.FillRectangle(fill, 5 + i * 2, 10, 1.2f, 2);
        }
    }
}
