namespace MyRemote.Core.Models;

/// <summary>
/// Represents the unique local or remote device identity.
/// </summary>
public sealed record DeviceIdentity(
    string DeviceId,
    string DisplayName,
    string MachineName,
    string OperatingSystem,
    string PublicKeyFingerprint,
    DateTimeOffset CreatedAtUtc);
