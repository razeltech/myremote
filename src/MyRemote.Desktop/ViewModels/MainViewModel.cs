using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;
using MyRemote.Desktop.Services;
using MyRemote.Network;
using AppThemeMode = MyRemote.Core.Models.ThemeMode;

namespace MyRemote.Desktop.ViewModels;

public enum NavSection
{
    Workbench = 0,
    Sessions = 1,
    Logs = 2,
    Settings = 3,
    About = 4
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IDeviceIdentityProvider _identityProvider;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ISessionHistoryRepository _historyRepository;
    private readonly ISecurityService _securityService;
    private readonly ISessionManager _sessionManager;
    private readonly RemoteSessionCoordinator _coordinator;
    private readonly InternetDiagnosticsService _diagnosticsService;
    private readonly ThemeService _themeService;
    private readonly MyRemote.Core.Logging.RollingFileLoggerProvider _logProvider;
    private readonly ILogger<MainViewModel> _logger;

    private DeviceIdentity? _localIdentity;
    private NavSection _activeSection = NavSection.Workbench;
    private string _deviceId = "--- --- ---";
    private string _publicKeyFingerprint = string.Empty;
    private string _partnerIdInput = string.Empty;
    private string _statusMessage = "Ready for incoming or outgoing connections";
    private ConnectionState _connectionState = ConnectionState.Idle;
    private string _copyButtonLabel = "Copy ID";
    private bool _isSettingsSavedNoticeVisible;
    private bool _isHostSessionActive;
    private string _activeHostBannerText = string.Empty;
    private string _diagnosticsOutput = "Click 'Run Network Diagnostics' to check STUN reflexive address and IPv6 availability.";

    // Settings fields
    private string _displayName = Environment.MachineName;
    private AppThemeMode _selectedTheme = AppThemeMode.Dark;
    private int _listenPort = 48720;
    private int _discoveryPort = 48721;
    private bool _enableLanDiscovery = true;
    private int _targetFps = 30;
    private int _jpegQuality = 75;
    private bool _allowInputByDefault = true;
    private bool _allowClipboardByDefault;
    private bool _allowFileTransferByDefault;
    private string _stunServer = "stun.l.google.com:19302";
    private string _customSignalingUrl = string.Empty;
    private string _customTurnServer = string.Empty;
    private string _turnUsername = string.Empty;
    private string _turnPassword = string.Empty;
    private string _hostChatInput = string.Empty;
    private string _internetStatusText = "Internet: connecting…";
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    public MainViewModel(
        IDeviceIdentityProvider identityProvider,
        ISettingsRepository settingsRepository,
        ISessionHistoryRepository historyRepository,
        ISecurityService securityService,
        ISessionManager sessionManager,
        RemoteSessionCoordinator coordinator,
        InternetDiagnosticsService diagnosticsService,
        ThemeService themeService,
        MyRemote.Core.Logging.RollingFileLoggerProvider logProvider,
        ILogger<MainViewModel> logger,
        string logDirectory)
    {
        _identityProvider = identityProvider;
        _settingsRepository = settingsRepository;
        _historyRepository = historyRepository;
        _securityService = securityService;
        _sessionManager = sessionManager;
        _coordinator = coordinator;
        _diagnosticsService = diagnosticsService;
        _themeService = themeService;
        _logProvider = logProvider;
        _logger = logger;
        LogDirectory = logDirectory;

        foreach (var existing in _logProvider.GetRecentLines())
        {
            LogEntries.Add(existing);
        }
        _logProvider.LogWritten += line =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LogEntries.Add(line);
                while (LogEntries.Count > 500) LogEntries.RemoveAt(0);
            });
        };

        _sessionManager.StateChanged += (_, state) =>
        {
            Application.Current?.Dispatcher.Invoke(async () =>
            {
                ConnectionState = state;
                OnPropertyChanged(nameof(ConnectionStateLabel));
                OnPropertyChanged(nameof(IsConnected));
                var session = _sessionManager.CurrentSession;
                IsHostSessionActive = session is { IsHost: true } && state == ConnectionState.Connected;
                ActiveHostBannerText = session is null
                    ? string.Empty
                    : $"ACTIVE REMOTE CONTROL: {session.RemoteDisplayName} ({session.RemoteEndpoint}) — Permissions: {session.GrantedPermissions}";

                if (state == ConnectionState.Idle)
                {
                    await RefreshHistoryAsync();
                }
            });
        };

        _coordinator.HostSessionStarted += (_, session) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                HostChatMessages.Clear();
                ActiveSection = NavSection.Workbench;
                App.RestoreMainWindow();
            });
        };

        _coordinator.HostChatReceived += (_, chat) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                HostChatMessages.Add($"[{chat.SentAtUtc.ToLocalTime():HH:mm}] {chat.SenderName}: {chat.Message}");
                App.RestoreMainWindow();
            });
        };

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshInternetStatus();
        _statusTimer.Start();

        _coordinator.PeersUpdated += (_, peers) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                DiscoveredPeers.Clear();
                foreach (var p in peers) DiscoveredPeers.Add(p);
                OnPropertyChanged(nameof(HasNoDiscoveredPeers));
            });
        };

        CopyIdCommand = new AsyncRelayCommand(CopyIdAsync);
        ToggleThemeCommand = new AsyncRelayCommand(ToggleThemeAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !string.IsNullOrWhiteSpace(PartnerIdInput));
        DisconnectHostCommand = new AsyncRelayCommand(() => _coordinator.DisconnectAsync("Terminated by host"));
        RevokeInputCommand = new AsyncRelayCommand(() => _coordinator.RevokeInputAsync());
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync);
        CopyLogsCommand = new RelayCommand(CopyLogs);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        ClearLogsCommand = new RelayCommand(() => LogEntries.Clear());
        SendHostChatCommand = new AsyncRelayCommand(SendHostChatAsync, () => !string.IsNullOrWhiteSpace(HostChatInput));
    }

    public string LogDirectory { get; }
    public string AppVersion { get; } =
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    public ObservableCollection<DiscoveredPeer> DiscoveredPeers { get; } = [];
    public ObservableCollection<SessionHistoryEntry> RecentSessions { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];
    public ObservableCollection<string> HostChatMessages { get; } = [];
    public bool HasNoDiscoveredPeers => DiscoveredPeers.Count == 0;

    public NavSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                OnPropertyChanged(nameof(IsWorkbenchVisible));
                OnPropertyChanged(nameof(IsSessionsVisible));
                OnPropertyChanged(nameof(IsLogsVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsAboutVisible));
            }
        }
    }

    public bool IsWorkbenchVisible => ActiveSection == NavSection.Workbench;
    public bool IsSessionsVisible => ActiveSection == NavSection.Sessions;
    public bool IsLogsVisible => ActiveSection == NavSection.Logs;
    public bool IsSettingsVisible => ActiveSection == NavSection.Settings;
    public bool IsAboutVisible => ActiveSection == NavSection.About;

    public string DeviceId { get => _deviceId; private set => SetProperty(ref _deviceId, value); }
    public string PublicKeyFingerprint { get => _publicKeyFingerprint; private set => SetProperty(ref _publicKeyFingerprint, value); }
    public string PartnerIdInput { get => _partnerIdInput; set => SetProperty(ref _partnerIdInput, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public ConnectionState ConnectionState { get => _connectionState; private set => SetProperty(ref _connectionState, value); }
    public string ConnectionStateLabel => ConnectionState.ToString();
    public bool IsConnected => ConnectionState == ConnectionState.Connected;
    public string CopyButtonLabel { get => _copyButtonLabel; private set => SetProperty(ref _copyButtonLabel, value); }
    public bool IsSettingsSavedNoticeVisible { get => _isSettingsSavedNoticeVisible; private set => SetProperty(ref _isSettingsSavedNoticeVisible, value); }
    public bool IsHostSessionActive { get => _isHostSessionActive; private set => SetProperty(ref _isHostSessionActive, value); }
    public string ActiveHostBannerText { get => _activeHostBannerText; private set => SetProperty(ref _activeHostBannerText, value); }
    public string DiagnosticsOutput { get => _diagnosticsOutput; private set => SetProperty(ref _diagnosticsOutput, value); }

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public AppThemeMode SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                _themeService.ApplyTheme(value);
                OnPropertyChanged(nameof(ThemeToggleLabel));
            }
        }
    }
    public string ThemeToggleLabel => SelectedTheme == AppThemeMode.Dark ? "Switch to Light" : "Switch to Dark";
    public int ListenPort { get => _listenPort; set => SetProperty(ref _listenPort, value); }
    public int DiscoveryPort { get => _discoveryPort; set => SetProperty(ref _discoveryPort, value); }
    public bool EnableLanDiscovery { get => _enableLanDiscovery; set => SetProperty(ref _enableLanDiscovery, value); }
    public int TargetFps { get => _targetFps; set => SetProperty(ref _targetFps, value); }
    public int JpegQuality { get => _jpegQuality; set => SetProperty(ref _jpegQuality, value); }
    public bool AllowInputByDefault { get => _allowInputByDefault; set => SetProperty(ref _allowInputByDefault, value); }
    public bool AllowClipboardByDefault { get => _allowClipboardByDefault; set => SetProperty(ref _allowClipboardByDefault, value); }
    public bool AllowFileTransferByDefault { get => _allowFileTransferByDefault; set => SetProperty(ref _allowFileTransferByDefault, value); }
    public string StunServer { get => _stunServer; set => SetProperty(ref _stunServer, value); }
    public string CustomSignalingUrl { get => _customSignalingUrl; set => SetProperty(ref _customSignalingUrl, value); }
    public string CustomTurnServer { get => _customTurnServer; set => SetProperty(ref _customTurnServer, value); }
    public string TurnUsername { get => _turnUsername; set => SetProperty(ref _turnUsername, value); }
    public string TurnPassword { get => _turnPassword; set => SetProperty(ref _turnPassword, value); }
    public string HostChatInput { get => _hostChatInput; set => SetProperty(ref _hostChatInput, value); }
    public string InternetStatusText { get => _internetStatusText; private set => SetProperty(ref _internetStatusText, value); }

    public ICommand CopyIdCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectHostCommand { get; }
    public ICommand RevokeInputCommand { get; }
    public ICommand RunDiagnosticsCommand { get; }
    public ICommand CopyLogsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand SendHostChatCommand { get; }

    public async Task InitializeAsync()
    {
        _localIdentity = await _identityProvider.GetOrCreateIdentityAsync();
        DeviceId = _localIdentity.DeviceId;
        PublicKeyFingerprint = _localIdentity.PublicKeyFingerprint;

        var settings = await _settingsRepository.LoadAsync();
        ApplyLoadedSettings(settings);
        await RefreshHistoryAsync();

        _coordinator.StartNode(_localIdentity, settings);
        _logger.LogInformation("MainViewModel initialized for DeviceId {DeviceId}", DeviceId);
    }

    public Task ConnectToDiscoveredPeerAsync(DiscoveredPeer peer)
    {
        PartnerIdInput = peer.DeviceId;
        return ConnectEndpointAsync(peer.IpAddress, peer.Port, peer.DisplayName);
    }

    private async Task RefreshHistoryAsync()
    {
        var recent = await _historyRepository.GetRecentAsync(20);
        RecentSessions.Clear();
        foreach (var item in recent) RecentSessions.Add(item);
    }

    private void ApplyLoadedSettings(AppSettings s)
    {
        DisplayName = string.IsNullOrWhiteSpace(s.DisplayName) ? Environment.MachineName : s.DisplayName;
        SelectedTheme = s.Theme;
        ListenPort = s.ListenPort;
        DiscoveryPort = s.DiscoveryPort;
        EnableLanDiscovery = s.EnableLanDiscovery;
        TargetFps = s.TargetFps;
        JpegQuality = s.JpegQuality;
        AllowInputByDefault = s.AllowInputByDefaultInPrompt;
        AllowClipboardByDefault = s.AllowClipboardByDefaultInPrompt;
        AllowFileTransferByDefault = s.AllowFileTransferByDefaultInPrompt;
        StunServer = s.StunServer;
        CustomSignalingUrl = s.CustomSignalingUrl;
        CustomTurnServer = s.CustomTurnServer;
        TurnUsername = s.TurnUsername;
        TurnPassword = s.TurnPassword;
    }

    private void RefreshInternetStatus()
    {
        var relay = _coordinator.HasTurnRelay ? "TURN relay configured" : "no TURN relay (strict NATs will fail)";
        InternetStatusText = _coordinator.IsInternetSignalingReady
            ? $"Internet: online as {DeviceId} — {relay}"
            : _coordinator.InternetSignalingError is { } err
                ? $"Internet: {err}"
                : "Internet: connecting to signaling server…";
    }

    private async Task SendHostChatAsync()
    {
        var text = HostChatInput.Trim();
        if (string.IsNullOrEmpty(text)) return;
        HostChatInput = string.Empty;
        HostChatMessages.Add($"[{DateTime.Now:HH:mm}] You: {text}");
        await _coordinator.SendHostChatAsync(text);
    }

    private async Task CopyIdAsync()
    {
        try
        {
            Clipboard.SetDataObject(DeviceId, false);
            CopyButtonLabel = "Copied";
            await Task.Delay(1800);
            CopyButtonLabel = "Copy ID";
        }
        catch
        {
            StatusMessage = "Clipboard is busy; copy your ID manually.";
        }
    }

    private async Task ToggleThemeAsync()
    {
        SelectedTheme = SelectedTheme == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark;
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Environment.MachineName : DisplayName.Trim(),
            Theme = SelectedTheme,
            ListenPort = Math.Clamp(ListenPort, 1024, 65535),
            DiscoveryPort = Math.Clamp(DiscoveryPort, 1024, 65535),
            EnableLanDiscovery = EnableLanDiscovery,
            TargetFps = Math.Clamp(TargetFps, 5, 60),
            JpegQuality = Math.Clamp(JpegQuality, 20, 95),
            AllowInputByDefaultInPrompt = AllowInputByDefault,
            AllowClipboardByDefaultInPrompt = AllowClipboardByDefault,
            AllowFileTransferByDefaultInPrompt = AllowFileTransferByDefault,
            StunServer = StunServer.Trim(),
            CustomSignalingUrl = CustomSignalingUrl.Trim(),
            CustomTurnServer = CustomTurnServer.Trim(),
            TurnUsername = TurnUsername.Trim(),
            TurnPassword = TurnPassword
        };

        await _settingsRepository.SaveAsync(settings);
        if (_localIdentity is not null)
        {
            _coordinator.UpdateLiveSettings(_localIdentity, settings);
        }
        StatusMessage = "Settings saved";
        IsSettingsSavedNoticeVisible = true;
        await Task.Delay(2000);
        IsSettingsSavedNoticeVisible = false;
    }

    private async Task ConnectAsync()
    {
        var raw = PartnerIdInput.Trim();
        if (_securityService.IsValidDeviceId(raw))
        {
            var formatted = _securityService.FormatDeviceId(raw);
            PartnerIdInput = formatted;
            var peer = DiscoveredPeers.FirstOrDefault(p => p.DeviceId == formatted);
            if (peer is not null)
            {
                await ConnectEndpointAsync(peer.IpAddress, peer.Port, peer.DisplayName);
                return;
            }

            if (_localIdentity is null) return;
            StatusMessage = $"Connecting over Internet P2P to {formatted} — awaiting host approval...";
            var internetRes = await _coordinator.ConnectInternetByDeviceIdAsync(_localIdentity, DisplayName, formatted);
            StatusMessage = internetRes.Approved
                ? $"Connected to {formatted} (Internet P2P)"
                : $"Internet connection failed: {internetRes.Reason ?? "Peer offline or declined"}";
            return;
        }

        // Support direct IP or IP:Port (for LAN/VPN/Port-forwarded Internet)
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        if (IPAddress.TryParse(parts[0], out _))
        {
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : ListenPort;
            await ConnectEndpointAsync(parts[0], port, parts[0]);
            return;
        }

        StatusMessage = "Enter a valid 9-digit Device ID or IP[:Port] address.";
    }

    private async Task ConnectEndpointAsync(string hostIp, int port, string label)
    {
        if (_localIdentity is null) return;
        StatusMessage = $"Requesting session with {label} ({hostIp}:{port}) — awaiting host approval...";
        var res = await _coordinator.ConnectToPeerAsync(_localIdentity, DisplayName, hostIp, port, label);
        StatusMessage = res.Approved
            ? $"Connected to {label}"
            : $"Connection declined: {res.Reason ?? "Unknown error"}";
    }

    private async Task RunDiagnosticsAsync()
    {
        DiagnosticsOutput = "Running STUN & IPv6 network diagnostics...";
        var report = await _diagnosticsService.RunDiagnosticsAsync(StunServer);
        DiagnosticsOutput = report.Summary;
        _logger.LogInformation("Network diagnostics: {Summary}", report.Summary);
    }

    private void CopyLogs()
    {
        try
        {
            Clipboard.SetDataObject(string.Join(Environment.NewLine, LogEntries), false);
            StatusMessage = "Audit logs copied to clipboard";
        }
        catch
        {
            StatusMessage = "Failed to copy logs to clipboard";
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open log directory");
        }
    }
}
