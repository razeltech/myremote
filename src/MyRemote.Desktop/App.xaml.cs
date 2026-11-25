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

namespace MyRemote.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private RollingFileLoggerProvider? _fileLoggerProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        services.AddSingleton<InternetDiagnosticsService>();
        services.AddSingleton<RemoteSessionCoordinator>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<IDeviceIdentityProvider>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<ISessionHistoryRepository>(),
            sp.GetRequiredService<ISecurityService>(),
            sp.GetRequiredService<ISessionManager>(),
            sp.GetRequiredService<RemoteSessionCoordinator>(),
            sp.GetRequiredService<InternetDiagnosticsService>(),
            sp.GetRequiredService<ThemeService>(),
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
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<RemoteSessionCoordinator>()?.Dispose();
        _fileLoggerProvider?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
