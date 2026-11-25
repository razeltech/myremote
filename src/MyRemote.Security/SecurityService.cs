using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MyRemote.Core.Interfaces;

namespace MyRemote.Security;

/// <summary>
/// Provides cryptographic session tokens, constant-time validation, Device ID validation, and rate limiting.
/// </summary>
public sealed partial class SecurityService : ISecurityService
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _rateWindows = new();

    public string GenerateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public bool ValidateSessionToken(string? expectedToken, string? providedToken)
    {
        if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(providedToken))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public bool IsValidDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var normalized = deviceId.Trim().Replace("-", "").Replace(" ", "");
        return NineDigitsRegex().IsMatch(normalized);
    }

    public string FormatDeviceId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 9) return raw.Trim();
        return $"{digits[..3]}-{digits[3..6]}-{digits[6..9]}";
    }

    public bool CheckRateLimit(string sourceKey, int maxRequestsPerWindow = 5, int windowSeconds = 30)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddSeconds(-windowSeconds);
        var queue = _rateWindows.GetOrAdd(sourceKey, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= maxRequestsPerWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    public string ComputeSha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"^\d{9}$")]
    private static partial Regex NineDigitsRegex();
}
