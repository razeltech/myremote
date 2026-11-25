using System.Security.Cryptography;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.Storage;

/// <summary>
/// Persists and loads the local DeviceIdentity in SQLite so the Device ID remains stable across restarts.
/// </summary>
public sealed class SqliteDeviceIdentityProvider(SqliteDatabase database) : IDeviceIdentityProvider
{
    private DeviceIdentity? _cached;

    public async Task<DeviceIdentity> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        await using var conn = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var select = conn.CreateCommand())
        {
            select.CommandText = """
                SELECT device_id, display_name, machine_name, operating_system, public_key_fingerprint, created_at_utc
                FROM device_identity WHERE id = 1;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _cached = new DeviceIdentity(
                    DeviceId: reader.GetString(0),
                    DisplayName: reader.GetString(1),
                    MachineName: reader.GetString(2),
                    OperatingSystem: reader.GetString(3),
                    PublicKeyFingerprint: reader.GetString(4),
                    CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(5)));
                return _cached;
            }
        }

        var identity = CreateNewIdentity();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO device_identity (id, device_id, display_name, machine_name, operating_system, public_key_fingerprint, created_at_utc)
                VALUES (1, $deviceId, $displayName, $machineName, $os, $fingerprint, $created);
                """;
            insert.Parameters.AddWithValue("$deviceId", identity.DeviceId);
            insert.Parameters.AddWithValue("$displayName", identity.DisplayName);
            insert.Parameters.AddWithValue("$machineName", identity.MachineName);
            insert.Parameters.AddWithValue("$os", identity.OperatingSystem);
            insert.Parameters.AddWithValue("$fingerprint", identity.PublicKeyFingerprint);
            insert.Parameters.AddWithValue("$created", identity.CreatedAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _cached = identity;
        return identity;
    }

    private static DeviceIdentity CreateNewIdentity()
    {
        // Generate a 9-digit numeric Device ID formatted as XXX-XXX-XXX using CSPRNG.
        var digits = new char[9];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        var deviceId = $"{new string(digits, 0, 3)}-{new string(digits, 3, 3)}-{new string(digits, 6, 3)}";
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(spki))[..16];

        return new DeviceIdentity(
            DeviceId: deviceId,
            DisplayName: Environment.MachineName,
            MachineName: Environment.MachineName,
            OperatingSystem: Environment.OSVersion.VersionString,
            PublicKeyFingerprint: fingerprint,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }
}
