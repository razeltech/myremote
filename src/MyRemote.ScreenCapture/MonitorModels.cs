namespace MyRemote.ScreenCapture;

public sealed record MonitorDescriptor(
    int Index,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

public sealed record CapturedFrame(
    int MonitorIndex,
    int Width,
    int Height,
    byte[] JpegBytes,
    DateTimeOffset CapturedAtUtc);

public interface IScreenCaptureService
{
    IReadOnlyList<MonitorDescriptor> GetMonitors();
    CapturedFrame? CaptureMonitor(int monitorIndex, int jpegQuality = 75, double scale = 1.0);
}
