using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.Storage;

public sealed class SqliteSessionHistoryRepository(SqliteDatabase database) : ISessionHistoryRepository
{
    public async Task<IReadOnlyList<SessionHistoryEntry>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = new List<SessionHistoryEntry>();
        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, remote_device_id, remote_display_name, was_host, transport, started_at_utc, ended_at_utc, outcome
            FROM session_history
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SessionHistoryEntry(
                Id: reader.GetInt64(0),
                RemoteDeviceId: reader.GetString(1),
                RemoteDisplayName: reader.GetString(2),
                WasHost: reader.GetInt32(3) == 1,
                Transport: (TransportMode)reader.GetInt32(4),
                StartedAtUtc: DateTimeOffset.Parse(reader.GetString(5)),
                EndedAtUtc: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                Outcome: reader.GetString(7)));
        }

        return results;
    }

    public async Task<long> RecordStartAsync(SessionHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_history (remote_device_id, remote_display_name, was_host, transport, started_at_utc, ended_at_utc, outcome)
            VALUES ($deviceId, $displayName, $wasHost, $transport, $started, $ended, $outcome);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$deviceId", entry.RemoteDeviceId);
        cmd.Parameters.AddWithValue("$displayName", entry.RemoteDisplayName);
        cmd.Parameters.AddWithValue("$wasHost", entry.WasHost ? 1 : 0);
        cmd.Parameters.AddWithValue("$transport", (int)entry.Transport);
        cmd.Parameters.AddWithValue("$started", entry.StartedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$ended", entry.EndedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", entry.Outcome);

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    public async Task RecordEndAsync(long id, DateTimeOffset endedAtUtc, string outcome, CancellationToken cancellationToken = default)
    {
        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE session_history
            SET ended_at_utc = $ended, outcome = $outcome
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ended", endedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$outcome", outcome);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
