# MyRemote — Project Guide (read this first)

Consent-first Windows→Windows remote desktop app. C# / .NET 9 / WPF. ~5k LOC.
Owner goal: the owner's **own security-first** remote desktop for supporting their clients (small private distribution, not for sale), with no AnyDesk-style session limits. Do not suggest third-party tools as replacements.
Strategy & phased plan: [ROADMAP.md](ROADMAP.md). Older docs (ARCHITECTURE/SECURITY/PROGRESS/TEST_REPORT) are partly aspirational — trust the code and this file over them.

## Build / test / ship
```powershell
dotnet build MyRemote.sln -c Release          # 0 warnings expected
dotnet test tests/MyRemote.Tests/MyRemote.Tests.csproj   # 26 tests, all pass (WebRtcLiveDiagnosticTests hits the real internet)
dotnet publish src/MyRemote.Desktop/MyRemote.Desktop.csproj -c Release -r win-x64 --self-contained true -o release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```
Output: single `release/MyRemote.exe` (unsigned). Runtime data: `%LOCALAPPDATA%\MyRemote\` (`myremote.db` SQLite, `logs\`). Received files: `%USERPROFILE%\Downloads\MyRemote`.

## Projects (dependency direction: Desktop → everything; libs → Core)
| Project | TFM | What lives there |
|---|---|---|
| `MyRemote.Core` | net9.0 | Models (`SessionModels.cs`: `ConnectionState`, `SessionPermissions` [Flags], `RemoteSession`, `AppSettings`), interfaces (`IRepositories.cs`), `SessionManager` (single-session state machine), `RollingFileLogger` (redacts `token=`/`password=`) |
| `MyRemote.Storage` | net9.0 | SQLite: device identity (9-digit ID `XXX-XXX-XXX`), settings, session history |
| `MyRemote.Security` | net9.0 | `SecurityService`: 256-bit session tokens, fixed-time compare, ID validation, per-key sliding-window rate limit |
| `MyRemote.ScreenCapture` | net9.0-windows | `Win32ScreenCaptureService`: GDI `BitBlt` full-frame → WPF `JpegBitmapEncoder`. No cursor, no dirty rects |
| `MyRemote.Input` | net9.0-windows | `Win32InputInjector`: `SendInput` (VK only, no scancodes/extended flag), gated by permission + token |
| `MyRemote.Network` | net9.0 | `IPeerTransport` + two impls (below), `WireFraming` (5-byte header: type u8 + len i32 BE, 16 MB cap), `WireMessages` DTOs, `LanDiscoveryService` (UDP 48721 broadcast), `InternetDiagnosticsService` (STUN), `TlsCertificateHelper` |
| `MyRemote.FileTransfer` | net9.0 | 64 KB chunks, filename sanitize, SHA-256 verify on complete |
| `MyRemote.Desktop` | net9.0-windows | WPF exe. `App.xaml.cs` = DI composition root + tray icon. `RemoteSessionCoordinator` = the glue. `MainViewModel`, `MainWindow` (host dashboard), `IncomingRequestWindow` (consent dialog), `SessionViewerWindow` (controller view), `VoiceAudioService` (winmm 16 kHz PCM) |
| `MyRemote.Tests` | xUnit | storage, security, framing, loopback TLS, capture, file transfer, live WebRTC signaling |

## Two transports, one interface
`IPeerTransport` (events for every message type + `SendRawAsync` / `SendTokenJsonAsync` / `DisconnectAsync`).
- **`RemotePeerTransport` — LAN / direct IP.** TCP `48720` on `IPAddress.Any` + `SslStream` TLS 1.2/1.3, ephemeral self-signed ECDSA cert. One read loop per session → `DispatchFrame`.
- **`WebRtcPeerTransport` — Internet by Device ID.** SIPSorcery `RTCPeerConnection` + one reliable ordered DataChannel `"myremote"`. Signaling = PeerJS protocol over WebSocket to public `0.peerjs.com` (peer id `myremote-<9digits>`) unless `AppSettings.CustomSignalingUrl`. ICE: Google + Cloudflare STUN, plus TURN only from `CustomTurnServer` + `TurnUsername`/`TurnPassword` (**no public TURN fallback**: OpenRelay is dead, verified 2026-11-28). Without TURN, CGNAT/symmetric-NAT peers cannot connect, which is the #1 cause of "remote doesn't connect". App-level chunking: 13-byte header (msgId u32, chunkIdx u16, total u16, type u8, totalLen i32) with ≤16 000-byte chunks, reassembled in `ChunkAssembly`.
  - Controller phases with distinct errors: ANSWER (15 s, or PeerJS `EXPIRE` = offline) → DataChannel open (30 s = NAT problem) → consent (60 s + margin).
  - `_pcGeneration`: every peer connection has a generation; callbacks from older ones are ignored. Keep this when touching the file.
  - Client queues its ICE candidates until the OFFER is sent; host keeps early candidates from the offering peer.
  - `IsCongested` (bufferedAmount > 256 KB) → host skips frames; `FileChunk` sends wait for buffer < 1 MB.
- `MainViewModel.ConnectAsync` routing: 9-digit ID found in LAN discovery → LAN; 9-digit ID otherwise → WebRTC; `IP[:port]` → LAN transport direct.

## Session flow (both transports)
1. Controller sends `HelloRequest {DeviceId, DisplayName, RequestedPermissions, Nonce, ProtocolVersion}`. Host rejects `ProtocolVersion != ProtocolInfo.CurrentVersion` (now 2) with an "update both PCs" message. **Bump `ProtocolInfo.CurrentVersion` whenever the wire format changes.**
2. Host: rate-limit (5/30 s per IP or peer id) → **atomically claims the slot** with `ISessionManager.TryTransition(Idle → …)` (a second request is refused, no second dialog) → `IncomingApprovalHandler(request, ct)` → `IncomingRequestWindow` (60 s auto-reject; `ct` closes it if the requester leaves).
3. Host replies `ApprovalResponse {Approved, GrantedPermissions, SessionToken}`; both sides `SessionManager.StartSession`.
4. `RemoteSessionCoordinator.OnSessionChanged` (new SessionId, IsHost) starts streaming: capture JPEG → `ScreenFrame` at target FPS (5–60, default 30, quality 75, scale 0.85). Never start streaming from the approval handler.
5. Controller → host: `InputCommand`, `SelectMonitor`, `QualitySettings`, `ClipboardText`, `ChatText`, `FileOffer`, `FileComplete` as `TokenEnvelope<T>{SessionToken, Data}` JSON. `FileChunk` and `VoiceAudio` use `BinaryEnvelope` (`[u8 len][token][u8 len][id][data]`), sent via `PeerTransportExtensions.SendTokenBinaryAsync`.
6. File flow: `FileOffer` → host prompt (non-blocking) → host `FileAccept {TransferId, Accepted}` → controller streams chunks → `FileComplete` → host verifies SHA-256.
7. Both directions: `ChatText` (host chat panel in `MainWindow` banner), `ClipboardText` (auto via `ClipboardMonitor` when ClipboardShare granted).
8. Host can `RevokeInputAsync` (sends `PermissionUpdate`) or disconnect (`Disconnect` message).
Message enum: `Protocol/WireFraming.cs` `WireMessageType` (1–17). Adding a message = enum value + handling in **both** transports' dispatch switch + coordinator/viewer wiring + protocol version bump if incompatible.

## Conventions
- Primary constructors, records for DTOs, `ConfigureAwait(false)` in libs, `System.Text.Json` defaults (PascalCase).
- UI thread work via `Application.Current.Dispatcher`. Transports raise events on network threads.
- Every controller→host action must check **both** `ISecurityService.ValidateSessionToken` and `ISessionManager.HasPermission`.
- Design rule from the original spec (`prompt.md`): no hidden access, explicit consent for every session, host can always terminate. Do not add silent unattended access without an explicit opt-in design (see ROADMAP).
- Version: single source is `<Version>` in `MyRemote.Desktop.csproj` (now 1.0.5); `MainViewModel.AppVersion` reads the assembly. Add a `CHANGELOG.md` entry per release.
- Only one instance per Windows user (named mutex in `App.OnStartup`).

## Known defects (remaining after Phase 0 / v1.0.5)
Fixed in 1.0.5: dead TURN fallback, no TURN credentials, early ICE candidates dropped, double consent dialog, streaming race, unbounded WebRTC frame queue, blocking/unsafe file transfer, no host chat, one-way clipboard, voice without token, no version check, multi-instance ID clash. See CHANGELOG.md.

Security (Phase 3)
- LAN client accepts **any** TLS cert: `RemotePeerTransport.cs` `(_,_,_,_) => true` → LAN MITM possible.
- No real device identity: `SqliteDeviceIdentityProvider.cs` generates a key, stores only a fingerprint, discards the private key. Nothing is signed.
- Public PeerJS: anyone can register `myremote-<id>` while that PC is offline; controller never verifies who answered.
- TURN password stored in plain text in SQLite settings (should use DPAPI).
Correctness / UX
- `ConnectionState.Reconnecting` exists but nothing reconnects; any blip ends the session.
- Video: full JPEG frames (Phase 1: DXGI + dirty rects + H.264).
Capability gaps (architectural, Phase 4)
- Runs as the logged-in user → cannot see/click UAC prompts, lock screen, Ctrl+Alt+Del, or elevated windows (UIPI). No Windows service.
- Remote cursor not drawn; Alt+Tab / Win key captured locally; no extended-key flag; no Unicode text input.

## Where to change what
- New permission → `SessionPermissions` + `IncomingRequestWindow` checkbox + enforcement at receive site.
- Capture/codec → implement `IScreenCaptureService` (in `MonitorModels.cs`), swap DI registration in `App.xaml.cs`.
- Infra endpoints → `WebRtcPeerTransport.BuildSignalingUri` / `BuildIceServers`, defaults in `AppSettings`.
- Connection troubleshooting: logs at `%LOCALAPPDATA%\MyRemote\logs` show every ICE candidate (`typ host/srflx/relay`), ICE state and the phase that failed. Verify a TURN server with test `Turn_Relay_Probe` (env vars `MYREMOTE_TURN_URL/USER/PASS`).
