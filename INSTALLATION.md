# MyRemote Installation & Release Guide

## Installing from Release Folder
1. Build or obtain the `release/` folder (`dotnet publish src/MyRemote.Desktop/MyRemote.Desktop.csproj -c Release -o release`).
2. Run PowerShell as Administrator or standard user and execute:
   ```powershell
   .\installer\Install-MyRemote.ps1
   ```
   This installs MyRemote to `%LocalAppData%\Programs\MyRemote` and creates a Start Menu shortcut.

## Uninstalling
Run:
```powershell
.\installer\Uninstall-MyRemote.ps1
```
To also remove local SQLite settings and logs, pass `-PurgeData`.
