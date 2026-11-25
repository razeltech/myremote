# MyRemote Troubleshooting Guide

1. **LAN peer does not appear in Discovered LAN Devices:**
   - Ensure both PCs are on the same subnet and Windows Firewall allows UDP `48721` and TCP `48720` on Private networks.
   - Verify "Enable Local Network (LAN) Peer Discovery" is checked in Settings.
2. **Connection rejected or timed out:**
   - The host user must click **Accept Session** on the Incoming Connection Request dialog.
   - Repeated rapid connection attempts from the same IP are rate-limited (5 per 30 seconds).
3. **Mouse or keyboard input does not move on host:**
   - Check that "Mouse & Keyboard Control" was granted in the Incoming Request dialog and was not revoked via **Revoke Input**.
   - Note: UAC elevation prompts on Windows run on the Secure Desktop and cannot be controlled by non-elevated applications (by design, for Windows security).
4. **Logs location:**
   - `%LocalAppData%\MyRemote\logs\myremote-YYYY-MM-DD.log`
