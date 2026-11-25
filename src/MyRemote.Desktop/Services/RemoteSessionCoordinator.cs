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
/// Coordinates LAN discovery, LAN TLS & Internet WebRTC sessions, screen streaming, input injection, clipboard, and file transfer.
/// </summary>
public sealed class RemoteSessionCoordinator : IDisposable
{
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

    private IPeerTransport _activeTransport;
    private CancellationTokenSource? _streamCts;
    private int _activeMonitorIndex;
    private int _targetFps = 30;
    private int _jpegQuality = 75;
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
        _activeTransport = lanTransport;
        _captureService = captureService;
        _inputInjector = inputInjector;
        _fileTransfer = fileTransfer;
        _securityService = securityService;
        _sessionManager = sessionManager;
        _historyRepository = historyRepository;
        _logger = logger;

        WireTransportEvents(_lanTransport);
        WireTransportEvents(_webRtcTransport);
    }

    public bool IsInternetSignalingReady => _webRtcTransport.IsSignalingConnected;

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
            Application.Current?.Dispatcher.Invoke(() =>
            {
                try { Clipboard.SetDataObject(text, false); } catch { /* ignore */ }
            });
        };
        t.FileOfferPayloadReceived += OnFileOfferReceived;
        t.FileChunkReceived += OnFileChunkReceived;
        t.FileCompleteReceived += OnFileCompleteReceived;
        t.SessionDisconnected += OnSessionDisconnected;
    }

    public void StartNode(DeviceIdentity identity, AppSettings settings)
    {
        _targetFps = settings.TargetFps;
        _jpegQuality = settings.JpegQuality;

        _lanTransport.IncomingApprovalHandler = req => HandleIncomingApprovalAsync(_lanTransport, req, settings);
        _webRtcTransport.IncomingApprovalHandler = req => HandleIncomingApprovalAsync(_webRtcTransport, req, settings);

        _lanTransport.StartListening(identity, settings.ListenPort);
        _webRtcTransport.StartSignaling(identity, settings);

        if (settings.EnableLanDiscovery)
        {
            _discovery.Start(identity, settings.DisplayName, settings.ListenPort, settings.DiscoveryPort);
        }
    }

    public void UpdateLiveSettings(DeviceIdentity identity, AppSettings settings)
    {
        _targetFps = Math.Clamp(settings.TargetFps, 5, 60);
        _jpegQuality = Math.Clamp(settings.JpegQuality, 20, 95);

        _lanTransport.IncomingApprovalHandler = req => HandleIncomingApprovalAsync(_lanTransport, req, settings);
        _webRtcTransport.IncomingApprovalHandler = req => HandleIncomingApprovalAsync(_webRtcTransport, req, settings);
        if (!_webRtcTransport.IsSignalingConnected)
        {
            _webRtcTransport.StartSignaling(identity, settings);
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

    private async Task<SessionApprovalResult> HandleIncomingApprovalAsync(IPeerTransport sourceTransport, SessionRequest request, AppSettings settings)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new IncomingRequestWindow(request, settings);
            bool accepted = dlg.ShowDialog() == true;
            if (!accepted)
            {
                return new SessionApprovalResult(false, SessionPermissions.None, null, "Rejected by host user.");
            }

            _activeTransport = sourceTransport;
            var token = _securityService.GenerateSessionToken();
            _ = StartHostStreamingAsync(sourceTransport, request);
            return new SessionApprovalResult(true, dlg.GrantedPermissions, token, null);
        }).Task;
    }

    private async Task StartHostStreamingAsync(IPeerTransport transport, SessionRequest request)
    {
        _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
            0, request.RequesterDeviceId, request.RequesterDisplayName, true,
            transport is WebRtcPeerTransport ? TransportMode.InternetDirect : TransportMode.LanDirect,
            DateTimeOffset.UtcNow, null, "Active"));

        await Task.Delay(150);
        var monitors = _captureService.GetMonitors();
        await transport.SendRawAsync(WireMessageType.MonitorList, JsonSerializer.SerializeToUtf8Bytes(monitors));

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && transport.IsSessionActive)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (_sessionManager.HasPermission(SessionPermissions.ViewScreen))
                {
                    var frame = _captureService.CaptureMonitor(_activeMonitorIndex, _jpegQuality, _scale);
                    if (frame is not null)
                    {
                        await transport.SendRawAsync(WireMessageType.ScreenFrame, frame.JpegBytes, ct);
                    }
                }

                int delayMs = Math.Max(16, (1000 / Math.Clamp(_targetFps, 5, 60)) - (int)sw.ElapsedMilliseconds);
                await Task.Delay(delayMs, ct);
            }
        }, ct);
    }

    public async Task<SessionApprovalResult> ConnectToPeerAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string hostIp,
        int port,
        string remoteLabel)
    {
        _activeTransport = _lanTransport;
        var result = await _lanTransport.ConnectAsync(
            localIdentity,
            localDisplayName,
            hostIp,
            port,
            SessionPermissions.AllControl);

        if (result.Approved)
        {
            _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
                0, hostIp, remoteLabel, false, TransportMode.LanDirect, DateTimeOffset.UtcNow, null, "Active"));

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
        _activeTransport = _webRtcTransport;
        var result = await _webRtcTransport.ConnectAsync(
            localIdentity,
            localDisplayName,
            targetDeviceId,
            SessionPermissions.AllControl);

        if (result.Approved)
        {
            _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
                0, targetDeviceId, targetDeviceId, false, TransportMode.InternetDirect, DateTimeOffset.UtcNow, null, "Active"));

            Application.Current.Dispatcher.Invoke(() =>
            {
                var viewer = new SessionViewerWindow(_webRtcTransport, _sessionManager, _fileTransfer, $"{targetDeviceId} (Internet WebRTC)");
                viewer.Show();
            });
        }

        return result;
    }

    public Task DisconnectAsync(string reason = "Disconnected by host") => _activeTransport.DisconnectAsync(reason);

    public async Task RevokeInputAsync()
    {
        var session = _sessionManager.CurrentSession;
        if (session is null) return;
        var updated = session.GrantedPermissions & ~(SessionPermissions.MouseInput | SessionPermissions.KeyboardInput);
        _sessionManager.UpdatePermissions(updated);
        await _activeTransport.SendRawAsync(WireMessageType.PermissionUpdate, JsonSerializer.SerializeToUtf8Bytes(updated));
    }

    private void OnInputPayloadReceived(object? sender, byte[] payload)
    {
        var env = JsonSerializer.Deserialize<TokenEnvelope<RemoteInputEvent>>(payload);
        if (env is null) return;
        _inputInjector.TryInject(env.Data, env.SessionToken);
    }

    private void OnFileOfferReceived(object? sender, byte[] payload)
    {
        var env = JsonSerializer.Deserialize<TokenEnvelope<FileOfferMetadata>>(payload);
        var session = _sessionManager.CurrentSession;
        if (env is null || session is null || !_securityService.ValidateSessionToken(session.SessionToken, env.SessionToken)) return;
        if (!_sessionManager.HasPermission(SessionPermissions.FileTransfer)) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var res = MessageBox.Show(
                $"Accept incoming file '{env.Data.FileName}' ({env.Data.TotalBytes:N0} bytes)?",
                "Incoming File Transfer — MyRemote",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                var recvDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "MyRemote");
                _fileTransfer.BeginReceive(env.Data, recvDir);
            }
        });
    }

    private async void OnFileChunkReceived(object? sender, FileChunkPayload chunk)
    {
        try
        {
            await _fileTransfer.AppendChunkAsync(chunk.TransferId, chunk.Data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append incoming file chunk");
        }
    }

    private async void OnFileCompleteReceived(object? sender, string transferId)
    {
        var result = await _fileTransfer.CompleteReceiveAsync(transferId);
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (result.IsCompleted)
            {
                MessageBox.Show($"File '{result.FileName}' received and SHA-256 verified in Downloads\\MyRemote.", "File Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"File '{result.FileName}' failed: {result.ErrorMessage}", "File Transfer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private async void OnSessionDisconnected(object? sender, string reason)
    {
        _streamCts?.Cancel();
        if (_activeHistoryRowId is long rowId)
        {
            _activeHistoryRowId = null;
            await _historyRepository.RecordEndAsync(rowId, DateTimeOffset.UtcNow, reason);
        }
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _discovery.Dispose();
        _lanTransport.Dispose();
        _webRtcTransport.Dispose();
    }
}
