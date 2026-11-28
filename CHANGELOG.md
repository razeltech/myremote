# Changelog

## [1.0.5] - 2026-11-28 — Phase 0: connection reliability & stabilization
**Both PCs must run 1.0.5** (wire protocol v2; older peers get a clear "update" message).

Internet connections (root causes of "remote PC doesn't connect")
- Removed the public OpenRelay TURN fallback: it no longer returns relay candidates, so strict-NAT/CGNAT peers always timed out. Added **TURN username/password** settings (previously a self-hosted TURN could not be authenticated) and a coturn guide in NETWORKING.md.
- Fixed ICE candidates sent before the OFFER being silently dropped on the host.
- Candidates from other peers / stale connections are no longer applied to the current connection.
- Connection is now three timed phases with specific errors: offline/no answer (15 s), no network path → configure TURN (30 s), host consent (60 s). Previously one 45 s timeout also covered the human clicking Accept.
- Handles PeerJS `EXPIRE` (peer offline) and `ID-TAKEN`; signaling counts as ready only after the server confirms the ID.
- Generation guard: callbacks from a closed peer connection can no longer end the new session.
- Single-instance guard (a second copy shared the Device ID → "ID taken" and port-in-use crash).

Stability & security
- Host claims the session slot atomically before the consent dialog; a second request can no longer open a second dialog or overwrite the session.
- Consent dialog shows a 60 s auto-reject countdown and closes itself if the requester gives up.
- Streaming starts only after the session exists (removed `Task.Delay(150)` race).
- "Latest frame wins" flow control on WebRTC: frames are skipped while the send buffer is backed up instead of piling up seconds of lag.
- File transfer: host answers Accept/Reject before any data is sent, prompt no longer blocks the network thread, chunks are binary (no base64), ordered writes, oversize protection, progress in the viewer.
- Voice audio now requires the session token.
- Host-side chat panel; automatic two-way text clipboard sync (when Clipboard permission is granted).
- Clear LAN errors (refused / timed out / host did not answer). Monitor list cached instead of enumerated every frame.
- Version now comes from the assembly (1.0.5).

## [1.0.0] - 2026-11-25
- Initial complete release of MyRemote (Phases 1–6).
- Modular 9-project .NET 9 + WPF architecture.
- Offline LAN UDP discovery and TLS 1.2/1.3 peer-to-peer remote desktop.
- Explicit host approval dialog, live permission revocation, and immediate disconnect.
- Multi-monitor capture, quality/scale controls, full-screen, clipboard sync, SHA-256 file transfer, and session chat.
- RFC 5389 STUN & IPv6 network diagnostics.
- Automated xUnit test suite (12/12 passing) and PowerShell installer/uninstaller.
