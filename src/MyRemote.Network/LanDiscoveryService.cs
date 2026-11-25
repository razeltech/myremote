using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Models;

namespace MyRemote.Network;

/// <summary>
/// Discovers MyRemote peers on the local Wi-Fi/Ethernet subnet via UDP broadcast without internet access.
/// </summary>
public sealed class LanDiscoveryService(ILogger<LanDiscoveryService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event EventHandler<IReadOnlyList<DiscoveredPeer>>? PeersUpdated;

    public IReadOnlyList<DiscoveredPeer> CurrentPeers =>
        _peers.Values.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public void Start(DeviceIdentity localIdentity, string displayName, int listenPort, int discoveryPort)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loopTask = Task.Run(() => RunDiscoveryLoopAsync(localIdentity, displayName, listenPort, discoveryPort, ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _peers.Clear();
    }

    private async Task RunDiscoveryLoopAsync(
        DeviceIdentity localIdentity,
        string displayName,
        int listenPort,
        int discoveryPort,
        CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.EnableBroadcast = true;

            var receiveTask = ReceiveLoopAsync(udp, localIdentity.DeviceId, ct);
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);

            while (!ct.IsCancellationRequested)
            {
                var announcement = new DiscoveryAnnouncement(
                    localIdentity.DeviceId,
                    displayName,
                    listenPort,
                    Environment.OSVersion.VersionString);

                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement));
                try
                {
                    await udp.SendAsync(bytes, broadcastEndpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "LAN discovery broadcast skipped");
                }

                PruneStalePeers();
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LAN discovery service stopped unexpectedly");
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, string localDeviceId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var msg = JsonSerializer.Deserialize<DiscoveryAnnouncement>(json);
                if (msg is null || string.IsNullOrWhiteSpace(msg.DeviceId) || msg.DeviceId == localDeviceId)
                {
                    continue;
                }

                var peer = new DiscoveredPeer(
                    msg.DeviceId,
                    string.IsNullOrWhiteSpace(msg.DisplayName) ? msg.DeviceId : msg.DisplayName,
                    result.RemoteEndPoint.Address.ToString(),
                    msg.ListenPort,
                    msg.OperatingSystem,
                    DateTimeOffset.UtcNow);

                _peers[peer.DeviceId] = peer;
                PeersUpdated?.Invoke(this, CurrentPeers);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore malformed UDP datagrams
            }
        }
    }

    private void PruneStalePeers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-8);
        bool changed = false;
        foreach (var kvp in _peers)
        {
            if (kvp.Value.LastSeenUtc < cutoff && _peers.TryRemove(kvp.Key, out _))
            {
                changed = true;
            }
        }

        if (changed)
        {
            PeersUpdated?.Invoke(this, CurrentPeers);
        }
    }

    public void Dispose() => Stop();

    private sealed record DiscoveryAnnouncement(
        string DeviceId,
        string DisplayName,
        int ListenPort,
        string OperatingSystem);
}
