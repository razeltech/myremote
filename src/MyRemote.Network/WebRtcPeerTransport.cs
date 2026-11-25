using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;
using MyRemote.Network.Protocol;
using SIPSorcery.Net;

namespace MyRemote.Network;

/// <summary>
/// Internet P2P WebRTC transport using PeerJS-compatible WebSocket signaling and SIPSorcery ICE/STUN/TURN DataChannels.
/// Enables two PCs in different networks/cities to connect by 9-digit Device ID with explicit host approval.
/// </summary>
public sealed class WebRtcPeerTransport(
    ISessionManager sessionManager,
    ISecurityService securityService,
    ILogger<WebRtcPeerTransport> logger) : IPeerTransport, IDisposable
{
    private const string DefaultPeerJsHost = "0.peerjs.com";
    private const int MaxChunkBytes = 16000;

    private readonly SemaphoreSlim _wsWriteLock = new(1, 1);
    private readonly SemaphoreSlim _dcWriteLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, ChunkAssembly> _assemblies = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _signalingCts;
    private DeviceIdentity? _localIdentity;
    private string _localPeerId = string.Empty;
    private string _customSignalingUrl = string.Empty;
    private string _customTurnServer = string.Empty;
    private string _stunServer = "stun.l.google.com:19302";

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;
    private string _remotePeerId = string.Empty;
    private string _activeConnId = string.Empty;
    private TaskCompletionSource<SessionApprovalResult>? _outgoingApprovalTcs;
    private uint _nextMsgId;

    public Func<SessionRequest, Task<SessionApprovalResult>>? IncomingApprovalHandler { get; set; }
    public bool IsSignalingConnected => _ws?.State == WebSocketState.Open;
    public bool IsSessionActive => _dc?.readyState == RTCDataChannelState.open && sessionManager.State == ConnectionState.Connected;

    public event EventHandler<byte[]>? ScreenFrameReceived;
    public event EventHandler<byte[]>? InputCommandPayloadReceived;
    public event EventHandler<byte[]>? MonitorListPayloadReceived;
    public event EventHandler<int>? SelectMonitorRequested;
    public event EventHandler<QualityPayload>? QualitySettingsReceived;
    public event EventHandler<string>? ClipboardTextReceived;
    public event EventHandler<ChatPayload>? ChatReceived;
    public event EventHandler<byte[]>? FileOfferPayloadReceived;
    public event EventHandler<FileChunkPayload>? FileChunkReceived;
    public event EventHandler<string>? FileCompleteReceived;
    public event EventHandler<SessionPermissions>? PermissionsUpdated;
    public event EventHandler<string>? SessionDisconnected;

    public void StartSignaling(DeviceIdentity localIdentity, AppSettings settings)
    {
        StopSignaling();
        _localIdentity = localIdentity;
        _localPeerId = ToPeerId(localIdentity.DeviceId);
        _stunServer = settings.StunServer;
        _customSignalingUrl = settings.CustomSignalingUrl;
        _customTurnServer = settings.CustomTurnServer;

        _signalingCts = new CancellationTokenSource();
        var ct = _signalingCts.Token;
        _ = Task.Run(() => SignalingLoopAsync(ct), ct);
    }

    public void StopSignaling()
    {
        _signalingCts?.Cancel();
        _signalingCts?.Dispose();
        _signalingCts = null;
        try { _ws?.Dispose(); } catch { /* ignore */ }
        _ws = null;
    }

    public static string ToPeerId(string deviceId) =>
        $"myremote-{deviceId.Replace("-", "").Trim()}";

    public static string FromPeerId(string peerId)
    {
        var raw = peerId.StartsWith("myremote-", StringComparison.OrdinalIgnoreCase)
            ? peerId["myremote-".Length..]
            : peerId;
        return raw.Length == 9 ? $"{raw[..3]}-{raw[3..6]}-{raw[6..9]}" : raw;
    }

    private Uri BuildSignalingUri()
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        if (!string.IsNullOrWhiteSpace(_customSignalingUrl) &&
            Uri.TryCreate(_customSignalingUrl, UriKind.Absolute, out var custom))
        {
            var sep = string.IsNullOrEmpty(custom.Query) ? "?" : "&";
            return new Uri($"{custom}{sep}id={Uri.EscapeDataString(_localPeerId)}&token={token}");
        }

        return new Uri($"wss://{DefaultPeerJsHost}/peerjs?key=peerjs&id={Uri.EscapeDataString(_localPeerId)}&token={token}&version=1.5.4");
    }

    private List<RTCIceServer> BuildIceServers()
    {
        var list = new List<RTCIceServer>
        {
            new() { urls = string.IsNullOrWhiteSpace(_stunServer) ? "stun:stun.l.google.com:19302" : $"stun:{_stunServer.Replace("stun:", "")}" },
            new() { urls = "stun:stun.cloudflare.com:3478" }
        };

        if (!string.IsNullOrWhiteSpace(_customTurnServer))
        {
            list.Add(new RTCIceServer { urls = _customTurnServer });
        }
        else
        {
            // Free community OpenRelay TURN fallback (same as multiplayer-kit) for strict NATs
            list.Add(new RTCIceServer
            {
                urls = "turn:openrelay.metered.ca:80",
                username = "openrelayproject",
                credential = "openrelayproject"
            });
            list.Add(new RTCIceServer
            {
                urls = "turn:openrelay.metered.ca:443?transport=tcp",
                username = "openrelayproject",
                credential = "openrelayproject"
            });
        }

        return list;
    }

    private async Task SignalingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(BuildSignalingUri(), ct).ConfigureAwait(false);
                logger.LogInformation("Connected to internet signaling as {PeerId}", _localPeerId);

                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(async () =>
                {
                    while (!hbCts.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), hbCts.Token).ConfigureAwait(false);
                        await SendSignalAsync(new { type = "HEARTBEAT" }, hbCts.Token).ConfigureAwait(false);
                    }
                }, hbCts.Token);

                var buffer = new byte[64 * 1024];
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, res.Count);
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Close) break;
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    await HandleSignalMessageAsync(json, ct).ConfigureAwait(false);
                }

                hbCts.Cancel();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Signaling connection interrupted; retrying in 5s");
            }

            try { _ws?.Dispose(); } catch { /* ignore */ }
            _ws = null;
            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<SessionApprovalResult> ConnectAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string targetDeviceId,
        SessionPermissions requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        if (!IsSignalingConnected)
        {
            return new SessionApprovalResult(false, SessionPermissions.None, null, "Internet signaling is not connected yet. Check internet access.");
        }

        if (!sessionManager.TryTransitionTo(ConnectionState.Connecting))
        {
            return new SessionApprovalResult(false, SessionPermissions.None, null, "Another session is already active.");
        }

        CleanupPeerConnection();
        _remotePeerId = ToPeerId(targetDeviceId);
        _activeConnId = $"dc_{Guid.NewGuid():N}";
        _outgoingApprovalTcs = new TaskCompletionSource<SessionApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = BuildIceServers() });
            _dc = await _pc.createDataChannel("myremote").ConfigureAwait(false);
            WireDataChannel(_dc, isHost: false, localIdentity, localDisplayName, requestedPermissions);

            _pc.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                _ = SendSignalAsync(new
                {
                    type = "CANDIDATE",
                    dst = _remotePeerId,
                    payload = new
                    {
                        candidate = new
                        {
                            candidate = candidate.ToString(),
                            sdpMid = candidate.sdpMid ?? "0",
                            sdpMLineIndex = candidate.sdpMLineIndex
                        },
                        type = "data",
                        connectionId = _activeConnId
                    }
                }, CancellationToken.None);
            };

            _pc.onconnectionstatechange += state =>
            {
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                {
                    _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(false, SessionPermissions.None, null, $"WebRTC connection {state}."));
                    CleanupSession($"WebRTC {state}");
                }
            };

            var offer = _pc.createOffer();
            await _pc.setLocalDescription(offer).ConfigureAwait(false);
            logger.LogInformation("Sending WebRTC OFFER to {RemotePeerId}", _remotePeerId);

            await SendSignalAsync(new
            {
                type = "OFFER",
                dst = _remotePeerId,
                payload = new
                {
                    sdp = new { type = "offer", sdp = offer.sdp },
                    type = "data",
                    connectionId = _activeConnId,
                    label = "myremote",
                    serialization = "binary"
                }
            }, cancellationToken).ConfigureAwait(false);

            sessionManager.TryTransitionTo(ConnectionState.AwaitingHostApproval);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            using var reg = timeoutCts.Token.Register(() =>
                _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(false, SessionPermissions.None, null, "Timed out waiting for remote peer or host approval.")));

            var result = await _outgoingApprovalTcs.Task.ConfigureAwait(false);
            if (!result.Approved)
            {
                CleanupSession(result.Reason ?? "Rejected");
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebRTC outgoing connection failed");
            CleanupSession(ex.Message);
            return new SessionApprovalResult(false, SessionPermissions.None, null, ex.Message);
        }
    }

    private async Task HandleSignalMessageAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var msgType = typeProp.GetString();

        switch (msgType)
        {
            case "OPEN":
                logger.LogInformation("Signaling registered as {PeerId}", _localPeerId);
                break;

            case "OFFER":
                logger.LogInformation("Received WebRTC OFFER from {Src}", root.TryGetProperty("src", out var s) ? s.GetString() : "unknown");
                await HandleIncomingOfferAsync(root, ct).ConfigureAwait(false);
                break;

            case "ANSWER":
                logger.LogInformation("Received WebRTC ANSWER");
                if (_pc is not null && root.TryGetProperty("payload", out var ansPayload) &&
                    ansPayload.TryGetProperty("sdp", out var ansSdp) &&
                    ansSdp.TryGetProperty("sdp", out var ansSdpStr))
                {
                    var res = _pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = ansSdpStr.GetString()
                    });
                    logger.LogInformation("SetRemoteDescription(Answer) => {Result}", res);
                }
                break;

            case "CANDIDATE":
                if (_pc is not null && root.TryGetProperty("payload", out var candPayload) &&
                    candPayload.TryGetProperty("candidate", out var candObj) &&
                    candObj.TryGetProperty("candidate", out var candStr))
                {
                    _pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candStr.GetString(),
                        sdpMid = candObj.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : "0",
                        sdpMLineIndex = candObj.TryGetProperty("sdpMLineIndex", out var idx) ? (ushort)idx.GetInt32() : (ushort)0
                    });
                }
                break;

            case "ERROR":
            case "ID-TAKEN":
                var errMsg = root.TryGetProperty("payload", out var p) && p.TryGetProperty("msg", out var m) ? m.GetString() : msgType;
                _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(false, SessionPermissions.None, null, errMsg ?? "Signaling error"));
                break;
        }
    }

    private async Task HandleIncomingOfferAsync(JsonElement root, CancellationToken ct)
    {
        if (_localIdentity is null) return;
        var srcPeer = root.TryGetProperty("src", out var srcProp) ? srcProp.GetString() ?? "" : "";
        if (!securityService.CheckRateLimit(srcPeer, maxRequestsPerWindow: 5, windowSeconds: 30)) return;
        if (sessionManager.State != ConnectionState.Idle) return;

        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("sdp", out var sdpObj) ||
            !sdpObj.TryGetProperty("sdp", out var sdpStr))
        {
            return;
        }

        var connId = payload.TryGetProperty("connectionId", out var cid) ? cid.GetString() ?? "" : "";

        CleanupPeerConnection();
        _remotePeerId = srcPeer;
        _activeConnId = connId;

        _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = BuildIceServers() });
        _pc.ondatachannel += dc =>
        {
            _dc = dc;
            WireDataChannel(dc, isHost: true, _localIdentity, _localIdentity.DisplayName, SessionPermissions.AllControl);
        };

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            _ = SendSignalAsync(new
            {
                type = "CANDIDATE",
                dst = _remotePeerId,
                payload = new
                {
                    candidate = new
                    {
                        candidate = candidate.ToString(),
                        sdpMid = candidate.sdpMid ?? "0",
                        sdpMLineIndex = candidate.sdpMLineIndex
                    },
                    type = "data",
                    connectionId = _activeConnId
                }
            }, CancellationToken.None);
        };

        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                CleanupSession($"WebRTC {state}");
            }
        };

        var setRes = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdpStr.GetString()
        });
        logger.LogInformation("SetRemoteDescription(Offer) => {Result}", setRes);

        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        logger.LogInformation("Sending WebRTC ANSWER to {RemotePeerId}", _remotePeerId);

        await SendSignalAsync(new
        {
            type = "ANSWER",
            dst = _remotePeerId,
            payload = new
            {
                sdp = new { type = "answer", sdp = answer.sdp },
                type = "data",
                connectionId = _activeConnId
            }
        }, ct).ConfigureAwait(false);
    }

    private void WireDataChannel(
        RTCDataChannel dc,
        bool isHost,
        DeviceIdentity localIdentity,
        string localDisplayName,
        SessionPermissions requestedPermissions)
    {
        dc.onopen += () =>
        {
            if (!isHost)
            {
                var hello = new HelloPayload(localIdentity.DeviceId, localDisplayName, requestedPermissions, Guid.NewGuid().ToString("N"));
                _ = SendRawAsync(WireMessageType.HelloRequest, JsonSerializer.SerializeToUtf8Bytes(hello));
            }
        };

        dc.onmessage += (_, _, data) =>
        {
            ProcessIncomingChunk(data, isHost, localIdentity);
        };

        dc.onclose += () => CleanupSession("DataChannel closed");
    }

    private void ProcessIncomingChunk(byte[] packet, bool isHost, DeviceIdentity localIdentity)
    {
        if (packet.Length < 13) return;
        uint msgId = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4));
        ushort chunkIdx = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        ushort totalChunks = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var msgType = (WireMessageType)packet[8];
        int totalLen = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(9, 4));

        if (!Enum.IsDefined(typeof(WireMessageType), msgType) || totalLen < 0 || totalLen > WireFraming.MaxPayloadBytes || totalChunks == 0)
        {
            return;
        }

        byte[] body = packet[13..];
        byte[]? completePayload = null;

        if (totalChunks == 1)
        {
            completePayload = body;
        }
        else
        {
            var assembly = _assemblies.GetOrAdd(msgId, _ => new ChunkAssembly(totalChunks, totalLen));
            if (assembly.Add(chunkIdx, body))
            {
                _assemblies.TryRemove(msgId, out _);
                completePayload = assembly.Assemble();
            }
        }

        if (completePayload is null) return;
        _ = HandleCompleteFrameAsync(msgType, completePayload, isHost, localIdentity);
    }

    private async Task HandleCompleteFrameAsync(WireMessageType type, byte[] payload, bool isHost, DeviceIdentity localIdentity)
    {
        if (type == WireMessageType.HelloRequest && isHost)
        {
            var hello = JsonSerializer.Deserialize<HelloPayload>(payload);
            if (hello is null || !securityService.IsValidDeviceId(hello.DeviceId))
            {
                await SendRawAsync(WireMessageType.ApprovalResponse, JsonSerializer.SerializeToUtf8Bytes(
                    new ApprovalPayload(false, SessionPermissions.None, null, "Invalid device identity."))).ConfigureAwait(false);
                CleanupSession("Invalid identity");
                return;
            }

            var req = new SessionRequest(
                Guid.NewGuid().ToString("N"),
                hello.DeviceId,
                string.IsNullOrWhiteSpace(hello.DisplayName) ? hello.DeviceId : hello.DisplayName,
                $"WebRTC ({FromPeerId(_remotePeerId)})",
                hello.RequestedPermissions,
                DateTimeOffset.UtcNow,
                hello.Nonce);

            var approval = IncomingApprovalHandler is not null
                ? await IncomingApprovalHandler(req).ConfigureAwait(false)
                : new SessionApprovalResult(false, SessionPermissions.None, null, "No host handler.");

            await SendRawAsync(WireMessageType.ApprovalResponse, JsonSerializer.SerializeToUtf8Bytes(
                new ApprovalPayload(approval.Approved, approval.GrantedPermissions, approval.SessionToken, approval.Reason))).ConfigureAwait(false);

            if (!approval.Approved || string.IsNullOrWhiteSpace(approval.SessionToken))
            {
                CleanupSession(approval.Reason ?? "Rejected by host");
                return;
            }

            sessionManager.StartSession(new RemoteSession(
                req.RequestId,
                localIdentity.DeviceId,
                req.RequesterDeviceId,
                req.RequesterDisplayName,
                req.RequesterIpAddress,
                IsHost: true,
                TransportMode.InternetDirect,
                approval.GrantedPermissions,
                DateTimeOffset.UtcNow,
                approval.SessionToken));
            return;
        }

        if (type == WireMessageType.ApprovalResponse && !isHost)
        {
            var approval = JsonSerializer.Deserialize<ApprovalPayload>(payload);
            if (approval is null || !approval.Approved || string.IsNullOrWhiteSpace(approval.SessionToken))
            {
                _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(false, SessionPermissions.None, null, approval?.Reason ?? "Rejected by host."));
                return;
            }

            var remoteId = FromPeerId(_remotePeerId);
            sessionManager.StartSession(new RemoteSession(
                Guid.NewGuid().ToString("N"),
                localIdentity.DeviceId,
                remoteId,
                remoteId,
                "WebRTC P2P",
                IsHost: false,
                TransportMode.InternetDirect,
                approval.GrantedPermissions,
                DateTimeOffset.UtcNow,
                approval.SessionToken));

            _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(true, approval.GrantedPermissions, approval.SessionToken, null));
            return;
        }

        var session = sessionManager.CurrentSession;
        if (session is null) return;

        switch (type)
        {
            case WireMessageType.ScreenFrame when !isHost:
                ScreenFrameReceived?.Invoke(this, payload);
                break;
            case WireMessageType.MonitorList when !isHost:
                MonitorListPayloadReceived?.Invoke(this, payload);
                break;
            case WireMessageType.InputCommand when isHost:
                InputCommandPayloadReceived?.Invoke(this, payload);
                break;
            case WireMessageType.SelectMonitor when isHost:
                var monEnv = JsonSerializer.Deserialize<TokenEnvelope<int>>(payload);
                if (monEnv is not null && securityService.ValidateSessionToken(session.SessionToken, monEnv.SessionToken))
                    SelectMonitorRequested?.Invoke(this, monEnv.Data);
                break;
            case WireMessageType.QualitySettings when isHost:
                var qEnv = JsonSerializer.Deserialize<TokenEnvelope<QualityPayload>>(payload);
                if (qEnv is not null && securityService.ValidateSessionToken(session.SessionToken, qEnv.SessionToken))
                    QualitySettingsReceived?.Invoke(this, qEnv.Data);
                break;
            case WireMessageType.ClipboardText:
                var clipEnv = JsonSerializer.Deserialize<TokenEnvelope<string>>(payload);
                if (clipEnv is not null && securityService.ValidateSessionToken(session.SessionToken, clipEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.ClipboardShare))
                    ClipboardTextReceived?.Invoke(this, clipEnv.Data);
                break;
            case WireMessageType.ChatText:
                var chatEnv = JsonSerializer.Deserialize<TokenEnvelope<ChatPayload>>(payload);
                if (chatEnv is not null && securityService.ValidateSessionToken(session.SessionToken, chatEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.SessionChat))
                    ChatReceived?.Invoke(this, chatEnv.Data);
                break;
            case WireMessageType.FileOffer:
                FileOfferPayloadReceived?.Invoke(this, payload);
                break;
            case WireMessageType.FileChunk:
                var chunkEnv = JsonSerializer.Deserialize<TokenEnvelope<FileChunkPayload>>(payload);
                if (chunkEnv is not null && securityService.ValidateSessionToken(session.SessionToken, chunkEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.FileTransfer))
                    FileChunkReceived?.Invoke(this, chunkEnv.Data);
                break;
            case WireMessageType.FileComplete:
                var doneEnv = JsonSerializer.Deserialize<TokenEnvelope<string>>(payload);
                if (doneEnv is not null && securityService.ValidateSessionToken(session.SessionToken, doneEnv.SessionToken) &&
                    sessionManager.HasPermission(SessionPermissions.FileTransfer))
                    FileCompleteReceived?.Invoke(this, doneEnv.Data);
                break;
            case WireMessageType.PermissionUpdate when !isHost:
                var perm = JsonSerializer.Deserialize<SessionPermissions>(payload);
                sessionManager.UpdatePermissions(perm);
                PermissionsUpdated?.Invoke(this, perm);
                break;
            case WireMessageType.Disconnect:
                var reason = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Peer disconnected";
                CleanupSession(reason);
                break;
        }
    }

    public async Task SendRawAsync(WireMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var dc = _dc;
        if (dc is null || dc.readyState != RTCDataChannelState.open) return;

        uint msgId = Interlocked.Increment(ref _nextMsgId);
        int totalLen = payload.Length;
        ushort totalChunks = (ushort)Math.Max(1, (totalLen + MaxChunkBytes - 1) / MaxChunkBytes);

        await _dcWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dc is null || _dc.readyState != RTCDataChannelState.open) return;

            for (ushort i = 0; i < totalChunks; i++)
            {
                int offset = i * MaxChunkBytes;
                int count = Math.Min(MaxChunkBytes, totalLen - offset);
                byte[] packet = new byte[13 + count];
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), msgId);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), i);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), totalChunks);
                packet[8] = (byte)type;
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(9, 4), totalLen);
                if (count > 0)
                {
                    payload.Slice(offset, count).CopyTo(packet.AsMemory(13));
                }

                _dc.send(packet);
            }
        }
        finally
        {
            _dcWriteLock.Release();
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
            if (_dc?.readyState == RTCDataChannelState.open)
            {
                await SendRawAsync(WireMessageType.Disconnect, Encoding.UTF8.GetBytes(reason)).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
        CleanupSession(reason);
    }

    private void CleanupPeerConnection()
    {
        var dc = _dc;
        var pc = _pc;
        _dc = null;
        _pc = null;
        _assemblies.Clear();

        try { dc?.close(); } catch { /* ignore */ }
        try { pc?.close(); } catch { /* ignore */ }
    }

    private void CleanupSession(string reason)
    {
        CleanupPeerConnection();
        if (sessionManager.CurrentSession is not null || sessionManager.State != ConnectionState.Idle)
        {
            sessionManager.EndSession(reason);
            SessionDisconnected?.Invoke(this, reason);
        }
    }

    private async Task SendSignalAsync(object obj, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await _wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SendSignalAsync failed");
        }
        finally
        {
            _wsWriteLock.Release();
        }
    }

    public void Dispose()
    {
        StopSignaling();
        CleanupPeerConnection();
        _wsWriteLock.Dispose();
        _dcWriteLock.Dispose();
    }

    private sealed class ChunkAssembly(ushort totalChunks, int totalLength)
    {
        private readonly byte[][] _chunks = new byte[totalChunks][];
        private int _received;

        public bool Add(ushort index, byte[] data)
        {
            if (index >= _chunks.Length || _chunks[index] is not null) return false;
            _chunks[index] = data;
            return Interlocked.Increment(ref _received) == _chunks.Length;
        }

        public byte[] Assemble()
        {
            var result = new byte[totalLength];
            int offset = 0;
            foreach (var c in _chunks)
            {
                if (c is null) continue;
                Buffer.BlockCopy(c, 0, result, offset, Math.Min(c.Length, totalLength - offset));
                offset += c.Length;
            }
            return result;
        }
    }
}
