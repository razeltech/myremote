# MyRemote Networking Guide

## Ports used
| Port | Protocol | Purpose |
|---|---|---|
| `48720` (configurable) | TCP | LAN / direct-IP TLS session |
| `48721` (configurable) | UDP | LAN peer discovery broadcast |
| outbound 443 | TCP (WebSocket) | Internet signaling (PeerJS protocol) |
| outbound UDP (random) | UDP | WebRTC ICE / STUN / TURN |

## LAN mode (offline)
PCs on the same Wi-Fi/Ethernet subnet discover each other automatically. No internet or server needed.

## Internet mode: how a connection is made
1. **Signaling:** both PCs keep a WebSocket open to the signaling server (`0.peerjs.com` by default) under their Device ID. It only forwards the connection setup messages (OFFER, ANSWER and ICE candidates). Screen data never goes through it.
2. **ICE:** each PC learns its public address from STUN (Google/Cloudflare) and both try to reach each other directly.
3. **TURN relay (fallback):** if a direct path is impossible, traffic is relayed through a TURN server. The traffic stays end-to-end encrypted with DTLS, so the relay can't read it.

### Why internet connections fail without TURN
Direct P2P works only when the NAT on at least one side allows it. It fails when:
- one PC is on **mobile data / hotspot** (carrier-grade NAT), or
- a router uses **symmetric NAT**, or a corporate firewall blocks UDP.

Up to v1.0.4 MyRemote used the public "OpenRelay" TURN server. **It no longer gives relay addresses** (verified 2026-11-28 on ports 80, 443 and 3478), so those connections timed out. v1.0.5 removed it. You need your own TURN server, which is cheap and private.

The controller's error message tells you which step failed:
| Message | Meaning | Fix |
|---|---|---|
| "Remote PC … is offline" / "did not answer" | Host not running MyRemote, not online, or busy | Start MyRemote on the host and check its "Internet: online" line |
| "could not open a direct network path … Configure your own TURN relay" | NAT blocks P2P | Set up TURN (below) |
| "your TURN relay gave no relay address" | Wrong TURN URL/credentials or blocked port | Run the TURN probe test (below) |
| "Version mismatch … Update MyRemote on both PCs" | Different MyRemote versions | Install the same version on both |
| "Device ID … is already registered" | Two MyRemote copies share an ID | Close the other copy (v1.0.5 allows only one per user) |

## Set up your own TURN relay (coturn), about 20 minutes
Any small Linux VPS with a public IPv4 address works (1 vCPU / 512 MB is enough for a few sessions).

1. **Install** (Ubuntu/Debian):
   ```bash
   sudo apt update && sudo apt install -y coturn
   sudo sed -i 's/#TURNSERVER_ENABLED=1/TURNSERVER_ENABLED=1/' /etc/default/coturn
   ```
2. **Configure** `/etc/turnserver.conf` (replace `PUBLIC_IP` and pick a long random password):
   ```ini
   listening-port=3478
   fingerprint
   lt-cred-mech
   realm=myremote
   user=myremote:CHANGE_ME_LONG_RANDOM_PASSWORD
   external-ip=PUBLIC_IP
   min-port=49160
   max-port=49200
   no-cli
   no-multicast-peers
   # Never relay into private networks (protects the VPS's own network)
   denied-peer-ip=10.0.0.0-10.255.255.255
   denied-peer-ip=172.16.0.0-172.31.255.255
   denied-peer-ip=192.168.0.0-192.168.255.255
   denied-peer-ip=127.0.0.0-127.255.255.255
   denied-peer-ip=169.254.0.0-169.254.255.255
   ```
3. **Open the firewall** (both the VPS firewall and the cloud provider's security group):
   UDP+TCP `3478` and UDP `49160-49200`.
   ```bash
   sudo ufw allow 3478 && sudo ufw allow 49160:49200/udp
   sudo systemctl restart coturn
   ```
4. **Configure MyRemote** on **both** PCs under Settings, then click Save:
   - TURN Relay Server(s): `turn:PUBLIC_IP:3478, turn:PUBLIC_IP:3478?transport=tcp`
   - TURN Username: `myremote`
   - TURN Password: the password from step 2

   The status line should then read "Internet: online as … TURN relay configured".
5. **Verify from Windows** before testing with a client:
   ```powershell
   $env:MYREMOTE_TURN_URL="turn:PUBLIC_IP:3478"; $env:MYREMOTE_TURN_USER="myremote"; $env:MYREMOTE_TURN_PASS="..."
   dotnet test tests/MyRemote.Tests --filter Turn_Relay_Probe --logger "console;verbosity=detailed"
   ```
   It passes only if a `typ relay` candidate appears.

Security notes: use a long random password and give it only to people who use your MyRemote build. The relay sees encrypted traffic only. Per-session expiring credentials are planned (ROADMAP Phase 2).

## Optional: your own signaling server
The public `0.peerjs.com` only sees Device IDs and connection setup messages, never screen data. To remove that dependency too:
```bash
docker run -d --restart unless-stopped -p 9000:9000 peerjs/peerjs-server
```
Then set **Custom Signaling URL** to `ws://PUBLIC_IP:9000/peerjs` on both PCs. For production, put it behind TLS (e.g. Caddy) and use `wss://your.domain/peerjs`. Both PCs must use the **same** signaling server.

## Direct IP / VPN
If the host forwards TCP `48720`, or both PCs share a VPN (ZeroTier/Tailscale/WireGuard) or IPv6, enter `IP:port` in the Connect bar to use the TLS transport directly.
