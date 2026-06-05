<#
.SYNOPSIS
Builds SwiftWatchdog service and tray app, then optionally compiles the installer.

.DESCRIPTION
Produces:
  publish\service\SwiftWatchdog.exe     - Windows service
  publish\tray\SwiftWatchdogTray.exe    - System tray controller
  installer\SwiftWatchdog-Setup.exe     - Inno Setup installer (if ISCC is available)

Requires:
  - .NET 8 SDK:      https://dotnet.microsoft.com/download
  - Inno Setup 6:    https://jrsoftware.org/isinfo.php  (optional, for installer)

.NOTES
To deploy without the installer, copy both EXEs to C:\ProgramData\SwiftWatchdog\
and run SwiftWatchdog.exe --install from an elevated prompt.
#>

$root       = $PSScriptRoot
$serviceDir = Join-Path $root "SwiftWatchdog"
$trayDir    = Join-Path $root "SwiftWatchdogTray"
$issFile    = Join-Path $root "Installer\SwiftWatchdog.iss"
$pubService = Join-Path $root "publish\service"
$pubTray    = Join-Path $root "publish\tray"

$ErrorActionPreference = "Stop"

function Build-Project($label, $projectDir, $outputDir) {
    Write-Host ""
    Write-Host "Building $label..." -ForegroundColor Cyan

    dotnet publish $projectDir `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        --output $outputDir

    if ($LASTEXITCODE -ne 0) {
        Write-Host "$label build FAILED." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "$label built OK -> $outputDir" -ForegroundColor Green
}

Build-Project "SwiftWatchdog Service" $serviceDir $pubService
Build-Project "SwiftWatchdog Tray"    $trayDir    $pubTray

# -- Inno Setup (optional) ------------------------------------------------------
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) {
    Write-Host ""
    Write-Host "Compiling installer..." -ForegroundColor Cyan

    New-Item -ItemType Directory -Force -Path (Join-Path $root "installer") | Out-Null
    & $iscc $issFile

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Installer built OK -> installer\SwiftWatchdog-Setup.exe" -ForegroundColor Green
    } else {
        Write-Host "Installer build FAILED (exit $LASTEXITCODE)." -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "Inno Setup not found - skipping installer." -ForegroundColor Yellow
    Write-Host "Install from: https://jrsoftware.org/isinfo.php"
    Write-Host ""
    Write-Host "To deploy manually without the installer:"
    Write-Host "  1. Copy publish\service\SwiftWatchdog.exe    -> C:\ProgramData\SwiftWatchdog\"
    Write-Host "  2. Copy publish\tray\SwiftWatchdogTray.exe   -> C:\ProgramData\SwiftWatchdog\"
    Write-Host "  3. Run SwiftWatchdog.exe --install  (elevated)"
    Write-Host "  4. Add SwiftWatchdogTray.exe to startup manually"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
