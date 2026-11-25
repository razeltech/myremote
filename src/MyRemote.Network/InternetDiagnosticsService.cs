using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MyRemote.Network;

public sealed record InternetDiagnosticReport(
    bool StunReachable,
    string? MappedPublicEndpoint,
    bool HasGlobalIPv6,
    IReadOnlyList<string> LocalIPv4Addresses,
    IReadOnlyList<string> LocalIPv6Addresses,
    string Summary);

/// <summary>
/// Performs real RFC 5389 STUN Binding requests and local IPv4/IPv6 interface diagnostics.
/// Does not claim STUN relays media or guarantees NAT traversal behind symmetric NATs.
/// </summary>
public sealed class InternetDiagnosticsService
{
    private const uint StunMagicCookie = 0x2112A442;

    public async Task<InternetDiagnosticReport> RunDiagnosticsAsync(
        string stunHostPort,
        CancellationToken cancellationToken = default)
    {
        var ipv4 = new List<string>();
        var ipv6 = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4.Add(ua.Address.ToString());
                }
                else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ua.Address.IsIPv6LinkLocal)
                {
                    ipv6.Add(ua.Address.ToString());
                }
            }
        }

        string? mappedEndpoint = null;
        bool stunOk = false;
        string stunNote;

        if (string.IsNullOrWhiteSpace(stunHostPort))
        {
            stunNote = "No STUN server configured.";
        }
        else
        {
            try
            {
                mappedEndpoint = await QueryStunBindingAsync(stunHostPort, cancellationToken).ConfigureAwait(false);
                stunOk = mappedEndpoint is not null;
                stunNote = stunOk
                    ? $"STUN Binding succeeded: public reflexive address {mappedEndpoint}. (Note: STUN discovers reflexive address only; symmetric NAT or CGNAT may still require port forwarding or a self-hosted TURN relay.)"
                    : "STUN server did not return a mapped address.";
            }
            catch (Exception ex)
            {
                stunNote = $"STUN Binding failed: {ex.Message}";
            }
        }

        var summary = $"{stunNote} Global IPv6 available: {(ipv6.Count > 0 ? "Yes" : "No")}. Local IPv4 count: {ipv4.Count}.";
        return new InternetDiagnosticReport(stunOk, mappedEndpoint, ipv6.Count > 0, ipv4, ipv6, summary);
    }

    public static async Task<string?> QueryStunBindingAsync(string hostPort, CancellationToken ct)
    {
        var parts = hostPort.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var host = parts[0];
        int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 19302;

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        if (addresses.Length == 0) return null;

        byte[] txId = new byte[12];
        RandomNumberGenerator.Fill(txId);

        byte[] request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), 0x0001); // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0x0000); // Length
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), StunMagicCookie);
        txId.CopyTo(request.AsSpan(8, 12));

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 3000;
        await udp.SendAsync(request, new IPEndPoint(addresses[0], port), ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        var response = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
        return ParseStunMappedAddress(response.Buffer);
    }

    public static string? ParseStunMappedAddress(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 20) return null;
        ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(buffer[0..2]);
        if (msgType != 0x0101) return null; // Binding Success Response

        int msgLen = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..4]);
        int offset = 20;
        int end = Math.Min(buffer.Length, 20 + msgLen);

        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            offset += 4;
            if (offset + attrLen > end) break;

            var val = buffer.Slice(offset, attrLen);
            if (attrType == 0x0020 && attrLen >= 8 && val[1] == 0x01) // XOR-MAPPED-ADDRESS IPv4
            {
                ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(val[2..4]);
                int port = xPort ^ (int)(StunMagicCookie >> 16);
                Span<byte> ip = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(ip, BinaryPrimitives.ReadUInt32BigEndian(val[4..8]) ^ StunMagicCookie);
                return $"{new IPAddress(ip)}:{port}";
            }
            if (attrType == 0x0001 && attrLen >= 8 && val[1] == 0x01) // MAPPED-ADDRESS IPv4
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(val[2..4]);
                return $"{new IPAddress(val[4..8])}:{port}";
            }

            offset += (attrLen + 3) & ~3; // 4-byte padding
        }

        return null;
    }
}
