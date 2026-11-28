using System.Text;
using MyRemote.Core.Models;

namespace MyRemote.Network.Protocol;

public static class ProtocolInfo
{
    /// <summary>Bump when the wire format changes incompatibly. Hosts reject other versions with a clear message.</summary>
    public const int CurrentVersion = 2;
}

/// <summary>Controller → host session request. Peers older than v2 omit ProtocolVersion, so it deserializes as 0.</summary>
public sealed record HelloPayload(
    string DeviceId,
    string DisplayName,
    SessionPermissions RequestedPermissions,
    string Nonce,
    int ProtocolVersion = 0);

public sealed record ApprovalPayload(
    bool Approved,
    SessionPermissions GrantedPermissions,
    string? SessionToken,
    string? Reason);

public sealed record TokenEnvelope<T>(string SessionToken, T Data);

public sealed record QualityPayload(int TargetFps, int JpegQuality, double Scale);

public sealed record FileChunkPayload(string TransferId, byte[] Data);

/// <summary>Host → controller answer to a FileOffer (sent as WireMessageType.FileAccept).</summary>
public sealed record FileDecisionPayload(string TransferId, bool Accepted, string? Reason = null);

public sealed record ChatPayload(string SenderName, string Message, DateTimeOffset SentAtUtc);

/// <summary>
/// Compact binary envelope for bulk payloads (file chunks, voice) so they are not base64-inflated inside JSON.
/// Layout: [u8 tokenLen][token UTF-8][u8 idLen][id UTF-8][data...]
/// </summary>
public static class BinaryEnvelope
{
    public static byte[] Pack(string token, string id, ReadOnlySpan<byte> data)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var idBytes = Encoding.UTF8.GetBytes(id);
        if (tokenBytes.Length > 255 || idBytes.Length > 255)
        {
            throw new ArgumentException("Token or id too long for binary envelope.");
        }

        var buffer = new byte[2 + tokenBytes.Length + idBytes.Length + data.Length];
        int o = 0;
        buffer[o++] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(buffer, o); o += tokenBytes.Length;
        buffer[o++] = (byte)idBytes.Length;
        idBytes.CopyTo(buffer, o); o += idBytes.Length;
        data.CopyTo(buffer.AsSpan(o));
        return buffer;
    }

    public static bool TryUnpack(byte[] payload, out string token, out string id, out ReadOnlyMemory<byte> data)
    {
        token = string.Empty;
        id = string.Empty;
        data = ReadOnlyMemory<byte>.Empty;
        if (payload.Length < 2) return false;

        int tokenLen = payload[0];
        if (payload.Length < 1 + tokenLen + 1) return false;
        int idLen = payload[1 + tokenLen];
        int dataStart = 2 + tokenLen + idLen;
        if (payload.Length < dataStart) return false;

        token = Encoding.UTF8.GetString(payload, 1, tokenLen);
        id = Encoding.UTF8.GetString(payload, 2 + tokenLen, idLen);
        data = payload.AsMemory(dataStart);
        return true;
    }
}
