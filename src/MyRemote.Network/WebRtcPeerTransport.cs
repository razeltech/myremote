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
/// <remarks>
/// Connection phases (controller side) and the failure message each one produces:
///  1. Signaling OFFER → ANSWER   : no answer / EXPIRE  → "remote PC offline or busy".
///  2. ICE + DTLS → DataChannel open: timeout          → "no network path; configure TURN relay".
///  3. Hello → host consent dialog  : timeout/reject    → "host did not accept".
/// Every RTCPeerConnection gets a generation number; callbacks from an older connection are ignored so that
/// closing an old connection can never tear down the new session.
/// </remarks>
public sealed class WebRtcPeerTransport(
    ISessionManager sessionManager,
    ISecurityService securityService,
    ILogger<WebRtcPeerTransport> logger) : IPeerTransport, IDisposable
{
    private const string DefaultPeerJsHost = "0.peerjs.com";
    private const int MaxChunkBytes = 16000;
    private const int MaxPendingCandidates = 64;
    private const ulong CongestedBufferBytes = 256 * 1024;
    private const ulong FileChunkBufferLimitBytes = 1024 * 1024;

    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ChannelOpenTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _wsWriteLock = new(1, 1);
    private readonly SemaphoreSlim _dcWriteLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, ChunkAssembly> _assemblies = new();
    private readonly ConcurrentQueue<PendingCandidate> _pendingRemoteCandidates = new();
    private readonly ConcurrentQueue<object> _outgoingCandidatesBeforeOffer = new();
    private readonly object _pcLock = new();

    private ClientWebSocket? _ws;
    private volatile bool _signalingRegistered;
    private CancellationTokenSource? _signalingCts;
    private DeviceIdentity? _localIdentity;
    private string _localPeerId = string.Empty;
    private string _customSignalingUrl = string.Empty;
    private string _customTurnServer = string.Empty;
    private string _turnUsername = string.Empty;
    private string _turnPassword = string.Empty;
    private string _stunServer = "stun.l.google.com:19302";

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dc;
    private int _pcGeneration;
    private volatile bool _remoteDescriptionSet;
    private volatile bool _offerSent;
    private volatile bool _ownsSession;
    private string _remotePeerId = string.Empty;
    private string _activeConnId = string.Empty;
    private int _localRelayCandidates;
    private int _localSrflxCandidates;
    private TaskCompletionSource<bool>? _answerTcs;
    private TaskCompletionSource<bool>? _channelOpenTcs;
    private TaskCompletionSource<SessionApprovalResult>? _outgoingApprovalTcs;
    private CancellationTokenSource? _pendingApprovalCts;
    private uint _nextMsgId;

    public Func<SessionRequest, CancellationToken, Task<SessionApprovalResult>>? IncomingApprovalHandler { get; set; }

    /// <summary>True once the signaling server confirmed our peer id (PeerJS "OPEN").</summary>
    public bool IsSignalingConnected => _ws?.State == WebSocketState.Open && _signalingRegistered;

    /// <summary>Last signaling problem worth showing to the user (e.g. ID already taken), or null.</summary>
    public string? LastSignalingError { get; private set; }

    /// <summary>True when at least one TURN relay is configured; without it, strict NAT / CGNAT peers cannot connect.</summary>
    public bool HasTurnRelay => ParseTurnUrls(_customTurnServer).Count > 0;

    public bool IsSessionActive => _dc?.readyState == RTCDataChannelState.open && sessionManager.State == ConnectionState.Connected;

    public bool IsCongested => _dc is { } dc && dc.bufferedAmount > CongestedBufferBytes;

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

    // ───────────────────────────── Signaling ─────────────────────────────

    public void StartSignaling(DeviceIdentity localIdentity, AppSettings settings)
    {
        StopSignaling();
        _localIdentity = localIdentity;
        _localPeerId = ToPeerId(localIdentity.DeviceId);
        _stunServer = settings.StunServer;
        _customSignalingUrl = settings.CustomSignalingUrl;
        _customTurnServer = settings.CustomTurnServer;
        _turnUsername = settings.TurnUsername;
        _turnPassword = settings.TurnPassword;

        if (!HasTurnRelay)
        {
            logger.LogWarning("No TURN relay configured. Internet sessions will only work when at least one side has an open/cone NAT. Configure a TURN server in Settings for reliable connections.");
        }

        _signalingCts = new CancellationTokenSource();
        var ct = _signalingCts.Token;
        _ = Task.Run(() => SignalingLoopAsync(ct), ct);
    }

    /// <summary>Applies new ICE/TURN settings without dropping signaling (used when only relay settings change).</summary>
    public bool SignalingSettingsDiffer(AppSettings settings) =>
        !string.Equals(_customSignalingUrl, settings.CustomSignalingUrl, StringComparison.Ordinal);

    public void UpdateIceSettings(AppSettings settings)
    {
        _stunServer = settings.StunServer;
        _customTurnServer = settings.CustomTurnServer;
        _turnUsername = settings.TurnUsername;
        _turnPassword = settings.TurnPassword;
    }

    public void StopSignaling()
    {
        _signalingCts?.Cancel();
        _signalingCts?.Dispose();
        _signalingCts = null;
        _signalingRegistered = false;
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
            var key = custom.Query.Contains("key=", StringComparison.OrdinalIgnoreCase) ? "" : "key=peerjs&";
            return new Uri($"{custom}{sep}{key}id={Uri.EscapeDataString(_localPeerId)}&token={token}&version=1.5.4");
        }

        return new Uri($"wss://{DefaultPeerJsHost}/peerjs?key=peerjs&id={Uri.EscapeDataString(_localPeerId)}&token={token}&version=1.5.4");
    }

    /// <summary>
    /// Splits a comma/space separated TURN list and normalizes "host:port" to "turn:host:port".
    /// </summary>
    public static IReadOnlyList<string> ParseTurnUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)
                ? u
                : $"turn:{u}")
            .ToList();
    }

    internal List<RTCIceServer> BuildIceServers()
    {
        var stun = string.IsNullOrWhiteSpace(_stunServer)
            ? "stun:stun.l.google.com:19302"
            : $"stun:{_stunServer.Replace("stun:", "", StringComparison.OrdinalIgnoreCase)}";

        var list = new List<RTCIceServer>
        {
            new() { urls = stun },
            new() { urls = "stun:stun.cloudflare.com:3478" }
        };

        // NOTE: the old public "openrelay.metered.ca" fallback was removed: it no longer issues relay candidates
        // (verified 2026-11-28 on ports 80/443/3478), so it only slowed ICE down. Use your own coturn instead.
        foreach (var url in ParseTurnUrls(_customTurnServer))
        {
            list.Add(new RTCIceServer
            {
                urls = url,
                username = _turnUsername,
                credential = _turnPassword
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
                _signalingRegistered = false;
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(BuildSignalingUri(), ct).ConfigureAwait(false);
                logger.LogInformation("Connected to internet signaling as {PeerId}", _localPeerId);

                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!hbCts.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(20), hbCts.Token).ConfigureAwait(false);
                            await SendSignalAsync(new { type = "HEARTBEAT" }, hbCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { /* normal */ }
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
                    try
                    {
                        await HandleSignalMessageAsync(json, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Failed to handle signaling message");
                    }
                }

                hbCts.Cancel();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogInformation("Signaling connection interrupted ({Message}); retrying in 5s", ex.Message);
            }

            _signalingRegistered = false;
            try { _ws?.Dispose(); } catch { /* ignore */ }
            _ws = null;
            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleSignalMessageAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var msgType = typeProp.GetString();
        var src = root.TryGetProperty("src", out var srcProp) ? srcProp.GetString() ?? string.Empty : string.Empty;

        switch (msgType)
        {
            case "OPEN":
                _signalingRegistered = true;
                LastSignalingError = null;
                logger.LogInformation("Signaling registered as {PeerId}", _localPeerId);
                break;

            case "OFFER":
                logger.LogInformation("Received WebRTC OFFER from {Src}", src);
                await HandleIncomingOfferAsync(root, src, ct).ConfigureAwait(false);
                break;

            case "ANSWER":
                HandleAnswer(root, src);
                break;

            case "CANDIDATE":
                HandleRemoteCandidate(root, src);
                break;

            case "EXPIRE":
                // PeerJS: a message we sent could not be delivered because the destination is not connected.
                if (string.Equals(src, _remotePeerId, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Signaling server reports {Peer} is not online", src);
                    FailOutgoing($"Remote PC {FromPeerId(src)} is offline (MyRemote not running or not connected to the internet).");
                }
                break;

            case "ID-TAKEN":
                LastSignalingError = $"Your Device ID {FromPeerId(_localPeerId)} is already registered on the signaling server. Is MyRemote open twice, or on another PC with a copied database?";
                logger.LogError("{Error}", LastSignalingError);
                FailOutgoing(LastSignalingError);
                break;

            case "ERROR":
                var errMsg = root.TryGetProperty("payload", out var p) && p.TryGetProperty("msg", out var m) ? m.GetString() : msgType;
                LastSignalingError = errMsg;
                logger.LogWarning("Signaling server error: {Error}", errMsg);
                FailOutgoing(errMsg ?? "Signaling error");
                break;
        }
    }

    // ───────────────────────────── Controller side ─────────────────────────────

    public async Task<SessionApprovalResult> ConnectAsync(
        DeviceIdentity localIdentity,
        string localDisplayName,
        string targetDeviceId,
        SessionPermissions requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        if (!IsSignalingConnected)
        {
            return Fail(LastSignalingError ?? "Internet signaling is not connected yet. Check internet access and try again in a few seconds.");
        }

        var targetPeerId = ToPeerId(targetDeviceId);
        if (string.Equals(targetPeerId, _localPeerId, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("That is this PC's own Device ID.");
        }

        if (!sessionManager.TryTransition(ConnectionState.Idle, ConnectionState.Connecting))
        {
            return Fail("Another session is already active or pending.");
        }
        _ownsSession = true;

        int gen;
        lock (_pcLock)
        {
            CleanupPeerConnection();
            gen = _pcGeneration;
            _remotePeerId = targetPeerId;
            _activeConnId = $"dc_{Guid.NewGuid():N}";
            _offerSent = false;
            _localRelayCandidates = 0;
            _localSrflxCandidates = 0;
            _outgoingCandidatesBeforeOffer.Clear();
            _answerTcs = NewTcs<bool>();
            _channelOpenTcs = NewTcs<bool>();
            _outgoingApprovalTcs = NewTcs<SessionApprovalResult>();
        }

        var answerTcs = _answerTcs;
        var channelOpenTcs = _channelOpenTcs;
        var approvalTcs = _outgoingApprovalTcs;

        try
        {
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = BuildIceServers() });
            _pc = pc;
            var dc = await pc.createDataChannel("myremote").ConfigureAwait(false);
            _dc = dc;
            WireDataChannel(dc, gen, isHost: false, localIdentity, localDisplayName, requestedPermissions);
            WirePeerConnectionDiagnostics(pc, gen, "client");

            pc.onicecandidate += candidate =>
            {
                if (candidate is null || gen != _pcGeneration) return;
                CountLocalCandidate(candidate.ToString());
                var msg = BuildCandidateSignal(candidate);
                if (_offerSent)
                {
                    _ = SendSignalAsync(msg, CancellationToken.None);
                }
                else
                {
                    // PeerJS relays messages in order; a candidate sent before the OFFER reaches the host before it
                    // has a peer connection. Hold it until the OFFER is out.
                    _outgoingCandidatesBeforeOffer.Enqueue(msg);
                }
            };

            pc.onconnectionstatechange += state =>
            {
                if (gen != _pcGeneration) return;
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    channelOpenTcs.TrySetResult(false);
                    approvalTcs.TrySetResult(new SessionApprovalResult(false, SessionPermissions.None, null, $"WebRTC connection {state}."));
                    CleanupSession($"WebRTC {state}");
                }
            };

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer).ConfigureAwait(false);
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

            _offerSent = true;
            while (_outgoingCandidatesBeforeOffer.TryDequeue(out var queued))
            {
                await SendSignalAsync(queued, cancellationToken).ConfigureAwait(false);
            }

            // Phase 1: remote PC answers the offer.
            if (!await WaitAsync(answerTcs.Task, AnswerTimeout, cancellationToken).ConfigureAwait(false))
            {
                return Abort(approvalTcs, $"Remote PC {targetDeviceId} did not answer. It may be offline, already in a session, or running an older MyRemote version.");
            }

            // Phase 2: ICE finds a network path and the DataChannel opens.
            if (!await WaitAsync(channelOpenTcs.Task, ChannelOpenTimeout, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "DataChannel did not open. Local candidates: srflx={Srflx}, relay={Relay}, TURN configured={Turn}",
                    _localSrflxCandidates, _localRelayCandidates, HasTurnRelay);

                var reason = !HasTurnRelay
                    ? "Reached the remote PC but could not open a direct network path (strict NAT / CGNAT / firewall). " +
                      "Configure your own TURN relay in Settings → Internet (see NETWORKING.md)."
                    : _localRelayCandidates == 0
                        ? "Could not open a network path, and your TURN relay gave no relay address. Check TURN URL, username and password."
                        : "Could not open a network path even via the TURN relay. Check that the remote PC can reach the TURN server and its firewall allows MyRemote.";
                return Abort(approvalTcs, reason);
            }

            // Phase 3: host user accepts.
            sessionManager.TryTransition(ConnectionState.Connecting, ConnectionState.AwaitingHostApproval);
            var approvalWait = ApprovalTimeout + TimeSpan.FromSeconds(15);
            if (!await WaitAsync(approvalTcs.Task, approvalWait, cancellationToken).ConfigureAwait(false))
            {
                return Abort(approvalTcs, "The remote user did not accept the request in time.");
            }

            var result = await approvalTcs.Task.ConfigureAwait(false);
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
            return Fail(ex.Message);
        }
    }

    private SessionApprovalResult Abort(TaskCompletionSource<SessionApprovalResult> tcs, string reason)
    {
        // A more specific reason (e.g. "offline", "ID taken") may already have been recorded.
        if (tcs.Task.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(tcs.Task.Result.Reason))
        {
            reason = tcs.Task.Result.Reason!;
        }
        logger.LogWarning("Outgoing WebRTC connection aborted: {Reason}", reason);
        tcs.TrySetResult(Fail(reason));
        CleanupSession(reason);
        return Fail(reason);
    }

    private void FailOutgoing(string reason)
    {
        var tcs = _outgoingApprovalTcs;
        if (tcs is null || tcs.Task.IsCompleted) return;
        tcs.TrySetResult(Fail(reason));
        _answerTcs?.TrySetResult(false);
        _channelOpenTcs?.TrySetResult(false);
    }

    private void HandleAnswer(JsonElement root, string src)
    {
        if (_pc is null || !string.Equals(src, _remotePeerId, StringComparison.OrdinalIgnoreCase)) return;
        if (!root.TryGetProperty("payload", out var ansPayload) ||
            !ansPayload.TryGetProperty("sdp", out var ansSdp) ||
            !ansSdp.TryGetProperty("sdp", out var ansSdpStr))
        {
            return;
        }

        var connId = ansPayload.TryGetProperty("connectionId", out var cid) ? cid.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(connId) && connId != _activeConnId) return;

        var res = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = ansSdpStr.GetString()
        });
        logger.LogInformation("Received WebRTC ANSWER; SetRemoteDescription => {Result}", res);
        if (res != SetDescriptionResultEnum.OK)
        {
            FailOutgoing($"Remote PC sent an invalid answer ({res}).");
            return;
        }

        _remoteDescriptionSet = true;
        DrainPendingCandidates();
        _answerTcs?.TrySetResult(true);
    }

    // ───────────────────────────── Host side ─────────────────────────────

    private async Task HandleIncomingOfferAsync(JsonElement root, string srcPeer, CancellationToken ct)
    {
        if (_localIdentity is null || string.IsNullOrEmpty(srcPeer)) return;
        if (!securityService.CheckRateLimit(srcPeer, maxRequestsPerWindow: 5, windowSeconds: 30))
        {
            logger.LogWarning("Rate-limited WebRTC offer from {Src}", srcPeer);
            return;
        }

        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("sdp", out var sdpObj) ||
            !sdpObj.TryGetProperty("sdp", out var sdpStr))
        {
            return;
        }

        if (!sessionManager.TryTransition(ConnectionState.Idle, ConnectionState.Connecting))
        {
            logger.LogInformation("Ignoring WebRTC offer from {Src}: another session is active or pending", srcPeer);
            return;
        }
        _ownsSession = true;

        var connId = payload.TryGetProperty("connectionId", out var cid) ? cid.GetString() ?? "" : "";
        int gen;
        lock (_pcLock)
        {
            CleanupPeerConnection(keepCandidatesFrom: srcPeer);
            gen = _pcGeneration;
            _remotePeerId = srcPeer;
            _activeConnId = connId;
            _localRelayCandidates = 0;
            _localSrflxCandidates = 0;
        }

        try
        {
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = BuildIceServers() });
            _pc = pc;
            var identity = _localIdentity;
            pc.ondatachannel += dc =>
            {
                if (gen != _pcGeneration) return;
                _dc = dc;
                WireDataChannel(dc, gen, isHost: true, identity, identity.DisplayName, SessionPermissions.AllControl);
            };
            WirePeerConnectionDiagnostics(pc, gen, "host");

            pc.onicecandidate += candidate =>
            {
                if (candidate is null || gen != _pcGeneration) return;
                CountLocalCandidate(candidate.ToString());
                _ = SendSignalAsync(BuildCandidateSignal(candidate), CancellationToken.None);
            };

            pc.onconnectionstatechange += state =>
            {
                if (gen != _pcGeneration) return;
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    CleanupSession($"WebRTC {state}");
                }
            };

            var setRes = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = sdpStr.GetString()
            });
            logger.LogInformation("SetRemoteDescription(Offer) => {Result}", setRes);
            if (setRes != SetDescriptionResultEnum.OK)
            {
                CleanupSession($"Invalid offer ({setRes})");
                return;
            }

            _remoteDescriptionSet = true;
            DrainPendingCandidates();

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer).ConfigureAwait(false);
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

            // If the controller never manages to open the channel (NAT failure), release the slot.
            _ = Task.Delay(ChannelOpenTimeout + TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ =>
            {
                if (gen == _pcGeneration && sessionManager.State == ConnectionState.Connecting)
                {
                    logger.LogWarning("Incoming WebRTC connection from {Src} never opened (likely NAT/firewall); releasing", srcPeer);
                    CleanupSession("Incoming connection timed out");
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to handle incoming WebRTC offer");
            CleanupSession("Incoming offer failed");
        }
    }

    // ───────────────────────────── ICE candidates ─────────────────────────────

    private object BuildCandidateSignal(RTCIceCandidate candidate) => new
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
    };

    private void CountLocalCandidate(string candidate)
    {
        if (candidate.Contains("typ relay", StringComparison.Ordinal)) Interlocked.Increment(ref _localRelayCandidates);
        else if (candidate.Contains("typ srflx", StringComparison.Ordinal)) Interlocked.Increment(ref _localSrflxCandidates);
        logger.LogInformation("Local ICE candidate: {Candidate}", candidate);
    }

    private void HandleRemoteCandidate(JsonElement root, string src)
    {
        if (!root.TryGetProperty("payload", out var candPayload) ||
            !candPayload.TryGetProperty("candidate", out var candObj) ||
            !candObj.TryGetProperty("candidate", out var candStr))
        {
            return;
        }

        var connId = candPayload.TryGetProperty("connectionId", out var cid) ? cid.GetString() ?? "" : "";
        var init = new RTCIceCandidateInit
        {
            candidate = candStr.GetString(),
            sdpMid = candObj.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : "0",
            sdpMLineIndex = candObj.TryGetProperty("sdpMLineIndex", out var idx) && idx.ValueKind == JsonValueKind.Number ? (ushort)idx.GetInt32() : (ushort)0
        };

        var pc = _pc;
        bool forCurrentPeer = pc is not null &&
            string.Equals(src, _remotePeerId, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(connId) || connId == _activeConnId);

        if (forCurrentPeer && _remoteDescriptionSet)
        {
            logger.LogInformation("Remote ICE candidate: {Candidate}", init.candidate);
            pc!.addIceCandidate(init);
        }
        else if (pc is null || (forCurrentPeer && !_remoteDescriptionSet))
        {
            // Arrived before the OFFER/ANSWER was processed; keep it (bounded) until we can apply it.
            if (_pendingRemoteCandidates.Count < MaxPendingCandidates)
            {
                _pendingRemoteCandidates.Enqueue(new PendingCandidate(src, connId, init));
            }
        }
    }

    private void DrainPendingCandidates()
    {
        var pc = _pc;
        if (pc is null) return;
        int count = _pendingRemoteCandidates.Count;
        for (int i = 0; i < count && _pendingRemoteCandidates.TryDequeue(out var c); i++)
        {
            bool matches = string.Equals(c.Src, _remotePeerId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(c.ConnId) || c.ConnId == _activeConnId);
            if (matches)
            {
                logger.LogInformation("Applying queued remote ICE candidate: {Candidate}", c.Init.candidate);
                pc.addIceCandidate(c.Init);
            }
        }
    }

    private void WirePeerConnectionDiagnostics(RTCPeerConnection pc, int gen, string role)
    {
        pc.oniceconnectionstatechange += state =>
        {
            if (gen == _pcGeneration) logger.LogInformation("ICE state ({Role}): {State}", role, state);
        };
        pc.onicegatheringstatechange += state =>
        {
            if (gen != _pcGeneration) return;
            logger.LogInformation("ICE gathering ({Role}): {State} (srflx={Srflx}, relay={Relay})", role, state, _localSrflxCandidates, _localRelayCandidates);
        };
        pc.onconnectionstatechange += state =>
        {
            if (gen == _pcGeneration) logger.LogInformation("WebRTC connection state ({Role}): {State}", role, state);
        };
    }

    // ───────────────────────────── Data channel ─────────────────────────────

    private void WireDataChannel(
        RTCDataChannel dc,
        int gen,
        bool isHost,
        DeviceIdentity localIdentity,
        string localDisplayName,
        SessionPermissions requestedPermissions)
    {
        dc.onopen += () =>
        {
            if (gen != _pcGeneration) return;
            logger.LogInformation("DataChannel open ({Role})", isHost ? "host" : "client");
            if (!isHost)
            {
                _channelOpenTcs?.TrySetResult(true);
                var hello = new HelloPayload(localIdentity.DeviceId, localDisplayName, requestedPermissions, Guid.NewGuid().ToString("N"), ProtocolInfo.CurrentVersion);
                _ = SendRawAsync(WireMessageType.HelloRequest, JsonSerializer.SerializeToUtf8Bytes(hello));
            }
        };

        dc.onmessage += (_, _, data) =>
        {
            if (gen != _pcGeneration) return;
            ProcessIncomingChunk(data, gen, isHost, localIdentity);
        };

        dc.onclose += () =>
        {
            if (gen == _pcGeneration) CleanupSession("DataChannel closed");
        };
    }

    private void ProcessIncomingChunk(byte[] packet, int gen, bool isHost, DeviceIdentity localIdentity)
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
        _ = HandleCompleteFrameAsync(msgType, completePayload, gen, isHost, localIdentity);
    }

    private async Task HandleCompleteFrameAsync(WireMessageType type, byte[] payload, int gen, bool isHost, DeviceIdentity localIdentity)
    {
        try
        {
            if (type == WireMessageType.HelloRequest && isHost)
            {
                await HandleHelloAsHostAsync(payload, gen, localIdentity).ConfigureAwait(false);
                return;
            }

            if (type == WireMessageType.ApprovalResponse && !isHost)
            {
                HandleApprovalAsController(payload, localIdentity);
                return;
            }

            DispatchSessionFrame(type, payload, isHost);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to handle {Type} frame", type);
        }
    }

    private async Task HandleHelloAsHostAsync(byte[] payload, int gen, DeviceIdentity localIdentity)
    {
        if (!sessionManager.TryTransition(ConnectionState.Connecting, ConnectionState.AwaitingHostApproval))
        {
            return; // duplicate hello or not our session
        }

        var hello = JsonSerializer.Deserialize<HelloPayload>(payload);
        string? rejectReason = hello is null || !securityService.IsValidDeviceId(hello.DeviceId)
            ? "Invalid device identity."
            : hello.ProtocolVersion != ProtocolInfo.CurrentVersion
                ? $"Version mismatch (host protocol v{ProtocolInfo.CurrentVersion}, yours v{hello.ProtocolVersion}). Update MyRemote on both PCs."
                : null;
        if (rejectReason is not null)
        {
            logger.LogWarning("Rejecting WebRTC peer {Peer}: {Reason}", _remotePeerId, rejectReason);
            await SendRawAsync(WireMessageType.ApprovalResponse, JsonSerializer.SerializeToUtf8Bytes(
                new ApprovalPayload(false, SessionPermissions.None, null, rejectReason))).ConfigureAwait(false);
            CleanupSession(rejectReason);
            return;
        }

        var req = new SessionRequest(
            Guid.NewGuid().ToString("N"),
            hello!.DeviceId,
            string.IsNullOrWhiteSpace(hello.DisplayName) ? hello.DeviceId : hello.DisplayName,
            $"Internet ({FromPeerId(_remotePeerId)})",
            hello.RequestedPermissions,
            DateTimeOffset.UtcNow,
            hello.Nonce);

        var approvalCts = new CancellationTokenSource(ApprovalTimeout);
        _pendingApprovalCts = approvalCts;
        SessionApprovalResult approval;
        try
        {
            approval = IncomingApprovalHandler is not null
                ? await IncomingApprovalHandler(req, approvalCts.Token).ConfigureAwait(false)
                : new SessionApprovalResult(false, SessionPermissions.None, null, "No host handler.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingApprovalCts, null, approvalCts);
            approvalCts.Dispose();
        }

        // The requester may have given up (or a new connection replaced this one) while the dialog was open.
        if (gen != _pcGeneration || _dc?.readyState != RTCDataChannelState.open)
        {
            logger.LogInformation("Approval result for {Peer} discarded: requester is gone", req.RequesterDeviceId);
            return;
        }

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
    }

    private void HandleApprovalAsController(byte[] payload, DeviceIdentity localIdentity)
    {
        var approval = JsonSerializer.Deserialize<ApprovalPayload>(payload);
        if (approval is null || !approval.Approved || string.IsNullOrWhiteSpace(approval.SessionToken))
        {
            _outgoingApprovalTcs?.TrySetResult(Fail(approval?.Reason ?? "Rejected by host."));
            return;
        }

        var remoteId = FromPeerId(_remotePeerId);
        sessionManager.StartSession(new RemoteSession(
            Guid.NewGuid().ToString("N"),
            localIdentity.DeviceId,
            remoteId,
            remoteId,
            "Internet P2P",
            IsHost: false,
            TransportMode.InternetDirect,
            approval.GrantedPermissions,
            DateTimeOffset.UtcNow,
            approval.SessionToken));

        _outgoingApprovalTcs?.TrySetResult(new SessionApprovalResult(true, approval.GrantedPermissions, approval.SessionToken, null));
    }

    private void DispatchSessionFrame(WireMessageType type, byte[] payload, bool isHost)
    {
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
            case WireMessageType.FileOffer when isHost:
                FileOfferPayloadReceived?.Invoke(this, payload);
                break;
            case WireMessageType.FileAccept when !isHost:
                var decEnv = JsonSerializer.Deserialize<TokenEnvelope<FileDecisionPayload>>(payload);
                if (decEnv is not null && securityService.ValidateSessionToken(session.SessionToken, decEnv.SessionToken))
                    FileDecisionReceived?.Invoke(this, decEnv.Data);
                break;
            case WireMessageType.FileChunk:
                if (BinaryEnvelope.TryUnpack(payload, out var chunkToken, out var chunkId, out var chunkData) &&
                    securityService.ValidateSessionToken(session.SessionToken, chunkToken) &&
                    sessionManager.HasPermission(SessionPermissions.FileTransfer))
                    FileChunkReceived?.Invoke(this, new FileChunkPayload(chunkId, chunkData.ToArray()));
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
            case WireMessageType.VoiceAudio:
                if (BinaryEnvelope.TryUnpack(payload, out var voiceToken, out _, out var pcm) &&
                    securityService.ValidateSessionToken(session.SessionToken, voiceToken) &&
                    sessionManager.HasPermission(SessionPermissions.VoiceChat))
                    VoiceAudioReceived?.Invoke(this, pcm.ToArray());
                break;
            case WireMessageType.Disconnect:
                var reason = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Peer disconnected";
                CleanupSession(reason);
                break;
        }
    }

    // ───────────────────────────── Sending ─────────────────────────────

    public async Task SendRawAsync(WireMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var dc = _dc;
        if (dc is null || dc.readyState != RTCDataChannelState.open) return;

        // Bulk file data must not flood the SCTP buffer (it would starve screen frames and use unbounded memory).
        if (type == WireMessageType.FileChunk)
        {
            while (dc.readyState == RTCDataChannelState.open && dc.bufferedAmount > FileChunkBufferLimitBytes)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "DataChannel send failed for {Type}", type);
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

    // ───────────────────────────── Cleanup ─────────────────────────────

    /// <summary>Closes the current peer connection. Bumps the generation first so its close callbacks are ignored.</summary>
    private void CleanupPeerConnection(string? keepCandidatesFrom = null)
    {
        RTCDataChannel? dc;
        RTCPeerConnection? pc;
        lock (_pcLock)
        {
            Interlocked.Increment(ref _pcGeneration);
            dc = _dc;
            pc = _pc;
            _dc = null;
            _pc = null;
            _remoteDescriptionSet = false;
            _assemblies.Clear();

            if (keepCandidatesFrom is null)
            {
                _pendingRemoteCandidates.Clear();
            }
            else
            {
                // Keep early candidates sent by the peer whose OFFER we're about to process.
                var keep = _pendingRemoteCandidates.Where(c => string.Equals(c.Src, keepCandidatesFrom, StringComparison.OrdinalIgnoreCase)).ToList();
                _pendingRemoteCandidates.Clear();
                foreach (var c in keep) _pendingRemoteCandidates.Enqueue(c);
            }
        }

        try { dc?.close(); } catch { /* ignore */ }
        try { pc?.close(); } catch { /* ignore */ }
    }

    private void CleanupSession(string reason)
    {
        try { _pendingApprovalCts?.Cancel(); } catch (ObjectDisposedException) { /* already done */ }
        _answerTcs?.TrySetResult(false);
        _channelOpenTcs?.TrySetResult(false);
        _outgoingApprovalTcs?.TrySetResult(Fail(reason));
        CleanupPeerConnection();

        if (_ownsSession)
        {
            _ownsSession = false;
            if (sessionManager.CurrentSession is not null || sessionManager.State != ConnectionState.Idle)
            {
                sessionManager.EndSession(reason);
                SessionDisconnected?.Invoke(this, reason);
            }
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

    private static SessionApprovalResult Fail(string reason) =>
        new(false, SessionPermissions.None, null, reason);

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for <paramref name="task"/>; returns false on timeout, otherwise the task's completion (true for non-bool tasks).</summary>
    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (finished != task) return false;
        return task is not Task<bool> boolTask || boolTask.Result;
    }

    public void Dispose()
    {
        StopSignaling();
        CleanupPeerConnection();
        _wsWriteLock.Dispose();
        _dcWriteLock.Dispose();
    }

    private sealed record PendingCandidate(string Src, string ConnId, RTCIceCandidateInit Init);

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
