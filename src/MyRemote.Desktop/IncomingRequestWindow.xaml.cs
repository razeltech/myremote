using System.Windows;
using System.Windows.Threading;
using MyRemote.Core.Models;

namespace MyRemote.Desktop;

public partial class IncomingRequestWindow : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private readonly DateTime _expiresAtUtc;

    public IncomingRequestWindow(SessionRequest request, AppSettings settings, TimeSpan timeout)
    {
        InitializeComponent();
        _expiresAtUtc = DateTime.UtcNow + timeout;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _countdownTimer.Tick += (_, _) =>
        {
            var left = _expiresAtUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                CloseAsExpired();
                return;
            }
            CountdownText.Text = $"Auto-reject in {Math.Ceiling(left.TotalSeconds):0} s";
        };
        _countdownTimer.Start();
        Closed += (_, _) => _countdownTimer.Stop();
        RequesterNameText.Text = request.RequesterDisplayName;
        RequesterMetaText.Text = $"ID: {request.RequesterDeviceId}  |  IP: {request.RequesterIpAddress}";

        InputCheck.IsChecked = settings.AllowInputByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.MouseInput);
        ClipboardCheck.IsChecked = settings.AllowClipboardByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.ClipboardShare);
        FileTransferCheck.IsChecked = settings.AllowFileTransferByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.FileTransfer);
        ChatCheck.IsChecked = request.RequestedPermissions.HasFlag(SessionPermissions.SessionChat);
        VoiceChatCheck.IsChecked = request.RequestedPermissions.HasFlag(SessionPermissions.VoiceChat);
    }

    public SessionPermissions GrantedPermissions { get; private set; } = SessionPermissions.None;

    /// <summary>True when the dialog closed because of the timeout or because the requester gave up.</summary>
    public bool WasExpired { get; private set; }

    /// <summary>Closes the dialog as rejected (timeout, or the requester disconnected).</summary>
    public void CloseAsExpired()
    {
        if (!IsLoaded || WasExpired) return;
        WasExpired = true;
        GrantedPermissions = SessionPermissions.None;
        try { DialogResult = false; } catch (InvalidOperationException) { /* not shown as dialog */ }
        Close();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var perms = SessionPermissions.ViewScreen;
        if (InputCheck.IsChecked == true)
        {
            perms |= SessionPermissions.MouseInput | SessionPermissions.KeyboardInput;
        }
        if (ClipboardCheck.IsChecked == true) perms |= SessionPermissions.ClipboardShare;
        if (FileTransferCheck.IsChecked == true) perms |= SessionPermissions.FileTransfer;
        if (ChatCheck.IsChecked == true) perms |= SessionPermissions.SessionChat;
        if (VoiceChatCheck.IsChecked == true) perms |= SessionPermissions.VoiceChat;

        GrantedPermissions = perms;
        DialogResult = true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        GrantedPermissions = SessionPermissions.None;
        DialogResult = false;
        Close();
    }
}
