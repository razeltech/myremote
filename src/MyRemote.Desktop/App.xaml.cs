using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Logging;
using MyRemote.Core.Services;
using MyRemote.Desktop.Services;
using MyRemote.Desktop.ViewModels;
using MyRemote.FileTransfer;
using MyRemote.Input;
using MyRemote.Network;
using MyRemote.ScreenCapture;
using MyRemote.Security;
using MyRemote.Storage;
using Forms = System.Windows.Forms;

namespace MyRemote.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private RollingFileLoggerProvider? _fileLoggerProvider;
    private Forms.NotifyIcon? _trayIcon;
    private bool _trayHintShown;
    private Mutex? _singleInstanceMutex;

    public static bool IsExplicitExit { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Two copies would share one Device ID: the signaling server rejects the second ("ID taken")
        // and the TCP listener port collides. Allow exactly one instance per Windows user.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\MyRemote.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MyRemote is already running. Look for its icon in the system tray (bottom-right, near the clock).",
                "MyRemote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyRemote");
        var logDir = Path.Combine(appDataDir, "logs");
        var dbPath = Path.Combine(appDataDir, "myremote.db");

        _fileLoggerProvider = new RollingFileLoggerProvider(logDir, LogLevel.Information);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_fileLoggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton(new SqliteDatabase(dbPath));
        services.AddSingleton<IDeviceIdentityProvider, SqliteDeviceIdentityProvider>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        services.AddSingleton<ISessionHistoryRepository, SqliteSessionHistoryRepository>();
        services.AddSingleton<ISecurityService, SecurityService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IScreenCaptureService, Win32ScreenCaptureService>();
        services.AddSingleton<IInputInjector, Win32InputInjector>();
        services.AddSingleton<FileTransferService>();
        services.AddSingleton<LanDiscoveryService>();
        services.AddSingleton<RemotePeerTransport>();
        services.AddSingleton<WebRtcPeerTransport>();
        services.AddSingleton<InternetDiagnosticsService>();
        services.AddSingleton<RemoteSessionCoordinator>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton(_fileLoggerProvider);
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<IDeviceIdentityProvider>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<ISessionHistoryRepository>(),
            sp.GetRequiredService<ISecurityService>(),
            sp.GetRequiredService<ISessionManager>(),
            sp.GetRequiredService<RemoteSessionCoordinator>(),
            sp.GetRequiredService<InternetDiagnosticsService>(),
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<RollingFileLoggerProvider>(),
            sp.GetRequiredService<ILogger<MainViewModel>>(),
            logDir));
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        DispatcherUnhandledException += (_, args) =>
        {
            var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
            logger.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(
                $"An unexpected error occurred: {args.Exception.Message}",
                "MyRemote Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var vm = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = vm;
        await vm.InitializeAsync();
        InitializeTrayIcon(mainWindow, vm);
        mainWindow.Show();
    }

    private void InitializeTrayIcon(MainWindow mainWindow, MainViewModel vm)
    {
        System.Drawing.Icon? icon = null;
        try
        {
            var streamInfo = GetResourceStream(new Uri("pack://application:,,,/Assets/myremote.ico"));
            if (streamInfo is not null)
            {
                icon = new System.Drawing.Icon(streamInfo.Stream);
            }
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MyRemote", null, (_, _) => RestoreMainWindow(mainWindow));
        menu.Items.Add($"Copy ID ({vm.DeviceId})", null, (_, _) =>
        {
            try { Clipboard.SetDataObject(vm.DeviceId, false); } catch { /* ignore */ }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit MyRemote", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = $"MyRemote — Ready ({vm.DeviceId})",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => RestoreMainWindow(mainWindow);
    }

    public void NotifyMinimizedToTray()
    {
        if (_trayHintShown || _trayIcon is null) return;
        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "MyRemote is running in the tray",
            "Right-click the MyRemote tray icon to open or exit.",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>Shows and focuses the main window (e.g. when this PC starts being controlled).</summary>
    public static void RestoreMainWindow(Window? mainWindow = null)
    {
        mainWindow ??= Current?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }
        mainWindow.Activate();
    }

    public void ExitApplication()
    {
        IsExplicitExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _serviceProvider?.GetService<RemoteSessionCoordinator>()?.Dispose();
        _fileLoggerProvider?.Dispose();
        _serviceProvider?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
