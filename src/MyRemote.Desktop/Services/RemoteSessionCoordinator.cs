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
/// Coordinates LAN discovery, TLS host/client sessions, screen streaming, input injection, clipboard, and file transfer.
/// </summary>
public sealed class RemoteSessionCoordinator : IDisposable
{
    private readonly LanDiscoveryService _discovery;
    private readonly RemotePeerTransport _transport;
    private readonly IScreenCaptureService _captureService;
    private readonly IInputInjector _inputInjector;
    private readonly FileTransferService _fileTransfer;
    private readonly ISecurityService _securityService;
    private readonly ISessionManager _sessionManager;
    private readonly ISessionHistoryRepository _historyRepository;
    private readonly ILogger<RemoteSessionCoordinator> _logger;

    private CancellationTokenSource? _streamCts;
    private int _activeMonitorIndex;
    private int _targetFps = 30;
    private int _jpegQuality = 75;
    private double _scale = 0.85;
    private long? _activeHistoryRowId;

    public RemoteSessionCoordinator(
        LanDiscoveryService discovery,
        RemotePeerTransport transport,
        IScreenCaptureService captureService,
        IInputInjector inputInjector,
        FileTransferService fileTransfer,
        ISecurityService securityService,
        ISessionManager sessionManager,
        ISessionHistoryRepository historyRepository,
        ILogger<RemoteSessionCoordinator> logger)
    {
        _discovery = discovery;
        _transport = transport;
        _captureService = captureService;
        _inputInjector = inputInjector;
        _fileTransfer = fileTransfer;
        _securityService = securityService;
        _sessionManager = sessionManager;
        _historyRepository = historyRepository;
        _logger = logger;

        _transport.InputCommandPayloadReceived += OnInputPayloadReceived;
        _transport.SelectMonitorRequested += (_, idx) => _activeMonitorIndex = idx;
        _transport.QualitySettingsReceived += (_, q) =>
        {
            _targetFps = Math.Clamp(q.TargetFps, 5, 60);
            _jpegQuality = Math.Clamp(q.JpegQuality, 20, 95);
            _scale = Math.Clamp(q.Scale, 0.25, 1.0);
        };
        _transport.ClipboardTextReceived += (_, text) =>
        {
            Application.Current?.Dispatcher.Invoke(() => Clipboard.SetText(text));
        };
        _transport.FileOfferPayloadReceived += OnFileOfferReceived;
        _transport.FileChunkReceived += OnFileChunkReceived;
        _transport.FileCompleteReceived += OnFileCompleteReceived;
        _transport.SessionDisconnected += OnSessionDisconnected;
    }

    public event EventHandler<IReadOnlyList<DiscoveredPeer>>? PeersUpdated
    {
        add => _discovery.PeersUpdated += value;
        remove => _discovery.PeersUpdated -= value;
    }

    public void StartNode(DeviceIdentity identity, AppSettings settings)
    {
        _targetFps = settings.TargetFps;
        _jpegQuality = settings.JpegQuality;

        _transport.IncomingApprovalHandler = request => HandleIncomingApprovalAsync(request, settings);
        _transport.StartListening(identity, settings.ListenPort);

        if (settings.EnableLanDiscovery)
        {
            _discovery.Start(identity, settings.DisplayName, settings.ListenPort, settings.DiscoveryPort);
        }
    }

    public void UpdateLiveSettings(DeviceIdentity identity, AppSettings settings)
    {
        _targetFps = Math.Clamp(settings.TargetFps, 5, 60);
        _jpegQuality = Math.Clamp(settings.JpegQuality, 20, 95);
        _transport.IncomingApprovalHandler = request => HandleIncomingApprovalAsync(request, settings);

        if (settings.EnableLanDiscovery)
        {
            _discovery.Start(identity, settings.DisplayName, settings.ListenPort, settings.DiscoveryPort);
        }
        else
        {
            _discovery.Stop();
        }
    }

    private async Task<SessionApprovalResult> HandleIncomingApprovalAsync(SessionRequest request, AppSettings settings)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new IncomingRequestWindow(request, settings);
            bool accepted = dlg.ShowDialog() == true;
            if (!accepted)
            {
                return new SessionApprovalResult(false, SessionPermissions.None, null, "Rejected by host user.");
            }

            var token = _securityService.GenerateSessionToken();
            _ = StartHostStreamingAsync(request, token);
            return new SessionApprovalResult(true, dlg.GrantedPermissions, token, null);
        }).Task;
    }

    private async Task StartHostStreamingAsync(SessionRequest request, string token)
    {
        _activeHistoryRowId = await _historyRepository.RecordStartAsync(new SessionHistoryEntry(
            0, request.RequesterDeviceId, request.RequesterDisplayName, true, TransportMode.LanDirect, DateTimeOffset.UtcNow, null, "Active"));

        await Task.Delay(150);
        var monitors = _captureService.GetMonitors();
        await _transport.SendRawAsync(WireMessageType.MonitorList, JsonSerializer.SerializeToUtf8Bytes(monitors));

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _transport.IsSessionActive)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (_sessionManager.HasPermission(SessionPermissions.ViewScreen))
                {
                    var frame = _captureService.CaptureMonitor(_activeMonitorIndex, _jpegQuality, _scale);
                    if (frame is not null)
                    {
                        await _transport.SendRawAsync(WireMessageType.ScreenFrame, frame.JpegBytes, ct);
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
        var result = await _transport.ConnectAsync(
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
                var viewer = new SessionViewerWindow(_transport, _sessionManager, _fileTransfer, $"{remoteLabel} ({hostIp}:{port})");
                viewer.Show();
            });
        }

        return result;
    }

    public Task DisconnectAsync(string reason = "Disconnected by host") => _transport.DisconnectAsync(reason);

    public async Task RevokeInputAsync()
    {
        var session = _sessionManager.CurrentSession;
        if (session is null) return;
        var updated = session.GrantedPermissions & ~(SessionPermissions.MouseInput | SessionPermissions.KeyboardInput);
        _sessionManager.UpdatePermissions(updated);
        await _transport.SendRawAsync(WireMessageType.PermissionUpdate, JsonSerializer.SerializeToUtf8Bytes(updated));
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
        _transport.Dispose();
    }
}
