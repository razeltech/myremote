using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyRemote.Desktop.Services;

/// <summary>
/// Raises <see cref="TextCopied"/> whenever the local user copies text, using a message-only window and
/// AddClipboardFormatListener (no polling). Must be created on the UI thread.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private string? _suppressText;

    public ClipboardMonitor()
    {
        _source = new HwndSource(new HwndSourceParameters("MyRemoteClipboardMonitor")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0
        });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    public event Action<string>? TextCopied;

    /// <summary>Sets the local clipboard from the remote side without echoing it back.</summary>
    public void SetTextFromRemote(string text)
    {
        _suppressText = text;
        try { Clipboard.SetDataObject(text, true); }
        catch { /* clipboard busy; ignore */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmClipboardUpdate) return IntPtr.Zero;

        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch
        {
            // Another process holds the clipboard; skip this update.
        }

        if (string.IsNullOrEmpty(text)) return IntPtr.Zero;
        if (text == _suppressText)
        {
            _suppressText = null;
            return IntPtr.Zero;
        }

        TextCopied?.Invoke(text);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
