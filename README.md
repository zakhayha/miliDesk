# miliDesk

A small always-on Windows overlay for **CPU**, **GPU**, **RAM**, and **Ethernet**. Live rings sit on the desktop, expand in place for extra stats, and can sit on the taskbar next to the tray icons.

Built as a .NET Framework 4 WPF app. Hardware temperatures use [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

Settings stay on your PC only (`%AppData%\DeskMonitor`). Nothing about your hardware is stored in this repository.

## Features

- Four gauges: CPU temperature and total load, GPU, RAM, Ethernet up/down
- Hover a card to expand it in place (CPU cores, GPU load / memory / power / clock)
- CPU cores as bars or rings, three across
- Separate cards or one grouped panel
- Card look: solid, frosted, or glass, plus card opacity and grain
- Optional taskbar strip with live values beside the tray icons
- Colors, size, opacity, Celsius / Fahrenheit, refresh interval
- Start with Windows, always on top, snap to screen corners

CPU **usage** is the whole processor (all logical cores combined). Per-core load is in the expanded CPU card. CPU **temperature** needs administrator so the LibreHardwareMonitor driver can load; without it the reading stays blank.

## Run

Windows 10 or 11, 64-bit.

1. Build (below), or copy a `dist` folder that already contains `DeskMonitor.exe` and its DLLs.
2. Run `DeskMonitor.exe`.
3. Accept the UAC prompt if you want CPU temperature.
4. Close from the tray icon.

## Build

Needs the 64-bit .NET Framework 4 developer pack (`csc.exe` under `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\compile.ps1 -OutName dist
```

Output is `dist\DeskMonitor.exe` plus the LibreHardwareMonitor DLLs from `lib\lhm`.

## License

MIT. LibreHardwareMonitor and its dependencies keep their own licenses.
