using System.Text.Json;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.Storage;

public sealed class SqliteSettingsRepository(SqliteDatabase database) : ISettingsRepository
{
    private const string SettingsKey = "app_settings_v1";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", SettingsKey);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AppSettings();
        }

        return JsonSerializer.Deserialize<AppSettings>(raw) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = JsonSerializer.Serialize(settings);
        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        cmd.Parameters.AddWithValue("$value", json);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
