using System.Windows;
using MyRemote.Desktop.ViewModels;

namespace MyRemote.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void NavWorkbench_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = NavSection.Workbench;
    }

    private void NavSessions_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = NavSection.Sessions;
    }

    private void NavLogs_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = NavSection.Logs;
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = NavSection.Settings;
    }

    private void NavAbout_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = NavSection.About;
    }

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { Tag: MyRemote.Core.Models.DiscoveredPeer peer })
        {
            await Vm.ConnectToDiscoveredPeerAsync(peer);
        }
    }
}
