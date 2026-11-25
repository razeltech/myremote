using MyRemote.Core.Models;

namespace MyRemote.Core.Interfaces;

public interface IDeviceIdentityProvider
{
    Task<DeviceIdentity> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISessionHistoryRepository
{
    Task<IReadOnlyList<SessionHistoryEntry>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<long> RecordStartAsync(SessionHistoryEntry entry, CancellationToken cancellationToken = default);
    Task RecordEndAsync(long id, DateTimeOffset endedAtUtc, string outcome, CancellationToken cancellationToken = default);
}

public interface ISecurityService
{
    string GenerateSessionToken();
    bool ValidateSessionToken(string? expectedToken, string? providedToken);
    bool IsValidDeviceId(string? deviceId);
    string FormatDeviceId(string raw);
    bool CheckRateLimit(string sourceKey, int maxRequestsPerWindow = 5, int windowSeconds = 30);
    string ComputeSha256Hex(ReadOnlySpan<byte> data);
}

public interface ISessionManager
{
    ConnectionState State { get; }
    RemoteSession? CurrentSession { get; }
    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<RemoteSession?>? SessionChanged;

    bool TryTransitionTo(ConnectionState nextState);
    void StartSession(RemoteSession session);
    void UpdatePermissions(SessionPermissions updatedPermissions);
    bool HasPermission(SessionPermissions permission);
    void EndSession(string reason = "Disconnected");
}
