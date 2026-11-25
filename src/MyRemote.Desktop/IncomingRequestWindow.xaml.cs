using System.Windows;
using MyRemote.Core.Models;

namespace MyRemote.Desktop;

public partial class IncomingRequestWindow : Window
{
    public IncomingRequestWindow(SessionRequest request, AppSettings settings)
    {
        InitializeComponent();
        RequesterNameText.Text = request.RequesterDisplayName;
        RequesterMetaText.Text = $"ID: {request.RequesterDeviceId}  |  IP: {request.RequesterIpAddress}";

        InputCheck.IsChecked = settings.AllowInputByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.MouseInput);
        ClipboardCheck.IsChecked = settings.AllowClipboardByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.ClipboardShare);
        FileTransferCheck.IsChecked = settings.AllowFileTransferByDefaultInPrompt &&
            request.RequestedPermissions.HasFlag(SessionPermissions.FileTransfer);
        ChatCheck.IsChecked = request.RequestedPermissions.HasFlag(SessionPermissions.SessionChat);
    }

    public SessionPermissions GrantedPermissions { get; private set; } = SessionPermissions.None;

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
