using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;
using WpfSeparator = System.Windows.Controls.Separator;

namespace DeskMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            if (!IsAdministrator())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
            else
            {
                KillOtherInstances();
            }

            if (Startup.IsEnabled()) Startup.SetEnabled(true);

            bool created;
            var mutex = new Mutex(true, "DeskMonitor.SingleInstance", out created);
            if (!created) return;

            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
            };
                app.Run(new OverlayWindow());
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void KillOtherInstances()
        {
            int me = Process.GetCurrentProcess().Id;
            Process[] list = Process.GetProcessesByName("DeskMonitor");
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    if (list[i].Id != me) list[i].Kill();
                }
                catch
                {
                }
                finally
                {
                    list[i].Dispose();
                }
            }
        }
    }

    internal sealed class OverlayWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExToolwindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        private SensorReader _sensors;
        private readonly DispatcherTimer _timer;
        private readonly HeatGauge _cpu = new HeatGauge("CPU");
        private readonly HeatGauge _gpu = new HeatGauge("GPU");
        private readonly HeatGauge _ram = new HeatGauge("RAM");
        private readonly HeatGauge _net = new HeatGauge("ETH");
        private readonly CardShell _cpuWrap = new CardShell();
        private readonly CardShell _gpuWrap = new CardShell();
        private readonly CardShell _ramWrap = new CardShell();
        private readonly CardShell _netWrap = new CardShell();
        private readonly StackPanel _gauges = new StackPanel();
        private readonly CardShell _groupCard = new CardShell();
        private readonly Grid _shell = new Grid();
        private bool _backdropQueued;
        private readonly Button _gear;
        private readonly Border _grip;
        private readonly SettingsStore _settings = SettingsStore.Load();
        private TrayHub _tray;
        private TrayFlyout _flyout;
        private TaskbarDock _dock;
        private CustomizeWindow _customize;
        private int _busy;
        private Snapshot _last;
        private bool _resizing;
        private double _resizeStartScale;
        private double _resizeStartWidth;

        public SettingsStore Settings { get { return _settings; } }
        public Snapshot LastSnapshot { get { return _last; } }

        public OverlayWindow()
        {
            Title = "DeskMonitor";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = _settings.Topmost;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            FontWeight = FontWeights.Normal;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _gear = BuildGear();
            _grip = BuildGrip();
            Content = BuildShell();
            ContextMenu = BuildMenu();
            ApplyVisual();
            ShowChrome(false);

            MouseLeftButtonDown += OnDragStart;
            MouseLeftButtonUp += delegate { if (!_resizing) SavePosition(); };
            LocationChanged += delegate
            {
                if (!IsLoaded) return;
                SavePosition();
                QueueBackdrop();
                if (_customize != null) _customize.DockTo(this);
            };
            SizeChanged += delegate { QueueBackdrop(); };
            MouseEnter += delegate { ShowChrome(true); };
            MouseLeave += delegate
            {
                if (_customize == null && !_resizing) ShowChrome(false);
                HideAllTips();
            };
            SourceInitialized += delegate { ApplyToolWindowStyle(); };
            Loaded += OnLoaded;
            Closed += delegate { Cleanup(); };

            _timer = new DispatcherTimer();
            _timer.Tick += delegate { RefreshAsync(); };
            ApplyInterval();
        }

        private Grid BuildShell()
        {
            _gauges.Orientation = Orientation.Horizontal;
            _gauges.HorizontalAlignment = HorizontalAlignment.Center;
            _gauges.VerticalAlignment = VerticalAlignment.Top;
            _cpuWrap.Inner = _cpu.Root;
            _gpuWrap.Inner = _gpu.Root;
            _ramWrap.Inner = _ram.Root;
            _netWrap.Inner = _net.Root;
            _cpuWrap.VerticalAlignment = VerticalAlignment.Top;
            _gpuWrap.VerticalAlignment = VerticalAlignment.Top;
            _ramWrap.VerticalAlignment = VerticalAlignment.Top;
            _netWrap.VerticalAlignment = VerticalAlignment.Top;
            _gauges.Children.Add(_cpuWrap);
            _gauges.Children.Add(_gpuWrap);
            _gauges.Children.Add(_ramWrap);
            _gauges.Children.Add(_netWrap);
            BindHover(_cpuWrap, _cpu);
            BindHover(_gpuWrap, _gpu);
            BindHover(_ramWrap, _ram);
            BindHover(_netWrap, _net);

            _groupCard.Inner = _gauges;
            _groupCard.HorizontalAlignment = HorizontalAlignment.Center;
            _groupCard.VerticalAlignment = VerticalAlignment.Center;

            Grid.SetZIndex(_gear, 4);
            Grid.SetZIndex(_grip, 4);
            _shell.Background = Theme.Hit;
            _shell.Children.Add(_groupCard);
            _shell.Children.Add(_gear);
            _shell.Children.Add(_grip);
            return _shell;
        }

        private Button BuildGear()
        {
            var button = new Button
            {
                Width = 30,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Background = Theme.ChromeFill,
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Settings",
                Padding = new Thickness(6),
                Content = ChromeIcons.Gear(16)
            };
            button.Click += delegate { OpenCustomize(); };
            button.Template = ChromeButtonTemplate();
            return button;
        }

        private Border BuildGrip()
        {
            var grip = new Border
            {
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 4),
                Background = Theme.Hit,
                Cursor = Cursors.SizeNWSE,
                Child = ChromeIcons.Grip(16),
                ToolTip = "Resize"
            };
            grip.MouseLeftButtonDown += OnResizeStart;
            grip.MouseMove += OnResizeMove;
            grip.MouseLeftButtonUp += OnResizeEnd;
            return grip;
        }

        private static ControlTemplate ChromeButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            template.VisualTree = border;
            return template;
        }

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (_resizing) return;
            var src = e.OriginalSource as DependencyObject;
            if (IsInside(_gear, src) || IsInside(_grip, src)) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void OnResizeStart(object sender, MouseButtonEventArgs e)
        {
            _resizing = true;
            _resizeStartScale = _settings.Scale;
            _resizeStartWidth = Math.Max(160, ActualWidth);
            _grip.CaptureMouse();
            e.Handled = true;
        }

        private void OnResizeMove(object sender, MouseEventArgs e)
        {
            if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return;
            double width = Math.Max(140, e.GetPosition(this).X);
            _settings.Scale = SettingsStore.Clamp(_resizeStartScale * (width / _resizeStartWidth), 0.7, 1.85);
            ApplyVisual();
            if (_last != null) Paint(_last);
            if (_customize != null) _customize.DockTo(this);
        }

        private void OnResizeEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            _grip.ReleaseMouseCapture();
            _settings.Save();
            if (_customize != null) _customize.Pull();
            SavePosition();
        }

        private void ShowChrome(bool on)
        {
            _gear.Opacity = on || _customize != null ? 1 : 0;
            _grip.Opacity = on || _resizing ? 1 : 0.4;
        }

        private static bool IsInside(DependencyObject root, DependencyObject src)
        {
            while (src != null)
            {
                if (src == root) return true;
                src = VisualTreeHelper.GetParent(src);
            }
            return false;
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var customize = new MenuItem { Header = "Customize..." };
            customize.Click += delegate { OpenCustomize(); };

            var topmost = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = Topmost };
            topmost.Click += delegate
            {
                Topmost = topmost.IsChecked == true;
                _settings.Topmost = Topmost;
                _settings.Save();
                if (_tray != null && _tray.TopmostItem != null) _tray.TopmostItem.Checked = Topmost;
            };

            var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = Startup.IsEnabled() };
            startup.Click += delegate
            {
                Startup.SetEnabled(startup.IsChecked == true);
                if (_tray != null && _tray.StartupItem != null) _tray.StartupItem.Checked = startup.IsChecked == true;
            };

            var admin = new MenuItem { Header = "Restart as administrator" };
            admin.Click += delegate { RestartElevated(); };

            var exit = new MenuItem { Header = "Exit" };
            exit.Click += delegate
            {
                _settings.Save();
                Application.Current.Shutdown();
            };

            menu.Items.Add(customize);
            menu.Items.Add(new WpfSeparator());
            menu.Items.Add(topmost);
            menu.Items.Add(startup);
            menu.Items.Add(new WpfSeparator());
            menu.Items.Add(admin);
            menu.Items.Add(exit);
            return menu;
        }

        public void OpenCustomize()
        {
            Dispatcher.BeginInvoke(new Action(ShowCustomize), DispatcherPriority.Input);
        }

        private void ShowCustomize()
        {
            try
        {
            if (_customize != null)
            {
                _customize.Activate();
                    ShowChrome(true);
                return;
            }
            _customize = new CustomizeWindow(this);
                _customize.Closed += delegate
                {
                    _customize = null;
                    if (!IsMouseOver) ShowChrome(false);
                };
                ShowChrome(true);
            _customize.Show();
                _customize.Activate();
                _customize.DockTo(this);
            }
            catch (Exception ex)
            {
                Log.Write("settings", ex);
                if (_customize != null)
                {
                    try { _customize.Close(); } catch { }
                    _customize = null;
                }
            }
        }

        public void ApplyFromSettings()
        {
            Topmost = _settings.Topmost;
            if (_tray != null && _tray.TopmostItem != null) _tray.TopmostItem.Checked = Topmost;
            ApplyVisual();
            ApplyInterval();
            _settings.Save();
            if (_last != null) Paint(_last);
            if (_customize != null) _customize.DockTo(this);
        }

        public void Snap(string corner)
        {
            var work = SystemParameters.WorkArea;
            double pad = 28;
            double w = ActualWidth > 80 ? ActualWidth : 520;
            double h = ActualHeight > 60 ? ActualHeight : 180;
            if (corner == "TL") { Left = work.Left + pad; Top = work.Top + pad; }
            else if (corner == "TR") { Left = work.Right - w - pad; Top = work.Top + pad; }
            else if (corner == "BL") { Left = work.Left + pad; Top = work.Bottom - h - pad; }
            else if (corner == "BR") { Left = work.Right - w - pad; Top = work.Bottom - h - pad; }
            SavePosition();
        }

        public void RestartElevated()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private void ApplyVisual()
        {
            Opacity = _settings.Opacity;
            double s = _settings.Scale;
            _cpu.ApplyScale(s, _settings);
            _gpu.ApplyScale(s, _settings);
            _ram.ApplyScale(s, _settings);
            _net.ApplyScale(s, _settings);

            bool[] show = { _settings.ShowCpu, _settings.ShowGpu, _settings.ShowRam, _settings.ShowNet };
            CardShell[] wraps = { _cpuWrap, _gpuWrap, _ramWrap, _netWrap };
            double gap = (_settings.SeparateCharts ? 10 : 8) * s;
            for (int i = 0; i < wraps.Length; i++)
            {
                wraps[i].Visibility = show[i] ? Visibility.Visible : Visibility.Collapsed;
                bool after = false;
                for (int j = i + 1; j < show.Length; j++) if (show[j]) after = true;
                wraps[i].Margin = new Thickness(0, 0, (show[i] && after) ? gap : 0, 0);
                StyleWrap(wraps[i], s);
            }
            StyleGroup(s);
            double card = Math.Max(_cpu.RingSize, Math.Max(_gpu.RingSize, Math.Max(_ram.RingSize, _net.RingSize))) * 1.16 + 24 * s;
            for (int i = 0; i < wraps.Length; i++)
            {
                if (!show[i]) continue;
                wraps[i].Width = card;
                wraps[i].MinWidth = card;
                wraps[i].MaxWidth = card;
            }
            if (_tray != null) _tray.Apply(_settings);
            ApplyDock();
            if (_flyout != null && _flyout.IsVisible) _flyout.ApplyLayout(_settings);
            QueueBackdrop();
        }

        private void StyleGroup(double s)
        {
            var room = Theme.ShadowRoom();
            string style = SettingsStore.NormalizeCardStyle(_settings.CardStyle);
            if (_settings.SeparateCharts)
            {
                _groupCard.Bare(room);
                _groupCard.BorderBrush = Brushes.Transparent;
                _groupCard.BorderThickness = new Thickness(0);
                _groupCard.Margin = new Thickness(0);
                _groupCard.Effect = null;
            }
            else
            {
                _groupCard.Dress(26, new Thickness(16 * s),
                    Theme.CardFill(_settings.CardOpacity, style), Theme.Sheen(style), _settings.Grain);
                _groupCard.BorderBrush = Theme.CardEdge(style);
                _groupCard.BorderThickness = new Thickness(1);
                _groupCard.Margin = room;
                _groupCard.Effect = Theme.CardShadow();
            }
        }

        private void StyleWrap(CardShell wrap, double s)
        {
            if (_settings.SeparateCharts)
            {
                string style = SettingsStore.NormalizeCardStyle(_settings.CardStyle);
                wrap.Dress(22, new Thickness(12 * s, 18 * s, 12 * s, 12 * s),
                    Theme.CardFill(_settings.CardOpacity, style), Theme.Sheen(style), _settings.Grain);
                wrap.BorderBrush = Theme.CardEdge(style);
                wrap.BorderThickness = new Thickness(1);
                wrap.Effect = Theme.CardShadow();
            }
            else
            {
                wrap.Bare(new Thickness(0));
                wrap.BorderThickness = new Thickness(0);
                wrap.Effect = null;
                wrap.CacheMode = null;
            }
        }

        /// <summary>
        /// The frosted fill maps the wallpaper behind each card, so it has to be
        /// rebuilt after layout settles and whenever the window moves.
        /// </summary>
        private void QueueBackdrop()
        {
            if (_backdropQueued) return;
            _backdropQueued = true;
            Dispatcher.BeginInvoke(new Action(PaintBackdrop), DispatcherPriority.Loaded);
        }

        private void PaintBackdrop()
        {
            _backdropQueued = false;
            if (!IsLoaded) return;
            string style = SettingsStore.NormalizeCardStyle(_settings.CardStyle);
            CardShell[] cards = _settings.SeparateCharts
                ? new[] { _cpuWrap, _gpuWrap, _ramWrap, _netWrap }
                : new[] { _groupCard };

            if (style == "solid")
            {
                foreach (var card in cards) card.Backdrop = null;
                return;
            }

            Rect monitor = MonitorBounds();
            foreach (var card in cards)
            {
                if (card.Visibility != Visibility.Visible || card.ActualWidth < 4)
                {
                    card.Backdrop = null;
                    continue;
                }
                card.Backdrop = Frost.For(DeviceRect(card), monitor);
            }
        }

        private Matrix DeviceMatrix()
        {
            var src = PresentationSource.FromVisual(this);
            return src != null && src.CompositionTarget != null
                ? src.CompositionTarget.TransformToDevice
                : Matrix.Identity;
        }

        private Rect DeviceRect(FrameworkElement element)
        {
            Point offset = element.TranslatePoint(new Point(0, 0), this);
            Matrix m = DeviceMatrix();
            return new Rect(
                (Left + offset.X) * m.M11,
                (Top + offset.Y) * m.M22,
                element.ActualWidth * m.M11,
                element.ActualHeight * m.M22);
        }

        private Rect MonitorBounds()
        {
            Matrix m = DeviceMatrix();
            var probe = new Drawing.Point((int)(Left * m.M11) + 4, (int)(Top * m.M22) + 4);
            var screen = Forms.Screen.FromPoint(probe);
            return new Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
        }

        private void BindHover(Border wrap, HeatGauge gauge)
        {
            wrap.MouseEnter += delegate
            {
                HideTipsExcept(gauge);
                gauge.ShowTip(true);
            };
            wrap.MouseLeave += delegate { gauge.ShowTip(false); };
        }

        private void HideAllTips()
        {
            _cpu.ShowTip(false);
            _gpu.ShowTip(false);
            _ram.ShowTip(false);
            _net.ShowTip(false);
        }

        private void HideTipsExcept(HeatGauge keep)
        {
            if (keep != _cpu) _cpu.ShowTip(false);
            if (keep != _gpu) _gpu.ShowTip(false);
            if (keep != _ram) _ram.ShowTip(false);
            if (keep != _net) _net.ShowTip(false);
        }

        private void ApplyInterval()
        {
            _timer.Interval = TimeSpan.FromSeconds(_settings.IntervalSec);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RestorePosition();
            BuildTray();
            Dispatcher.BeginInvoke(new Action(delegate
            {
                KeepOnScreen();
                if (_sensors == null) _sensors = new SensorReader();
                RefreshAsync();
            }), DispatcherPriority.Background);
            _timer.Start();
        }

        private void RestorePosition()
        {
            if (_settings.HasPosition)
            {
                Left = _settings.X;
                Top = _settings.Y;
            }
            else
            {
                Snap("TR");
            }
        }

        private void KeepOnScreen()
        {
            UpdateLayout();
            double w = Math.Max(ActualWidth, 160);
            double h = Math.Max(ActualHeight, 80);
            var primary = Forms.Screen.PrimaryScreen.WorkingArea;
            if (Left > primary.Right - 96 && Left < primary.Right + 48)
            {
                Left = primary.Right - w - 28;
                Top = Math.Max(primary.Top + 20, Math.Min(Top, primary.Bottom - h - 20));
                SavePosition();
                return;
            }
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            var all = Forms.Screen.AllScreens;
            var box = new Drawing.Rectangle((int)Math.Round(Left), (int)Math.Round(Top), (int)Math.Ceiling(w), (int)Math.Ceiling(h));
            int best = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var hit = Drawing.Rectangle.Intersect(all[i].WorkingArea, box);
                int n = Math.Max(0, hit.Width) * Math.Max(0, hit.Height);
                if (n > best)
                {
                    best = n;
                    area = all[i].WorkingArea;
                }
            }
            if (best < 80 * 40)
            {
                Snap("TR");
                SavePosition();
                return;
            }
            double x = Left;
            double y = Top;
            if (x + w > area.Right) x = area.Right - w - 12;
            if (y + h > area.Bottom) y = area.Bottom - h - 12;
            if (x < area.Left) x = area.Left + 12;
            if (y < area.Top) y = area.Top + 12;
            Left = x;
            Top = y;
            SavePosition();
        }

        private void BuildTray()
        {
            _flyout = new TrayFlyout(this);
            _tray = new TrayHub(this, _flyout);
            _tray.Apply(_settings);
            ApplyDock();
        }

        private void ApplyDock()
        {
            if (_flyout == null) return;
            if (!_settings.TaskbarStrip)
            {
                if (_dock != null)
                {
                    _dock.Dispose();
                    _dock = null;
                }
                return;
            }
            if (_dock == null) _dock = new TaskbarDock(this, _flyout);
            _dock.Apply(_settings);
            if (_last != null) _dock.SetValues(_last, this);
        }

        private async Task RefreshAsync()
        {
            if (_sensors == null) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var snap = await Task.Run(new Func<Snapshot>(_sensors.Read)).ConfigureAwait(true);
                _last = snap;
                Paint(snap);
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void Paint(Snapshot snap)
        {
            PaintGauges(_cpu, _gpu, _ram, _net, snap);
            if (_flyout != null && _flyout.IsVisible) _flyout.Paint(snap, this);
            if (_dock != null) _dock.SetValues(snap, this);
            if (_tray != null)
            {
                _tray.SetTips(
                    "CPU  " + FormatTemp(snap.CpuTempC) + "  " + FormatPct(snap.CpuLoad),
                    "GPU  " + FormatTemp(snap.GpuTempC) + "  " + FormatPct(snap.GpuLoad),
                    "RAM  " + FormatRam(snap.RamUsedGb) + "  " + FormatPct(snap.RamLoad),
                    "ETH  ↓ " + FormatNet(snap.NetDownMBps) + "  ↑ " + FormatNet(snap.NetUpMBps));
            }
        }

        public void PaintGauges(HeatGauge cpu, HeatGauge gpu, HeatGauge ram, HeatGauge net, Snapshot snap)
        {
            cpu.Set(FormatTemp(snap.CpuTempC), FormatPct(snap.CpuLoad), HeatFromTemp(snap.CpuTempC, snap.CpuLoad), snap.CpuLoad, snap.CpuApp, _settings.CpuColor, _settings.ShowPercent);
            gpu.Set(FormatTemp(snap.GpuTempC), FormatPct(snap.GpuLoad), HeatFromTemp(snap.GpuTempC, snap.GpuLoad), snap.GpuLoad, snap.GpuApp, _settings.GpuColor, _settings.ShowPercent);
            if (_settings.ShowPercent)
                ram.Set(FormatPct(snap.RamLoad), FormatRam(snap.RamUsedGb), HeatFromLoad(snap.RamLoad), snap.RamLoad, snap.RamApp, _settings.RamColor, true);
            else
                ram.Set(FormatRam(snap.RamUsedGb), "", HeatFromLoad(snap.RamLoad), snap.RamLoad, snap.RamApp, _settings.RamColor, false);
            net.Set("↓ " + FormatNet(snap.NetDownMBps), "↑ " + FormatNet(snap.NetUpMBps), HeatFromLoad(snap.NetLoad), snap.NetLoad, snap.NetApp, _settings.NetColor, true);
            cpu.SetDetail(CpuDetail(snap), snap.CpuCores, _settings.CpuColor, _settings.CpuCoresView, null);
            gpu.SetDetail(null, null, _settings.GpuColor, null, GpuStats(snap));
            ram.SetDetail(null, null, _settings.RamColor, null, RamStats(snap));
            net.SetDetail(NetTitle(snap), null, _settings.NetColor, null, NetStats(snap));
        }

        public string DockText(int index, Snapshot snap)
        {
            if (snap == null) return "";
            switch (index)
            {
                case 0: return FormatPct(snap.CpuLoad);
                case 1: return FormatPct(snap.GpuLoad);
                case 2: return FormatPct(snap.RamLoad);
                default: return FormatNet(snap.NetDownMBps);
            }
        }

        private string FormatTemp(double? c)
        {
            if (!c.HasValue) return "—";
            double v = _settings.Fahrenheit ? (c.Value * 9.0 / 5.0) + 32.0 : c.Value;
            return string.Format(CultureInfo.InvariantCulture, "{0:0}°", Math.Round(v));
        }

        private static string FormatPct(double? pct)
        {
            return pct.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Round(pct.Value))
                : "—";
        }

        private static string FormatRam(double? gb)
        {
            return gb.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", gb.Value)
                : "—";
        }

        private static string FormatNet(double? mbps)
        {
            if (!mbps.HasValue) return "—";
            double bytesPerSec = Math.Max(0, mbps.Value) * 1000000.0;
            if (bytesPerSec < 1)
                return "0 B";
            if (bytesPerSec < 1000)
                return string.Format(CultureInfo.InvariantCulture, "{0:0} B", bytesPerSec);
            if (bytesPerSec < 1000000)
            {
                double kb = bytesPerSec / 1000.0;
                return kb < 10
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", kb)
                    : string.Format(CultureInfo.InvariantCulture, "{0:0} KB", kb);
            }
            double mb = bytesPerSec / 1000000.0;
            if (mb < 10)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", mb);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} MB", mb);
        }

        private static StatItem[] GpuStats(Snapshot snap)
        {
            var list = new List<StatItem>();
            if (snap.GpuLoad.HasValue)
                list.Add(new StatItem("Load", FormatPct(snap.GpuLoad), snap.GpuLoad.Value));
            if (snap.GpuMemUsedGb.HasValue && snap.GpuMemTotalGb.HasValue && snap.GpuMemTotalGb.Value > 0)
            {
                list.Add(new StatItem(
                    "Memory",
                    string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0} GB", snap.GpuMemUsedGb.Value, snap.GpuMemTotalGb.Value),
                    100.0 * snap.GpuMemUsedGb.Value / snap.GpuMemTotalGb.Value));
            }
            if (snap.GpuPowerW.HasValue)
            {
                double cap = snap.GpuPowerLimitW.HasValue && snap.GpuPowerLimitW.Value > 1
                    ? snap.GpuPowerLimitW.Value
                    : Math.Max(150, snap.GpuPowerW.Value);
                list.Add(new StatItem(
                    "Power",
                    string.Format(CultureInfo.InvariantCulture, "{0:0} W", snap.GpuPowerW.Value),
                    100.0 * snap.GpuPowerW.Value / cap));
            }
            if (snap.GpuClockMHz.HasValue)
            {
                double cap = snap.GpuMaxClockMHz.HasValue && snap.GpuMaxClockMHz.Value > 1
                    ? snap.GpuMaxClockMHz.Value
                    : Math.Max(2100, snap.GpuClockMHz.Value);
                list.Add(new StatItem(
                    "Clock",
                    string.Format(CultureInfo.InvariantCulture, "{0:0} MHz", snap.GpuClockMHz.Value),
                    100.0 * snap.GpuClockMHz.Value / cap));
            }
            return list.ToArray();
        }

        private static StatItem[] RamStats(Snapshot snap)
        {
            var list = new List<StatItem>();
            if (snap.RamUsedGb.HasValue && snap.RamTotalGb.HasValue)
            {
                list.Add(new StatItem(
                    "Used",
                    string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} GB", snap.RamUsedGb.Value, snap.RamTotalGb.Value),
                    snap.RamLoad ?? (100.0 * snap.RamUsedGb.Value / snap.RamTotalGb.Value)));
                double free = Math.Max(0, snap.RamTotalGb.Value - snap.RamUsedGb.Value);
                list.Add(new StatItem(
                    "Free",
                    string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", free),
                    100.0 * free / snap.RamTotalGb.Value));
            }
            else if (snap.RamLoad.HasValue)
            {
                list.Add(new StatItem("Used", FormatPct(snap.RamLoad), snap.RamLoad.Value));
            }
            return list.ToArray();
        }

        private static string NetTitle(Snapshot snap)
        {
            if (!string.IsNullOrEmpty(snap.NetName) && snap.NetLinkMbps.HasValue && snap.NetLinkMbps.Value > 0)
            {
                if (snap.NetLinkMbps.Value >= 1000)
                    return string.Format(CultureInfo.InvariantCulture, "{0}  {1:0.#} Gbps", snap.NetName, snap.NetLinkMbps.Value / 1000.0);
                return string.Format(CultureInfo.InvariantCulture, "{0}  {1:0} Mbps", snap.NetName, snap.NetLinkMbps.Value);
            }
            if (!string.IsNullOrEmpty(snap.NetName)) return snap.NetName;
            return "Ethernet";
        }

        private static StatItem[] NetStats(Snapshot snap)
        {
            var list = new List<StatItem>();
            double link = snap.NetLinkMbps.HasValue && snap.NetLinkMbps.Value > 0 ? snap.NetLinkMbps.Value : 1000;
            double downPct = 0;
            double upPct = 0;
            if (snap.NetDownMBps.HasValue)
                downPct = Math.Max(0, Math.Min(100, snap.NetDownMBps.Value * 8.0 / link * 100.0));
            if (snap.NetUpMBps.HasValue)
                upPct = Math.Max(0, Math.Min(100, snap.NetUpMBps.Value * 8.0 / link * 100.0));
            list.Add(new StatItem("Down", "↓ " + FormatNet(snap.NetDownMBps), downPct));
            list.Add(new StatItem("Up", "↑ " + FormatNet(snap.NetUpMBps), upPct));
            return list.ToArray();
        }

        private static string CpuDetail(Snapshot snap)
        {
            int n = snap.CpuCores != null ? snap.CpuCores.Length : 0;
            if (n <= 0) return FormatPct(snap.CpuLoad);
            return n + " cores";
        }

        private static double HeatFromTemp(double? tempC, double? load)
        {
            double fromTemp = 0;
            if (tempC.HasValue) fromTemp = (tempC.Value - 30.0) / 65.0;
            double fromLoad = load.HasValue ? load.Value / 100.0 : 0;
            return Clamp01(Math.Max(fromTemp, fromLoad * 0.72));
        }

        private static double HeatFromLoad(double? load)
        {
            return Clamp01((load ?? 0) / 100.0);
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private void SavePosition()
        {
            _settings.X = Left;
            _settings.Y = Top;
            _settings.HasPosition = true;
            _settings.Save();
        }

        private void ApplyToolWindowStyle()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(hwnd, GwlExStyle);
            Native.SetWindowLong(hwnd, GwlExStyle, ex | WsExToolwindow | WsExNoActivate);
        }

        private void Cleanup()
        {
            _timer.Stop();
            if (_customize != null) _customize.Close();
            if (_flyout != null) _flyout.Close();
            if (_dock != null) _dock.Dispose();
            if (_tray != null) _tray.Dispose();
            if (_sensors != null) _sensors.Dispose();
        }
    }

    internal sealed class HeatGauge
    {
        public StackPanel Root { get; private set; }

        private readonly Grid _stage = new Grid();
        private readonly Ellipse _bloom = new Ellipse();
        private readonly Ellipse _core = new Ellipse();
        private readonly Ellipse _track = new Ellipse();
        private readonly Ellipse _progress = new Ellipse();
        private readonly DropShadowEffect _glow = new DropShadowEffect();
        private readonly TextBlock _primary;
        private readonly TextBlock _secondary;
        private readonly TextBlock _label;
        private readonly TextBlock _app;
        private readonly Viewbox _icon;
        private readonly Grid _caption = new Grid();
        private readonly StackPanel _readout = new StackPanel();
        private readonly DetailView _pinned = new DetailView();
        private readonly RevealBox _reveal = new RevealBox();
        private readonly Border _detailCard = new Border();
        private readonly Border _divider = new Border();
        private bool _hoverOn;
        private bool _detailOpen;
        private double _size = 104;
        private double _stroke = 9;
        private double _scale = 1;
        private double _lastPct;

        public HeatGauge(string name)
        {
            _glow.RenderingBias = RenderingBias.Performance;
            _progress.Effect = _glow;
            _progress.StrokeStartLineCap = PenLineCap.Round;
            _progress.StrokeEndLineCap = PenLineCap.Round;
            _progress.StrokeDashCap = PenLineCap.Round;
            _progress.RenderTransformOrigin = new Point(0.5, 0.5);
            _progress.RenderTransform = new RotateTransform(-90);
            _progress.Fill = Brushes.Transparent;
            _track.Fill = Brushes.Transparent;
            _track.StrokeStartLineCap = PenLineCap.Round;
            _track.StrokeEndLineCap = PenLineCap.Round;

            _bloom.HorizontalAlignment = HorizontalAlignment.Center;
            _bloom.VerticalAlignment = VerticalAlignment.Center;
            _core.HorizontalAlignment = HorizontalAlignment.Center;
            _core.VerticalAlignment = VerticalAlignment.Center;
            _track.HorizontalAlignment = HorizontalAlignment.Center;
            _track.VerticalAlignment = VerticalAlignment.Center;
            _progress.HorizontalAlignment = HorizontalAlignment.Center;
            _progress.VerticalAlignment = VerticalAlignment.Center;
            _core.Fill = Theme.SoftCore();

            _primary = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _secondary = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Percent,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _label = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            _icon = HardwareIcons.Create(name);
            _app = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Normal,
                Foreground = Theme.Mute,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Hidden
            };

            _readout.HorizontalAlignment = HorizontalAlignment.Center;
            _readout.VerticalAlignment = VerticalAlignment.Center;
            _readout.Children.Add(_primary);
            _readout.Children.Add(_secondary);

            _stage.Children.Add(_bloom);
            _stage.Children.Add(_core);
            _stage.Children.Add(_track);
            _stage.Children.Add(_progress);
            _stage.Children.Add(_readout);
            _stage.HorizontalAlignment = HorizontalAlignment.Center;

            _caption.Children.Add(_icon);
            _caption.Children.Add(_label);

            _pinned.HorizontalAlignment = HorizontalAlignment.Stretch;
            _divider.Height = 1;
            _divider.Background = Theme.Hairline;
            _divider.HorizontalAlignment = HorizontalAlignment.Stretch;

            var detailStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            detailStack.Children.Add(_divider);
            detailStack.Children.Add(_pinned);
            _detailCard.Child = detailStack;
            _detailCard.Opacity = 0;
            _reveal.Child = _detailCard;

            Root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            Root.Children.Add(_stage);
            Root.Children.Add(_caption);
            Root.Children.Add(_app);
            Root.Children.Add(_reveal);
            ApplyScale(1, null);
        }

        public void ApplyScale(double s, SettingsStore settings)
        {
            double nameScale = settings != null ? settings.NameScale : 1.25;
            double percentScale = settings != null ? settings.PercentScale : 1.35;
            bool asIcon = settings != null && settings.NameAsIcon;

            _scale = s;
            _stroke = 9 * s;
            _primary.FontSize = 20 * s;
            _secondary.FontSize = 13.5 * s * percentScale;
            _secondary.Margin = new Thickness(0, 2 * s, 0, 0);
            _label.FontSize = 13.5 * s * nameScale;
            _stage.Margin = new Thickness(0, 8 * s, 0, 0);
            _caption.Margin = new Thickness(0, 10 * s, 0, 2 * s);
            _icon.Height = 16 * s * nameScale;
            _icon.Width = 16 * s * nameScale;
            _icon.Visibility = asIcon ? Visibility.Visible : Visibility.Collapsed;
            _label.Visibility = asIcon ? Visibility.Collapsed : Visibility.Visible;
            _app.FontSize = 10 * s;
            _app.Margin = new Thickness(0, 3 * s, 0, 0);
            _app.MinHeight = 12 * s;
            _divider.Margin = new Thickness(0, 0, 0, 9 * s);
            _detailCard.Margin = new Thickness(0, 9 * s, 0, 0);
            SizeRing();
        }

        public double RingSize { get { return _size; } }

        public void ShowTip(bool on)
        {
            _hoverOn = on;
            ShowDetail();
        }

        public void SetDetail(string text, double[] bars, string colorKey, string cpuView, StatItem[] stats)
        {
            _pinned.Update(text, bars, colorKey, cpuView, stats);
            ShowDetail();
        }

        private void ShowDetail()
        {
            bool on = _hoverOn && _pinned.HasContent;
            if (on == _detailOpen) return;
            _detailOpen = on;

            _reveal.BeginAnimation(RevealBox.RevealProperty, new DoubleAnimation
            {
                To = on ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(on ? 220 : 160)),
                EasingFunction = new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn }
            });
            _detailCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                To = on ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(on ? 190 : 110)),
                BeginTime = TimeSpan.FromMilliseconds(on ? 70 : 0)
            });
        }

        public void MatchRing(double size)
        {
            if (size <= _size) return;
            _size = size;
            ApplyRingGeometry();
        }

        private void SizeRing()
        {
            double w = Math.Max(MeasureString("↓ 999.9 MB", _primary.FontSize), MeasureString("99.9 GB", _primary.FontSize));
            double h = MeasureHeight(_primary.FontSize) + 2 * _scale + MeasureHeight(_secondary.FontSize);
            double pad = 12 + 6 * Math.Max(0.7, _scale);
            double inner = Math.Sqrt((w + pad * 2) * (w + pad * 2) + (h + pad * 2) * (h + pad * 2));
            double min = 104 * Math.Max(0.7, _scale);
            _size = Math.Max(min, inner + _stroke);
            ApplyRingGeometry();
        }

        private static double MeasureString(string text, double fontSize)
        {
            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                fontSize,
                Brushes.White);
            return Math.Max(ft.Width, text.Length * fontSize * 0.62);
        }

        private static double MeasureHeight(double fontSize)
        {
            return Math.Max(fontSize * 1.2, 8);
        }

        private void ApplyRingGeometry()
        {
            double stage = _size * 1.16;
            _stage.Width = stage;
            _stage.Height = stage;
            _track.Width = _size;
            _track.Height = _size;
            _track.StrokeThickness = _stroke;
            _progress.Width = _size;
            _progress.Height = _size;
            _progress.StrokeThickness = _stroke;
            _bloom.Width = stage;
            _bloom.Height = stage;
            _core.Width = Math.Max(8, _size - _stroke - 4);
            _core.Height = _core.Width;
            _app.MaxWidth = _size + 8 * _scale;
            PaintProgress(_lastPct);
        }

        private void PaintProgress(double pct)
        {
            _lastPct = pct;
            if (pct < 0.012)
            {
                _progress.Opacity = 0;
                return;
            }

            _progress.Opacity = 1;
            double radius = (_size - _stroke) / 2.0;
            double circ = 2.0 * Math.PI * radius;
            if (pct > 0.992)
            {
                _progress.StrokeDashArray = null;
            }
            else
            {
                double dash = (circ * pct) / _stroke;
                double gap = Math.Max(0.08, (circ * (1.0 - pct)) / _stroke);
                _progress.StrokeDashArray = new DoubleCollection { dash, gap };
            }
        }

        public void Set(string primary, string secondary, double heat, double? progressPct, string app, string colorKey, bool showPercent)
        {
            _primary.Text = primary;
            _secondary.Text = secondary;
            _secondary.Visibility = showPercent ? Visibility.Visible : Visibility.Collapsed;
            heat = Theme.Clamp01(heat);
            Color hot;
            Color mid;
            Color cool;
            if (string.IsNullOrEmpty(colorKey) || colorKey == "heat")
            {
                hot = Theme.Heat(heat);
                mid = Theme.Heat(Theme.Clamp01(heat * 0.62 + 0.12));
                cool = Theme.Heat(heat * 0.28);
            }
            else
            {
                Color accent = Theme.Accent(colorKey);
                cool = Theme.Mix(accent, Color.FromRgb(18, 18, 22), 0.45);
                mid = Theme.Mix(cool, accent, 0.55 + 0.2 * heat);
                hot = Theme.Mix(accent, Colors.White, 0.08 * heat);
            }

            _track.Stroke = Theme.Freeze(Color.FromArgb((byte)(42 + 58 * heat), hot.R, hot.G, hot.B));

            var bloom = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.44),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            double peak = 36 + 86 * heat;
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)peak, hot.R, hot.G, hot.B), 0));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.82), hot.R, hot.G, hot.B), 0.28));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.52), mid.R, mid.G, mid.B), 0.46));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.26), mid.R, mid.G, mid.B), 0.62));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.1), cool.R, cool.G, cool.B), 0.78));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.03), cool.R, cool.G, cool.B), 0.9));
            bloom.GradientStops.Add(new GradientStop(Color.FromArgb(0, cool.R, cool.G, cool.B), 1));
            bloom.Freeze();
            _bloom.Fill = bloom;

            _glow.Color = hot;
            _glow.BlurRadius = 10 + 16 * heat;
            _glow.ShadowDepth = 3 + 3 * heat;
            _glow.Direction = 245;
            _glow.Opacity = 0.32 + 0.48 * heat;

            var stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0.12, 0.02),
                EndPoint = new Point(0.96, 0.88)
            };
            stroke.GradientStops.Add(new GradientStop(cool, 0));
            stroke.GradientStops.Add(new GradientStop(mid, 0.46));
            stroke.GradientStops.Add(new GradientStop(hot, 1));
            stroke.Freeze();
            _progress.Stroke = stroke;
            _primary.Foreground = heat > 0.8 ? Theme.Freeze(hot) : Theme.Value;

            double pct = progressPct.HasValue ? Theme.Clamp01(progressPct.Value / 100.0) : 0;
            PaintProgress(pct);

            if (string.IsNullOrEmpty(app))
            {
                _app.Text = " ";
                _app.Visibility = Visibility.Hidden;
            }
            else
            {
                _app.Text = app;
                _app.Visibility = Visibility.Visible;
            }
        }
    }

    internal static class Log
    {
        public static void Write(string scope, Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMonitor");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("s") + "  " + scope + "  " + ex + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal sealed class StatItem
    {
        public string Name;
        public string Value;
        public double Pct;

        public StatItem(string name, string value, double pct)
        {
            Name = name;
            Value = value;
            Pct = pct;
        }
    }

    /// <summary>
    /// Reports a fraction of its child's height so an expanding card can be animated
    /// through the layout system, letting the window grow with it.
    /// </summary>
    internal sealed class RevealBox : Decorator
    {
        public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
            "Reveal",
            typeof(double),
            typeof(RevealBox),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public RevealBox()
        {
            ClipToBounds = true;
        }

        public double Reveal
        {
            get { return (double)GetValue(RevealProperty); }
            set { SetValue(RevealProperty, value); }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var child = Child;
            if (child == null) return new Size(0, 0);
            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
            Size want = child.DesiredSize;
            return new Size(want.Width, want.Height * Theme.Clamp01(Reveal));
        }

        protected override Size ArrangeOverride(Size size)
        {
            var child = Child;
            if (child != null)
            {
                child.Arrange(new Rect(0, 0, size.Width, Math.Max(size.Height, child.DesiredSize.Height)));
            }
            return size;
        }
    }

    internal sealed class DetailView : StackPanel
    {
        private readonly TextBlock _text;
        private readonly CoreBarsView _bars = new CoreBarsView();
        private readonly CoreRingsView _rings = new CoreRingsView();
        private readonly StatList _stats = new StatList();

        public bool HasContent { get; private set; }

        public DetailView()
        {
            Orientation = Orientation.Vertical;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            _text = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Percent,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Children.Add(_text);
            Children.Add(_bars);
            Children.Add(_rings);
            Children.Add(_stats);
        }

        public void Update(string text, double[] cores, string colorKey, string cpuView, StatItem[] stats)
        {
            bool hasText = !string.IsNullOrEmpty(text);
            _text.Text = hasText ? text : "";
            _text.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

            int n = cores == null ? 0 : cores.Length;
            bool rings = n > 0 && SettingsStore.NormalizeCpuView(cpuView) == "pie";
            bool bars = n > 0 && !rings;
            if (bars) _bars.SetCores(cores, colorKey);
            else _bars.Clear();
            if (rings) _rings.SetCores(cores, colorKey);
            else _rings.Clear();
            int statN = stats == null ? 0 : stats.Length;
            if (statN > 0) _stats.Set(stats, colorKey);
            else _stats.Clear();
            _bars.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
            _rings.Visibility = rings ? Visibility.Visible : Visibility.Collapsed;
            _stats.Visibility = statN > 0 ? Visibility.Visible : Visibility.Collapsed;
            HasContent = hasText || n > 0 || statN > 0;
        }
    }

    internal sealed class StatList : StackPanel
    {
        private readonly List<StatRow> _rows = new List<StatRow>();

        public StatList()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        public void Clear()
        {
            Children.Clear();
            _rows.Clear();
        }

        public void Set(StatItem[] items, string colorKey)
        {
            int n = items == null ? 0 : items.Length;
            while (_rows.Count < n)
            {
                var row = new StatRow();
                _rows.Add(row);
                Children.Add(row);
            }
            while (_rows.Count > n && _rows.Count > 0)
            {
                int last = _rows.Count - 1;
                Children.Remove(_rows[last]);
                _rows.RemoveAt(last);
            }
            for (int i = 0; i < n; i++) _rows[i].Set(items[i], colorKey);
        }
    }

    internal sealed class StatRow : StackPanel
    {
        private readonly TextBlock _name;
        private readonly TextBlock _value;
        private readonly Border _track;
        private readonly Border _fill;
        private double _load;

        public StatRow()
        {
            Orientation = Orientation.Vertical;
            Margin = new Thickness(0, 0, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 18 });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _name = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Mute,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _value = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Percent,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(_value, 1);
            head.Children.Add(_name);
            head.Children.Add(_value);
            _track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Theme.Freeze(48, 255, 255, 255),
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _fill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _track.Child = _fill;
            _track.SizeChanged += delegate { PaintFill(); };
            Children.Add(head);
            Children.Add(_track);
        }

        public void Set(StatItem item, string colorKey)
        {
            _load = Theme.Clamp01(item.Pct / 100.0);
            _name.Text = item.Name;
            _value.Text = item.Value;
            Color color = string.IsNullOrEmpty(colorKey) || colorKey == "heat"
                ? Theme.Heat(_load)
                : Theme.Mix(Theme.Accent(colorKey), Colors.White, 0.08 * _load);
            _fill.Background = Theme.Freeze(color);
            PaintFill();
        }

        private void PaintFill()
        {
            double w = _track.ActualWidth;
            if (w < 1) w = 80;
            _fill.Width = _load < 0.01 ? 0 : Math.Max(3, w * _load);
        }
    }

    internal sealed class CoreBarsView : UniformGrid
    {
        private readonly List<CoreBarCell> _cells = new List<CoreBarCell>();

        public CoreBarsView()
        {
            Columns = 3;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Margin = new Thickness(0, 0, 0, 2);
        }

        public void Clear()
        {
            Children.Clear();
            _cells.Clear();
        }

        public void SetCores(double[] cores, string colorKey)
        {
            int n = cores == null ? 0 : cores.Length;
            while (_cells.Count < n)
            {
                var cell = new CoreBarCell();
                _cells.Add(cell);
                Children.Add(cell);
            }
            while (_cells.Count > n && _cells.Count > 0)
            {
                int last = _cells.Count - 1;
                Children.Remove(_cells[last]);
                _cells.RemoveAt(last);
            }
            for (int i = 0; i < n; i++) _cells[i].Set(i + 1, cores[i], colorKey);
        }
    }

    internal sealed class CoreBarCell : StackPanel
    {
        private readonly TextBlock _index;
        private readonly Border _track;
        private readonly Border _fill;
        private readonly TextBlock _pct;
        private double _load;

        public CoreBarCell()
        {
            Orientation = Orientation.Vertical;
            Margin = new Thickness(3, 3, 3, 4);
            _index = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Mute,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _track = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = Theme.Freeze(48, 255, 255, 255),
                Margin = new Thickness(0, 3, 0, 2),
                ClipToBounds = true
            };
            _fill = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _track.Child = _fill;
            _track.SizeChanged += delegate { PaintFill(); };
            _pct = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Percent,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Children.Add(_index);
            Children.Add(_track);
            Children.Add(_pct);
        }

        public void Set(int index, double load, string colorKey)
        {
            _load = Theme.Clamp01(load / 100.0);
            _index.Text = index.ToString(CultureInfo.InvariantCulture);
            _pct.Text = string.Format(CultureInfo.InvariantCulture, "{0:0}%", Math.Round(load));
            Color color = string.IsNullOrEmpty(colorKey) || colorKey == "heat"
                ? Theme.Heat(_load)
                : Theme.Mix(Theme.Accent(colorKey), Colors.White, 0.08 * _load);
            _fill.Background = Theme.Freeze(color);
            PaintFill();
        }

        private void PaintFill()
        {
            double w = _track.ActualWidth;
            if (w < 1) w = 40;
            _fill.Width = _load < 0.01 ? 0 : Math.Max(3, w * _load);
        }
    }

    internal sealed class CoreRingsView : UniformGrid
    {
        private readonly List<CoreRingCell> _cells = new List<CoreRingCell>();

        public CoreRingsView()
        {
            Columns = 3;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Margin = new Thickness(0, 0, 0, 2);
        }

        public void Clear()
        {
            Children.Clear();
            _cells.Clear();
        }

        public void SetCores(double[] cores, string colorKey)
        {
            int n = cores == null ? 0 : cores.Length;
            while (_cells.Count < n)
            {
                var cell = new CoreRingCell();
                _cells.Add(cell);
                Children.Add(cell);
            }
            while (_cells.Count > n && _cells.Count > 0)
            {
                int last = _cells.Count - 1;
                Children.Remove(_cells[last]);
                _cells.RemoveAt(last);
            }
            for (int i = 0; i < n; i++) _cells[i].Set(i + 1, cores[i], colorKey);
        }
    }

    internal sealed class CoreRingCell : Grid
    {
        private const double Size = 36;
        private const double Stroke = 2.8;
        private readonly Ellipse _track = new Ellipse();
        private readonly Ellipse _arc = new Ellipse();
        private readonly TextBlock _num;

        public CoreRingCell()
        {
            Width = Size + 6;
            Height = Size + 6;
            Margin = new Thickness(2);
            _track.Width = Size;
            _track.Height = Size;
            _track.StrokeThickness = Stroke;
            _track.Fill = Brushes.Transparent;
            _track.HorizontalAlignment = HorizontalAlignment.Center;
            _track.VerticalAlignment = VerticalAlignment.Center;
            _arc.Width = Size;
            _arc.Height = Size;
            _arc.StrokeThickness = Stroke;
            _arc.Fill = Brushes.Transparent;
            _arc.StrokeStartLineCap = PenLineCap.Round;
            _arc.StrokeEndLineCap = PenLineCap.Round;
            _arc.StrokeDashCap = PenLineCap.Round;
            _arc.RenderTransformOrigin = new Point(0.5, 0.5);
            _arc.RenderTransform = new RotateTransform(-90);
            _arc.HorizontalAlignment = HorizontalAlignment.Center;
            _arc.VerticalAlignment = VerticalAlignment.Center;
            _num = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            Children.Add(_track);
            Children.Add(_arc);
            Children.Add(_num);
        }

        public void Set(int index, double load, string colorKey)
        {
            double t = Theme.Clamp01(load / 100.0);
            _num.Text = index.ToString(CultureInfo.InvariantCulture);
            Color color = string.IsNullOrEmpty(colorKey) || colorKey == "heat"
                ? Theme.Heat(Math.Max(0.06, t))
                : Theme.Accent(colorKey);
            _track.Stroke = Theme.Freeze(Color.FromArgb(48, color.R, color.G, color.B));
            _arc.Stroke = Theme.Freeze(color);
            double radius = (Size - Stroke) / 2.0;
            double circ = 2.0 * Math.PI * radius;
            if (t < 0.03)
            {
                _arc.Opacity = 0;
                return;
            }
            _arc.Opacity = 1;
            if (t > 0.97)
            {
                _arc.StrokeDashArray = null;
            }
            else
            {
                double dash = (circ * t) / Stroke;
                double gap = Math.Max(0.08, (circ * (1.0 - t)) / Stroke);
                _arc.StrokeDashArray = new DoubleCollection { dash, gap };
            }
        }
    }

    internal static class Theme
    {
        public static readonly Brush Card = Freeze(242, 14, 14, 16);
        public static readonly Brush Panel = Freeze(248, 17, 17, 20);
        public static readonly Brush Well = Freeze(16, 255, 255, 255);
        public static readonly Brush Field = Freeze(255, 26, 26, 31);
        public static readonly Brush Core = Freeze(236, 12, 12, 14);
        public static readonly Brush Stroke = Freeze(40, 255, 255, 255);
        public static readonly Brush Hairline = Freeze(28, 255, 255, 255);
        public static readonly Brush Label = Freeze(255, 220, 220, 224);
        public static readonly Brush Percent = Freeze(255, 244, 244, 247);
        public static readonly Brush Value = Freeze(255, 248, 248, 250);
        public static readonly Brush Warm = Freeze(255, 224, 176, 112);
        public static readonly Brush Hot = Freeze(255, 224, 112, 96);
        public static readonly Brush Mute = Freeze(255, 160, 160, 166);
        public static readonly Brush Hit = Freeze(1, 0, 0, 0);
        public static readonly Brush ChromeFill = Freeze(210, 22, 22, 26);
        public static readonly Color TrackTint = Color.FromArgb(255, 38, 38, 42);

        private static readonly Color[] HeatStops =
        {
            Color.FromRgb(0x2E, 0xE6, 0xC8),
            Color.FromRgb(0x4A, 0xDE, 0x80),
            Color.FromRgb(0xF5, 0xC5, 0x42),
            Color.FromRgb(0xFB, 0x8B, 0x3C),
            Color.FromRgb(0xF4, 0x3F, 0x5E)
        };

        private static readonly double[] HeatAt = { 0.0, 0.26, 0.52, 0.74, 1.0 };

        public static Brush SoftCore()
        {
            var fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(236, 12, 12, 14), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(236, 12, 12, 14), 0.82));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(150, 12, 12, 14), 0.94));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, 12, 12, 14), 1));
            fill.Freeze();
            return fill;
        }

        /// <summary>Card fill for a style, where a frosted backdrop needs a thinner tint to read through.</summary>
        public static Brush CardFill(double opacity, string style)
        {
            double weight = style == "glass" ? 0.34 : (style == "frost" ? 0.6 : 1.0);
            byte a = (byte)Math.Round(255 * Clamp01(opacity) * weight);
            return Freeze(Color.FromArgb(a, 14, 14, 16));
        }

        public static Brush Sheen(string style)
        {
            if (style == "solid") return null;
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0.1, 0),
                EndPoint = new Point(0.9, 1)
            };
            byte top = (byte)(style == "glass" ? 34 : 18);
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(top, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(top / 3), 255, 255, 255), 0.42));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.78));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(top / 4), 255, 255, 255), 1));
            sheen.Freeze();
            return sheen;
        }

        public static Brush CardEdge(string style)
        {
            return style == "solid" ? Stroke : Freeze(style == "glass" ? (byte)70 : (byte)54, 255, 255, 255);
        }

        public const double ShadowBlur = 26;
        public const double ShadowDepth = 7;

        public static DropShadowEffect CardShadow()
        {
            return new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = ShadowBlur,
                ShadowDepth = ShadowDepth,
                Direction = 270,
                Opacity = 0.46,
                RenderingBias = RenderingBias.Quality
            };
        }

        public static Thickness ShadowRoom()
        {
            double side = ShadowBlur * 0.5 + 4;
            return new Thickness(side, Math.Max(6, side - ShadowDepth), side, side + ShadowDepth);
        }

        public static Color Accent(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "teal": return Color.FromRgb(0x2E, 0xE6, 0xC8);
                case "green": return Color.FromRgb(0x4A, 0xDE, 0x80);
                case "amber": return Color.FromRgb(0xF5, 0xC5, 0x42);
                case "orange": return Color.FromRgb(0xFB, 0x8B, 0x3C);
                case "rose": return Color.FromRgb(0xF4, 0x3F, 0x5E);
                case "blue": return Color.FromRgb(0x5B, 0xB0, 0xFF);
                case "violet": return Color.FromRgb(0xA7, 0x8B, 0xFA);
                default: return Heat(0.55);
            }
        }

        public static Brush Swatch(string key)
        {
            if (key == "heat")
            {
                var g = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                g.GradientStops.Add(new GradientStop(HeatStops[0], 0));
                g.GradientStops.Add(new GradientStop(HeatStops[2], 0.5));
                g.GradientStops.Add(new GradientStop(HeatStops[4], 1));
                g.Freeze();
                return g;
            }
            return Freeze(Accent(key));
        }

        public static string SwatchName(string key)
        {
            if (key == "heat") return "Heat";
            if (string.IsNullOrEmpty(key)) return "Heat";
            return char.ToUpperInvariant(key[0]) + key.Substring(1);
        }

        public static Color Heat(double t)
        {
            t = Clamp01(t);
            for (int i = 1; i < HeatAt.Length; i++)
            {
                if (t <= HeatAt[i])
                {
                    double span = HeatAt[i] - HeatAt[i - 1];
                    double local = span <= 0 ? 0 : (t - HeatAt[i - 1]) / span;
                    return Lerp(HeatStops[i - 1], HeatStops[i], local);
                }
            }
            return HeatStops[HeatStops.Length - 1];
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Clamp01(t);
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        public static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
        {
            return Freeze(Color.FromArgb(a, r, g, b));
        }

        public static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }
    }

    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "DeskMonitor";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key != null && key.GetValue(Name) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                if (enabled)
                {
                    var exe = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(Name, '"' + exe + '"');
                }
                else
                {
                    key.DeleteValue(Name, false);
                }
            }
        }
    }
}
