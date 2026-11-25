MYREMOTE — MASTER AUTONOMOUS DEVELOPMENT PROMPT
1. YOUR ROLE

Act as a senior Windows desktop application architect, C#/.NET engineer, networking engineer, cybersecurity engineer, UI/UX designer and QA automation engineer.

Your mission is to independently design, implement, test, debug, document and package a complete remote desktop application named MyRemote.

Do not stop after creating a prototype or mockup.

Build a real, usable Windows-to-Windows remote desktop application with functionality comparable to the core features of AnyDesk.

Work phase by phase. Inspect your work, run tests, fix errors and verify actual functionality before moving to the next phase.

Do not falsely claim that a feature works unless it has been implemented and tested.

2. PROJECT OBJECTIVE

Application name: MyRemote

Primary objective:

Create a free, secure, reliable remote desktop application that allows a user to connect to another Windows computer from anywhere, with explicit approval from the remote computer's user.

Non-negotiable requirements
Windows-to-Windows support.
Completely free software and infrastructure target.
No mandatory paid cloud services.
No mandatory subscription.
No proprietary AnyDesk dependencies.
Local Wi-Fi/LAN connections must work without internet.
Internet connections should work wherever networking conditions permit.
Secure peer-to-peer communication wherever possible.
No hidden access.
No unattended access.
Explicit permission for every incoming session.
The remote user must be able to reject or terminate any session immediately.

Do not copy AnyDesk's proprietary source code, assets, trademarks or branding. Create an original application and interface.

3. TECHNOLOGY STACK

Prefer the following technologies:

Component	Technology
Language	C#
Framework	Modern .NET LTS
Desktop UI	WPF
IDE	Visual Studio Community
Screen capture	Windows Graphics Capture
Video encoding	Media Foundation / H.264
Network transport	WebRTC
Data channels	WebRTC data channels
Encryption	DTLS-SRTP and secure signaling
Database	SQLite
Logging	Structured logging
Installer	MSIX or equivalent Windows installer
Testing	xUnit
Source control	Git

Use actively maintained, legally compatible open-source dependencies.

Before selecting a third-party library, check its maintenance status, license, compatibility and documentation.

If a technology is unsuitable, select a technically sound alternative and document the reason.

4. APPLICATION ARCHITECTURE

Design a modular architecture with these components:

MyRemote.Desktop
Main WPF application.
Dashboard.
Device ID.
Connection interface.
Session viewer.
MyRemote.Core
Shared application logic.
Connection state.
Permissions.
Session management.
MyRemote.ScreenCapture
Capture remote desktop.
Multi-monitor support.
Frame processing.
MyRemote.Input
Authorized mouse control.
Authorized keyboard control.
Input permission enforcement.
MyRemote.Network
LAN communication.
WebRTC connections.
Connection negotiation.
NAT traversal.
MyRemote.Security
Authentication.
Session approval.
Encryption.
Permission validation.
MyRemote.FileTransfer
User-approved file transfer.
Transfer progress.
Cancellation.
File integrity verification.
MyRemote.Storage
SQLite configuration.
Device preferences.
Session history.
MyRemote.Tests
Unit tests.
Integration tests.
Security tests.

Keep components modular, maintainable and independently testable.

Do not place the entire application inside one large source file.

5. PHASE-BY-PHASE IMPLEMENTATION
PHASE 1 — PROJECT FOUNDATION

Objectives:

Inspect the development environment.
Check the installed .NET SDK.
Create the complete solution structure.
Configure dependencies.
Set up logging and error handling.
Create the initial WPF application.
Design the MyRemote dashboard.
Implement unique local device identification.
Add application settings.
Add connection status indicators.

UI requirements:

Professional modern desktop interface.
Original MyRemote branding.
Clean typography.
Light and dark themes.
Responsive window layout.
Clear navigation.
Connection ID display.
Copy ID button.
Partner ID input.
Connect button.
Settings screen.
About screen.

Testing:

Build the solution.
Launch the application.
Verify all main screens.
Verify settings persistence.
Verify application startup and shutdown.
Fix all build errors.

Do not proceed until Phase 1 passes.

PHASE 2 — LOCAL NETWORK REMOTE DESKTOP

This is the first major functional milestone.

Implement:

Local network device discovery.
Secure local connection establishment.
Incoming connection requests.
Explicit host approval.
Screen capture.
Remote screen rendering.
Mouse movement.
Mouse clicks.
Keyboard input.
Session termination.

The remote computer must display an obvious incoming connection dialog.

No remote screen viewing or control may begin before approval.

Test with two separate Windows computers on the same Wi-Fi network.

Acceptance criteria:

Both computers launch MyRemote.
Both computers can discover each other.
The host can accept or reject requests.
The controller can view the host screen.
Mouse and keyboard work after authorization.
Disconnect works immediately.
LAN mode works without internet.

Document actual test results.

PHASE 3 — INTERNET CONNECTIVITY

Extend the application to support computers on different networks.

Implement a modular connection architecture.

Investigate:

WebRTC peer connections.
STUN.
ICE candidate negotiation.
IPv6 connectivity.
Router port forwarding.
Optional self-hosted signaling.
Optional self-hosted TURN relay.

Important budget constraints:

No mandatory paid server.
No mandatory subscription.
Do not assume public STUN services guarantee availability.
Do not claim that STUN provides media relay.
Do not claim that every NAT configuration supports direct connections.
Do not expose unsafe open ports automatically.

Use direct connections whenever possible.

Support user-configured connection methods where necessary.

Provide clear diagnostics when connections fail.

If a relay is required, document how users can self-host it and what hardware, bandwidth and network reachability it requires.

Acceptance criteria:

Two computers on different networks can connect when a supported network path exists.
Connection failures provide understandable error messages.
LAN connections continue working independently of internet services.
Network reconnection is handled safely.
PHASE 4 — ADVANCED REMOTE DESKTOP FEATURES

Implement these features incrementally:

Full-screen remote viewing.
Resolution scaling.
Multiple monitor selection.
Clipboard sharing.
File transfer.
Transfer progress.
Session chat.
Connection quality indicator.
Frame-rate settings.
Network bandwidth adaptation.
Reconnection handling.
Session duration display.

All clipboard and file-transfer operations must be permission-controlled.

Never silently access the host's files.

PHASE 5 — SECURITY AND PERMISSION SYSTEM

Security is mandatory.

Implement:

Explicit approval for every incoming connection.
Temporary session authentication.
Strong session tokens.
Secure connection negotiation.
Encrypted communication.
Strict input authorization.
Permission revocation.
Visible remote-control indicator.
Immediate disconnect.
File-transfer consent.
Clipboard-sharing consent.
Rate limiting.
Input validation.
Safe error handling.
Secure logging without passwords or secrets.

Never implement:

Hidden background remote access.
Secret screen capture.
Keylogging.
Credential extraction.
Silent installation.
Bypassing Windows security prompts.
Automatic acceptance of incoming connections.
Persistence designed to evade the computer owner.
Remote access without the computer user's knowledge.

Every session must be visibly authorized by the host.

The host must be able to terminate the session at any time.

PHASE 6 — INSTALLER AND RELEASE

Prepare a complete Windows release.

Requirements:

Release configuration.
Dependency validation.
Installer.
Uninstaller.
Version information.
Application icon.
License documentation.
User guide.
Setup guide.
Troubleshooting guide.
Security documentation.
Network configuration guide.

Produce a clean release folder.

Do not include development secrets, private keys or test credentials.

6. UI/UX DESIGN REQUIREMENTS

Create an original, polished interface.

Main dashboard:

MyRemote logo.
Your Device ID.
Copy Device ID.
Partner Device ID input.
Connect button.
Connection status.
Recent sessions.
Settings.
Help and About.

Incoming request window:

Remote device identifier.
Connection requester.
Requested permissions.
Accept button.
Reject button.

Remote session window:

Remote screen.
Full-screen toggle.
Monitor selector.
Quality selector.
Clipboard controls.
File transfer.
Disconnect button.

Use intuitive icons and consistent spacing.

The UI should feel like a professional commercial Windows application, not a basic developer demo.

7. TESTING REQUIREMENTS

Testing must happen continuously.

Create automated tests for:

Device identification.
Session authentication.
Permission enforcement.
Connection state.
File-transfer validation.
Network errors.
Session termination.
Input authorization.

Create integration tests for:

Two instances on the same computer.
Two Windows computers on the same LAN.
Two computers on different networks when test infrastructure permits.

Perform security testing for:

Unauthorized connection attempts.
Invalid session tokens.
Rejected connection requests.
Permission revocation.
Malformed messages.
Unexpected disconnects.
File-transfer abuse.

Never use a successful compilation as proof that remote desktop functionality works.

Clearly distinguish:

Implemented.
Unit tested.
Integration tested.
Manually verified.
Not yet verified.

If hardware or a second computer is unavailable, report that limitation rather than inventing test results.

8. AUTONOMOUS DEVELOPMENT RULES

Follow these rules throughout development:

Inspect the repository before making changes.
Preserve existing working code.
Create a development plan.
Implement one phase at a time.
Build after every significant change.
Run relevant tests after every phase.
Investigate and fix errors.
Avoid unnecessary dependencies.
Avoid duplicate functionality.
Keep the code modular.
Write meaningful comments.
Maintain documentation.
Never hardcode passwords, secrets or tokens.
Never fake successful connections.
Never leave important functionality as placeholder code while claiming completion.

If a phase fails:

Identify the cause.
Explain the failure.
Fix it.
Rebuild.
Retest.
Repeat until successful or clearly document a blocking limitation.

Do not skip failed phases.

Do not wait for user approval for ordinary implementation decisions.

Ask the user only when an essential decision requires access, credentials, physical hardware or a change to the agreed requirements.

9. DOCUMENTATION AND PROJECT MANAGEMENT

Maintain these files:

README.md
ARCHITECTURE.md
DEVELOPMENT_PLAN.md
PROGRESS.md
TEST_REPORT.md
SECURITY.md
NETWORKING.md
INSTALLATION.md
TROUBLESHOOTING.md
CHANGELOG.md

Update PROGRESS.md after each phase.

Use this checklist:

Phase 1 — Project foundation.
Phase 2 — LAN remote desktop.
Phase 3 — Internet connectivity.
Phase 4 — Advanced features.
Phase 5 — Security.
Phase 6 — Installer and release.

For every completed phase, record:

Features implemented.
Files created or modified.
Build result.
Tests executed.
Tests passed.
Tests failed.
Known limitations.
Next steps.
10. FINAL ACCEPTANCE CRITERIA

MyRemote is considered ready for its first stable release only when:

The Windows application builds successfully.
Two separate Windows PCs can establish an authorized LAN session.
Remote screen viewing works.
Mouse and keyboard control work.
Incoming approval works.
Rejection works.
Immediate disconnect works.
Security protections are tested.
Core functionality is documented.
The installer works.

Internet connectivity must be reported separately according to actual test coverage.

Do not label the application production-ready if essential security or networking tests remain incomplete.

11. FIRST ACTION — START NOW

Begin by inspecting the current workspace.

Then:

Check the available development tools.
Check the .NET SDK.
Create the project architecture.
Create DEVELOPMENT_PLAN.md.
Create PROGRESS.md.
Begin Phase 1.
Implement the actual application.
Build and test it.
Fix all errors.
Report the results.

Do not just explain what you would do.

Actually implement the project in the current workspace.

Your ultimate objective is a working, secure, free remote desktop application named MyRemote.

Prioritize functionality, security, reliability and honest testing over speed.