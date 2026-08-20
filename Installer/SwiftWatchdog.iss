; SwiftWatchdog Inno Setup Script
; Produces: SwiftWatchdog-Setup.exe
; Single UAC click installs both service and tray app.

#define AppName      "SwiftWatchdog"
#define AppVersion   "1.0.0"
#define AppPublisher "Harry Boardman"
#define InstallDir   "{commonappdata}\SwiftWatchdog"
#define ServiceExe   "SwiftWatchdog.exe"
#define TrayExe      "SwiftWatchdogTray.exe"

[Setup]
AppId={{B4E2A3D1-7F5C-4A8B-9E2D-1C3F6A0B5D7E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={#InstallDir}
DisableDirPage=yes
OutputDir=..\installer
OutputBaseFilename=SwiftWatchdog-Setup
SetupIconFile={#SourcePath}\..\SwiftWatchdogTray\Resources\icon_active.ico
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
; Silent install supported: SwiftWatchdog-Setup.exe /SILENT
AllowNoIcons=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={#InstallDir}\{#ServiceExe}
UninstallDisplayName=SwiftWatchdog

[Files]
Source: "..\publish\service\{#ServiceExe}"; DestDir: "{#InstallDir}"; Flags: ignoreversion
Source: "..\publish\tray\{#TrayExe}";      DestDir: "{#InstallDir}"; Flags: ignoreversion

[Icons]

[Registry]
; Run tray app at login for ALL users (HKLM Run key)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "SwiftWatchdogTray"; \
  ValueData: """{#InstallDir}\{#TrayExe}"""; Flags: uninsdeletevalue

[Run]
; Install the service - runhidden + nowait so the installer doesn't hang on the service host process
Filename: "{#InstallDir}\{#ServiceExe}"; Parameters: "--install"; Flags: runhidden; StatusMsg: "Installing SwiftWatchdog service..."

; Launch tray app for the current user unconditionally - nowait so installer continues
Filename: "{#InstallDir}\{#TrayExe}"; Flags: nowait shellexec; StatusMsg: "Starting tray icon..."

[UninstallRun]
; Stop and remove service on uninstall
Filename: "{#InstallDir}\{#ServiceExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"

; Kill tray app if running, then delete it immediately while we have elevation
Filename: "taskkill.exe"; Parameters: "/F /IM {#TrayExe}"; Flags: runhidden waituntilterminated; RunOnceId: "KillTray"
Filename: "cmd.exe"; Parameters: "/C timeout /t 1 /nobreak && del /F /Q ""{#InstallDir}\{#TrayExe}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteTray"

[UninstallDelete]
; Remove remaining files and folder — logs are left intentionally
Type: files; Name: "{#InstallDir}\PAUSED"
Type: files; Name: "{#InstallDir}\{#ServiceExe}"
Type: files; Name: "{#InstallDir}\{#TrayExe}"
Type: dirifempty; Name: "{#InstallDir}"

[Code]
// Kill any running tray instance before upgrade/reinstall
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill.exe', '/F /IM SwiftWatchdogTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
