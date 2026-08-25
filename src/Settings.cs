using System;
using System.Globalization;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;

namespace DeskMonitor
{
    internal sealed class SettingsStore
    {
        public double X;
        public double Y;
        public bool HasPosition;
        public bool Topmost = true;
        public double Opacity = 0.94;
        public double Scale = 1.0;
        public bool ShowCpu = true;
        public bool ShowGpu = true;
        public bool ShowRam = true;
        public bool ShowNet = true;
        public bool Fahrenheit;
        public int IntervalSec = 1;
        public double NameScale = 1.25;
        public double PercentScale = 1.35;
        public bool NameAsIcon;
        public bool ShowPercent = true;
        public bool SeparateCharts = true;
        public bool AdvancedMode;
        public double CardOpacity = 0.95;
        public string CardStyle = "solid";
        public double Grain;
        public bool TaskbarStrip;
        public string CpuCoresView = "bars";
        public string CpuColor = "heat";
        public string GpuColor = "heat";
        public string RamColor = "heat";
        public string NetColor = "teal";

        private static string PathName
        {
            get
            {
                // DESKMONITOR_DATA lets a second copy run against its own config,
                // which keeps preview builds from fighting the installed one.
                string home = Environment.GetEnvironmentVariable("DESKMONITOR_DATA");
                if (string.IsNullOrEmpty(home))
                {
                    home = IOPath.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DeskMonitor");
                }
                return IOPath.Combine(home, "settings.ini");
            }
        }

        public static SettingsStore Load()
        {
            var s = new SettingsStore();
            try
            {
                if (!File.Exists(PathName)) return s;
                foreach (var line in File.ReadAllLines(PathName))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    if (key == "X") { s.X = ParseD(val); s.HasPosition = true; }
                    else if (key == "Y") { s.Y = ParseD(val); s.HasPosition = true; }
                    else if (key == "Topmost") s.Topmost = IsTrue(val);
                    else if (key == "Opacity") s.Opacity = Clamp(ParseD(val), 0.45, 1.0);
                    else if (key == "Scale") s.Scale = Clamp(ParseD(val), 0.7, 1.85);
                    else if (key == "ShowCpu") s.ShowCpu = IsTrue(val);
                    else if (key == "ShowGpu") s.ShowGpu = IsTrue(val);
                    else if (key == "ShowRam") s.ShowRam = IsTrue(val);
                    else if (key == "ShowNet") s.ShowNet = IsTrue(val);
                    else if (key == "Fahrenheit") s.Fahrenheit = IsTrue(val);
                    else if (key == "NameScale") s.NameScale = Clamp(ParseD(val), 0.8, 1.8);
                    else if (key == "PercentScale") s.PercentScale = Clamp(ParseD(val), 0.8, 1.8);
                    else if (key == "NameAsIcon") s.NameAsIcon = IsTrue(val);
                    else if (key == "ShowPercent") s.ShowPercent = IsTrue(val);
                    else if (key == "SeparateCharts") s.SeparateCharts = IsTrue(val);
                    else if (key == "AdvancedMode") s.AdvancedMode = IsTrue(val);
                    else if (key == "CardOpacity") s.CardOpacity = Clamp(ParseD(val), 0.15, 1.0);
                    else if (key == "CardStyle") s.CardStyle = NormalizeCardStyle(val);
                    else if (key == "Grain") s.Grain = Clamp(ParseD(val), 0, 1);
                    else if (key == "TaskbarStrip") s.TaskbarStrip = IsTrue(val);
                    else if (key == "CpuCoresView") s.CpuCoresView = NormalizeCpuView(val);
                    else if (key == "CpuColor") s.CpuColor = NormalizeColor(val);
                    else if (key == "GpuColor") s.GpuColor = NormalizeColor(val);
                    else if (key == "RamColor") s.RamColor = NormalizeColor(val);
                    else if (key == "NetColor") s.NetColor = NormalizeColor(val);
                    else if (key == "IntervalSec")
                    {
                        int n;
                        if (int.TryParse(val, out n)) s.IntervalSec = Math.Max(1, Math.Min(5, n));
                    }
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            try
            {
                var dir = IOPath.GetDirectoryName(PathName);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                Put(sb, "X", X);
                Put(sb, "Y", Y);
                Put(sb, "Topmost", Topmost);
                Put(sb, "Opacity", Opacity);
                Put(sb, "Scale", Scale);
                Put(sb, "ShowCpu", ShowCpu);
                Put(sb, "ShowGpu", ShowGpu);
                Put(sb, "ShowRam", ShowRam);
                Put(sb, "ShowNet", ShowNet);
                Put(sb, "Fahrenheit", Fahrenheit);
                Put(sb, "IntervalSec", IntervalSec.ToString(CultureInfo.InvariantCulture));
                Put(sb, "NameScale", NameScale);
                Put(sb, "PercentScale", PercentScale);
                Put(sb, "NameAsIcon", NameAsIcon);
                Put(sb, "ShowPercent", ShowPercent);
                Put(sb, "SeparateCharts", SeparateCharts);
                Put(sb, "AdvancedMode", AdvancedMode);
                Put(sb, "CardOpacity", CardOpacity);
                Put(sb, "CardStyle", CardStyle);
                Put(sb, "Grain", Grain);
                Put(sb, "TaskbarStrip", TaskbarStrip);
                Put(sb, "CpuColor", CpuColor);
                Put(sb, "GpuColor", GpuColor);
                Put(sb, "RamColor", RamColor);
                Put(sb, "NetColor", NetColor);
                Put(sb, "CpuCoresView", CpuCoresView);
                File.WriteAllText(PathName, sb.ToString());
            }
            catch
            {
            }
        }

        private static void Put(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(value).Append('\n');
        }

        private static void Put(StringBuilder sb, string key, double value)
        {
            Put(sb, key, value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Put(StringBuilder sb, string key, bool value)
        {
            Put(sb, key, value ? "1" : "0");
        }

        private static bool IsTrue(string val)
        {
            return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static double ParseD(string val)
        {
            return double.Parse(val, CultureInfo.InvariantCulture);
        }

        public static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static string NormalizeColor(string val)
        {
            if (string.IsNullOrEmpty(val)) return "heat";
            val = val.Trim().ToLowerInvariant();
            if (val == "heat" || val == "teal" || val == "green" || val == "amber" ||
                val == "orange" || val == "rose" || val == "blue" || val == "violet")
            {
                return val;
            }
            return "heat";
        }

        public static string NormalizeCardStyle(string val)
        {
            if (string.IsNullOrEmpty(val)) return "solid";
            val = val.Trim().ToLowerInvariant();
            if (val == "frost" || val == "glass") return val;
            return "solid";
        }

        public static string NormalizeCpuView(string val)
        {
            if (string.IsNullOrEmpty(val)) return "bars";
            val = val.Trim().ToLowerInvariant();
            if (val == "pie" || val == "piechart" || val == "chart") return "pie";
            return "bars";
        }
    }
}
