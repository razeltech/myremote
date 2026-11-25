namespace MyRemote.Core.Models;

public enum ConnectionState
{
    Idle = 0,
    Discovering = 1,
    Connecting = 2,
    AwaitingHostApproval = 3,
    Connected = 4,
    Reconnecting = 5,
    Disconnecting = 6,
    Failed = 7
}

public enum ThemeMode
{
    Dark = 0,
    Light = 1
}

public enum TransportMode
{
    LanDirect = 0,
    InternetDirect = 1,
    InternetRelay = 2
}

[Flags]
public enum SessionPermissions
{
    None = 0,
    ViewScreen = 1 << 0,
    MouseInput = 1 << 1,
    KeyboardInput = 1 << 2,
    ClipboardShare = 1 << 3,
    FileTransfer = 1 << 4,
    SessionChat = 1 << 5,
    AllControl = ViewScreen | MouseInput | KeyboardInput | ClipboardShare | FileTransfer | SessionChat
}

public sealed record SessionRequest(
    string RequestId,
    string RequesterDeviceId,
    string RequesterDisplayName,
    string RequesterIpAddress,
    SessionPermissions RequestedPermissions,
    DateTimeOffset RequestedAtUtc,
    string Nonce);

public sealed record SessionApprovalResult(
    bool Approved,
    SessionPermissions GrantedPermissions,
    string? SessionToken,
    string? Reason);

public sealed record RemoteSession(
    string SessionId,
    string LocalDeviceId,
    string RemoteDeviceId,
    string RemoteDisplayName,
    string RemoteEndpoint,
    bool IsHost,
    TransportMode Transport,
    SessionPermissions GrantedPermissions,
    DateTimeOffset StartedAtUtc,
    string SessionToken);

public sealed record DiscoveredPeer(
    string DeviceId,
    string DisplayName,
    string IpAddress,
    int Port,
    string OperatingSystem,
    DateTimeOffset LastSeenUtc);

public sealed record SessionHistoryEntry(
    long Id,
    string RemoteDeviceId,
    string RemoteDisplayName,
    bool WasHost,
    TransportMode Transport,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Outcome);

public sealed class AppSettings
{
    public string DisplayName { get; set; } = Environment.MachineName;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public int ListenPort { get; set; } = 48720;
    public int DiscoveryPort { get; set; } = 48721;
    public bool EnableLanDiscovery { get; set; } = true;
    public int TargetFps { get; set; } = 30;
    public int JpegQuality { get; set; } = 75;
    public bool AllowInputByDefaultInPrompt { get; set; } = true;
    public bool AllowClipboardByDefaultInPrompt { get; set; } = false;
    public bool AllowFileTransferByDefaultInPrompt { get; set; } = false;
    public string StunServer { get; set; } = "stun.l.google.com:19302";
    public string CustomSignalingUrl { get; set; } = string.Empty;
    public string CustomTurnServer { get; set; } = string.Empty;
}
