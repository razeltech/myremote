using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyRemote.ScreenCapture;

/// <summary>
/// Captures Windows desktop monitors using Win32 GDI BitBlt and encodes frames to JPEG.
/// </summary>
public sealed class Win32ScreenCaptureService : IScreenCaptureService
{
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;
    private const uint MonitorinfoFPrimary = 0x00000001;
    private static readonly TimeSpan MonitorCacheLifetime = TimeSpan.FromSeconds(2);

    private IReadOnlyList<MonitorDescriptor> _cachedMonitors = [];
    private DateTime _monitorsCachedAtUtc = DateTime.MinValue;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        int idx = 0;

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData)
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                monitors.Add(new MonitorDescriptor(
                    Index: idx++,
                    DeviceName: info.szDevice,
                    Left: info.rcMonitor.Left,
                    Top: info.rcMonitor.Top,
                    Width: info.rcMonitor.Right - info.rcMonitor.Left,
                    Height: info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary: (info.dwFlags & MonitorinfoFPrimary) != 0));
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors;
    }

    public CapturedFrame? CaptureMonitor(int monitorIndex, int jpegQuality = 75, double scale = 1.0)
    {
        // Enumerating monitors every frame is wasteful; refresh every couple of seconds to catch layout changes.
        if (DateTime.UtcNow - _monitorsCachedAtUtc > MonitorCacheLifetime || _cachedMonitors.Count == 0)
        {
            _cachedMonitors = GetMonitors();
            _monitorsCachedAtUtc = DateTime.UtcNow;
        }
        var monitors = _cachedMonitors;
        if (monitors.Count == 0) return null;

        var target = monitorIndex >= 0 && monitorIndex < monitors.Count
            ? monitors[monitorIndex]
            : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        if (target.Width <= 0 || target.Height <= 0) return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            var bmi = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = target.Width,
                    biHeight = -target.Height, // top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            hBitmap = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, hBitmap);
            if (!BitBlt(memDc, 0, 0, target.Width, target.Height, screenDc, target.Left, target.Top, Srccopy | Captureblt))
            {
                return null;
            }

            int stride = target.Width * 4;
            int byteCount = stride * target.Height;
            var rawPixels = new byte[byteCount];
            Marshal.Copy(bitsPtr, rawPixels, 0, byteCount);

            BitmapSource source = BitmapSource.Create(
                target.Width,
                target.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                rawPixels,
                stride);

            double clampedScale = Math.Clamp(scale, 0.25, 1.0);
            if (clampedScale < 0.99)
            {
                source = new TransformedBitmap(source, new ScaleTransform(clampedScale, clampedScale));
            }
            source.Freeze();

            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = Math.Clamp(jpegQuality, 20, 95)
            };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var ms = new MemoryStream();
            encoder.Save(ms);

            return new CapturedFrame(
                MonitorIndex: target.Index,
                Width: source.PixelWidth,
                Height: source.PixelHeight,
                JpegBytes: ms.ToArray(),
                CapturedAtUtc: DateTimeOffset.UtcNow);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }
}
