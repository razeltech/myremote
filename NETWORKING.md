# MyRemote Networking Guide

## Ports Used
- **TCP `48720`** (configurable): TLS-encrypted peer session transport.
- **UDP `48721`** (configurable): Local subnet peer discovery broadcast.

## LAN Mode (Offline)
- Both computers on the same Wi-Fi or Ethernet subnet discover each other automatically via UDP broadcast.
- No internet connection or external server is required.

## Internet Connectivity & NAT Traversal
- **Direct IP / Port Forwarding:** If the host router forwards TCP `48720` to the host PC (or both peers share a VPN/ZeroTier/Tailscale/IPv6 path), the controller can connect directly by entering `IP:Port` in the Connect bar.
- **STUN Diagnostics:** Settings -> **Run Network Diagnostics** sends an RFC 5389 STUN Binding Request to the configured STUN server (`stun.l.google.com:19302` by default) and reports your public reflexive `IP:Port` and global IPv6 availability.
- **Important Limitations:** STUN only discovers your public reflexive address; it does **not** relay media. Symmetric NATs and Carrier-Grade NAT (CGNAT) often block direct inbound connections.
- **Optional Self-Hosted TURN / Signaling:** If your network blocks direct peer-to-peer connections, you can self-host an open-source TURN server (such as `coturn` on a small VPS with 1 vCPU, 1 GB RAM, and a public IPv4 address, opening UDP/TCP `3478` and relay ports `49152-65535`) and configure it in Settings.
