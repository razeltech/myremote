# MyRemote — Master TODO & Roadmap

## Completed (v1.0.0 – v1.0.4)
- [x] **Modular .NET 9 + WPF Architecture** (`Core`, `Storage`, `Security`, `ScreenCapture`, `Input`, `Network`, `FileTransfer`, `Desktop`, `Tests`)
- [x] **SQLite Persistence** (stable 9-digit CSPRNG Device ID, AppSettings, SessionHistory)
- [x] **Light & Dark Themes** with live switching and custom `myremote.ico`
- [x] **Standalone Single-File Executable** (`MyRemote.exe`, `win-x64` self-contained, zero DLLs)
- [x] **LAN Mode (Offline)** — UDP peer discovery (`48721`) + TLS 1.2/1.3 `SslStream` (`48720`)
- [x] **Internet P2P Mode** — `SIPSorcery` WebRTC + PeerJS signaling + STUN/OpenRelay TURN + early ICE candidate queuing
- [x] **Explicit Consent & Security** — Incoming approval dialog, granular permissions, 256-bit session tokens, rate limiting, live **Revoke Input**, immediate **Terminate Session**
- [x] **Remote Desktop Features** — Multi-monitor capture, quality/scale selector, full-screen (`F11`), clipboard sync, SHA-256 verified file transfer, session text chat, 16 kHz PCM voice chat
- [x] **Diagnostics & Logs** — RFC 5389 STUN + IPv6 diagnostics, live **Logs & Audit Trail** UI with secret redaction
- [x] **System Tray (Minimize-to-Tray)** — Visible tray icon with Open / Copy ID / Exit menu

## Verification Pending
- [ ] **Cross-City Real-Hardware Test:** Test `MyRemote.exe` (`v1.0.4+`) between two physical Windows PCs in different cities and capture **Logs & Audit** results.

## Roadmap to AnyDesk / RustDesk Parity
- [ ] **GPU Screen Capture & Video Codec:** Replace GDI `BitBlt` + JPEG with **DXGI Desktop Duplication** (`IDXGIOutputDuplication`) + hardware **H.264 / VP8** encoding for 60 FPS low-bandwidth streaming.
- [ ] **Dedicated Self-Hosted Rendezvous & Relay:** Provide optional one-click Docker setup for `peerjs-server` + `coturn` (or RustDesk-style `hbbs`/`hbbr`) so users don't rely on free community servers.
- [ ] **Opus Voice & System Audio Loopback:** Compress voice with `Concentus` (Opus) and stream remote PC system audio via WASAPI loopback.
- [ ] **Dual-Pane File Manager & Address Book:** Add interactive remote folder browsing and a saved/pinned peer address book.
- [ ] **Auto-Updater & Signed MSIX Installer:** Code-sign releases and add in-app update checks against GitHub Releases.
