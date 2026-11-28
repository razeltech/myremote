using Microsoft.Extensions.Logging;
using MyRemote.Core.Models;
using MyRemote.Core.Services;
using MyRemote.Network;
using MyRemote.Security;
using Xunit.Abstractions;

namespace MyRemote.Tests;

public class WebRtcLiveDiagnosticTests(ITestOutputHelper output)
{
    private sealed class OutputLoggerFactory(ITestOutputHelper output, string prefix) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new OutputLogger(output, $"{prefix}:{categoryName}");
        public void Dispose() { }
    }

    private sealed class OutputLogger(ITestOutputHelper output, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            output.WriteLine($"[{logLevel}] [{cat}] {formatter(state, exception)} {exception}");
        }
    }

    [Fact]
    public async Task WebRtc_TwoPeers_OverPeerJs_EstablishesDataChannelAndApproval()
    {
        var hostFactory = new OutputLoggerFactory(output, "HOST");
        var clientFactory = new OutputLoggerFactory(output, "CLIENT");

        var hostSec = new SecurityService();
        var hostMgr = new SessionManager(hostFactory.CreateLogger<SessionManager>());
        using var host = new WebRtcPeerTransport(hostMgr, hostSec, hostFactory.CreateLogger<WebRtcPeerTransport>());

        var clientSec = new SecurityService();
        var clientMgr = new SessionManager(clientFactory.CreateLogger<SessionManager>());
        using var client = new WebRtcPeerTransport(clientMgr, clientSec, clientFactory.CreateLogger<WebRtcPeerTransport>());

        var rand = Random.Shared.Next(100, 999);
        var hostId = new DeviceIdentity($"{rand}-111-222", "Host", "Host", "Win", "FP1", DateTimeOffset.UtcNow);
        var clientId = new DeviceIdentity($"{rand}-333-444", "Client", "Client", "Win", "FP2", DateTimeOffset.UtcNow);

        bool approvalTriggered = false;
        host.IncomingApprovalHandler = (req, _) =>
        {
            approvalTriggered = true;
            output.WriteLine($"Approval triggered for {req.RequesterDeviceId}");
            return Task.FromResult(new SessionApprovalResult(true, SessionPermissions.AllControl, hostSec.GenerateSessionToken(), null));
        };

        var settings = new AppSettings();
        host.StartSignaling(hostId, settings);
        client.StartSignaling(clientId, settings);

        for (int i = 0; i < 30 && (!host.IsSignalingConnected || !client.IsSignalingConnected); i++)
        {
            await Task.Delay(250);
        }
        output.WriteLine($"HostSig={host.IsSignalingConnected}, ClientSig={client.IsSignalingConnected}");
        Assert.True(host.IsSignalingConnected);
        Assert.True(client.IsSignalingConnected);

        var res = await client.ConnectAsync(clientId, "Client", hostId.DeviceId, SessionPermissions.AllControl);
        output.WriteLine($"ConnectResult: Approved={res.Approved}, Reason={res.Reason}");
        Assert.True(approvalTriggered, $"Approval handler was not triggered. Reason={res.Reason}");
        Assert.True(res.Approved, res.Reason);
    }

    [Fact]
    public async Task SIPSorcery_Gathers_Stun_Reflexive_Candidate()
    {
        var candidates = await GatherAsync([new SIPSorcery.Net.RTCIceServer { urls = "stun:stun.l.google.com:19302" }], TimeSpan.FromSeconds(3.5));
        Assert.Contains(candidates, c => c.Contains("typ srflx"));
    }

    /// <summary>
    /// Verifies YOUR TURN relay. Set MYREMOTE_TURN_URL (e.g. turn:relay.example.com:3478),
    /// MYREMOTE_TURN_USER and MYREMOTE_TURN_PASS, then run:
    ///   dotnet test --filter Turn_Relay_Probe --logger "console;verbosity=detailed"
    /// Without the variables the test only prints instructions.
    /// </summary>
    [Fact]
    public async Task Turn_Relay_Probe()
    {
        var url = Environment.GetEnvironmentVariable("MYREMOTE_TURN_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            output.WriteLine("MYREMOTE_TURN_URL not set; skipping TURN probe.");
            return;
        }

        var candidates = await GatherAsync(
        [
            new SIPSorcery.Net.RTCIceServer
            {
                urls = url,
                username = Environment.GetEnvironmentVariable("MYREMOTE_TURN_USER") ?? "",
                credential = Environment.GetEnvironmentVariable("MYREMOTE_TURN_PASS") ?? ""
            }
        ], TimeSpan.FromSeconds(8));

        Assert.True(candidates.Any(c => c.Contains("typ relay")),
            "TURN server gave no relay candidate: check URL, port/firewall (UDP+TCP 3478 and relay range), username and password.");
    }

    private async Task<List<string>> GatherAsync(List<SIPSorcery.Net.RTCIceServer> servers, TimeSpan wait)
    {
        var pc = new SIPSorcery.Net.RTCPeerConnection(new SIPSorcery.Net.RTCConfiguration { iceServers = servers });
        var candidates = new List<string>();
        pc.onicecandidate += c =>
        {
            if (c is null) return;
            lock (candidates) candidates.Add(c.ToString());
            output.WriteLine($"ICE: {c}");
        };

        await pc.createDataChannel("probe");
        await pc.setLocalDescription(pc.createOffer());
        await Task.Delay(wait);
        pc.close();
        lock (candidates) return [.. candidates];
    }
}
