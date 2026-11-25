using MyRemote.Core.Models;

namespace MyRemote.Network.Protocol;

public sealed record HelloPayload(
    string DeviceId,
    string DisplayName,
    SessionPermissions RequestedPermissions,
    string Nonce);

public sealed record ApprovalPayload(
    bool Approved,
    SessionPermissions GrantedPermissions,
    string? SessionToken,
    string? Reason);

public sealed record TokenEnvelope<T>(string SessionToken, T Data);

public sealed record QualityPayload(int TargetFps, int JpegQuality, double Scale);

public sealed record FileChunkPayload(string TransferId, byte[] Data);

public sealed record ChatPayload(string SenderName, string Message, DateTimeOffset SentAtUtc);
