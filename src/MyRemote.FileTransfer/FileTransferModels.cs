namespace MyRemote.FileTransfer;

public sealed record FileOfferMetadata(
    string TransferId,
    string FileName,
    long TotalBytes,
    string Sha256Hex);

public sealed record FileTransferProgress(
    string TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    bool IsCompleted,
    bool IsCancelled,
    string? ErrorMessage = null)
{
    public double Percentage => TotalBytes <= 0 ? 100.0 : Math.Clamp(BytesTransferred * 100.0 / TotalBytes, 0.0, 100.0);
}
