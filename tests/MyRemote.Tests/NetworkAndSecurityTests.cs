using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyRemote.Core.Models;
using MyRemote.Core.Services;
using MyRemote.Network;
using MyRemote.Network.Protocol;
using MyRemote.Security;

namespace MyRemote.Tests;

public class NetworkAndSecurityTests
{
    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task LoopbackSession_RejectedRequest_DoesNotStartSession()
    {
        int port = GetFreeTcpPort();
        var hostSec = new SecurityService();
        var hostMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var host = new RemotePeerTransport(hostMgr, hostSec, NullLogger<RemotePeerTransport>.Instance);

        host.IncomingApprovalHandler = (_, _) => Task.FromResult(
            new SessionApprovalResult(false, SessionPermissions.None, null, "User declined"));

        var hostId = new DeviceIdentity("111-111-111", "HostPC", "HostPC", "Windows", "FP1", DateTimeOffset.UtcNow);
        host.StartListening(hostId, port);

        var clientSec = new SecurityService();
        var clientMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var client = new RemotePeerTransport(clientMgr, clientSec, NullLogger<RemotePeerTransport>.Instance);
        var clientId = new DeviceIdentity("222-222-222", "ClientPC", "ClientPC", "Windows", "FP2", DateTimeOffset.UtcNow);

        var result = await client.ConnectAsync(clientId, "ClientPC", "127.0.0.1", port, SessionPermissions.AllControl);

        Assert.False(result.Approved);
        Assert.Equal("User declined", result.Reason);
        Assert.Equal(ConnectionState.Idle, clientMgr.State);
        Assert.Equal(ConnectionState.Idle, hostMgr.State);
    }

    [Fact]
    public async Task LoopbackSession_Approved_StreamsFramesAndEnforcesDisconnect()
    {
        int port = GetFreeTcpPort();
        var hostSec = new SecurityService();
        var hostMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var host = new RemotePeerTransport(hostMgr, hostSec, NullLogger<RemotePeerTransport>.Instance);

        host.IncomingApprovalHandler = (_, _) => Task.FromResult(
            new SessionApprovalResult(true, SessionPermissions.ViewScreen | SessionPermissions.MouseInput, hostSec.GenerateSessionToken(), null));

        var hostId = new DeviceIdentity("111-111-111", "HostPC", "HostPC", "Windows", "FP1", DateTimeOffset.UtcNow);
        host.StartListening(hostId, port);

        var clientSec = new SecurityService();
        var clientMgr = new SessionManager(NullLogger<SessionManager>.Instance);
        using var client = new RemotePeerTransport(clientMgr, clientSec, NullLogger<RemotePeerTransport>.Instance);
        var clientId = new DeviceIdentity("222-222-222", "ClientPC", "ClientPC", "Windows", "FP2", DateTimeOffset.UtcNow);

        var frameTcs = new TaskCompletionSource<byte[]>();
        client.ScreenFrameReceived += (_, bytes) => frameTcs.TrySetResult(bytes);

        var result = await client.ConnectAsync(clientId, "ClientPC", "127.0.0.1", port, SessionPermissions.ViewScreen | SessionPermissions.MouseInput);
        Assert.True(result.Approved);
        Assert.Equal(ConnectionState.Connected, clientMgr.State);

        var sampleFrame = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        await host.SendRawAsync(WireMessageType.ScreenFrame, sampleFrame);

        var received = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sampleFrame, received);

        await client.DisconnectAsync("Test done");
        await Task.Delay(150);
        Assert.Equal(ConnectionState.Idle, clientMgr.State);
    }

    [Fact]
    public async Task WireFraming_RejectsOversizedAndMalformedFrames()
    {
        using var ms = new MemoryStream();
        byte[] badHeader = new byte[5];
        badHeader[0] = (byte)WireMessageType.ScreenFrame;
        BinaryPrimitives.WriteInt32BigEndian(badHeader.AsSpan(1, 4), WireFraming.MaxPayloadBytes + 100);
        ms.Write(badHeader);
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => WireFraming.ReadFrameAsync(ms));

        using var ms2 = new MemoryStream([250, 0, 0, 0, 0]); // Unknown type 250
        await Assert.ThrowsAsync<InvalidDataException>(() => WireFraming.ReadFrameAsync(ms2));
    }

    [Fact]
    public void StunParser_ParsesXorMappedAddress()
    {
        // RFC 5389 Binding Success Response with XOR-MAPPED-ADDRESS for 203.0.113.45:54321
        byte[] packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 0x2112A442);

        // Attribute header: type 0x0020, len 8
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 8);
        packet[24] = 0;
        packet[25] = 0x01; // IPv4
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), (ushort)(54321 ^ (0x2112A442 >> 16)));
        uint ipInt = BinaryPrimitives.ReadUInt32BigEndian([203, 0, 113, 45]) ^ 0x2112A442;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28, 4), ipInt);

        var parsed = InternetDiagnosticsService.ParseStunMappedAddress(packet);
        Assert.Equal("203.0.113.45:54321", parsed);
    }

    [Fact]
    public void WebRtcPeerId_RoundTripsDeviceId()
    {
        var peerId = WebRtcPeerTransport.ToPeerId("482-910-334");
        Assert.Equal("myremote-482910334", peerId);
        Assert.Equal("482-910-334", WebRtcPeerTransport.FromPeerId(peerId));
    }
}
