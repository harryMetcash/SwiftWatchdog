# SwiftWatchdog

A lightweight Windows service that keeps the **SwiftPOS Service Monitoring Service** running reliably.

It fixes a known silent failure bug by force-restarting the service every day at 03:00, and recovers it immediately if it stops unexpectedly at any other time. A system tray app lets staff pause and resume the watchdog without needing administrator access.

---

## Features

- **Health check every 60 seconds** — restarts the service if it is not Running
- **Daily forced restart at 03:00** — prevents the silent failure bug regardless of service status
- **System tray controller** — pause/resume without elevation; icon shows current state
- **Self-contained EXEs** — no .NET runtime install required on target machines
- **Self-installs as a Windows service** — auto-starts, restarts on failure, runs as LocalSystem
- **Rolling log** — daily log files in `C:\ProgramData\SwiftWatchdog\`, auto-purged after 30 days

---

## Installation

Download **SwiftWatchdog-Setup.exe** from [Releases](../../releases/latest) and run it as Administrator (UAC prompt will appear).

> **Windows SmartScreen warning:** releases aren't code-signed yet (a free OSS signing certificate is pending — see [licensing](#license)), so Windows will show an "unknown publisher" / SmartScreen prompt. Click **More info → Run anyway** to proceed. To verify a download hasn't been tampered with, check its hash against the `.sha256` file attached to the same release — releases are built and published automatically by [GitHub Actions](.github/workflows/build.yml) from this public source, not uploaded by hand.

The installer:
1. Copies both EXEs to `C:\ProgramData\SwiftWatchdog\`
2. Registers and starts the **SwiftPOS Watchdog** Windows service
3. Adds the tray app to the HKLM Run key so it starts at login for all users

Silent install is also supported:
```cmd
SwiftWatchdog-Setup.exe /SILENT
```

---

## Usage

After installation the tray icon appears in the system notification area.

| Action | Result |
|---|---|
| Right-click → **Pause Watchdog** | Stops the SwiftPOS service and suspends monitoring |
| Right-click → **Resume Watchdog** | Restarts the SwiftPOS service and resumes monitoring |
| Green icon | Watchdog is active |
| Grey icon | Watchdog is paused |

Logs are written to `C:\ProgramData\SwiftWatchdog\watchdog-YYYY-MM-DD.log`.

---

## Uninstall

Use **Add or Remove Programs**, or from an elevated prompt:

```cmd
"C:\ProgramData\SwiftWatchdog\SwiftWatchdog.exe" --uninstall
```

---

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and optionally [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
.\BUILD.ps1
```

Outputs:
- `publish\service\SwiftWatchdog.exe`
- `publish\tray\SwiftWatchdogTray.exe`
- `installer\SwiftWatchdog-Setup.exe` *(if Inno Setup is installed)*

See [INSTALL.md](INSTALL.md) for manual deployment steps.

### Releases

Pushing a `v*` tag (e.g. `v1.0.1`) triggers [.github/workflows/build.yml](.github/workflows/build.yml), which builds both EXEs and the installer on a clean GitHub-hosted runner and publishes them to [Releases](../../releases) along with a `.sha256` checksum. Every push and pull request also runs the same build to catch breakage early. There's no manual upload step — this is what lets a code-signing service verify the published installer actually comes from this repository.

---

## Behaviour reference

| Setting | Value |
|---|---|
| Status check interval | 60 seconds |
| Daily forced restart | 03:00 |
| Stop timeout | 60 seconds (then force-kills process) |
| Start timeout | 120 seconds |
| Log retention | 30 days |
| Pause flag file | `C:\ProgramData\SwiftWatchdog\PAUSED` |

---

## License

MIT — see [LICENSE](LICENSE).
