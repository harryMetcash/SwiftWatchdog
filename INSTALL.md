# SwiftWatchdog

Monitors the SwiftPOS Service Monitoring Service and performs a daily forced restart
to prevent a known silent failure bug. Also recovers the service immediately if it
is found stopped unexpectedly.

A system tray app provides pause/resume control without requiring elevation.

## Requirements

- .NET 8 SDK (build only): https://dotnet.microsoft.com/download/dotnet/8.0
- Inno Setup 6 (optional, for installer): https://jrsoftware.org/isinfo.php
- Windows, run as Administrator for install/uninstall

---

## Build

Open PowerShell and run:

```powershell
.\BUILD.ps1
```

This produces:
- `publish\service\SwiftWatchdog.exe` — the Windows service
- `publish\tray\SwiftWatchdogTray.exe` — the system tray controller
- `Installer\SwiftWatchdog-Setup.exe` — one-click installer (if Inno Setup is present)

---

## Install via Installer (recommended)

Run `Installer\SwiftWatchdog-Setup.exe` as Administrator (or with UAC prompt).

This installs both EXEs to `C:\ProgramData\SwiftWatchdog\`, registers the service,
and adds the tray app to HKLM Run so it starts for all users at login.

---

## Manual Install (without installer)

1. Copy both EXEs to `C:\ProgramData\SwiftWatchdog\`
2. From an elevated prompt, install the service:
   ```cmd
   "C:\ProgramData\SwiftWatchdog\SwiftWatchdog.exe" --install
   ```
3. Add the tray app to startup manually (Task Scheduler or HKCU Run key)

---

## Verify

Open Services (`services.msc`) and confirm **SwiftPOS Watchdog** is present and Running.

Logs are written to:
```
C:\ProgramData\SwiftWatchdog\watchdog-YYYY-MM-DD.log
```
Logs roll daily and are automatically deleted after 30 days.

---

## Uninstall

Run the Windows Add/Remove Programs uninstaller, **or** from an elevated prompt:

```cmd
"C:\ProgramData\SwiftWatchdog\SwiftWatchdog.exe" --uninstall
```

---

## Behaviour

| | |
|---|---|
| Status check | Every 60 seconds — restarts service if not Running |
| Daily forced restart | 03:00 by default, configurable via tray Options |
| CPU monitoring | 65% (per-core) sustained 120s by default, configurable via tray Options; 5-minute fixed cooldown after a CPU-triggered restart |
| Stop timeout | 60 seconds before force-killing process |
| Start timeout | 120 seconds |
| Log retention | 30 days |
| Pause/Resume | Via system tray icon — writes/removes `C:\ProgramData\SwiftWatchdog\PAUSED` |
| Settings | Via tray icon → Options — reads/writes `C:\ProgramData\SwiftWatchdog\settings.json` |
