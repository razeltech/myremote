# MyRemote — Why it can't replace AnyDesk yet, and the plan to get there

**Goal:** I connect to my clients' PCs from anywhere, for as long as I need, with no 5-minute cutoffs or "commercial use" blocks. Clients should only have to run one file and click **Accept**.

Audit date: 2026-11-28 (v1.0.4 WIP). Code-level details and line references are in [PROJECT_GUIDE.md](PROJECT_GUIDE.md).

---

## 1. Why it isn't a replacement today

These are ordered by how much each one hurts a real support session.

| # | Gap | What you'd notice with a client | Root cause in code |
|---|-----|--------------------------------|--------------------|
| 1 | **Video pipeline** | Laggy and blurry on normal home internet, with delay that keeps growing | Every frame is a full-screen JPEG made with GDI BitBlt (about 100–250 KB per frame). At 30 FPS that's 25–60 Mbit/s. AnyDesk and RustDesk send only changed regions with a real codec, using 0.2–2 Mbit/s for office work. There's also no backpressure: frames pile up in the WebRTC send queue. |
| 2 | **Free public servers** | "Signaling not connected", or connections fail from some networks | Signaling runs on public `0.peerjs.com` and relay runs on the public `openrelay.metered.ca`. Both are shared and best-effort, with no guarantee, and either can disappear or throttle you. |
| 3 | **No admin / UAC / lock screen** | The screen freezes or goes black when the client's PC shows a UAC prompt (e.g. while you install software). You can't type a Windows password or press Ctrl+Alt+Del, and you lose the machine after a reboot. | The app runs as the logged-in user. AnyDesk installs a SYSTEM service that follows the secure desktop. |
| 4 | **No identity / trust** | Someone could claim your client's ID while it's offline, and on a LAN someone could intercept the connection | IDs are random numbers registered first-come on a public server. Nothing is signed, and the LAN TLS client accepts any certificate. |
| 5 | **Fragile sessions** | A Wi-Fi blip ends the session | `Reconnecting` exists in the state machine but nothing uses it. |
| 6 | **Input gaps** | Alt+Tab and the Win key act on *your* PC. Arrow and Delete keys misbehave in some apps. You can't see the remote cursor shape. | `SendInput` sends only the virtual-key code (no scancodes or extended flag). There's no low-level keyboard hook and no cursor capture. |
| 7 | **Support workflow** | The client can't chat back. Clipboard only goes your way, and only when you click a button. File transfer is unreliable. There's no address book. | Missing UI on the host side. The file protocol never waits for the host to accept. |
| 8 | **Distribution** | Windows SmartScreen shows "Windows protected your PC" and clients get scared | The exe is unsigned and there's no auto-update. |

What already works well: a clean modular architecture, a consent dialog with fine-grained permissions, both LAN and Internet transports, a single-file exe, logging, and passing tests. The foundation is sound. The weak spots are the media and infrastructure layers, which is exactly where AnyDesk spent its engineering.

### Principles
This is your own security-first tool for a small, trusted circle, not a product. So:
**own the infrastructure** (your TURN/signaling), **consent by default**, **verify identities**, and **send nothing that isn't needed**.

### Root cause of "remote client doesn't connect" (found 2026-11-28, fixed in 1.0.5)
1. The free public TURN relay (OpenRelay) returns **no relay candidates** on any port, so any client on mobile data / CGNAT / strict NAT could never connect. **You need your own TURN server** (≈20 min, see [NETWORKING.md](NETWORKING.md)).
2. A self-hosted TURN couldn't be used anyway: there was no username/password setting.
3. ICE candidates sent before the OFFER were dropped by the host.
4. A single 45 s timeout also covered the human clicking Accept, and failures all looked the same ("timed out").

---

## 2. Implementation plan

Each phase is shippable on its own. Rough effort is for one developer working part-time.

### Phase 0: Stabilize what exists ✅ done in v1.0.5
- [x] Connection root causes above (dead TURN, TURN credentials, candidate ordering, phased timeouts with clear errors, EXPIRE/ID-TAKEN handling, single instance).
- [x] Sync the version (csproj is the single source).
- [x] Host claims the session slot atomically before the dialog, rejects a second request while one is pending, and the dialog has a 60 s countdown and closes if the requester leaves.
- [x] Start streaming only after `StartSession` completes.
- [x] File transfer: Accept/Reject before data, non-blocking prompt, binary chunks, ordered writes, oversize guard, progress.
- [x] Token check on `VoiceAudio`; monitor list cached.
- [x] Host-side chat panel; automatic two-way text clipboard.
- [x] Pulled forward from Phase 1: "latest frame wins" flow control.
- [x] Protocol version handshake.
- [ ] **You:** set up your coturn (NETWORKING.md), run `Turn_Relay_Probe`, then test between two PCs in different cities.

### Phase 1: Make it fast (≈3–5 weeks), the biggest win
1. ~~**Flow control first**~~ ✅ done in 1.0.5 (`bufferedAmount` threshold).
2. **DXGI Desktop Duplication** (`IDXGIOutputDuplication`) replaces BitBlt. It's GPU-backed, gives you **dirty rectangles** and **cursor shape and position** for free, and handles 60 FPS.
3. **Intermediate step:** send only the dirty tiles (JPEG/WebP per tile) plus cursor updates. That's typically a 10–50× bandwidth cut for office work.
4. **Real codec:** H.264 through a Media Foundation encoder MFT (hardware NVENC/QuickSync/AMF, falling back to software). Send it on a WebRTC **video track** (unreliable, low latency) instead of the reliable DataChannel. Keep input and control on the DataChannel.
5. Adaptive bitrate and FPS from measured RTT and loss. Add a Speed / Balanced / Quality toggle in the viewer.

### Phase 2: Own your connectivity (≈1 week, about $0–5/month)
- [ ] A small VPS (or Oracle Cloud free tier) running **coturn** plus your own signaling server (`peerjs-server` in Docker, or a tiny ASP.NET WebSocket hub).
- [ ] Time-limited TURN credentials (TURN REST API, HMAC) instead of static public credentials.
- [ ] Bake your server address into the build and keep the public servers only as an optional fallback.
- [ ] A `docker-compose.yml` plus a short setup guide in `NETWORKING.md`.

### Phase 3: Identity and trust (≈1–2 weeks)
- [ ] Persist a real device key pair (ECDSA/Ed25519, private key protected with DPAPI). The ID is registered *with* the public key on your signaling server, so the first registration owns the ID and later ones must sign.
- [ ] Sign `Hello`/`Approval` with the device key. Bind the key to the DTLS/TLS fingerprint and remove the `=> true` certificate callback.
- [ ] Show a short fingerprint in the consent dialog. The controller pins hosts it has seen before (trust on first use) and warns when a key changes.

### Phase 4: Support-grade Windows integration (≈3–6 weeks), the AnyDesk "magic"
- [ ] Optional **install mode**: a Windows Service (LocalSystem) that launches a per-session agent with `CreateProcessAsUser` into the active console session and follows desktop switches (`OpenInputDesktop`/`SetThreadDesktop`).
  This gives you UAC prompts, the lock/login screen, Ctrl+Alt+Del (`SendSAS`), and **reconnecting after a reboot**.
- [ ] Portable mode stays as it is (no admin). In portable mode, the viewer shows a "Client must accept UAC locally" hint.
- [ ] Auto-reconnect: keep the session token alive for N seconds, then do an ICE restart or redial.

### Phase 5: Client-friendly workflow (≈2–3 weeks)
- [ ] **QuickSupport build:** a small exe whose only screen shows the ID and a big Accept/Deny button, pre-configured with your servers.
- [ ] Address book: saved clients with name, ID, pinned key, notes, and last session.
- [ ] Input: scancodes plus `KEYEVENTF_EXTENDEDKEY`, Unicode text, a low-level keyboard hook in the viewer for Alt+Tab and the Win key, Ctrl+Alt+Del and "Send keys" buttons, and remote cursor rendering.
- [ ] File manager (two-pane browse) and drag-and-drop file transfer.
- [ ] Opus voice (Concentus) instead of raw PCM.
- [ ] Session recording and an end-of-session summary, which is useful for billing clients.
- [ ] **Decision needed:** unattended access (connect without the client clicking Accept, protected by a strong per-machine password, set up only by the client, with a tray indicator that's always visible). AnyDesk offers this, but the original spec forbids it. Decide before Phase 4.

### Phase 6: Distribution (≈1 week, ongoing)
- [ ] Code signing (e.g. Azure Trusted Signing, about $10/month, or a free open-source signing program) to remove SmartScreen warnings.
- [ ] An auto-updater that checks GitHub Releases and verifies the signature before swapping.
- [ ] Crash reporting to the local log, plus "Send logs to technician" over the session.

### Suggested order if time is short
~~Phase 0 and flow control~~ (done), then **your own TURN now** (Phase 2, ≈20 min for the basic version), then Phase 3 (identity, since security is the point of this project), then Phase 1 steps 2–3 (DXGI and dirty tiles). At that point it's usable with clients for most jobs. Then Phase 3, Phase 4, Phase 5 and Phase 6.

## 3. Definition of "replaces AnyDesk for me"
- Connects by ID from a different city and network within 5 seconds, ≥95% of the time (including mobile hotspots and CGNAT, via your TURN server).
- 1080p office work at ≥20 FPS and under 150 ms latency on a 10 Mbit/s link.
- Can approve a UAC prompt and log back in after a reboot (in install mode).
- Survives a 10-second network drop without ending the session.
- The client experience is: run one file, click Accept. No warnings.
