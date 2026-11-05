#Requires -RunAsAdministrator
<#
.SYNOPSIS
Builds RgbBlaster and installs it as the Windows service "RgbBlaster", plus a tray icon that starts at logon.

.DESCRIPTION
Run again to update: the script stops the service, replaces the files and starts it again.
Settings, profiles and media stay in C:\ProgramData\RgbBlaster and are not touched.
rgbblaster.json (hardware configuration) comes from src\RgbBlaster.Host.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\RgbBlaster"
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$name = 'RgbBlaster'

# A copy started by hand holds the hardware; the service would exit at once.
$service = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
$manual = Get-Process rgbblaster-service -ErrorAction SilentlyContinue | Where-Object { -not $service -or $_.Id -ne $service.ProcessId }
if ($manual) {
    Write-Host "rgbblaster-service.exe is running outside the service (process $($manual.Id -join ', ')). Stop it (Ctrl+C in its window), then run this again." -ForegroundColor Yellow
    exit 1
}

if ($service) {
    Write-Host "Stopping the service..."
    Stop-Service $name -Force
}
Get-Process rgbblaster-tray -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Publishing to $InstallDir..."
dotnet publish "$repo\src\RgbBlaster.Service" -c Release -o $InstallDir -v q -nologo
if ($LASTEXITCODE) { throw "Service publish failed." }
dotnet publish "$repo\src\RgbBlaster.Tray" -c Release -o $InstallDir -v q -nologo
if ($LASTEXITCODE) { throw "Tray publish failed." }

$exe = Join-Path $InstallDir 'rgbblaster-service.exe'
if (-not $service) {
    New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'RgbBlaster' -StartupType Automatic `
        -Description 'RGB lighting, fan curves and pump LCD. Web GUI on http://localhost:27830/.' | Out-Null
} else {
    sc.exe config $name binPath= "`"$exe`"" start= auto | Out-Null
}
# Restart after a crash: 5 s, 10 s, then every 30 s; the failure count resets after a day.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "Starting the service..."
Start-Service $name

# Tray icon: per-user, started at logon, and now. Explorer starts it without administrator rights.
$tray = Join-Path $InstallDir 'rgbblaster-tray.exe'
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RgbBlasterTray' -Value "`"$tray`""
Start-Process explorer.exe -ArgumentList "`"$tray`""

$version = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Installed RgbBlaster $version. Service: $((Get-Service $name).Status). GUI: http://localhost:27830/" -ForegroundColor Green
Write-Host "Close vendor RGB apps (iCUE, Redragon, etc.); they fight over the same devices."
