# MyRemote Test Report

## Automated Test Suite (`MyRemote.Tests`)
- **Command:** `dotnet test tests/MyRemote.Tests/MyRemote.Tests.csproj`
- **Result:** 12 passed, 0 failed, 0 skipped.

### Coverage Matrix
| Area | Status | Notes |
|---|---|---|
| Device ID generation & SQLite persistence | Unit tested | Verified across DB re-opens |
| Settings & Session History SQLite repos | Unit tested | Verified read/write/update |
| SessionManager state machine & permission revocation | Unit tested | Verified live revocation |
| SecurityService (CSPRNG tokens, constant-time check, rate limit) | Unit tested | Verified sliding window |
| RollingFileLogger secret redaction | Unit tested | Redacts tokens & passwords |
| Win32ScreenCaptureService monitor enumeration | Unit tested | Verified >= 1 monitor |
| Win32InputInjector authorization & token gate | Unit tested | Rejects missing perm / bad token |
| FileTransferService permission, path sanitization, SHA-256 | Unit tested | Verified corruption rejection |
| WireFraming oversized/malformed frame rejection | Unit tested | Throws `InvalidDataException` |
| STUN RFC 5389 XOR-MAPPED-ADDRESS parser | Unit tested | Verified IPv4:Port decoding |
| Loopback TLS session (reject, approve, stream, disconnect) | Integration tested | Verified on `127.0.0.1` |
| WPF Desktop launch & shutdown | Manually verified | Verified `MainWindowTitle` & exit |
| Two physical Windows PCs on separate LAN/WAN hardware | Not yet verified | Single-machine environment; loopback TLS verified |
