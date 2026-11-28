using System.Buffers.Binary;
using System.Text.Json;

namespace MyRemote.Network.Protocol;

public enum WireMessageType : byte
{
    HelloRequest = 1,
    ApprovalResponse = 2,
    ScreenFrame = 3,
    InputCommand = 4,
    MonitorList = 5,
    SelectMonitor = 6,
    QualitySettings = 7,
    ClipboardText = 8,
    ChatText = 9,
    FileOffer = 10,
    FileAccept = 11,
    FileChunk = 12,
    FileComplete = 13,
    PermissionUpdate = 14,
    Disconnect = 15,
    Heartbeat = 16,
    VoiceAudio = 17
}

public sealed record WireFrame(WireMessageType Type, byte[] Payload);

public static class WireFraming
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024; // 16 MB

    public static async Task WriteFrameAsync(
        Stream stream,
        WireMessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException($"Payload size {payload.Length} exceeds {MaxPayloadBytes} bytes.");
        }

        byte[] header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(
        Stream stream,
        WireMessageType type,
        T value,
        CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return WriteFrameAsync(stream, type, bytes, cancellationToken);
    }

    public static async Task<WireFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[5];
        int read = await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0) return null; // clean EOF

        if (!Enum.IsDefined(typeof(WireMessageType), header[0]))
        {
            throw new InvalidDataException($"Unknown wire message type: {header[0]}");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Invalid wire frame length: {length}");
        }

        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            int bodyRead = await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            if (bodyRead < length) throw new EndOfStreamException("Incomplete wire frame payload.");
        }

        return new WireFrame((WireMessageType)header[0], payload);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }
}
