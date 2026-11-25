using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.Core.Services;

/// <summary>
/// Manages deterministic connection state transitions and active session permissions.
/// </summary>
public sealed class SessionManager(ILogger<SessionManager> logger) : ISessionManager
{
    private readonly object _sync = new();
    private ConnectionState _state = ConnectionState.Idle;
    private RemoteSession? _currentSession;

    public ConnectionState State
    {
        get { lock (_sync) return _state; }
    }

    public RemoteSession? CurrentSession
    {
        get { lock (_sync) return _currentSession; }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<RemoteSession?>? SessionChanged;

    public bool TryTransitionTo(ConnectionState nextState)
    {
        lock (_sync)
        {
            if (_state == nextState) return true;
            if (!IsValidTransition(_state, nextState))
            {
                logger.LogWarning("Rejected invalid state transition {From} -> {To}", _state, nextState);
                return false;
            }

            _state = nextState;
            logger.LogInformation("Connection state changed to {State}", nextState);
        }

        StateChanged?.Invoke(this, nextState);
        return true;
    }

    public void StartSession(RemoteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            _currentSession = session;
            _state = ConnectionState.Connected;
            logger.LogInformation(
                "Started session {SessionId} with {RemoteDeviceId} (IsHost={IsHost}, Permissions={Permissions})",
                session.SessionId, session.RemoteDeviceId, session.IsHost, session.GrantedPermissions);
        }

        StateChanged?.Invoke(this, ConnectionState.Connected);
        SessionChanged?.Invoke(this, session);
    }

    public void UpdatePermissions(SessionPermissions updatedPermissions)
    {
        RemoteSession? updated;
        lock (_sync)
        {
            if (_currentSession is null) return;
            updated = _currentSession with { GrantedPermissions = updatedPermissions };
            _currentSession = updated;
            logger.LogInformation("Updated session {SessionId} permissions to {Permissions}", updated.SessionId, updatedPermissions);
        }

        SessionChanged?.Invoke(this, updated);
    }

    public bool HasPermission(SessionPermissions permission)
    {
        lock (_sync)
        {
            return _state == ConnectionState.Connected
                && _currentSession is not null
                && (_currentSession.GrantedPermissions & permission) == permission;
        }
    }

    public void EndSession(string reason = "Disconnected")
    {
        lock (_sync)
        {
            if (_currentSession is not null)
            {
                logger.LogInformation("Ending session {SessionId}: {Reason}", _currentSession.SessionId, reason);
            }
            _currentSession = null;
            _state = ConnectionState.Idle;
        }

        SessionChanged?.Invoke(this, null);
        StateChanged?.Invoke(this, ConnectionState.Idle);
    }

    private static bool IsValidTransition(ConnectionState from, ConnectionState to) => (from, to) switch
    {
        (_, ConnectionState.Idle) => true,
        (_, ConnectionState.Failed) => true,
        (ConnectionState.Idle, ConnectionState.Discovering) => true,
        (ConnectionState.Idle, ConnectionState.Connecting) => true,
        (ConnectionState.Idle, ConnectionState.AwaitingHostApproval) => true,
        (ConnectionState.Discovering, ConnectionState.Connecting) => true,
        (ConnectionState.Discovering, ConnectionState.AwaitingHostApproval) => true,
        (ConnectionState.Connecting, ConnectionState.AwaitingHostApproval) => true,
        (ConnectionState.Connecting, ConnectionState.Connected) => true,
        (ConnectionState.AwaitingHostApproval, ConnectionState.Connected) => true,
        (ConnectionState.Connected, ConnectionState.Reconnecting) => true,
        (ConnectionState.Connected, ConnectionState.Disconnecting) => true,
        (ConnectionState.Reconnecting, ConnectionState.Connected) => true,
        (ConnectionState.Reconnecting, ConnectionState.Disconnecting) => true,
        (ConnectionState.Failed, ConnectionState.Connecting) => true,
        _ => false
    };
}
