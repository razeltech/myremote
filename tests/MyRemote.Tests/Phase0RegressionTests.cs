using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyRemote.Core.Models;
using MyRemote.Core.Services;
using MyRemote.FileTransfer;
using MyRemote.Network;
using MyRemote.Network.Protocol;
using MyRemote.Security;

namespace MyRemote.Tests;

/// <summary>Regression tests for the Phase 0 stabilization fixes (see ROADMAP.md).</summary>
public class Phase0RegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"myremote-p0-{Guid.NewGuid():N}");

    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void SessionManager_TryTransition_IsAtomicCompareAndSet()
    {
        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        Assert.True(mgr.TryTransition(ConnectionState.Idle, ConnectionState.AwaitingHostApproval));
        // A second claimant must fail even though TryTransitionTo(sameState) would have returned true.
        Assert.False(mgr.TryTransition(ConnectionState.Idle, ConnectionState.AwaitingHostApproval));
        Assert.Equal(ConnectionState.AwaitingHostApproval, mgr.State);
    }

    [Fact]
    public void BinaryEnvelope_RoundTrips_And_RejectsTruncated()
    {
        var data = new byte[] { 1, 2, 3, 250, 0, 7 };
        var packed = BinaryEnvelope.Pack("token-abc", "transfer-1", data);

        Assert.True(BinaryEnvelope.TryUnpack(packed, out var token, out var id, out var body));
        Assert.Equal("token-abc", token);
        Assert.Equal("transfer-1", id);
        Assert.Equal(data, body.ToArray());

        Assert.False(BinaryEnvelope.TryUnpack([], out _, out _, out _));
        Assert.False(BinaryEnvelope.TryUnpack([200, 1, 2], out _, out _, out _)); // token length beyond payload
    }

    [Fact]
    public void HelloPayload_FromOldClient_DeserializesAsVersionZero()
    {
        var json = """{"DeviceId":"111-222-333","DisplayName":"Old","RequestedPermissions":127,"Nonce":"n"}""";
        var hello = JsonSerializer.Deserialize<HelloPayload>(json)!;
        Assert.Equal(0, hello.ProtocolVersion);
        Assert.NotEqual(ProtocolInfo.CurrentVersion, hello.ProtocolVersion);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("relay.example.com:3478", 1)]
    [InlineData("turn:a.example.com:3478, turns:a.example.com:5349", 2)]
    [InlineData("turn:a:3478?transport=tcp;b:80", 2)]
    public void ParseTurnUrls_NormalizesAndSplits(string raw, int expected)
    {
        var urls = WebRtcPeerTransport.ParseTurnUrls(raw);
        Assert.Equal(expected, urls.Count);
        Assert.All(urls, u => Assert.True(u.StartsWith("turn:") || u.StartsWith("turns:"), u));
    }

    [Fact]
    public async Task Lan_SecondRequestWhileConsentPending_IsRejectedWithoutSecondDialog()
    {
        int port = GetFreeTcpPort();
        var hostMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var host = new RemotePeerTransport(hostMgr, new SecurityService(), NullLogger<RemotePeerTransport>.Instance);

        int dialogsShown = 0;
        var releaseFirst = new TaskCompletionSource();
        host.IncomingApprovalHandler = async (_, _) =>
        {
            Interlocked.Increment(ref dialogsShown);
            await releaseFirst.Task;
            return new SessionApprovalResult(false, SessionPermissions.None, null, "User declined");
        };
        host.StartListening(new DeviceIdentity("111-111-111", "Host", "Host", "Win", "FP", DateTimeOffset.UtcNow), port);

        var c1Mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var c1 = new RemotePeerTransport(c1Mgr, new SecurityService(), NullLogger<RemotePeerTransport>.Instance);
        var first = c1.ConnectAsync(new DeviceIdentity("222-222-222", "C1", "C1", "Win", "FP", DateTimeOffset.UtcNow), "C1", "127.0.0.1", port, SessionPermissions.AllControl);

        for (int i = 0; i < 50 && Volatile.Read(ref dialogsShown) == 0; i++) await Task.Delay(50);
        Assert.Equal(1, dialogsShown);
        Assert.Equal(ConnectionState.AwaitingHostApproval, hostMgr.State);

        var c2Mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var c2 = new RemotePeerTransport(c2Mgr, new SecurityService(), NullLogger<RemotePeerTransport>.Instance);
        var second = await c2.ConnectAsync(new DeviceIdentity("333-333-333", "C2", "C2", "Win", "FP", DateTimeOffset.UtcNow), "C2", "127.0.0.1", port, SessionPermissions.AllControl);

        Assert.False(second.Approved);
        Assert.Equal(1, dialogsShown);

        releaseFirst.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(firstResult.Approved);
        for (int i = 0; i < 40 && hostMgr.State != ConnectionState.Idle; i++) await Task.Delay(50);
        Assert.Equal(ConnectionState.Idle, hostMgr.State);
    }

    [Fact]
    public async Task Lan_OldProtocolVersion_IsRejectedWithUpdateMessage()
    {
        int port = GetFreeTcpPort();
        var hostMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var host = new RemotePeerTransport(hostMgr, new SecurityService(), NullLogger<RemotePeerTransport>.Instance);
        bool dialogShown = false;
        host.IncomingApprovalHandler = (_, _) =>
        {
            dialogShown = true;
            return Task.FromResult(new SessionApprovalResult(true, SessionPermissions.AllControl, "x", null));
        };
        host.StartListening(new DeviceIdentity("111-111-111", "Host", "Host", "Win", "FP", DateTimeOffset.UtcNow), port);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("test");

        // Simulates a v1.0.4 client: no ProtocolVersion field.
        await WireFraming.WriteJsonAsync(ssl, WireMessageType.HelloRequest,
            new { DeviceId = "222-222-222", DisplayName = "Old", RequestedPermissions = SessionPermissions.AllControl, Nonce = "n" });
        var frame = await WireFraming.ReadFrameAsync(ssl).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(frame);
        var approval = JsonSerializer.Deserialize<ApprovalPayload>(frame!.Payload)!;
        Assert.False(approval.Approved);
        Assert.Contains("Update MyRemote", approval.Reason);
        Assert.False(dialogShown);
    }

    [Fact]
    public async Task FileTransfer_ChunksBeyondAnnouncedSize_AreRejected()
    {
        Directory.CreateDirectory(_tempDir);
        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        mgr.StartSession(new RemoteSession("s", "1", "2", "p", "ip", true, TransportMode.LanDirect,
            SessionPermissions.FileTransfer, DateTimeOffset.UtcNow, "tok"));
        var ft = new FileTransferService(mgr);

        var offer = new FileOfferMetadata("t1", "small.bin", 4, new string('0', 64));
        Assert.True(ft.BeginReceive(offer, _tempDir));
        Assert.False(ft.AppendChunk("t1", [1, 2, 3])!.IsCancelled);
        var overflow = ft.AppendChunk("t1", [4, 5, 6]);
        Assert.True(overflow!.IsCancelled);

        var done = await ft.CompleteReceiveAsync("t1");
        Assert.Equal("Unknown transfer ID.", done.ErrorMessage); // already cancelled and removed
        Assert.False(File.Exists(Path.Combine(_tempDir, "small.bin")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }
}
