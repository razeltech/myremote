using Microsoft.Data.Sqlite;

namespace MyRemote.Storage;

/// <summary>
/// Manages SQLite schema initialization and connection creation.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private async Task EnsureInitializedAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS device_identity (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    device_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    machine_name TEXT NOT NULL,
                    operating_system TEXT NOT NULL,
                    public_key_fingerprint TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS session_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    remote_device_id TEXT NOT NULL,
                    remote_display_name TEXT NOT NULL,
                    was_host INTEGER NOT NULL,
                    transport INTEGER NOT NULL,
                    started_at_utc TEXT NOT NULL,
                    ended_at_utc TEXT NULL,
                    outcome TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
