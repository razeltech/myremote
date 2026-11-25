using MyRemote.Core.Models;
using MyRemote.Storage;

namespace MyRemote.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"myremote_test_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task DeviceIdentity_And_Settings_PersistAcrossReads()
    {
        var db = new SqliteDatabase(_dbPath);
        var idProvider = new SqliteDeviceIdentityProvider(db);

        var id1 = await idProvider.GetOrCreateIdentityAsync();
        var id2 = await new SqliteDeviceIdentityProvider(new SqliteDatabase(_dbPath)).GetOrCreateIdentityAsync();

        Assert.Equal(id1.DeviceId, id2.DeviceId);
        Assert.Matches(@"^\d{3}-\d{3}-\d{3}$", id1.DeviceId);

        var settingsRepo = new SqliteSettingsRepository(db);
        var settings = await settingsRepo.LoadAsync();
        settings.Theme = ThemeMode.Light;
        settings.TargetFps = 60;
        settings.DisplayName = "Workstation-01";
        await settingsRepo.SaveAsync(settings);

        var reloaded = await new SqliteSettingsRepository(new SqliteDatabase(_dbPath)).LoadAsync();
        Assert.Equal(ThemeMode.Light, reloaded.Theme);
        Assert.Equal(60, reloaded.TargetFps);
        Assert.Equal("Workstation-01", reloaded.DisplayName);
    }

    [Fact]
    public async Task SessionHistory_RecordsStartAndEnd()
    {
        var db = new SqliteDatabase(_dbPath);
        var repo = new SqliteSessionHistoryRepository(db);

        var rowId = await repo.RecordStartAsync(new SessionHistoryEntry(
            Id: 0,
            RemoteDeviceId: "999-888-777",
            RemoteDisplayName: "Lab-PC",
            WasHost: false,
            Transport: TransportMode.LanDirect,
            StartedAtUtc: DateTimeOffset.UtcNow,
            EndedAtUtc: null,
            Outcome: "Active"));

        await repo.RecordEndAsync(rowId, DateTimeOffset.UtcNow, "Completed");

        var recent = await repo.GetRecentAsync(10);
        Assert.Single(recent);
        Assert.Equal("999-888-777", recent[0].RemoteDeviceId);
        Assert.Equal("Completed", recent[0].Outcome);
        Assert.NotNull(recent[0].EndedAtUtc);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // Ignore cleanup lock in temp dir
        }
    }
}
