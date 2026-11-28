using System.Collections.Concurrent;
using System.Security.Cryptography;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.FileTransfer;

/// <summary>
/// Permission-gated, integrity-verified chunked file transfer manager.
/// </summary>
public sealed class FileTransferService(ISessionManager sessionManager)
{
    public const int ChunkSizeBytes = 64 * 1024;
    public const long MaxFileSizeBytes = 2L * 1024 * 1024 * 1024; // 2 GB

    private readonly ConcurrentDictionary<string, IncomingFileState> _incoming = new();

    public static string SanitizeFileName(string rawFileName)
    {
        var name = Path.GetFileName(rawFileName ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "received_file.bin" : name;
    }

    public async Task<FileOfferMetadata> PrepareOfferAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!sessionManager.HasPermission(SessionPermissions.FileTransfer))
        {
            throw new UnauthorizedAccessException("File transfer permission is not granted for this session.");
        }

        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("Selected file does not exist.", filePath);
        if (info.Length > MaxFileSizeBytes) throw new InvalidOperationException("File exceeds maximum 2 GB limit.");

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new FileOfferMetadata(
            TransferId: Guid.NewGuid().ToString("N"),
            FileName: SanitizeFileName(info.Name),
            TotalBytes: info.Length,
            Sha256Hex: Convert.ToHexString(hash).ToLowerInvariant());
    }

    public bool BeginReceive(FileOfferMetadata offer, string destinationDirectory)
    {
        if (!sessionManager.HasPermission(SessionPermissions.FileTransfer)) return false;
        if (offer.TotalBytes < 0 || offer.TotalBytes > MaxFileSizeBytes) return false;

        Directory.CreateDirectory(destinationDirectory);
        var safeName = SanitizeFileName(offer.FileName);
        var targetPath = Path.Combine(destinationDirectory, safeName);
        if (File.Exists(targetPath))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var ext = Path.GetExtension(safeName);
            targetPath = Path.Combine(destinationDirectory, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
        }

        var stream = File.Create(targetPath);
        _incoming[offer.TransferId] = new IncomingFileState(offer, targetPath, stream);
        return true;
    }

    /// <summary>
    /// Appends a chunk synchronously so chunks are written strictly in arrival order
    /// (async fire-and-forget writes could interleave and corrupt the file).
    /// </summary>
    public FileTransferProgress? AppendChunk(string transferId, ReadOnlySpan<byte> chunk)
    {
        if (!sessionManager.HasPermission(SessionPermissions.FileTransfer)) return null;
        if (!_incoming.TryGetValue(transferId, out var state)) return null;

        lock (state)
        {
            if (state.BytesWritten + chunk.Length > state.Offer.TotalBytes)
            {
                // Sender is sending more than it announced: abort rather than fill the disk.
                CancelReceive(transferId);
                return new FileTransferProgress(transferId, state.Offer.FileName, state.BytesWritten, state.Offer.TotalBytes,
                    IsCompleted: false, IsCancelled: true, ErrorMessage: "Received more data than announced; transfer cancelled.");
            }

            state.Stream.Write(chunk);
            state.BytesWritten += chunk.Length;
        }

        return new FileTransferProgress(
            transferId,
            state.Offer.FileName,
            state.BytesWritten,
            state.Offer.TotalBytes,
            IsCompleted: false,
            IsCancelled: false);
    }

    public async Task<FileTransferProgress> CompleteReceiveAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (!_incoming.TryRemove(transferId, out var state))
        {
            return new FileTransferProgress(transferId, "unknown", 0, 0, false, true, "Unknown transfer ID.");
        }

        await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await state.Stream.DisposeAsync().ConfigureAwait(false);

        await using var verifyStream = File.OpenRead(state.TargetPath);
        var hash = await SHA256.HashDataAsync(verifyStream, cancellationToken).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actualHex, state.Offer.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(state.TargetPath); } catch { /* ignore */ }
            return new FileTransferProgress(
                transferId,
                state.Offer.FileName,
                state.BytesWritten,
                state.Offer.TotalBytes,
                IsCompleted: false,
                IsCancelled: true,
                ErrorMessage: "SHA-256 integrity check failed; file discarded.");
        }

        return new FileTransferProgress(
            transferId,
            state.Offer.FileName,
            state.BytesWritten,
            state.Offer.TotalBytes,
            IsCompleted: true,
            IsCancelled: false);
    }

    public void CancelReceive(string transferId)
    {
        if (_incoming.TryRemove(transferId, out var state))
        {
            state.Stream.Dispose();
            try { File.Delete(state.TargetPath); } catch { /* ignore */ }
        }
    }

    private sealed class IncomingFileState(FileOfferMetadata offer, string targetPath, FileStream stream)
    {
        public FileOfferMetadata Offer { get; } = offer;
        public string TargetPath { get; } = targetPath;
        public FileStream Stream { get; } = stream;
        public long BytesWritten { get; set; }
    }
}
