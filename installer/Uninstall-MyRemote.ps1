param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\MyRemote",
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

Get-Process -Name "MyRemote.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Removed application directory '$InstallDir'."
}

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MyRemote"
if (Test-Path $startMenuDir) {
    Remove-Item -Path $startMenuDir -Recurse -Force
    Write-Host "Removed Start Menu shortcut."
}

if ($PurgeData) {
    $dataDir = Join-Path $env:LOCALAPPDATA "MyRemote"
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force
        Write-Host "Removed local MyRemote data directory '$dataDir'."
    }
}

Write-Host "MyRemote uninstalled."
