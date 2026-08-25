using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DeskMonitor
{
    internal sealed class CustomizeWindow : Window
    {
        private readonly OverlayWindow _overlay;
        private readonly SettingsStore _s;
        private readonly Slider _opacity;
        private readonly Slider _widgetSize;
        private readonly Slider _nameSize;
        private readonly Slider _percentSize;
        private readonly CheckBox _cpu;
        private readonly CheckBox _gpu;
        private readonly CheckBox _ram;
        private readonly CheckBox _net;
        private readonly CheckBox _topmost;
        private readonly CheckBox _startup;
        private readonly CheckBox _fahrenheit;
        private readonly CheckBox _nameAsIcon;
        private readonly CheckBox _showPercent;
        private readonly CheckBox _separate;
        private readonly CheckBox _taskbar;
        private readonly Slider _cardOpacity;
        private readonly Slider _grain;
        private readonly Picker _cpuView;
        private readonly Picker _cardStyle;
        private readonly ComboBox _interval;
        private readonly WrapPanel _cpuColors;
        private readonly WrapPanel _gpuColors;
        private readonly WrapPanel _ramColors;
        private readonly WrapPanel _netColors;
        private Button _close;
        private bool _ready;
        private bool _followHost = true;

        public CustomizeWindow(OverlayWindow overlay)
        {
            _overlay = overlay;
            _s = overlay.Settings;

            Title = "Settings";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            FontWeight = FontWeights.Normal;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add(UiChrome.Styles);

            _opacity = MakeSlider(45, 100, _s.Opacity * 100);
            _widgetSize = MakeSlider(70, 185, _s.Scale * 100);
            _nameSize = MakeSlider(80, 180, _s.NameScale * 100);
            _percentSize = MakeSlider(80, 180, _s.PercentScale * 100);
            _cpu = MakeCheck("CPU", _s.ShowCpu);
            _gpu = MakeCheck("GPU", _s.ShowGpu);
            _ram = MakeCheck("RAM", _s.ShowRam);
            _net = MakeCheck("Ethernet", _s.ShowNet);
            _topmost = MakeCheck("Always on top", _s.Topmost);
            _startup = MakeCheck("Start with Windows", Startup.IsEnabled());
            _fahrenheit = MakeCheck("Use Fahrenheit", _s.Fahrenheit);
            _nameAsIcon = MakeCheck("Names as icons", _s.NameAsIcon);
            _showPercent = MakeCheck("Show percentages", _s.ShowPercent);
            _separate = MakeCheck("Separate cards", _s.SeparateCharts);
            _taskbar = MakeCheck("Show monitors on the taskbar", _s.TaskbarStrip);
            _cardOpacity = MakeSlider(15, 100, _s.CardOpacity * 100);
            _grain = MakeSlider(0, 40, _s.Grain * 100);
            _cpuView = new Picker(new[] { "Bars", "Rings" }, CpuViewToIndex(_s.CpuCoresView), Push);
            _cardStyle = new Picker(new[] { "Solid", "Frosted", "Glass" }, CardStyleToIndex(_s.CardStyle), Push);
            _interval = MakeCombo(new[] { "1 second", "2 seconds", "3 seconds" }, IntervalToIndex(_s.IntervalSec));
            _cpuColors = MakeSwatches(_s.CpuColor);
            _gpuColors = MakeSwatches(_s.GpuColor);
            _ramColors = MakeSwatches(_s.RamColor);
            _netColors = MakeSwatches(_s.NetColor);

            Content = BuildUi();
            _ready = true;

            PreviewMouseLeftButtonDown += OnChromeDrag;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();
            };
            Loaded += delegate { DockTo(_overlay); };
        }

        public void DockTo(Window host)
        {
            if (!_followHost || !IsLoaded || host == null) return;
            UpdateLayout();
            var work = SystemParameters.WorkArea;
            double width = ActualWidth > 1 ? ActualWidth : 416;
            double height = ActualHeight > 1 ? ActualHeight : 520;
            double left = host.Left + (host.ActualWidth - width) / 2.0;
            left = Math.Max(work.Left + 8, Math.Min(left, work.Right - width - 8));
            double top = host.Top + host.ActualHeight + 2;
            if (top + height > work.Bottom - 8)
                top = Math.Max(work.Top + 8, work.Bottom - height - 8);
            Left = left;
            Top = top;
        }

        private void OnChromeDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
            if (!CanDragFrom(e.OriginalSource as DependencyObject)) return;
            _followHost = false;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private bool CanDragFrom(DependencyObject src)
        {
            while (src != null)
            {
                if (src == _close) return false;
                if (src is Button || src is Slider || src is CheckBox || src is ComboBox
                    || src is ScrollBar || src is Thumb || src is ComboBoxItem)
                {
                    return false;
                }
                src = VisualTreeHelper.GetParent(src);
            }
            return true;
        }

        public void Pull()
        {
            _ready = false;
            _opacity.Value = _s.Opacity * 100;
            _widgetSize.Value = _s.Scale * 100;
            _nameSize.Value = _s.NameScale * 100;
            _percentSize.Value = _s.PercentScale * 100;
            _cpu.IsChecked = _s.ShowCpu;
            _gpu.IsChecked = _s.ShowGpu;
            _ram.IsChecked = _s.ShowRam;
            _net.IsChecked = _s.ShowNet;
            _topmost.IsChecked = _s.Topmost;
            _startup.IsChecked = Startup.IsEnabled();
            _fahrenheit.IsChecked = _s.Fahrenheit;
            _nameAsIcon.IsChecked = _s.NameAsIcon;
            _showPercent.IsChecked = _s.ShowPercent;
            _separate.IsChecked = _s.SeparateCharts;
            _taskbar.IsChecked = _s.TaskbarStrip;
            _cardOpacity.Value = _s.CardOpacity * 100;
            _grain.Value = _s.Grain * 100;
            _cpuView.Set(CpuViewToIndex(_s.CpuCoresView));
            _cardStyle.Set(CardStyleToIndex(_s.CardStyle));
            _interval.SelectedIndex = IntervalToIndex(_s.IntervalSec);
            MarkSwatches(_cpuColors, _s.CpuColor);
            MarkSwatches(_gpuColors, _s.GpuColor);
            MarkSwatches(_ramColors, _s.RamColor);
            MarkSwatches(_netColors, _s.NetColor);
            _ready = true;
        }

        private UIElement BuildUi()
        {
            var body = new StackPanel();
            body.Children.Add(Group("Widget",
                TwoCol(Field("Size", _widgetSize), Field("Opacity", _opacity)),
                _separate));
            body.Children.Add(Group("Card look",
                TwoCol(Field("Card opacity", _cardOpacity), Field("Grain", _grain)),
                Field("Background", _cardStyle.Row),
                Hint("Frosted and Glass blur the wallpaper behind each card. Card opacity tints the fill on top of it.")));
            body.Children.Add(Group("CPU cores",
                _cpuView.Row,
                Hint("Rings are circular gauges with the core number inside. Bars sit three across.")));
            body.Children.Add(Group("Names & values",
                TwoCol(Field("Name size", _nameSize), Field("Percent size", _percentSize)),
                Checks(_nameAsIcon, _showPercent)));
            body.Children.Add(Group("Cards",
                Checks(_cpu, _gpu, _ram, _net)));
            body.Children.Add(Group("Colors",
                ColorRow("CPU", _cpuColors),
                ColorRow("GPU", _gpuColors),
                ColorRow("RAM", _ramColors),
                ColorRow("ETH", _netColors)));
            body.Children.Add(Group("Taskbar",
                Checks(_taskbar),
                Hint("When on, CPU, GPU, RAM, and Ethernet sit on the taskbar with live values in front of each icon. Turn it off to hide them.")));
            body.Children.Add(Group("Placement",
                SnapRow(),
                TwoCol(Field("Refresh", _interval), new Border()),
                Checks(_fahrenheit),
                Checks(_topmost, _startup)));
            body.Children.Add(Footer());

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(420, Math.Min(760, SystemParameters.WorkArea.Height - 160)),
                PanningMode = PanningMode.VerticalOnly,
                Style = UiChrome.Get("ThinScroll")
            };

            var shell = new StackPanel();
            shell.Children.Add(Header());
            shell.Children.Add(scroll);

            return new Border
            {
                Width = 380,
                Margin = new Thickness(18, 14, 18, 26),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(16, 14, 12, 14),
                Background = Theme.Panel,
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(1),
                Child = shell,
                Effect = Theme.CardShadow()
            };
        }

        private UIElement Header()
        {
            var title = new TextBlock
            {
                Text = "Settings",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                VerticalAlignment = VerticalAlignment.Center
            };
            _close = new Button
            {
                Content = "Close",
                FontSize = 12,
                Foreground = Theme.Mute,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(10, 5, 10, 5),
                Style = UiChrome.Get("Chip")
            };
            _close.Click += delegate { Close(); };

            var grid = new Grid { Margin = new Thickness(2, 0, 6, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_close, 1);
            grid.Children.Add(title);
            grid.Children.Add(_close);

            return new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Child = grid
            };
        }

        private UIElement Footer()
        {
            var admin = new Button
            {
                Content = "Restart as administrator",
                FontSize = 12,
                Foreground = Theme.Value,
                Background = Theme.Field,
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 6, 0),
                Style = UiChrome.Get("Chip")
            };
            admin.Click += delegate { _overlay.RestartElevated(); };

            var hint = new TextBlock
            {
                Text = "Hover a card for extra stats. CPU temperature needs administrator once.\n" + AppInfo.Name + " " + AppInfo.Version,
                FontSize = 11,
                Foreground = Theme.Mute,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 10, 6, 2)
            };

            var stack = new StackPanel();
            stack.Children.Add(admin);
            stack.Children.Add(hint);
            return stack;
        }

        private UIElement SnapRow()
        {
            var row = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(SnapButton("TL", "Top left"));
            row.Children.Add(SnapButton("TR", "Top right"));
            row.Children.Add(SnapButton("BL", "Bottom left"));
            row.Children.Add(SnapButton("BR", "Bottom right"));
            return row;
        }

        private Button SnapButton(string code, string tip)
        {
            var b = new Button
            {
                Content = code,
                ToolTip = tip,
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Value,
                Background = Theme.Field,
                BorderBrush = Theme.Stroke,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand,
                Style = UiChrome.Get("Chip")
            };
            b.Click += delegate
            {
                _overlay.Snap(code);
                DockTo(_overlay);
            };
            return b;
        }

        private static UIElement Group(string title, params UIElement[] rows)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Mute,
                Margin = new Thickness(2, 0, 0, 10)
            });
            for (int i = 0; i < rows.Length; i++) stack.Children.Add(rows[i]);

            return new Border
            {
                Background = Theme.Well,
                BorderBrush = Theme.Hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 11, 12, 8),
                Margin = new Thickness(2, 0, 6, 10),
                Child = stack
            };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = Theme.Mute,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 0, 4)
            };
        }

        private static UIElement TwoCol(UIElement a, UIElement b)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(b, 2);
            grid.Children.Add(a);
            grid.Children.Add(b);
            return grid;
        }

        private static UIElement Field(string name, UIElement control)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = Theme.Mute,
                Margin = new Thickness(2, 0, 0, 5)
            });
            stack.Children.Add(control);
            return stack;
        }

        private static UIElement Checks(params CheckBox[] boxes)
        {
            var grid = new UniformGrid
            {
                Columns = boxes.Length >= 3 ? 2 : boxes.Length,
                Margin = new Thickness(0, 0, 0, 2)
            };
            foreach (var box in boxes) grid.Children.Add(box);
            return grid;
        }

        private static UIElement ColorRow(string name, WrapPanel swatches)
        {
            swatches.Margin = new Thickness(0);
            swatches.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = Theme.Mute,
                Width = 34,
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new DockPanel { Margin = new Thickness(2, 0, 0, 9), LastChildFill = true };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(swatches);
            return row;
        }

        private Slider MakeSlider(double min, double max, double value)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                VerticalAlignment = VerticalAlignment.Center,
                Style = UiChrome.Get("Slim")
            };
            slider.ValueChanged += delegate { Push(); };
            return slider;
        }

        private CheckBox MakeCheck(string text, bool on)
        {
            var box = new CheckBox
            {
                Content = text,
                IsChecked = on,
                FontSize = 12,
                Foreground = Theme.Value,
                Margin = new Thickness(2, 4, 8, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Style = UiChrome.Get("Check")
            };
            box.Click += delegate { Push(); };
            return box;
        }

        private ComboBox MakeCombo(string[] items, int selected)
        {
            var box = new ComboBox { Style = UiChrome.Get("Combo") };
            foreach (var item in items) box.Items.Add(item);
            box.SelectedIndex = selected;
            box.SelectionChanged += delegate { Push(); };
            box.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (box.IsDropDownOpen) return;
                e.Handled = true;
                var parent = VisualTreeHelper.GetParent(box) as UIElement;
                if (parent == null) return;
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = box
                });
            };
            return box;
        }

        private WrapPanel MakeSwatches(string selected)
        {
            var panel = new WrapPanel();
            string[] keys = { "heat", "teal", "green", "amber", "orange", "rose", "blue", "violet" };
            for (int i = 0; i < keys.Length; i++)
            {
                string keyLocal = keys[i];
                var dot = new Ellipse
                {
                    Width = 17,
                    Height = 17,
                    Fill = Theme.Swatch(keyLocal),
                    Margin = new Thickness(0, 0, 7, 0),
                    Cursor = Cursors.Hand,
                    Stroke = Theme.Value,
                    Tag = keyLocal,
                    ToolTip = Theme.SwatchName(keyLocal)
                };
                dot.MouseLeftButtonUp += delegate
                {
                    MarkSwatches(panel, keyLocal);
                    Push();
                };
                panel.Children.Add(dot);
            }
            MarkSwatches(panel, selected);
            return panel;
        }

        private static void MarkSwatches(WrapPanel panel, string selected)
        {
            foreach (var child in panel.Children)
            {
                var dot = child as Ellipse;
                if (dot == null) continue;
                bool on = string.Equals(dot.Tag as string, selected, StringComparison.OrdinalIgnoreCase);
                dot.StrokeThickness = on ? 2 : 0;
            }
        }

        private static string SelectedSwatch(WrapPanel panel)
        {
            foreach (var child in panel.Children)
            {
                var dot = child as Ellipse;
                if (dot != null && dot.StrokeThickness > 1) return dot.Tag as string;
            }
            return "heat";
        }

        private void Push()
        {
            if (!_ready) return;
            _s.Opacity = _opacity.Value / 100.0;
            _s.Scale = SettingsStore.Clamp(_widgetSize.Value / 100.0, 0.7, 1.85);
            _s.NameScale = SettingsStore.Clamp(_nameSize.Value / 100.0, 0.8, 1.8);
            _s.PercentScale = SettingsStore.Clamp(_percentSize.Value / 100.0, 0.8, 1.8);
            _s.ShowCpu = _cpu.IsChecked == true;
            _s.ShowGpu = _gpu.IsChecked == true;
            _s.ShowRam = _ram.IsChecked == true;
            _s.ShowNet = _net.IsChecked == true;
            if (!_s.ShowCpu && !_s.ShowGpu && !_s.ShowRam && !_s.ShowNet)
            {
                _s.ShowCpu = true;
                _cpu.IsChecked = true;
            }
            _s.Topmost = _topmost.IsChecked == true;
            _s.Fahrenheit = _fahrenheit.IsChecked == true;
            _s.NameAsIcon = _nameAsIcon.IsChecked == true;
            _s.ShowPercent = _showPercent.IsChecked == true;
            _s.SeparateCharts = _separate.IsChecked == true;
            _s.TaskbarStrip = _taskbar.IsChecked == true;
            _s.CardOpacity = SettingsStore.Clamp(_cardOpacity.Value / 100.0, 0.15, 1.0);
            _s.Grain = SettingsStore.Clamp(_grain.Value / 100.0, 0, 1);
            _s.CardStyle = IndexToCardStyle(_cardStyle.Index);
            _s.CpuCoresView = IndexToCpuView(_cpuView.Index);
            _s.CpuColor = SelectedSwatch(_cpuColors);
            _s.GpuColor = SelectedSwatch(_gpuColors);
            _s.RamColor = SelectedSwatch(_ramColors);
            _s.NetColor = SelectedSwatch(_netColors);
            _s.IntervalSec = _interval.SelectedIndex + 1;
            if (_s.IntervalSec < 1) _s.IntervalSec = 1;
            Startup.SetEnabled(_startup.IsChecked == true);
            _overlay.ApplyFromSettings();
        }

        private static int IntervalToIndex(int sec)
        {
            if (sec <= 1) return 0;
            if (sec == 2) return 1;
            return 2;
        }

        private static int CpuViewToIndex(string view)
        {
            return SettingsStore.NormalizeCpuView(view) == "pie" ? 1 : 0;
        }

        private static string IndexToCpuView(int index)
        {
            return index == 1 ? "pie" : "bars";
        }

        private static int CardStyleToIndex(string style)
        {
            switch (SettingsStore.NormalizeCardStyle(style))
            {
                case "frost": return 1;
                case "glass": return 2;
                default: return 0;
            }
        }

        private static string IndexToCardStyle(int index)
        {
            switch (index)
            {
                case 1: return "frost";
                case 2: return "glass";
                default: return "solid";
            }
        }
    }

    /// <summary>A row of chips where exactly one is selected.</summary>
    internal sealed class Picker
    {
        private readonly Button[] _buttons;
        private readonly Action _changed;

        public UIElement Row { get; private set; }
        public int Index { get; private set; }

        public Picker(string[] labels, int index, Action changed)
        {
            _changed = changed;
            Index = index;
            _buttons = new Button[labels.Length];
            var row = new UniformGrid { Columns = labels.Length };
            for (int i = 0; i < labels.Length; i++)
            {
                int slot = i;
                var b = new Button
                {
                    Content = labels[i],
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0, 8, 0, 8),
                    Margin = new Thickness(0, 0, i == labels.Length - 1 ? 0 : 6, 0),
                    Cursor = Cursors.Hand,
                    Style = UiChrome.Get("Chip")
                };
                b.Click += delegate
                {
                    Index = slot;
                    Mark();
                    _changed();
                };
                _buttons[i] = b;
                row.Children.Add(b);
            }
            Row = row;
            Mark();
        }

        public void Set(int index)
        {
            Index = index;
            Mark();
        }

        private void Mark()
        {
            for (int i = 0; i < _buttons.Length; i++)
            {
                bool on = i == Index;
                _buttons[i].Background = on ? Theme.Field : Brushes.Transparent;
                _buttons[i].BorderBrush = on ? Theme.Value : Theme.Stroke;
                _buttons[i].Foreground = on ? Theme.Value : Theme.Mute;
            }
        }
    }

    internal static class UiChrome
    {
        private static ResourceDictionary _styles;

        public static ResourceDictionary Styles
        {
            get
            {
                if (_styles == null) _styles = Load();
                return _styles;
            }
        }

        public static Style Get(string key)
        {
            object found = Styles.Contains(key) ? Styles[key] : null;
            return found as Style;
        }

        private static ResourceDictionary Load()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("DeskMonitor.theme.xaml"))
                {
                    if (stream != null)
                    {
                        var dict = XamlReader.Load(stream) as ResourceDictionary;
                        if (dict != null) return dict;
                    }
                }
            }
            catch
            {
            }
            return new ResourceDictionary();
        }
    }
}
