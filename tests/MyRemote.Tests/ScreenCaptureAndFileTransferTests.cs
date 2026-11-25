using Microsoft.Extensions.Logging.Abstractions;
using MyRemote.Core.Models;
using MyRemote.Core.Services;
using MyRemote.FileTransfer;
using MyRemote.Input;
using MyRemote.ScreenCapture;
using MyRemote.Security;

namespace MyRemote.Tests;

public class ScreenCaptureAndFileTransferTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"myremote_ft_{Guid.NewGuid():N}");

    [Fact]
    public void ScreenCapture_EnumeratesAtLeastOneMonitor()
    {
        var capture = new Win32ScreenCaptureService();
        var monitors = capture.GetMonitors();
        Assert.NotEmpty(monitors);
        Assert.All(monitors, m =>
        {
            Assert.True(m.Width > 0);
            Assert.True(m.Height > 0);
        });
    }

    [Fact]
    public void InputInjector_RejectsInvalidTokenOrMissingPermission()
    {
        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        var sec = new SecurityService();
        var injector = new Win32InputInjector(mgr, sec);

        // No active session
        Assert.False(injector.TryInject(new RemoteInputEvent(InputEventType.MouseMove, 0.5, 0.5), "bad"));

        // Active session with ViewScreen only (no MouseInput)
        mgr.StartSession(new RemoteSession(
            "s1", "111-111-111", "222-222-222", "Peer", "127.0.0.1", true,
            TransportMode.LanDirect, SessionPermissions.ViewScreen, DateTimeOffset.UtcNow, "valid-token"));

        Assert.False(injector.TryInject(new RemoteInputEvent(InputEventType.MouseMove, 0.5, 0.5), "valid-token"));
        Assert.False(injector.TryInject(new RemoteInputEvent(InputEventType.KeyDown, VirtualKey: 0x41), "wrong-token"));
    }

    [Fact]
    public async Task FileTransfer_EnforcesPermission_SanitizesPath_AndVerifiesSha256()
    {
        Directory.CreateDirectory(_tempDir);
        var srcFile = Path.Combine(_tempDir, "sample.txt");
        await File.WriteAllTextAsync(srcFile, "Hello MyRemote secure transfer!");

        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        var ft = new FileTransferService(mgr);

        // Without permission
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ft.PrepareOfferAsync(srcFile));

        // Grant FileTransfer permission
        mgr.StartSession(new RemoteSession(
            "s1", "111-111-111", "222-222-222", "Peer", "127.0.0.1", true,
            TransportMode.LanDirect, SessionPermissions.FileTransfer, DateTimeOffset.UtcNow, "tok"));

        var offer = await ft.PrepareOfferAsync(srcFile);
        Assert.Equal("sample.txt", offer.FileName);

        // Sanitize path traversal
        Assert.Equal("evil.exe", FileTransferService.SanitizeFileName("../../evil.exe"));

        var recvDir = Path.Combine(_tempDir, "received");
        Assert.True(ft.BeginReceive(offer, recvDir));
        var bytes = await File.ReadAllBytesAsync(srcFile);
        await ft.AppendChunkAsync(offer.TransferId, bytes);
        var done = await ft.CompleteReceiveAsync(offer.TransferId);
        Assert.True(done.IsCompleted);
        Assert.False(done.IsCancelled);
        Assert.True(File.Exists(Path.Combine(recvDir, "sample.txt")));

        // Corrupted transfer fails SHA-256
        var badOffer = offer with { TransferId = "bad1", Sha256Hex = new string('0', 64) };
        Assert.True(ft.BeginReceive(badOffer, recvDir));
        await ft.AppendChunkAsync("bad1", bytes);
        var badDone = await ft.CompleteReceiveAsync("bad1");
        Assert.False(badDone.IsCompleted);
        Assert.True(badDone.IsCancelled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* ignore */ }
    }
}
