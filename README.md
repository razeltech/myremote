# MyRemote

**MyRemote** is a free, open-source, consent-first Windows-to-Windows remote desktop application built with **.NET 9** and **WPF**.

No mandatory cloud accounts, no subscriptions, no unattended background access. Every incoming session requires explicit approval by the local user.

## Key Features
- **Explicit Consent Only:** Every incoming connection displays a high-visibility approval dialog with granular per-session permissions (`View Screen`, `Mouse & Keyboard`, `Clipboard`, `File Transfer`, `Session Chat`).
- **Live Permission Revocation & Immediate Disconnect:** Hosts see an unmistakable active-control banner and can revoke input or terminate the session in one click.
- **Offline LAN Mode:** Automatic UDP peer discovery (`48721`) and TLS 1.2/1.3 encrypted peer-to-peer sessions (`48720`) on local Wi-Fi or Ethernet without internet access.
- **Standalone Portable `.exe`:** Single-file self-contained Windows x64 executable (`MyRemote.exe`) — zero installation and zero runtime prerequisites.
- **Multi-Monitor & Quality Controls:** Monitor selector, resolution scaling, FPS/JPEG quality tuning, full-screen (`F11`), clipboard sync, SHA-256 verified file transfer, and session chat.
- **Internet & NAT Diagnostics:** Built-in RFC 5389 STUN Binding diagnostics and IPv6 interface checks, plus support for direct IP/port-forwarded connections and optional self-hosted signaling/TURN configuration.
- **Light & Dark Themes:** Persistent SQLite settings, Device ID (`XXX-XXX-XXX`), and session history.

## Download & Run
Download `MyRemote.exe` from the [Releases](https://github.com/razeltech/myremote/releases) page and double-click to run on Windows x64 — no installation required.

## Build from Source
Requires **.NET 9 SDK** on Windows:
```powershell
dotnet build MyRemote.sln -c Release
dotnet test tests/MyRemote.Tests/MyRemote.Tests.csproj -c Release
dotnet publish src/MyRemote.Desktop/MyRemote.Desktop.csproj -c Release -r win-x64 --self-contained true -o release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Documentation
- [Architecture](ARCHITECTURE.md)
- [Security Model](SECURITY.md)
- [Networking & STUN/TURN Guide](NETWORKING.md)
- [Installation Guide](INSTALLATION.md)
- [Troubleshooting](TROUBLESHOOTING.md)
- [Test Report](TEST_REPORT.md)
- [Changelog](CHANGELOG.md)

## License
[MIT License](LICENSE)
