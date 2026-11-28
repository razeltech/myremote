using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;
using MyRemote.Network.Protocol;

namespace MyRemote.Network;

/// <summary>
/// TLS-encrypted peer-to-peer session transport for LAN and direct IP connections.
/// Enforces explicit host approval, rate limiting, and session token validation.
/// </summary>
public sealed class RemotePeerTransport(
    ISessionManager sessionManager,
    ISecurityService securityService,
    ILogger<RemotePeerTransport> logger) : IPeerTransport, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private TcpClient? _activeClient;
    private SslStream? _activeStream;
    private CancellationTokenSource? _sessionCts;
    private X509Certificate2? _serverCert;

    /// <summary>How long the host's consent dialog may stay open before the request is auto-rejected.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    public Func<SessionRequest, CancellationToken, Task<SessionApprovalResult>>? IncomingApprovalHandler { get; set; }

    public event EventHandler<byte[]>? ScreenFrameReceived;
    public event EventHandler<byte[]>? InputCommandPayloadReceived;
    public event EventHandler<byte[]>? MonitorListPayloadReceived;
    public event EventHandler<int>? SelectMonitorRequested;
    public event EventHandler<QualityPayload>? QualitySettingsReceived;
    public event EventHandler<string>? ClipboardTextReceived;
    public event EventHandler<ChatPayload>? ChatReceived;
    public event EventHandler<byte[]>? FileOfferPayloadReceived;
    public event EventHandler<FileDecisionPayload>? FileDecisionReceived;
    public event EventHandler<FileChunkPayload>? FileChunkReceived;
    public event EventHandler<string>? FileCompleteReceived;
    public event EventHandler<byte[]>? VoiceAudioReceived;
    public event EventHandler<SessionPermissions>? PermissionsUpdated;
    public event EventHandler<string>? SessionDisconnected;

    public bool IsSessionActive => _activeStream is not null && sessionManager.State == ConnectionState.Connected;

    public bool IsCongested => false;

    public void StartListening(DeviceIdentity localIdentity, int listenPort)
    {
        StopListening();
        _serverCert ??= TlsCertificateHelper.CreateEphemeralServerCertificate(localIdentity.DeviceId);
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _listener.Start();
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;
        _ = Task.Run(() => AcceptLoopAsync(localIdentity, ct), ct);
        logger.LogInformation("Listening for incoming TLS connections on port {Port}", listenPort);
    }

    public void StopListening()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
    }

    public async Task<SessionApprovalResult> ConnectAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string hostIp,
        int port,
        SessionPermissions requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        if (!sessionManager.TryTransition(ConnectionState.Idle, ConnectionState.Connecting))
        {
            return new SessionApprovalResult(false, SessionPermissions.None, null, "Another session is already active.");
        }

        TcpClient? client = null;
        SslStream? ssl = null;

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(hostIp, port, cancellationToken).ConfigureAwait(false);

            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = $"MyRemote-{hostIp}",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }, cancellationToken).ConfigureAwait(false);

            sessionManager.TryTransitionTo(ConnectionState.AwaitingHostApproval);

            var hello = new HelloPayload(
                localIdentity.DeviceId,
                localDisplayName,
                requestedPermissions,
                Guid.NewGuid().ToString("N"),
                ProtocolInfo.CurrentVersion);
            await WireFraming.WriteJsonAsync(ssl, WireMessageType.HelloRequest, hello, cancellationToken).ConfigureAwait(false);

            // The host user may take a while to click Accept; wait a little longer than the host-side timeout.
            using var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalCts.CancelAfter(ApprovalTimeout + TimeSpan.FromSeconds(10));
            var responseFrame = await WireFraming.ReadFrameAsync(ssl, approvalCts.Token).ConfigureAwait(false);
            if (responseFrame is null || responseFrame.Type != WireMessageType.ApprovalResponse)
            {
                sessionManager.EndSession("Invalid approval response");
                return new SessionApprovalResult(false, SessionPermissions.None, null, "Host closed connection before responding.");
            }

            var approval = JsonSerializer.Deserialize<ApprovalPayload>(responseFrame.Payload);
            if (approval is null || !approval.Approved || string.IsNullOrWhiteSpace(approval.SessionToken))
            {
                sessionManager.EndSession(approval?.Reason ?? "Rejected by host");
                return new SessionApprovalResult(false, SessionPermissions.None, null, approval?.Reason ?? "Connection rejected by host.");
            }

            _activeClient = client;
            _activeStream = ssl;
            _sessionCts = new CancellationTokenSource();

            var session = new RemoteSession(
                SessionId: Guid.NewGuid().ToString("N"),
                LocalDeviceId: localIdentity.DeviceId,
                RemoteDeviceId: hostIp,
                RemoteDisplayName: hostIp,
                RemoteEndpoint: $"{hostIp}:{port}",
                IsHost: false,
                Transport: TransportMode.LanDirect,
                GrantedPermissions: approval.GrantedPermissions,
                StartedAtUtc: DateTimeOffset.UtcNow,
                SessionToken: approval.SessionToken);

            sessionManager.StartSession(session);
            _ = Task.Run(() => ReadLoopAsync(ssl, isHost: false, _sessionCts.Token));

            return new SessionApprovalResult(true, approval.GrantedPermissions, approval.SessionToken, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outgoing connection to {Host}:{Port} failed", hostIp, port);
            ssl?.Dispose();
            client?.Dispose();
            var reason = ex switch
            {
                OperationCanceledException => "The remote user did not answer the request in time.",
                System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
                    => $"{hostIp}:{port} refused the connection (MyRemote not running there, or a firewall blocks TCP {port}).",
                System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut
                    => $"{hostIp}:{port} did not respond (wrong address, or a firewall blocks TCP {port}).",
                _ => ex.Message
            };
            sessionManager.EndSession($"Connection failed: {reason}");
            return new SessionApprovalResult(false, SessionPermissions.None, null, reason);
        }
    }

    private async Task AcceptLoopAsync(DeviceIdentity localIdentity, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleIncomingClientAsync(client, localIdentity, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error accepting incoming TCP client");
            }
        }
    }

    private async Task HandleIncomingClientAsync(TcpClient client, DeviceIdentity localIdentity, CancellationToken ct)
    {
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        SslStream? ssl = null;
        bool claimedState = false;

        try
        {
            if (!securityService.CheckRateLimit(remoteIp, maxRequestsPerWindow: 5, windowSeconds: 30))
            {
                logger.LogWarning("Rate-limited incoming connection from {RemoteIp}", remoteIp);
                client.Dispose();
                return;
            }

            // Claim the single session slot atomically so a second request cannot open a second consent dialog.
            if (!sessionManager.TryTransition(ConnectionState.Idle, ConnectionState.AwaitingHostApproval))
            {
                logger.LogInformation("Rejected incoming connection from {RemoteIp}: another session is active or pending", remoteIp);
                client.Dispose();
                return;
            }
            claimedState = true;

            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }, handshakeCts.Token).ConfigureAwait(false);

            var helloFrame = await WireFraming.ReadFrameAsync(ssl, handshakeCts.Token).ConfigureAwait(false);
            if (helloFrame is null || helloFrame.Type != WireMessageType.HelloRequest)
            {
                throw new InvalidDataException("Expected HelloRequest.");
            }

            var hello = JsonSerializer.Deserialize<HelloPayload>(helloFrame.Payload);
            string? rejectReason = hello is null || !securityService.IsValidDeviceId(hello.DeviceId)
                ? "Invalid device identity."
                : hello.ProtocolVersion != ProtocolInfo.CurrentVersion
                    ? $"Version mismatch (host protocol v{ProtocolInfo.CurrentVersion}, yours v{hello.ProtocolVersion}). Update MyRemote on both PCs."
                    : null;
            if (rejectReason is not null)
            {
                logger.LogWarning("Rejecting {RemoteIp}: {Reason}", remoteIp, rejectReason);
                await WireFraming.WriteJsonAsync(
                    ssl,
                    WireMessageType.ApprovalResponse,
                    new ApprovalPayload(false, SessionPermissions.None, null, rejectReason),
                    ct).ConfigureAwait(false);
                return;
            }

            var request = new SessionRequest(
                RequestId: Guid.NewGuid().ToString("N"),
                RequesterDeviceId: hello!.DeviceId,
                RequesterDisplayName: string.IsNullOrWhiteSpace(hello.DisplayName) ? hello.DeviceId : hello.DisplayName,
                RequesterIpAddress: remoteIp,
                RequestedPermissions: hello.RequestedPermissions,
                RequestedAtUtc: DateTimeOffset.UtcNow,
                Nonce: hello.Nonce);

            using var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            approvalCts.CancelAfter(ApprovalTimeout);
            SessionApprovalResult approval = IncomingApprovalHandler is not null
                ? await IncomingApprovalHandler(request, approvalCts.Token).ConfigureAwait(false)
                : new SessionApprovalResult(false, SessionPermissions.None, null, "No host approval handler registered.");

            if (!approval.Approved || string.IsNullOrWhiteSpace(approval.SessionToken))
            {
                await WireFraming.WriteJsonAsync(
                    ssl,
                    WireMessageType.ApprovalResponse,
                    new ApprovalPayload(false, SessionPermissions.None, null, approval.Reason ?? "Rejected by host."),
                    ct).ConfigureAwait(false);
                return;
            }

            await WireFraming.WriteJsonAsync(
                ssl,
                WireMessageType.ApprovalResponse,
                new ApprovalPayload(true, approval.GrantedPermissions, approval.SessionToken, null),
                ct).ConfigureAwait(false);

            _activeClient = client;
            _activeStream = ssl;
            _sessionCts = new CancellationTokenSource();
            claimedState = false; // ownership passes to the session; ReadLoopAsync cleans up

            var session = new RemoteSession(
                SessionId: request.RequestId,
                LocalDeviceId: localIdentity.DeviceId,
                RemoteDeviceId: request.RequesterDeviceId,
                RemoteDisplayName: request.RequesterDisplayName,
                RemoteEndpoint: remoteIp,
                IsHost: true,
                Transport: TransportMode.LanDirect,
                GrantedPermissions: approval.GrantedPermissions,
                StartedAtUtc: DateTimeOffset.UtcNow,
                SessionToken: approval.SessionToken);

            sessionManager.StartSession(session);
            await ReadLoopAsync(ssl, isHost: true, _sessionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Incoming session handling failed for {RemoteIp}", remoteIp);
        }
        finally
        {
            if (claimedState)
            {
                // Request never became a session: release the slot and the socket.
                ssl?.Dispose();
                client.Dispose();
                sessionManager.EndSession("Incoming request not completed");
            }
        }
    }

    private async Task ReadLoopAsync(SslStream stream, bool isHost, CancellationToken ct)
    {
        string reason = "Connection closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await WireFraming.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    reason = "Remote peer disconnected";
                    break;
                }

                DispatchFrame(frame, isHost);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "Session terminated";
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Session read loop ended");
            reason = $"Connection lost: {ex.Message}";
        }
        finally
        {
            CleanupActiveSession(reason);
        }
    }

    private void DispatchFrame(WireFrame frame, bool isHost)
    {
        var session = sessionManager.CurrentSession;
        if (session is null) return;

        switch (frame.Type)
        {
            case WireMessageType.ScreenFrame when !isHost:
                ScreenFrameReceived?.Invoke(this, frame.Payload);
                break;

            case WireMessageType.MonitorList when !isHost:
                MonitorListPayloadReceived?.Invoke(this, frame.Payload);
                break;

            case WireMessageType.InputCommand when isHost:
                InputCommandPayloadReceived?.Invoke(this, frame.Payload);
                break;

            case WireMessageType.SelectMonitor when isHost:
                var monEnv = JsonSerializer.Deserialize<TokenEnvelope<int>>(frame.Payload);
                if (monEnv is not null && securityService.ValidateSessionToken(session.SessionToken, monEnv.SessionToken))
                {
                    SelectMonitorRequested?.Invoke(this, monEnv.Data);
                }
                break;

            case WireMessageType.QualitySettings when isHost:
                var qEnv = JsonSerializer.Deserialize<TokenEnvelope<QualityPayload>>(frame.Payload);
                if (qEnv is not null && securityService.ValidateSessionToken(session.SessionToken, qEnv.SessionToken))
                {
                    QualitySettingsReceived?.Invoke(this, qEnv.Data);
                }
                break;

            case WireMessageType.ClipboardText:
                var clipEnv = JsonSerializer.Deserialize<TokenEnvelope<string>>(frame.Payload);
                if (clipEnv is not null &&
                    securityService.ValidateSessionToken(session.SessionToken, clipEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.ClipboardShare))
                {
                    ClipboardTextReceived?.Invoke(this, clipEnv.Data);
                }
                break;

            case WireMessageType.ChatText:
                var chatEnv = JsonSerializer.Deserialize<TokenEnvelope<ChatPayload>>(frame.Payload);
                if (chatEnv is not null &&
                    securityService.ValidateSessionToken(session.SessionToken, chatEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.SessionChat))
                {
                    ChatReceived?.Invoke(this, chatEnv.Data);
                }
                break;

            case WireMessageType.FileOffer:
                FileOfferPayloadReceived?.Invoke(this, frame.Payload);
                break;

            case WireMessageType.FileAccept when !isHost:
                var accEnv = JsonSerializer.Deserialize<TokenEnvelope<FileDecisionPayload>>(frame.Payload);
                if (accEnv is not null && securityService.ValidateSessionToken(session.SessionToken, accEnv.SessionToken))
                {
                    FileDecisionReceived?.Invoke(this, accEnv.Data);
                }
                break;

            case WireMessageType.FileChunk:
                if (BinaryEnvelope.TryUnpack(frame.Payload, out var chunkToken, out var chunkId, out var chunkData) &&
                    securityService.ValidateSessionToken(session.SessionToken, chunkToken) &&
                    sessionManager.HasPermission(SessionPermissions.FileTransfer))
                {
                    FileChunkReceived?.Invoke(this, new FileChunkPayload(chunkId, chunkData.ToArray()));
                }
                break;

            case WireMessageType.FileComplete:
                var doneEnv = JsonSerializer.Deserialize<TokenEnvelope<string>>(frame.Payload);
                if (doneEnv is not null &&
                    securityService.ValidateSessionToken(session.SessionToken, doneEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.FileTransfer))
                {
                    FileCompleteReceived?.Invoke(this, doneEnv.Data);
                }
                break;

            case WireMessageType.PermissionUpdate when !isHost:
                var perm = JsonSerializer.Deserialize<SessionPermissions>(frame.Payload);
                sessionManager.UpdatePermissions(perm);
                PermissionsUpdated?.Invoke(this, perm);
                break;

            case WireMessageType.VoiceAudio:
                if (BinaryEnvelope.TryUnpack(frame.Payload, out var voiceToken, out _, out var pcm) &&
                    securityService.ValidateSessionToken(session.SessionToken, voiceToken) &&
                    sessionManager.HasPermission(SessionPermissions.VoiceChat))
                {
                    VoiceAudioReceived?.Invoke(this, pcm.ToArray());
                }
                break;

            case WireMessageType.Disconnect:
                var msg = frame.Payload.Length > 0 ? Encoding.UTF8.GetString(frame.Payload) : "Peer disconnected";
                CleanupActiveSession(msg);
                break;
        }
    }

    public async Task SendRawAsync(WireMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var stream = _activeStream;
        if (stream is null) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activeStream is null) return;
            await WireFraming.WriteFrameAsync(_activeStream, type, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SendRawAsync failed for {Type}", type);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendTokenJsonAsync<T>(WireMessageType type, T data, CancellationToken ct = default)
    {
        var token = sessionManager.CurrentSession?.SessionToken;
        if (string.IsNullOrEmpty(token)) return Task.CompletedTask;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TokenEnvelope<T>(token, data));
        return SendRawAsync(type, bytes, ct);
    }

    public async Task DisconnectAsync(string reason = "Disconnected by user")
    {
        try
        {
            if (_activeStream is not null)
            {
                await SendRawAsync(WireMessageType.Disconnect, Encoding.UTF8.GetBytes(reason)).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore send errors during disconnect
        }

        CleanupActiveSession(reason);
    }

    private void CleanupActiveSession(string reason)
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;

        try { _activeStream?.Dispose(); } catch { /* ignore */ }
        try { _activeClient?.Dispose(); } catch { /* ignore */ }

        _activeStream = null;
        _activeClient = null;

        if (sessionManager.CurrentSession is not null || sessionManager.State != ConnectionState.Idle)
        {
            sessionManager.EndSession(reason);
            SessionDisconnected?.Invoke(this, reason);
        }
    }

    public void Dispose()
    {
        StopListening();
        CleanupActiveSession("Transport disposed");
        _serverCert?.Dispose();
        _writeLock.Dispose();
    }
}
