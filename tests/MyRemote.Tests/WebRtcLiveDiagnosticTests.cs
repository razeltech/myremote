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
        host.IncomingApprovalHandler = req =>
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
}
