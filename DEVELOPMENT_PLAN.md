# MyRemote — Development Plan

## Target Environment & Stack
- **Platform:** Windows-to-Windows (`net9.0-windows` for WPF/Windows APIs, `net9.0` for cross-layer libraries)
- **SDK:** .NET 9 (`9.0.304`)
- **UI Framework:** WPF (original Light & Dark themes, custom remote-desktop workbench layout)
- **Screen Capture:** Windows Graphics Capture / Desktop Duplication / GDI fallback with multi-monitor support
- **Network Transport:** LAN UDP discovery + TLS-secured TCP/WebRTC signaling & media/data channels
- **Storage:** SQLite (`Microsoft.Data.Sqlite`) behind repository interfaces
- **Logging:** Structured logging (`Microsoft.Extensions.Logging` + rolling file sink, secret-redacted)
- **Testing:** xUnit (`MyRemote.Tests`)

## Architecture & Projects
1. `MyRemote.Core` — Domain models, connection state machine, permissions, session lifecycle, interfaces.
2. `MyRemote.Storage` — SQLite persistence, settings repository, recent sessions repository.
3. `MyRemote.Security` — Device identity, Ed25519/ECDSA or HMAC/TLS session tokens, explicit approval, rate limiting, input validation.
4. `MyRemote.ScreenCapture` — Monitor enumeration, frame capture, scaling/encoding.
5. `MyRemote.Input` — Authorized mouse/keyboard injection (`SendInput`), permission enforcement.
6. `MyRemote.Network` — LAN UDP mDNS/broadcast discovery, TLS/WebRTC peer transport, ICE/STUN diagnostics.
7. `MyRemote.FileTransfer` — Chunked user-approved file transfer, SHA-256 integrity, cancellation, progress.
8. `MyRemote.Desktop` — WPF application (Dashboard, Incoming Request dialog, Session Viewer, Settings, About).
9. `MyRemote.Tests` — Unit, integration, and security tests.

## Phase Checklist
- **Phase 1 — Project Foundation:** Solution structure, logging, SQLite settings, unique Device ID, WPF workbench shell (Light/Dark themes, Dashboard, Settings, About).
- **Phase 2 — Local Network Remote Desktop:** LAN discovery, TLS connection, incoming approval dialog, screen capture & rendering, mouse/keyboard control, immediate disconnect.
- **Phase 3 — Internet Connectivity:** Modular WebRTC/ICE/STUN negotiation, IPv6, optional self-hosted signaling/TURN documentation, clear failure diagnostics.
- **Phase 4 — Advanced Remote Desktop Features:** Full-screen, resolution scaling, multi-monitor selector, permission-gated clipboard & file transfer, session chat, quality/FPS/bandwidth adaptation, reconnect, duration timer.
- **Phase 5 — Security & Permission System:** Strong ephemeral session tokens, strict input/clipboard/file permission revocation, visible host control indicator, rate limiting, malformed message handling, security test suite.
- **Phase 6 — Installer & Release:** Release build, dependency check, Windows installer/uninstaller, icon, license, user/setup/network/troubleshooting/security docs.
