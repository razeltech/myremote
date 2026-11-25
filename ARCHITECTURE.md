# MyRemote Architecture

## Solution Layout
- `MyRemote.Core` — Domain models (`DeviceIdentity`, `RemoteSession`, `SessionPermissions`, `AppSettings`), repository/security interfaces, secret-redacting `RollingFileLoggerProvider`, and `SessionManager` state machine.
- `MyRemote.Storage` — SQLite persistence (`SqliteDatabase`, `SqliteDeviceIdentityProvider`, `SqliteSettingsRepository`, `SqliteSessionHistoryRepository`).
- `MyRemote.Security` — CSPRNG session tokens, constant-time token comparison (`CryptographicOperations.FixedTimeEquals`), Device ID validation, sliding-window rate limiting, and SHA-256 helpers.
- `MyRemote.ScreenCapture` — Win32 GDI `BitBlt` + `EnumDisplayMonitors` multi-monitor capture with WPF JPEG encoding and scaling.
- `MyRemote.Input` — Win32 `SendInput` mouse/keyboard injector gated by `ISessionManager` permissions and `ISecurityService` token validation.
- `MyRemote.Network` — Length-prefixed `WireFraming` (16 MB cap), `TlsCertificateHelper` (ephemeral ECDSA P-256 `SslStream`), `LanDiscoveryService` (UDP broadcast), `RemotePeerTransport`, and `InternetDiagnosticsService` (RFC 5389 STUN).
- `MyRemote.FileTransfer` — Permission-gated chunked file transfer with path-traversal sanitization and SHA-256 verification.
- `MyRemote.Desktop` — WPF workbench UI (`MainWindow`, `IncomingRequestWindow`, `SessionViewerWindow`, `RemoteSessionCoordinator`, Light/Dark themes).
- `MyRemote.Tests` — xUnit unit, loopback TLS integration, capture, file transfer, and security tests.
