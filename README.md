# RAMMapTray

A lightweight Windows system tray utility and companion for Microsoft Sysinternals RAMMap, featuring a Windows 11-style flyout and native memory optimization via the Windows NT API.

## Features

- **Live Tray Icon:** Displays real-time RAM usage percentage directly in the notification area.
- **Windows 11 Flyout:** Dark mode flyout showing total, used, standby, and free physical memory.
- **Native Memory Purge:** Uses `NtSetSystemInformation` (`ntdll.dll`) with elevated privileges to flush:
  - Standby List
  - Working Sets (with per-process working set trimming)
  - Modified Page List
- **RAMMap Integration:** Detects and focuses running Sysinternals RAMMap instances, or launches `RAMMap64.exe` / `RAMMap.exe` if available.
- **UAC-free Autostart:** Batch script configuration via Windows Task Scheduler (`schtasks`) to start on login with administrator privileges without UAC prompts.

## Project Structure

```text
rammap-tray/
├── src/
│   ├── App.xaml / App.xaml.cs                   # Application entry point and single-instance mutex
│   ├── FlyoutWindow.xaml / FlyoutWindow.xaml.cs # Windows 11 dark mode flyout UI
│   ├── NativeMemory.cs                          # P/Invoke definitions for kernel32 and ntdll
│   ├── TrayIconManager.cs                       # Tray icon rendering and context menu
│   ├── app.manifest                             # Execution level and DPI settings
│   └── RAMMapTray.csproj                        # .NET project file
├── scripts/
│   ├── ConfigStartWindows.bat                   # Registers startup task via Task Scheduler
│   └── RunRAMMapTray.bat                        # Elevation launcher
├── .gitignore
└── README.md
```

## Requirements & Building

- Windows 10 (1809+) or Windows 11 (x64)
- .NET SDK (8.0, 9.0, or 10.0 preview) with Windows Desktop workload

### Build

```bash
cd src
dotnet build -c Release
```

### Publish (Single-File Executable)

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The compiled binary (`RAMMapTray.exe`) will be generated under `src/bin/Release/.../publish/`.

## Setup

1. Build or publish `RAMMapTray.exe`.
2. *(Optional)* Place `RAMMap64.exe` (from [Sysinternals](https://learn.microsoft.com/sysinternals/downloads/rammap)) in the same folder or in your system `PATH` if you want the "Open RAMMap" option to launch the official GUI.
3. Run `scripts/ConfigStartWindows.bat` as Administrator to enable start on Windows logon without UAC prompts.
