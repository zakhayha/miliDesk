using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using IOPath = System.IO.Path;

namespace DeskMonitor
{
    internal sealed class Snapshot
    {
        public double? CpuTempC;
        public double? CpuLoad;
        public double? GpuTempC;
        public double? GpuLoad;
        public double? RamUsedGb;
        public double? RamLoad;
        public double? NetDownMBps;
        public double? NetUpMBps;
        public double? NetLoad;
        public double? RamTotalGb;
        public double? GpuMemUsedGb;
        public double? GpuMemTotalGb;
        public double? GpuPowerW;
        public double? GpuClockMHz;
        public double? GpuMemClockMHz;
        public double? GpuPowerLimitW;
        public double? GpuMaxClockMHz;
        public double? NetLinkMbps;
        public string NetName;
        public double[] CpuCores;
        public string CpuApp;
        public string GpuApp;
        public string RamApp;
        public string NetApp;
    }

    /// <summary>
    /// Temperature sensors report null on the odd poll while the driver refreshes.
    /// Reusing the previous reading for a short window keeps the gauge from blinking.
    /// </summary>
    internal sealed class StickyValue
    {
        private readonly TimeSpan _hold;
        private double? _last;
        private DateTime _stamp;

        public StickyValue(TimeSpan hold)
        {
            _hold = hold;
        }

        public double? Filter(double? fresh)
        {
            if (fresh.HasValue)
            {
                _last = fresh;
                _stamp = DateTime.UtcNow;
                return fresh;
            }
            if (_last.HasValue && DateTime.UtcNow - _stamp <= _hold) return _last;
            _last = null;
            return null;
        }
    }

    internal sealed class SensorReader : IDisposable
    {
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;
        private bool _cpuInit;
        private long _prevNetIn;
        private long _prevNetOut;
        private long _prevNetTicks;
        private string _prevNetId = "";
        private bool _netInit;
        private readonly NvmlReader _nvml = new NvmlReader();
        private readonly LhmReader _lhm = new LhmReader();
        private readonly TopAppFinder _apps = new TopAppFinder();
        private readonly CoreSampler _cores = new CoreSampler();
        private readonly StickyValue _cpuTempHold = new StickyValue(TimeSpan.FromSeconds(20));
        private readonly StickyValue _gpuTempHold = new StickyValue(TimeSpan.FromSeconds(20));

        public bool CpuTempAvailable
        {
            get { return _lhm.Ready; }
        }

        public Snapshot Read()
        {
            var snap = new Snapshot();
            ReadCpuLoad(snap);
            snap.CpuCores = _cores.Read();
            ReadRam(snap);
            ReadEthernet(snap);
            _nvml.Read(snap);
            _lhm.Read(snap);
            if (!snap.CpuTempC.HasValue)
            {
                snap.CpuTempC = ReadAcpiTemps() ?? ReadThermalZone();
            }
            snap.CpuTempC = _cpuTempHold.Filter(snap.CpuTempC);
            snap.GpuTempC = _gpuTempHold.Filter(snap.GpuTempC);
            _apps.Fill(snap, _nvml);
            return snap;
        }

        private void ReadCpuLoad(Snapshot snap)
        {
            Native.FILETIME idleFt, kernelFt, userFt;
            if (!Native.GetSystemTimes(out idleFt, out kernelFt, out userFt))
            {
                return;
            }

            ulong idle = Native.ToUInt64(idleFt);
            ulong kernel = Native.ToUInt64(kernelFt);
            ulong user = Native.ToUInt64(userFt);

            if (_cpuInit)
            {
                ulong idleDelta = idle - _prevIdle;
                ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser);
                if (totalDelta > 0)
                {
                    double busy = 1.0 - (idleDelta / (double)totalDelta);
                    snap.CpuLoad = Math.Max(0, Math.Min(100, busy * 100.0));
                }
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _cpuInit = true;
        }

        private static void ReadRam(Snapshot snap)
        {
            var status = new Native.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (!Native.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                return;
            }

            ulong used = status.ullTotalPhys - status.ullAvailPhys;
            snap.RamUsedGb = used / 1073741824.0;
            snap.RamTotalGb = status.ullTotalPhys / 1073741824.0;
            snap.RamLoad = status.dwMemoryLoad;
        }

        private void ReadEthernet(Snapshot snap)
        {
            string id;
            string name;
            long inn;
            long outt;
            long speed;
            if (!TryReadEthernetBytes(out id, out name, out inn, out outt, out speed))
            {
                snap.NetDownMBps = null;
                snap.NetUpMBps = null;
                snap.NetLoad = null;
                _netInit = false;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (!_netInit || id != _prevNetId || inn < _prevNetIn || outt < _prevNetOut)
            {
                _prevNetIn = inn;
                _prevNetOut = outt;
                _prevNetTicks = now;
                _prevNetId = id;
                _netInit = true;
                System.Threading.Thread.Sleep(280);
                if (!TryReadEthernetBytes(out id, out name, out inn, out outt, out speed) || id != _prevNetId)
                {
                    snap.NetDownMBps = 0;
                    snap.NetUpMBps = 0;
                    snap.NetLoad = 0;
                    return;
                }
                now = Stopwatch.GetTimestamp();
            }

            double sec = (now - _prevNetTicks) / (double)Stopwatch.Frequency;
            if (sec < 0.15) sec = 0.15;
            double downBps = (inn - _prevNetIn) / sec;
            double upBps = (outt - _prevNetOut) / sec;
            if (downBps < 0) downBps = 0;
            if (upBps < 0) upBps = 0;

            snap.NetDownMBps = downBps / 1000000.0;
            snap.NetUpMBps = upBps / 1000000.0;
            if (speed > 0)
            {
                snap.NetLoad = Math.Max(0, Math.Min(100, (downBps + upBps) * 8.0 / speed * 100.0));
            }
            else
            {
                snap.NetLoad = Math.Max(0, Math.Min(100, (snap.NetDownMBps.Value + snap.NetUpMBps.Value) / 125.0 * 100.0));
            }
            if (downBps + upBps >= 512 && snap.NetLoad < 6)
            {
                snap.NetLoad = 6;
            }
            snap.NetName = name;
            if (speed > 0) snap.NetLinkMbps = speed / 1000000.0;
            if (downBps + upBps >= 200000) snap.NetApp = name;

            _prevNetIn = inn;
            _prevNetOut = outt;
            _prevNetTicks = now;
            _prevNetId = id;
        }

        private static bool TryReadEthernetBytes(out string id, out string name, out long inn, out long outt, out long speed)
        {
            id = "";
            name = null;
            inn = 0;
            outt = 0;
            speed = 0;
            try
            {
                NetworkInterface[] list = NetworkInterface.GetAllNetworkInterfaces();
                long bestBytes = -1;
                for (int i = 0; i < list.Length; i++)
                {
                    NetworkInterface ni = list[i];
                    if (!IsPhysicalEthernet(ni)) continue;
                    long rx;
                    long tx;
                    if (!TryNicBytes(ni, out rx, out tx)) continue;
                    long total = rx + tx;
                    if (total < bestBytes) continue;
                    bestBytes = total;
                    id = ni.Id ?? ni.Name ?? "";
                    name = string.IsNullOrEmpty(ni.Name) ? ni.Description : ni.Name;
                    inn = rx;
                    outt = tx;
                    speed = ni.Speed;
                }
                return bestBytes >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNicBytes(NetworkInterface ni, out long inn, out long outt)
        {
            inn = 0;
            outt = 0;
            try
            {
                IPInterfaceStatistics ip = ni.GetIPStatistics();
                inn = ip.BytesReceived;
                outt = ip.BytesSent;
                return true;
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
            try
            {
                IPv4InterfaceStatistics v4 = ni.GetIPv4Statistics();
                inn = v4.BytesReceived;
                outt = v4.BytesSent;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPhysicalEthernet(NetworkInterface ni)
        {
            if (ni == null || ni.OperationalStatus != OperationalStatus.Up) return false;
            string text = (ni.Name ?? "") + " " + (ni.Description ?? "");
            if (ContainsAny(text, "wi-fi", "wifi", "wireless", "wlan", "bluetooth", "loopback",
                "vmware", "hyper-v", "vethernet", "virtualbox", "vboxnet", "vpn", "tap-", "tun-",
                "wintun", "windscribe", "wireguard", "wan miniport", "isatap", "teredo", "npcap",
                "cisco anyconnect", "nordlynx", "tailscale", "zerotier"))
            {
                return false;
            }

            NetworkInterfaceType t = ni.NetworkInterfaceType;
            if (t == NetworkInterfaceType.Wireless80211 || t == NetworkInterfaceType.Ppp
                || t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel)
            {
                return false;
            }
            if (t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.FastEthernetT
                || t == NetworkInterfaceType.FastEthernetFx || t == NetworkInterfaceType.GigabitEthernet)
            {
                return true;
            }
            return ContainsAny(text, "ethernet", "realtek", "killer ethernet", "2.5gbe", "5gbe", "10gbe", "gigabit");
        }

        private static bool ContainsAny(string text, params string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (text.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static double? ReadAcpiTemps()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (var results = searcher.Get())
                {
                    double? hottest = null;
                    foreach (ManagementObject row in results)
                    {
                        var raw = Convert.ToDouble(row["CurrentTemperature"]);
                        var c = (raw / 10.0) - 273.15;
                        if (c >= 35 && c <= 110)
                        {
                            if (!hottest.HasValue || c > hottest.Value) hottest = c;
                        }
                        row.Dispose();
                    }
                    return hottest;
                }
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadThermalZone()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        var kelvin = Convert.ToDouble(row["Temperature"]);
                        var c = kelvin - 273.15;
                        row.Dispose();
                        if (c >= 35 && c <= 110) return c;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public void Dispose()
        {
            _nvml.Dispose();
            _lhm.Dispose();
        }
    }

    internal sealed class TopAppFinder
    {
        private const uint QueryLimited = 0x1000;
        private readonly Dictionary<int, ulong> _cpuTicks = new Dictionary<int, ulong>();
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private int _cores = Math.Max(1, Environment.ProcessorCount);

        public void Fill(Snapshot snap, NvmlReader nvml)
        {
            Process[] list = null;
            try { list = Process.GetProcesses(); }
            catch { return; }

            try
            {
                var cpuByName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
                var ramByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var seenCpu = new Dictionary<int, ulong>();
                DateTime now = DateTime.UtcNow;
                TimeSpan wall = (_lastSampleUtc == DateTime.MinValue) ? TimeSpan.Zero : now - _lastSampleUtc;

                for (int i = 0; i < list.Length; i++)
                {
                    Process p = list[i];
                    int pid;
                    string name;
                    long working;
                    try
                    {
                        pid = p.Id;
                        name = p.ProcessName;
                        working = p.WorkingSet64;
                    }
                    catch
                    {
                        continue;
                    }

                    if (pid <= 0 || IsNoise(name)) continue;

                    AddLong(ramByName, name, working);

                    ulong ticks;
                    if (!TryCpuTicks(pid, out ticks)) continue;
                    seenCpu[pid] = ticks;

                    ulong prev;
                    if (wall.TotalMilliseconds > 200 && _cpuTicks.TryGetValue(pid, out prev) && ticks >= prev)
                    {
                        AddULong(cpuByName, name, ticks - prev);
                    }
                }

                _cpuTicks.Clear();
                foreach (var pair in seenCpu) _cpuTicks[pair.Key] = pair.Value;
                _lastSampleUtc = now;

                if (IsHot(snap.CpuLoad)) snap.CpuApp = FormatCpu(cpuByName, wall);
                if (IsHot(snap.RamLoad)) snap.RamApp = FormatRam(ramByName);
                if (IsHot(snap.GpuLoad)) snap.GpuApp = FormatGpu(nvml);
            }
            finally
            {
                for (int i = 0; i < list.Length; i++)
                {
                    try { list[i].Dispose(); } catch { }
                }
            }
        }

        private static bool IsHot(double? load)
        {
            return load.HasValue && load.Value >= 90.0;
        }

        private string FormatCpu(Dictionary<string, ulong> cpuByName, TimeSpan wall)
        {
            string name;
            ulong ticks;
            if (!Best(cpuByName, out name, out ticks)) return null;
            if (wall.Ticks <= 0) return Line(name, null);
            double pct = (ticks / (double)wall.Ticks) / _cores * 100.0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return Line(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}%", Math.Round(pct)));
        }

        private static string FormatRam(Dictionary<string, long> ramByName)
        {
            string name;
            long bytes;
            if (!Best(ramByName, out name, out bytes)) return null;
            double gb = bytes / 1073741824.0;
            return Line(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} GB", gb));
        }

        private static string FormatGpu(NvmlReader nvml)
        {
            uint pid;
            uint util;
            ulong mem;
            if (!nvml.TryGetTop(out pid, out util, out mem) || pid == 0) return null;
            string name = NameFromPid((int)pid);
            if (string.IsNullOrEmpty(name)) name = "pid " + pid;
            if (util > 0)
            {
                return Line(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}%", util));
            }
            if (mem > 0 && mem != ulong.MaxValue)
            {
                return Line(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} GB", mem / 1073741824.0));
            }
            return Line(name, null);
        }

        private static bool TryCpuTicks(int pid, out ulong ticks)
        {
            ticks = 0;
            IntPtr h = Native.OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                Native.FILETIME create, exit, kernel, user;
                if (!Native.GetProcessTimes(h, out create, out exit, out kernel, out user)) return false;
                ticks = Native.ToUInt64(kernel) + Native.ToUInt64(user);
                return true;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        private static string NameFromPid(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!string.IsNullOrEmpty(p.ProcessName)) return p.ProcessName;
                }
            }
            catch
            {
            }

            IntPtr h = Native.OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                string path = sb.ToString();
                if (string.IsNullOrEmpty(path)) return null;
                return IOPath.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return null;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        private static void AddULong(Dictionary<string, ulong> map, string name, ulong amount)
        {
            ulong cur;
            if (map.TryGetValue(name, out cur)) map[name] = cur + amount;
            else map[name] = amount;
        }

        private static void AddLong(Dictionary<string, long> map, string name, long amount)
        {
            long cur;
            if (map.TryGetValue(name, out cur)) map[name] = cur + amount;
            else map[name] = amount;
        }

        private static bool Best<T>(Dictionary<string, T> map, out string name, out T amount) where T : IComparable
        {
            name = null;
            amount = default(T);
            bool any = false;
            foreach (var pair in map)
            {
                if (!any || pair.Value.CompareTo(amount) > 0)
                {
                    any = true;
                    name = pair.Key;
                    amount = pair.Value;
                }
            }
            return any && !string.IsNullOrEmpty(name);
        }

        private static bool IsNoise(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase);
        }

        private static string Line(string name, string usage)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.Length > 16) name = name.Substring(0, 15) + "…";
            if (string.IsNullOrEmpty(usage)) return name;
            return name + "  " + usage;
        }
    }

    internal sealed class LhmReader : IDisposable
    {
        private readonly object _computer;
        private readonly MethodInfo _open;
        private readonly MethodInfo _close;
        private readonly PropertyInfo _hardwareProp;
        private bool _opened;

        public bool Ready { get { return _opened; } }

        public LhmReader()
        {
            try
            {
                var dll = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "LibreHardwareMonitorLib.dll");
                if (!File.Exists(dll)) return;

                var asm = Assembly.LoadFrom(dll);
                var computerType = asm.GetType("LibreHardwareMonitor.Hardware.Computer");
                if (computerType == null) return;

                _computer = Activator.CreateInstance(computerType);
                SetBool(_computer, computerType, "IsCpuEnabled", true);
                SetBool(_computer, computerType, "IsGpuEnabled", true);
                SetBool(_computer, computerType, "IsMemoryEnabled", false);
                SetBool(_computer, computerType, "IsMotherboardEnabled", false);
                SetBool(_computer, computerType, "IsControllerEnabled", false);
                SetBool(_computer, computerType, "IsNetworkEnabled", false);
                SetBool(_computer, computerType, "IsStorageEnabled", false);
                SetBool(_computer, computerType, "IsPsuEnabled", false);
                SetBool(_computer, computerType, "IsBatteryEnabled", false);

                _open = computerType.GetMethod("Open");
                _close = computerType.GetMethod("Close");
                _hardwareProp = computerType.GetProperty("Hardware");
                _open.Invoke(_computer, null);
                _opened = true;
            }
            catch
            {
                _opened = false;
            }
        }

        public void Read(Snapshot snap)
        {
            if (!_opened) return;

            try
            {
                var hardware = _hardwareProp.GetValue(_computer, null) as IEnumerable;
                if (hardware == null) return;
                foreach (var hw in hardware)
                {
                    UpdateHardware(hw, snap);
                }
            }
            catch
            {
            }
        }

        private static void UpdateHardware(object hw, Snapshot snap)
        {
            try
            {
                hw.GetType().GetMethod("Update").Invoke(hw, null);
            }
            catch
            {
            }

            var typeName = Convert.ToString(GetProp(hw, "HardwareType"));
            var sensors = GetProp(hw, "Sensors") as IEnumerable;
            if (sensors != null)
            {
                foreach (var sensor in sensors)
                {
                    ApplySensor(typeName, sensor, snap);
                }
            }

            var subs = GetProp(hw, "SubHardware") as IEnumerable;
            if (subs != null)
            {
                foreach (var sub in subs)
                {
                    UpdateHardware(sub, snap);
                }
            }
        }

        private static void ApplySensor(string hardwareType, object sensor, Snapshot snap)
        {
            var sensorType = Convert.ToString(GetProp(sensor, "SensorType"));
            var name = Convert.ToString(GetProp(sensor, "Name")) ?? "";
            var raw = GetProp(sensor, "Value");
            if (raw == null) return;
            double value = Convert.ToDouble(raw);

            bool isCpu = string.Equals(hardwareType, "Cpu", StringComparison.OrdinalIgnoreCase);
            bool isGpu = hardwareType != null && hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

            if (isCpu && string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (IsPreferredCpuTemp(name) || !snap.CpuTempC.HasValue)
                {
                    if (value >= 20 && value <= 110) snap.CpuTempC = value;
                }
            }
            else if (isGpu && string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (!snap.GpuTempC.HasValue || NameContains(name, "Core"))
                {
                    if (value >= 20 && value <= 110) snap.GpuTempC = value;
                }
            }
            else if (isGpu && string.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase))
            {
                if (!snap.GpuLoad.HasValue && (NameContains(name, "Core") || NameContains(name, "D3D 3D") || name == "GPU Core"))
                {
                    snap.GpuLoad = value;
                }
            }
        }

        private static bool IsPreferredCpuTemp(string name)
        {
            return NameContains(name, "Package") || NameContains(name, "Tctl") || NameContains(name, "Tdie") || name == "CPU Average";
        }

        private static bool NameContains(string name, string token)
        {
            return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object GetProp(object obj, string name)
        {
            var p = obj.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(obj, null);
        }

        private static void SetBool(object instance, Type type, string name, bool value)
        {
            var p = type.GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(instance, value, null);
        }

        public void Dispose()
        {
            if (!_opened) return;
            try
            {
                if (_close != null) _close.Invoke(_computer, null);
            }
            catch
            {
            }
            _opened = false;
        }
    }

    internal sealed class NvmlReader : IDisposable
    {
        private const int NvmlSuccess = 0;
        private IntPtr _device;
        private bool _ready;

        public NvmlReader()
        {
            try
            {
                if (Native.nvmlInit_v2() != NvmlSuccess) return;
                if (Native.nvmlDeviceGetHandleByIndex_v2(0, out _device) != NvmlSuccess)
                {
                    Native.nvmlShutdown();
                    return;
                }
                _ready = true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public void Read(Snapshot snap)
        {
            if (!_ready) return;
            uint temp;
            if (Native.nvmlDeviceGetTemperature(_device, 0, out temp) == NvmlSuccess)
            {
                snap.GpuTempC = temp;
            }
            Native.NvmlUtilization util;
            if (Native.nvmlDeviceGetUtilizationRates(_device, out util) == NvmlSuccess)
            {
                snap.GpuLoad = util.gpu;
            }
            try
            {
                Native.NvmlMemory mem;
                if (Native.nvmlDeviceGetMemoryInfo(_device, out mem) == NvmlSuccess && mem.total > 0)
                {
                    snap.GpuMemUsedGb = mem.used / 1073741824.0;
                    snap.GpuMemTotalGb = mem.total / 1073741824.0;
                }
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint mw;
                if (Native.nvmlDeviceGetPowerUsage(_device, out mw) == NvmlSuccess)
                {
                    snap.GpuPowerW = mw / 1000.0;
                }
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint clock;
                if (Native.nvmlDeviceGetClockInfo(_device, 0, out clock) == NvmlSuccess)
                {
                    snap.GpuClockMHz = clock;
                }
                if (Native.nvmlDeviceGetClockInfo(_device, 2, out clock) == NvmlSuccess)
                {
                    snap.GpuMemClockMHz = clock;
                }
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint limit;
                if (Native.nvmlDeviceGetPowerManagementLimit(_device, out limit) == NvmlSuccess && limit > 0)
                {
                    snap.GpuPowerLimitW = limit / 1000.0;
                }
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint maxClock;
                if (Native.nvmlDeviceGetMaxClockInfo(_device, 0, out maxClock) == NvmlSuccess && maxClock > 0)
                {
                    snap.GpuMaxClockMHz = maxClock;
                }
            }
            catch (EntryPointNotFoundException) { }
        }

        public bool TryGetTop(out uint pid, out uint util, out ulong mem)
        {
            pid = 0;
            util = 0;
            mem = 0;
            if (!_ready) return false;
            if (TryTopUtilization(out pid, out util) && pid != 0) return true;
            pid = 0;
            util = 0;
            return TryTopMemory(out pid, out mem);
        }

        private bool TryTopUtilization(out uint pid, out uint util)
        {
            pid = 0;
            util = 0;
            try
            {
                uint count = 128;
                var samples = new Native.NvmlProcUtil[128];
                int st = Native.nvmlDeviceGetProcessUtilization(_device, samples, ref count, 0);
                if (st != NvmlSuccess && st != 7) return false;
                if (st == 7)
                {
                    if (count == 0 || count > 512) return false;
                    samples = new Native.NvmlProcUtil[count];
                    st = Native.nvmlDeviceGetProcessUtilization(_device, samples, ref count, 0);
                    if (st != NvmlSuccess || count == 0) return false;
                }
                if (count == 0) return false;
                uint best = 0;
                uint bestPid = 0;
                for (uint i = 0; i < count && i < samples.Length; i++)
                {
                    uint score = samples[i].smUtil;
                    if (samples[i].memUtil > score) score = samples[i].memUtil;
                    if (score > best && samples[i].pid != 0)
                    {
                        best = score;
                        bestPid = samples[i].pid;
                    }
                }
                if (bestPid == 0) return false;
                pid = bestPid;
                util = best;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private bool TryTopMemory(out uint pid, out ulong mem)
        {
            pid = 0;
            mem = 0;
            uint bestPid = 0;
            ulong bestMem = 0;
            CollectGpuProcs(true, ref bestPid, ref bestMem);
            CollectGpuProcs(false, ref bestPid, ref bestMem);
            if (bestPid == 0) return false;
            pid = bestPid;
            mem = bestMem;
            return true;
        }

        private void CollectGpuProcs(bool graphics, ref uint bestPid, ref ulong bestMem)
        {
            try
            {
                uint count = 64;
                var infos = new Native.NvmlProcessInfo[64];
                int st = graphics
                    ? Native.nvmlDeviceGetGraphicsRunningProcesses_v2(_device, ref count, infos)
                    : Native.nvmlDeviceGetComputeRunningProcesses_v2(_device, ref count, infos);
                if (st != NvmlSuccess) return;
                for (uint i = 0; i < count; i++)
                {
                    ulong mem = infos[i].usedGpuMemory;
                    if (mem == ulong.MaxValue) continue;
                    if (mem > bestMem && infos[i].pid != 0)
                    {
                        bestMem = mem;
                        bestPid = infos[i].pid;
                    }
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public void Dispose()
        {
            if (!_ready) return;
            try { Native.nvmlShutdown(); } catch { }
            _ready = false;
        }
    }

    internal sealed class CoreSampler
    {
        private long[] _idle;
        private long[] _kernel;
        private long[] _user;
        private bool _ready;

        public double[] Read()
        {
            int count = Environment.ProcessorCount;
            int stride = Marshal.SizeOf(typeof(Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
            int bytes = stride * Math.Max(count, 64);
            IntPtr buf = Marshal.AllocHGlobal(bytes);
            try
            {
                int returned;
                int status = Native.NtQuerySystemInformation(8, buf, bytes, out returned);
                if (status != 0)
                {
                    if (returned <= bytes) return null;
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(returned);
                    bytes = returned;
                    status = Native.NtQuerySystemInformation(8, buf, bytes, out returned);
                    if (status != 0) return null;
                }
                if (returned > 0) count = returned / stride;
                if (count <= 0) return null;

                if (_idle == null || _idle.Length != count)
                {
                    _idle = new long[count];
                    _kernel = new long[count];
                    _user = new long[count];
                    _ready = false;
                }

                var result = new double[count];
                for (int i = 0; i < count; i++)
                {
                    var info = (Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)Marshal.PtrToStructure(
                        new IntPtr(buf.ToInt64() + (long)i * stride),
                        typeof(Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                    if (_ready)
                    {
                        long idleDelta = info.IdleTime - _idle[i];
                        long kernelDelta = info.KernelTime - _kernel[i];
                        long userDelta = info.UserTime - _user[i];
                        long total = kernelDelta + userDelta;
                        if (total > 0)
                        {
                            double busy = 1.0 - (idleDelta / (double)total);
                            if (busy < 0) busy = 0;
                            if (busy > 1) busy = 1;
                            result[i] = busy * 100.0;
                        }
                    }
                    _idle[i] = info.IdleTime;
                    _kernel[i] = info.KernelTime;
                    _user[i] = info.UserTime;
                }
                _ready = true;
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlInit_v2();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetProcessUtilization", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetProcessUtilization(IntPtr device, [In, Out] NvmlProcUtil[] utilization, ref uint count, ulong lastSeenTimestamp);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetGraphicsRunningProcesses_v2(IntPtr device, ref uint infoCount, [In, Out] NvmlProcessInfo[] infos);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetComputeRunningProcesses_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetComputeRunningProcesses_v2(IntPtr device, ref uint infoCount, [In, Out] NvmlProcessInfo[] infos);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint clockMHz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int clockType, out uint clockMHz);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr data, int length, out int returned);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
            public uint Spare;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlMemory
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlUtilization
        {
            public uint gpu;
            public uint memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlProcUtil
        {
            public uint pid;
            public ulong timeStamp;
            public uint smUtil;
            public uint memUtil;
            public uint encUtil;
            public uint decUtil;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlProcessInfo
        {
            public uint pid;
            public ulong usedGpuMemory;
            public uint gpuInstanceId;
            public uint computeInstanceId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        public static ulong ToUInt64(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }
    }
}
