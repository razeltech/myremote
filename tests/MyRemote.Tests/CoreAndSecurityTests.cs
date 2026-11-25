using Microsoft.Extensions.Logging.Abstractions;
using MyRemote.Core.Logging;
using MyRemote.Core.Models;
using MyRemote.Core.Services;
using MyRemote.Security;

namespace MyRemote.Tests;

public class CoreAndSecurityTests
{
    [Fact]
    public void SessionManager_TransitionsAndPermissionEnforcement_WorkCorrectly()
    {
        var manager = new SessionManager(NullLogger<SessionManager>.Instance);
        Assert.Equal(ConnectionState.Idle, manager.State);
        Assert.False(manager.HasPermission(SessionPermissions.ViewScreen));

        Assert.True(manager.TryTransitionTo(ConnectionState.Connecting));
        Assert.True(manager.TryTransitionTo(ConnectionState.AwaitingHostApproval));

        var session = new RemoteSession(
            SessionId: "sess-1",
            LocalDeviceId: "111-222-333",
            RemoteDeviceId: "444-555-666",
            RemoteDisplayName: "RemotePC",
            RemoteEndpoint: "192.168.1.50:48720",
            IsHost: true,
            Transport: TransportMode.LanDirect,
            GrantedPermissions: SessionPermissions.ViewScreen | SessionPermissions.MouseInput,
            StartedAtUtc: DateTimeOffset.UtcNow,
            SessionToken: "token-abc");

        manager.StartSession(session);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.True(manager.HasPermission(SessionPermissions.ViewScreen));
        Assert.True(manager.HasPermission(SessionPermissions.MouseInput));
        Assert.False(manager.HasPermission(SessionPermissions.KeyboardInput));
        Assert.False(manager.HasPermission(SessionPermissions.FileTransfer));

        // Revoke mouse input live
        manager.UpdatePermissions(SessionPermissions.ViewScreen);
        Assert.False(manager.HasPermission(SessionPermissions.MouseInput));

        manager.EndSession("Host terminated");
        Assert.Equal(ConnectionState.Idle, manager.State);
        Assert.Null(manager.CurrentSession);
    }

    [Fact]
    public void SecurityService_TokenValidation_RateLimit_AndDeviceId_Work()
    {
        var sec = new SecurityService();
        var token = sec.GenerateSessionToken();
        Assert.True(token.Length >= 32);
        Assert.True(sec.ValidateSessionToken(token, token));
        Assert.False(sec.ValidateSessionToken(token, token + "x"));
        Assert.False(sec.ValidateSessionToken(token, null));

        Assert.True(sec.IsValidDeviceId("123-456-789"));
        Assert.True(sec.IsValidDeviceId("123456789"));
        Assert.False(sec.IsValidDeviceId("123-45-789"));
        Assert.Equal("123-456-789", sec.FormatDeviceId("123456789"));

        for (int i = 0; i < 3; i++)
        {
            Assert.True(sec.CheckRateLimit("10.0.0.5", maxRequestsPerWindow: 3, windowSeconds: 60));
        }
        Assert.False(sec.CheckRateLimit("10.0.0.5", maxRequestsPerWindow: 3, windowSeconds: 60));
    }

    [Fact]
    public void RollingFileLogger_RedactsSecrets()
    {
        var redacted = RollingFileLoggerProvider.RedactSecrets("Auth failed token=abc123xyz password=mysecret");
        Assert.DoesNotContain("abc123xyz", redacted);
        Assert.DoesNotContain("mysecret", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }
}
