#Requires -RunAsAdministrator
<#
.SYNOPSIS
Removes the RgbBlaster service, the tray icon and the program files.
Settings, profiles and media in C:\ProgramData\RgbBlaster are kept unless -RemoveData is given.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\RgbBlaster",
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$name = 'RgbBlaster'

Get-Process rgbblaster-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RgbBlasterTray' -ErrorAction SilentlyContinue

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force
    sc.exe delete $name | Out-Null
}
if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
if ($RemoveData) { Remove-Item -LiteralPath "$env:ProgramData\RgbBlaster" -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "Removed." -ForegroundColor Green
