param(
    [string]$SourceDir = "$PSScriptRoot\..\release",
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\MyRemote"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $SourceDir "MyRemote.exe"))) {
    throw "Release binary not found in '$SourceDir'."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path "$SourceDir\*" -Destination $InstallDir -Recurse -Force

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MyRemote"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenuDir "MyRemote.lnk"))
$shortcut.TargetPath = Join-Path $InstallDir "MyRemote.exe"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "MyRemote — Consent-First Windows Remote Desktop"
$shortcut.Save()

Write-Host "MyRemote installed to '$InstallDir' and added to Start Menu."
