using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;
using MyRemote.FileTransfer;
using MyRemote.Input;
using MyRemote.Network;
using MyRemote.Network.Protocol;
using MyRemote.ScreenCapture;

namespace MyRemote.Desktop.Services;

/// <summary>
/// Coordinates LAN discovery, LAN TLS & Internet WebRTC sessions, screen streaming, input injection, clipboard,
/// chat and file transfer. Host-side streaming is driven by <see cref="ISessionManager.SessionChanged"/>, so it
/// only ever starts after a session really exists.
/// </summary>
public sealed class RemoteSessionCoordinator : IDisposable
{
    private const int MaxClipboardChars = 256 * 1024;
    private static readonly TimeSpan MaxCongestionStall = TimeSpan.FromSeconds(2);

    private readonly LanDiscoveryService _discovery;
    private readonly RemotePeerTransport _lanTransport;
    private readonly WebRtcPeerTransport _webRtcTransport;
    private readonly IScreenCaptureService _captureService;
    private readonly IInputInjector _inputInjector;
    private readonly FileTransferService _fileTransfer;
    private readonly ISecurityService _securityService;
    private readonly ISessionManager _sessionManager;
    private readonly ISessionHistoryRepository _historyRepository;
    private readonly ILogger<RemoteSessionCoordinator> _logger;

    private readonly VoiceAudioService _hostVoiceAudio = new();
    private ClipboardMonitor? _clipboardMonitor;
    private AppSettings _settings = new();
    private int _listenPort;
    private CancellationTokenSource? _streamCts;
    private string? _currentSessionId;
    private volatile int _activeMonitorIndex;
    private volatile int _targetFps = 30;
    private volatile int _jpegQuality = 75;
    private double _scale = 0.85;
    private long? _activeHistoryRowId;

    public RemoteSessionCoordinator(
        LanDiscoveryService discovery,
        RemotePeerTransport lanTransport,
        WebRtcPeerTransport webRtcTransport,
        IScreenCaptureService captureService,
        IInputInjector inputInjector,
        FileTransferService fileTransfer,
        ISecurityService securityService,
        ISessionManager sessionManager,
        ISessionHistoryRepository historyRepository,
        ILogger<RemoteSessionCoordinator> logger)
    {
        _discovery = discovery;
        _lanTransport = lanTransport;
        _webRtcTransport = webRtcTransport;
        _captureService = captureService;
        _inputInjector = inputInjector;
        _fileTransfer = fileTransfer;
        _securityService = securityService;
        _sessionManager = sessionManager;
        _historyRepository = historyRepository;
        _logger = logger;

        WireTransportEvents(_lanTransport);
        WireTransportEvents(_webRtcTransport);
        _sessionManager.SessionChanged += OnSessionChanged;

        _lanTransport.IncomingApprovalHandler = (req, ct) => HandleIncomingApprovalAsync(req, ct);
        _webRtcTransport.IncomingApprovalHandler = (req, ct) => HandleIncomingApprovalAsync(req, ct);
    }

    public bool IsInternetSignalingReady => _webRtcTransport.IsSignalingConnected;
    public string? InternetSignalingError => _webRtcTransport.LastSignalingError;
    public bool HasTurnRelay => _webRtcTransport.HasTurnRelay;

    /// <summary>Raised (on a background thread) when this PC starts being controlled.</summary>
    public event EventHandler<RemoteSession>? HostSessionStarted;

    /// <summary>Raised (on a background thread) when the controller sends a chat message to this host.</summary>
    public event EventHandler<ChatPayload>? HostChatReceived;

    public event EventHandler<IReadOnlyList<DiscoveredPeer>>? PeersUpdated
    {
        add => _discovery.PeersUpdated += value;
        remove => _discovery.PeersUpdated -= value;
    }

    private void WireTransportEvents(IPeerTransport t)
    {
        t.InputCommandPayloadReceived += OnInputPayloadReceived;
        t.SelectMonitorRequested += (_, idx) => _activeMonitorIndex = idx;
        t.QualitySettingsReceived += (_, q) =>
        {
            _targetFps = Math.Clamp(q.TargetFps, 5, 60);
            _jpegQuality = Math.Clamp(q.JpegQuality, 20, 95);
            _scale = Math.Clamp(q.Scale, 0.25, 1.0);
        };
        t.ClipboardTextReceived += (_, text) =>
        {
            if (text.Length > MaxClipboardChars) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_clipboardMonitor is not null) _clipboardMonitor.SetTextFromRemote(text);
                else try { Clipboard.SetDataObject(text, true); } catch { /* ignore */ }
            });
        };
        t.ChatReceived += (_, chat) =>
        {
            if (_sessionManager.CurrentSession is { IsHost: true })
            {
                HostChatReceived?.Invoke(this, chat);
            }
        };
        t.FileOfferPayloadReceived += (_, payload) => OnFileOfferReceived(t, payload);
        t.FileChunkReceived += OnFileChunkReceived;
        t.FileCompleteReceived += OnFileCompleteReceived;
        t.VoiceAudioReceived += (_, pcm) =>
        {
            if (_sessionManager.CurrentSession is { IsHost: true } && _sessionManager.HasPermission(SessionPermissions.VoiceChat))
            {
                _hostVoiceAudio.StartPlayback();
                _hostVoiceAudio.PlayAudio(pcm);
            }
        };
        t.SessionDisconnected += OnSessionDisconnected;
    }

    /// <summary>Must be called on the UI thread.</summary>
    public void StartNode(DeviceIdentity identity, AppSettings settings)
    {
        _settings = settings;
        _targetFps = settings.TargetFps;
        _jpegQuality = settings.JpegQuality;

        _clipboardMonitor ??= new ClipboardMonitor();
        _clipboardMonitor.TextCopied += OnLocalClipboardText;

        _listenPort = settings.ListenPort;
        try
        {
            _lanTransport.StartListening(identity, settings.ListenPort);
        }
        catch (Exception ex)
        {
            // Another program owns the port: LAN/direct-IP incoming is unavailable, but Internet mode still works.
            _logger.LogError(ex, "Cannot listen on TCP {Port}; LAN/direct incoming connections disabled. Change the port in Settings.", settings.ListenPort);
        }
        _webRtcTransport.StartSignaling(identity, settings);

        if (settings.EnableLanDiscovery)
        {
            _discovery.Start(identity, settings.DisplayName, settings.ListenPort, settings.DiscoveryPort);
        }
    }

    public void UpdateLiveSettings(DeviceIdentity identity, AppSettings settings)
    {
        _settings = settings;
        _targetFps = Math.Clamp(settings.TargetFps, 5, 60);
        _jpegQuality = Math.Clamp(settings.JpegQuality, 20, 95);

        bool idle = _sessionManager.State == ConnectionState.Idle;

        if (settings.ListenPort != _listenPort && idle)
        {
            try
            {
                _lanTransport.StartListening(identity, settings.ListenPort);
                _listenPort = settings.ListenPort;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not listen on port {Port}; keeping {OldPort}", settings.ListenPort, _listenPort);
                _lanTransport.StartListening(identity, _listenPort);
            }
        }

        if (!_webRtcTransport.IsSignalingConnected || (_webRtcTransport.SignalingSettingsDiffer(settings) && idle))
        {
            _webRtcTransport.StartSignaling(identity, settings);
        }
        else
        {
            _webRtcTransport.UpdateIceSettings(settings);
        }

        if (settings.EnableLanDiscovery)
        {
            _discovery.Start(identity, settings.DisplayName, settings.ListenPort, settings.DiscoveryPort);
        }
        else
        {
            _discovery.Stop();
        }
    }

    private IPeerTransport TransportFor(RemoteSession session) =>
        session.Transport == TransportMode.LanDirect ? _lanTransport : _webRtcTransport;

    private IPeerTransport? CurrentTransport =>
        _sessionManager.CurrentSession is { } s ? TransportFor(s) : null;

    // ───────────────────────────── Consent ─────────────────────────────

    private async Task<SessionApprovalResult> HandleIncomingApprovalAsync(SessionRequest request, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return new SessionApprovalResult(false, SessionPermissions.None, null, "Host UI not available.");
        }

        _logger.LogInformation("Incoming session request from {Requester} ({Endpoint})", request.RequesterDeviceId, request.RequesterIpAddress);

        return await dispatcher.InvokeAsync(() =>
        {
            var dlg = new IncomingRequestWindow(request, _settings, RemotePeerTransport.ApprovalTimeout);
            using var reg = ct.Register(() => dispatcher.BeginInvoke(() => dlg.CloseAsExpired()));
            System.Media.SystemSounds.Exclamation.Play();

            bool accepted = dlg.ShowDialog() == true && !ct.IsCancellationRequested;
            if (!accepted)
            {
                var reason = dlg.WasExpired ? "The remote user did not answer in time." : "Rejected by host user.";
                _logger.LogInformation("Session request from {Requester} not accepted: {Reason}", request.RequesterDeviceId, reason);
                return new SessionApprovalResult(false, SessionPermissions.None, null, reason);
            }

            _logger.LogInformation("Session request from {Requester} accepted with {Permissions}", request.RequesterDeviceId, dlg.GrantedPermissions);
            return new SessionApprovalResult(true, dlg.GrantedPermissions, _securityService.GenerateSessionToken(), null);
        }).Task.ConfigureAwait(false);
    }

    private async void OnSessionChanged(object? sender, RemoteSession? session)
    {
        if (session is null || session.SessionId == _currentSessionId) return; // end, or permission update
        _currentSessionId = session.SessionId;

        if (!session.IsHost) return;

        try
        {
            HostSessionStarted?.Invoke(this, session);
            _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
                0, session.RemoteDeviceId, session.RemoteDisplayName, true, session.Transport,
                DateTimeOffset.UtcNow, null, "Active")).ConfigureAwait(false);

            await StartHostStreamingAsync(TransportFor(session), session.SessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start host streaming");
        }
    }

    // ───────────────────────────── Host streaming ─────────────────────────────

    private async Task StartHostStreamingAsync(IPeerTransport transport, string sessionId)
    {
        _activeMonitorIndex = 0;
        var monitors = _captureService.GetMonitors();
        await transport.SendRawAsync(WireMessageType.MonitorList, JsonSerializer.SerializeToUtf8Bytes(monitors)).ConfigureAwait(false);

        _streamCts?.Cancel();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            var congestedSince = (DateTime?)null;
            long skipped = 0;
            try
            {
                while (!ct.IsCancellationRequested && transport.IsSessionActive && _sessionManager.CurrentSession?.SessionId == sessionId)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    // "Latest frame wins": while the link is backed up, don't queue more frames; send a fresh one later.
                    if (transport.IsCongested)
                    {
                        congestedSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - congestedSince < MaxCongestionStall)
                        {
                            skipped++;
                            await Task.Delay(10, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    congestedSince = null;

                    if (_sessionManager.HasPermission(SessionPermissions.ViewScreen))
                    {
                        var frame = _captureService.CaptureMonitor(_activeMonitorIndex, _jpegQuality, _scale);
                        if (frame is not null)
                        {
                            await transport.SendRawAsync(WireMessageType.ScreenFrame, frame.JpegBytes, ct).ConfigureAwait(false);
                        }
                    }

                    int delayMs = Math.Max(5, (1000 / Math.Clamp(_targetFps, 5, 60)) - (int)sw.ElapsedMilliseconds);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* session ended */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host streaming loop stopped unexpectedly");
            }
            finally
            {
                _logger.LogInformation("Host streaming stopped (frames skipped for congestion: {Skipped})", skipped);
            }
        }, ct);
    }

    // ───────────────────────────── Controller connect ─────────────────────────────

    public async Task<SessionApprovalResult> ConnectToPeerAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string hostIp,
        int port,
        string remoteLabel)
    {
        var result = await _lanTransport.ConnectAsync(
            localIdentity,
            localDisplayName,
            hostIp,
            port,
            SessionPermissions.AllControl).ConfigureAwait(false);

        if (result.Approved)
        {
            _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
                0, hostIp, remoteLabel, false, TransportMode.LanDirect, DateTimeOffset.UtcNow, null, "Active")).ConfigureAwait(false);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var viewer = new SessionViewerWindow(_lanTransport, _sessionManager, _fileTransfer, $"{remoteLabel} ({hostIp}:{port})");
                viewer.Show();
            });
        }

        return result;
    }

    public async Task<SessionApprovalResult> ConnectInternetByDeviceIdAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string targetDeviceId)
    {
        var result = await _webRtcTransport.ConnectAsync(
            localIdentity,
            localDisplayName,
            targetDeviceId,
            SessionPermissions.AllControl).ConfigureAwait(false);

        if (result.Approved)
        {
            _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
                0, targetDeviceId, targetDeviceId, false, TransportMode.InternetDirect, DateTimeOffset.UtcNow, null, "Active")).ConfigureAwait(false);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var viewer = new SessionViewerWindow(_webRtcTransport, _sessionManager, _fileTransfer, $"{targetDeviceId} (Internet)");
                viewer.Show();
            });
        }

        return result;
    }

    // ───────────────────────────── Host controls ─────────────────────────────

    public Task DisconnectAsync(string reason = "Disconnected by host") =>
        CurrentTransport?.DisconnectAsync(reason) ?? Task.CompletedTask;

    public async Task RevokeInputAsync()
    {
        var session = _sessionManager.CurrentSession;
        if (session is null) return;
        var updated = session.GrantedPermissions & ~(SessionPermissions.MouseInput | SessionPermissions.KeyboardInput);
        _sessionManager.UpdatePermissions(updated);
        await TransportFor(session).SendRawAsync(WireMessageType.PermissionUpdate, JsonSerializer.SerializeToUtf8Bytes(updated)).ConfigureAwait(false);
    }

    /// <summary>Host → controller chat message.</summary>
    public Task SendHostChatAsync(string text)
    {
        var transport = CurrentTransport;
        if (transport is null || !_sessionManager.HasPermission(SessionPermissions.SessionChat)) return Task.CompletedTask;
        return transport.SendTokenJsonAsync(WireMessageType.ChatText, new ChatPayload(_settings.DisplayName, text, DateTimeOffset.UtcNow));
    }

    private void OnLocalClipboardText(string text)
    {
        var transport = CurrentTransport;
        if (transport is null || !transport.IsSessionActive) return;
        if (!_sessionManager.HasPermission(SessionPermissions.ClipboardShare)) return;
        if (text.Length > MaxClipboardChars) return;
        _ = transport.SendTokenJsonAsync(WireMessageType.ClipboardText, text);
    }

    // ───────────────────────────── Inbound handlers ─────────────────────────────

    private void OnInputPayloadReceived(object? sender, byte[] payload)
    {
        try
        {
            var env = JsonSerializer.Deserialize<TokenEnvelope<RemoteInputEvent>>(payload);
            if (env is null) return;
            _inputInjector.TryInject(env.Data, env.SessionToken);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Malformed input payload ignored");
        }
    }

    private void OnFileOfferReceived(IPeerTransport transport, byte[] payload)
    {
        TokenEnvelope<FileOfferMetadata>? env;
        try { env = JsonSerializer.Deserialize<TokenEnvelope<FileOfferMetadata>>(payload); }
        catch (JsonException) { return; }

        var session = _sessionManager.CurrentSession;
        if (env is null || session is null || !_securityService.ValidateSessionToken(session.SessionToken, env.SessionToken)) return;
        var offer = env.Data;

        if (!_sessionManager.HasPermission(SessionPermissions.FileTransfer) ||
            offer.TotalBytes < 0 || offer.TotalBytes > FileTransferService.MaxFileSizeBytes)
        {
            _ = transport.SendTokenJsonAsync(WireMessageType.FileAccept, new FileDecisionPayload(offer.TransferId, false, "File transfer not allowed."));
            return;
        }

        // Ask asynchronously: never block the transport's receive thread on a dialog.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var res = MessageBox.Show(
                $"{session.RemoteDisplayName} wants to send you a file:\n\n{FileTransferService.SanitizeFileName(offer.FileName)}  ({offer.TotalBytes:N0} bytes)\n\nAccept it into Downloads\\MyRemote?",
                "Incoming File — MyRemote",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            bool accepted = false;
            if (res == MessageBoxResult.Yes)
            {
                var recvDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "MyRemote");
                try { accepted = _fileTransfer.BeginReceive(offer, recvDir); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not prepare incoming file"); }
            }

            _ = transport.SendTokenJsonAsync(WireMessageType.FileAccept,
                new FileDecisionPayload(offer.TransferId, accepted, accepted ? null : "Declined by the remote user."));
        });
    }

    private void OnFileChunkReceived(object? sender, FileChunkPayload chunk)
    {
        try
        {
            var progress = _fileTransfer.AppendChunk(chunk.TransferId, chunk.Data);
            if (progress is { IsCancelled: true })
            {
                _logger.LogWarning("Incoming file {File} cancelled: {Error}", progress.FileName, progress.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append incoming file chunk");
            _fileTransfer.CancelReceive(chunk.TransferId);
        }
    }

    private async void OnFileCompleteReceived(object? sender, string transferId)
    {
        try
        {
            var result = await _fileTransfer.CompleteReceiveAsync(transferId).ConfigureAwait(false);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (result.IsCompleted)
                {
                    MessageBox.Show($"File '{result.FileName}' received and SHA-256 verified in Downloads\\MyRemote.", "File Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (result.ErrorMessage != "Unknown transfer ID.")
                {
                    MessageBox.Show($"File '{result.FileName}' failed: {result.ErrorMessage}", "File Transfer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to finalize incoming file");
        }
    }

    private async void OnSessionDisconnected(object? sender, string reason)
    {
        _streamCts?.Cancel();
        _currentSessionId = null;
        _hostVoiceAudio.Stop();
        _logger.LogInformation("Session ended: {Reason}", reason);
        if (_activeHistoryRowId is long rowId)
        {
            _activeHistoryRowId = null;
            try { await _historyRepository.RecordEndAsync(rowId, DateTimeOffset.UtcNow, reason).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to record session end"); }
        }
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _sessionManager.SessionChanged -= OnSessionChanged;
        _clipboardMonitor?.Dispose();
        _hostVoiceAudio.Dispose();
        _discovery.Dispose();
        _lanTransport.Dispose();
        _webRtcTransport.Dispose();
    }
}
