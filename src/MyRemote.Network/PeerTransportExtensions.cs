using MyRemote.Core.Interfaces;
using MyRemote.Network.Protocol;

namespace MyRemote.Network;

public static class PeerTransportExtensions
{
    /// <summary>Sends a token-authenticated binary payload (file chunk, voice) using <see cref="BinaryEnvelope"/>.</summary>
    public static Task SendTokenBinaryAsync(
        this IPeerTransport transport,
        ISessionManager sessionManager,
        WireMessageType type,
        string id,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        var token = sessionManager.CurrentSession?.SessionToken;
        if (string.IsNullOrEmpty(token)) return Task.CompletedTask;
        return transport.SendRawAsync(type, BinaryEnvelope.Pack(token, id, data.Span), ct);
    }
}
