using MyRemote.Core.Models;
using MyRemote.Network.Protocol;

namespace MyRemote.Network;

/// <summary>
/// Common transport abstraction for LAN TLS and Internet WebRTC peer sessions.
/// </summary>
public interface IPeerTransport
{
    bool IsSessionActive { get; }
    Func<SessionRequest, Task<SessionApprovalResult>>? IncomingApprovalHandler { get; set; }

    event EventHandler<byte[]>? ScreenFrameReceived;
    event EventHandler<byte[]>? InputCommandPayloadReceived;
    event EventHandler<byte[]>? MonitorListPayloadReceived;
    event EventHandler<int>? SelectMonitorRequested;
    event EventHandler<QualityPayload>? QualitySettingsReceived;
    event EventHandler<string>? ClipboardTextReceived;
    event EventHandler<ChatPayload>? ChatReceived;
    event EventHandler<byte[]>? FileOfferPayloadReceived;
    event EventHandler<FileChunkPayload>? FileChunkReceived;
    event EventHandler<string>? FileCompleteReceived;
    event EventHandler<SessionPermissions>? PermissionsUpdated;
    event EventHandler<string>? SessionDisconnected;

    Task SendRawAsync(WireMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    Task SendTokenJsonAsync<T>(WireMessageType type, T data, CancellationToken ct = default);
    Task DisconnectAsync(string reason = "Disconnected by user");
}
