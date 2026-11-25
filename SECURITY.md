# MyRemote Security Model

## Core Guarantees
1. **Explicit Consent Only:** `RemotePeerTransport` will not transition to `Connected` or stream any screen frames unless `IncomingApprovalHandler` returns `Approved = true` with a freshly generated 256-bit CSPRNG `SessionToken`.
2. **No Unattended Access:** There is no background Windows service, no saved password bypass, and no auto-accept setting.
3. **Encrypted Transport:** All peer-to-peer control, screen, input, clipboard, chat, and file-transfer traffic runs over `SslStream` (TLS 1.2 / TLS 1.3) using an ephemeral ECDSA P-256 certificate.
4. **Live Permission Enforcement & Revocation:** `Win32InputInjector` and `FileTransferService` verify both `ISessionManager.HasPermission(...)` and constant-time `SessionToken` equality on every action. The host can revoke input or terminate the session immediately from the red Active Session Banner.
5. **DoS & Abuse Protections:**
   - Sliding-window rate limiting on incoming connection attempts (`5` requests / `30s` per IP).
   - `WireFraming` enforces a strict `16 MB` maximum frame size and rejects unknown message types.
   - `FileTransferService` strips directory traversal sequences and verifies SHA-256 before committing received files.
   - `RollingFileLoggerProvider` redacts `token=...` and `password=...` patterns before writing logs.
