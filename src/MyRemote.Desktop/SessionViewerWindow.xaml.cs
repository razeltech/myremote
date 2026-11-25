using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;
using MyRemote.FileTransfer;
using MyRemote.Input;
using MyRemote.Network;
using MyRemote.Network.Protocol;
using MyRemote.ScreenCapture;

namespace MyRemote.Desktop;

public partial class SessionViewerWindow : Window
{
    private readonly RemotePeerTransport _transport;
    private readonly ISessionManager _sessionManager;
    private readonly FileTransferService _fileTransferService;
    private readonly DispatcherTimer _statsTimer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private int _selectedMonitorIndex;
    private int _frameCount;
    private long _bytesReceived;
    private bool _isFullScreen;
    private bool _isChatOpen;
    private DateTime _lastMouseMoveSent = DateTime.MinValue;

    public SessionViewerWindow(
        RemotePeerTransport transport,
        ISessionManager sessionManager,
        FileTransferService fileTransferService,
        string remoteTitle)
    {
        InitializeComponent();
        _transport = transport;
        _sessionManager = sessionManager;
        _fileTransferService = fileTransferService;
        RemoteTitleText.Text = remoteTitle;

        _transport.ScreenFrameReceived += OnScreenFrameReceived;
        _transport.MonitorListPayloadReceived += OnMonitorListReceived;
        _transport.ChatReceived += OnChatReceived;
        _transport.SessionDisconnected += OnSessionDisconnected;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) =>
        {
            var elapsed = DateTimeOffset.UtcNow - _startedAt;
            DurationText.Text = elapsed.ToString(@"hh\:mm\:ss");
            int fps = Interlocked.Exchange(ref _frameCount, 0);
            long kb = Interlocked.Exchange(ref _bytesReceived, 0) / 1024;
            TelemetryText.Text = $"{fps} FPS | {kb} KB/s";
        };
        _statsTimer.Start();
    }

    private void OnScreenFrameReceived(object? sender, byte[] jpegBytes)
    {
        Interlocked.Increment(ref _frameCount);
        Interlocked.Add(ref _bytesReceived, jpegBytes.Length);

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                using var ms = new MemoryStream(jpegBytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                RemoteScreenImage.Source = bmp;
            }
            catch
            {
                // Ignore corrupted frame
            }
        });
    }

    private void OnMonitorListReceived(object? sender, byte[] payload)
    {
        var list = JsonSerializer.Deserialize<List<MonitorDescriptor>>(payload);
        if (list is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            _monitors = list;
            MonitorCombo.Items.Clear();
            foreach (var m in list)
            {
                MonitorCombo.Items.Add($"Monitor {m.Index + 1} ({m.Width}x{m.Height})");
            }
            if (MonitorCombo.Items.Count > 0) MonitorCombo.SelectedIndex = 0;
        });
    }

    private void OnChatReceived(object? sender, ChatPayload chat)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ChatMessagesList.Items.Add($"[{chat.SentAtUtc.ToLocalTime():HH:mm}] {chat.SenderName}: {chat.Message}");
        });
    }

    private void OnSessionDisconnected(object? sender, string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MessageBox.Show($"Session ended: {reason}", "MyRemote Session", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        });
    }

    private (double nx, double ny) GetNormalizedCoords(MouseEventArgs e)
    {
        var pos = e.GetPosition(RemoteScreenImage);
        double w = Math.Max(1.0, RemoteScreenImage.ActualWidth);
        double h = Math.Max(1.0, RemoteScreenImage.ActualHeight);
        return (Math.Clamp(pos.X / w, 0.0, 1.0), Math.Clamp(pos.Y / h, 0.0, 1.0));
    }

    private MonitorDescriptor? ActiveMonitor =>
        _selectedMonitorIndex >= 0 && _selectedMonitorIndex < _monitors.Count ? _monitors[_selectedMonitorIndex] : null;

    private void SendInput(RemoteInputEvent ev)
    {
        if (!_transport.IsSessionActive) return;
        var mon = ActiveMonitor;
        var enriched = mon is null
            ? ev
            : ev with { MonitorLeft = mon.Left, MonitorTop = mon.Top, MonitorWidth = mon.Width, MonitorHeight = mon.Height };
        _ = _transport.SendTokenJsonAsync(WireMessageType.InputCommand, enriched);
    }

    private void RemoteScreen_MouseMove(object sender, MouseEventArgs e)
    {
        if ((DateTime.UtcNow - _lastMouseMoveSent).TotalMilliseconds < 16) return;
        _lastMouseMoveSent = DateTime.UtcNow;
        var (nx, ny) = GetNormalizedCoords(e);
        SendInput(new RemoteInputEvent(InputEventType.MouseMove, nx, ny));
    }

    private void RemoteScreen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenHost.Focus();
        var (nx, ny) = GetNormalizedCoords(e);
        var btn = e.ChangedButton switch
        {
            MouseButton.Left => RemoteMouseButton.Left,
            MouseButton.Right => RemoteMouseButton.Right,
            MouseButton.Middle => RemoteMouseButton.Middle,
            _ => RemoteMouseButton.None
        };
        if (btn != RemoteMouseButton.None)
        {
            SendInput(new RemoteInputEvent(InputEventType.MouseDown, nx, ny, btn));
        }
    }

    private void RemoteScreen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var (nx, ny) = GetNormalizedCoords(e);
        var btn = e.ChangedButton switch
        {
            MouseButton.Left => RemoteMouseButton.Left,
            MouseButton.Right => RemoteMouseButton.Right,
            MouseButton.Middle => RemoteMouseButton.Middle,
            _ => RemoteMouseButton.None
        };
        if (btn != RemoteMouseButton.None)
        {
            SendInput(new RemoteInputEvent(InputEventType.MouseUp, nx, ny, btn));
        }
    }

    private void RemoteScreen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var (nx, ny) = GetNormalizedCoords(e);
        SendInput(new RemoteInputEvent(InputEventType.MouseWheel, nx, ny, WheelDelta: e.Delta));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ChatInputBox.IsKeyboardFocused) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk > 0 && vk <= 0xFE)
        {
            SendInput(new RemoteInputEvent(InputEventType.KeyDown, VirtualKey: (ushort)vk));
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (ChatInputBox.IsKeyboardFocused) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk > 0 && vk <= 0xFE)
        {
            SendInput(new RemoteInputEvent(InputEventType.KeyUp, VirtualKey: (ushort)vk));
        }
    }

    private void MonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MonitorCombo.SelectedIndex < 0) return;
        _selectedMonitorIndex = MonitorCombo.SelectedIndex;
        _ = _transport.SendTokenJsonAsync(WireMessageType.SelectMonitor, _selectedMonitorIndex);
    }

    private void QualityCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QualityCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item || item.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int q) && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double s))
        {
            _ = _transport.SendTokenJsonAsync(WireMessageType.QualitySettings, new QualityPayload(30, q, s));
        }
    }

    private void SyncClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_sessionManager.HasPermission(SessionPermissions.ClipboardShare))
        {
            MessageBox.Show("Clipboard sharing was not granted by the host.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Clipboard.ContainsText())
        {
            _ = _transport.SendTokenJsonAsync(WireMessageType.ClipboardText, Clipboard.GetText());
        }
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_sessionManager.HasPermission(SessionPermissions.FileTransfer))
        {
            MessageBox.Show("File transfer was not granted by the host.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var offer = await _fileTransferService.PrepareOfferAsync(dlg.FileName);
            await _transport.SendTokenJsonAsync(WireMessageType.FileOffer, offer);

            byte[] buffer = new byte[FileTransferService.ChunkSizeBytes];
            await using var fs = File.OpenRead(dlg.FileName);
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                await _transport.SendTokenJsonAsync(WireMessageType.FileChunk, new FileChunkPayload(offer.TransferId, buffer[..read]));
            }
            await _transport.SendTokenJsonAsync(WireMessageType.FileComplete, offer.TransferId);
            MessageBox.Show($"Sent {offer.FileName} ({offer.TotalBytes} bytes).", "File Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"File transfer failed: {ex.Message}", "File Transfer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleChat_Click(object sender, RoutedEventArgs e)
    {
        _isChatOpen = !_isChatOpen;
        ChatColumn.Width = _isChatOpen ? new GridLength(280) : new GridLength(0);
    }

    private void SendChat_Click(object sender, RoutedEventArgs e)
    {
        var text = ChatInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInputBox.Clear();
        ChatMessagesList.Items.Add($"[{DateTime.Now:HH:mm}] You: {text}");
        _ = _transport.SendTokenJsonAsync(WireMessageType.ChatText, new ChatPayload("Controller", text, DateTimeOffset.UtcNow));
    }

    private void ToggleFullScreen_Click(object sender, RoutedEventArgs e)
    {
        _isFullScreen = !_isFullScreen;
        WindowStyle = _isFullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;
        FullScreenBtn.Content = _isFullScreen ? "Exit Full Screen" : "Full Screen";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await _transport.DisconnectAsync("Disconnected by controller");
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _statsTimer.Stop();
        _transport.ScreenFrameReceived -= OnScreenFrameReceived;
        _transport.MonitorListPayloadReceived -= OnMonitorListReceived;
        _transport.ChatReceived -= OnChatReceived;
        _transport.SessionDisconnected -= OnSessionDisconnected;
        if (_transport.IsSessionActive)
        {
            await _transport.DisconnectAsync("Viewer window closed");
        }
        base.OnClosed(e);
    }
}
