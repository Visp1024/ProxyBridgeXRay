using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls;
using ProxyBridge.GUI.Views;
using ProxyBridge.GUI.Services;
using ProxyBridge.GUI.Common;

namespace ProxyBridge.GUI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private const int MAX_CONNECTION_LOG_LINES = 100;
    private const int MAX_ACTIVITY_LOG_LINES = 100;

    private string _title = "ProxyBridge";
    private int _selectedTabIndex;
    private string _connectionsLog = "";
    private string _activityLog = "";
    private string _connectionsSearchText = "";
    private string _activitySearchText = "";
    private string _filteredConnectionsLog = "";
    private string _filteredActivityLog = "";
    private bool _isProxyRulesDialogOpen;
    private bool _isProxySettingsDialogOpen;
    private bool _isAddRuleViewOpen;
    private string _newProcessName = "";
    private string _newProxyAction = "PROXY";
    private bool _startWithWindows;
    private Window? _mainWindow;
    private ProxyBridgeService? _proxyService;
    private bool _isServiceInitialized = false;
    private readonly SettingsService _settingsService = new SettingsService();

    private string _currentProxyType = "SOCKS5";
    private string _currentProxyIp = "";
    private string _currentProxyPort = "";
    private string _currentProxyUsername = "";
    private string _currentProxyPassword = "";

    private XRayConfig _xRayConfig = new();
    private readonly Services.XRayService _xRayService = new();
    private bool _isXRayRunning;
    private string _xRayStatusText = "XRay: Stopped";
    private bool _isRoutingRunning;
    private bool _autoStartRouting = true;
    // proxy settings saved before XRay started
    private string _savedProxyType = "SOCKS5";
    private string _savedProxyIp = "";
    private string _savedProxyPort = "";
    private string _savedProxyUsername = "";
    private string _savedProxyPassword = "";

    private readonly List<string> _pendingConnectionLogs = new(128);
    private readonly List<string> _pendingActivityLogs = new(64);
    private readonly object _connectionLogLock = new();
    private readonly object _activityLogLock = new();
    private DispatcherTimer? _connectionLogTimer;
    private DispatcherTimer? _activityLogTimer;

    public void SetMainWindow(Window window)
    {
        _mainWindow = window;

        if (_isServiceInitialized)
            return;

        _isServiceInitialized = true;
        LoadConfiguration();

        // Start log flush timers (independent of routing service)
        _connectionLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _connectionLogTimer.Tick += (s, e) =>
        {
            List<string> logsToAdd;
            lock (_connectionLogLock)
            {
                if (_pendingConnectionLogs.Count == 0) return;
                logsToAdd = new List<string>(_pendingConnectionLogs);
                _pendingConnectionLogs.Clear();
            }

            ConnectionsLog += string.Join("", logsToAdd);

            var lines = ConnectionsLog.Split('\n');
            if (lines.Length > MAX_CONNECTION_LOG_LINES)
            {
                var linesToKeep = lines.Skip(lines.Length - MAX_CONNECTION_LOG_LINES).ToArray();
                ConnectionsLog = string.Join("\n", linesToKeep);
            }
        };

        _activityLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _activityLogTimer.Tick += (s, e) =>
        {
            List<string> logsToAdd;
            lock (_activityLogLock)
            {
                if (_pendingActivityLogs.Count == 0) return;
                logsToAdd = new List<string>(_pendingActivityLogs);
                _pendingActivityLogs.Clear();
            }
            ActivityLog += string.Join("", logsToAdd);
        };
        _activityLogTimer.Start();

        if (_autoStartRouting)
            IsRoutingRunning = StartRoutingInternal();

        _ = CheckForUpdatesOnStartupAsync();

        if (_xRayConfig.AutoStartXRay)
            _ = TryAutoStartXRayAsync();
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string ConnectionsLog
    {
        get => _connectionsLog;
        set
        {
            if (SetProperty(ref _connectionsLog, value))
            {
                if (string.IsNullOrWhiteSpace(_connectionsSearchText))
                    FilteredConnectionsLog = _connectionsLog;
            }
        }
    }

    public string ActivityLog
    {
        get => _activityLog;
        set
        {
            if (SetProperty(ref _activityLog, value))
            {
                if (!string.IsNullOrEmpty(_activityLog))
                {
                    var lines = _activityLog.Split('\n');
                    if (lines.Length > MAX_ACTIVITY_LOG_LINES)
                    {
                        var oldLog = _activityLog;
                        var linesToKeep = lines.Skip(lines.Length - MAX_ACTIVITY_LOG_LINES).ToArray();
                        _activityLog = string.Join("\n", linesToKeep);
                        oldLog = null!;
                    }
                }

                if (string.IsNullOrWhiteSpace(_activitySearchText))
                    FilteredActivityLog = _activityLog;
            }
        }
    }



    public bool IsProxyRulesDialogOpen
    {
        get => _isProxyRulesDialogOpen;
        set => SetProperty(ref _isProxyRulesDialogOpen, value);
    }

    public bool IsProxySettingsDialogOpen
    {
        get => _isProxySettingsDialogOpen;
        set => SetProperty(ref _isProxySettingsDialogOpen, value);
    }

    public bool IsAddRuleViewOpen
    {
        get => _isAddRuleViewOpen;
        set => SetProperty(ref _isAddRuleViewOpen, value);
    }

    public string ConnectionsSearchText
    {
        get => _connectionsSearchText;
        set => SetProperty(ref _connectionsSearchText, value);
    }

    public string ActivitySearchText
    {
        get => _activitySearchText;
        set => SetProperty(ref _activitySearchText, value);
    }

    public string FilteredConnectionsLog
    {
        get => _filteredConnectionsLog;
        set => SetProperty(ref _filteredConnectionsLog, value);
    }

    public string FilteredActivityLog
    {
        get => _filteredActivityLog;
        set => SetProperty(ref _filteredActivityLog, value);
    }

    public string NewProcessName
    {
        get => _newProcessName;
        set => SetProperty(ref _newProcessName, value);
    }

    public string NewProxyAction
    {
        get => _newProxyAction;
        set => SetProperty(ref _newProxyAction, value);
    }

    public ObservableCollection<ProxyRule> ProxyRules { get; } = new();

    private bool _dnsViaProxy = true;
    public bool DnsViaProxy
    {
        get => _dnsViaProxy;
        set
        {
            if (SetProperty(ref _dnsViaProxy, value))
            {
                _proxyService?.SetDnsViaProxy(value);
                SaveConfigurationInternal();

            }
        }
    }

    private bool _localhostViaProxy = false;  // Default: disabled for security
    public bool LocalhostViaProxy
    {
        get => _localhostViaProxy;
        set
        {
            if (SetProperty(ref _localhostViaProxy, value))
            {
                _proxyService?.SetLocalhostViaProxy(value);
                SaveConfigurationInternal();
            }
        }
    }

    private bool _isTrafficLoggingEnabled = true;
    public bool IsTrafficLoggingEnabled
    {
        get => _isTrafficLoggingEnabled;
        set
        {
            if (SetProperty(ref _isTrafficLoggingEnabled, value))
            {
                if (value)
                {
                    ProxyBridgeService.SetTrafficLoggingEnabled(true);
                    _connectionLogTimer?.Start();
                }
                else
                {
                    _connectionLogTimer?.Stop();
                    lock (_connectionLogLock)
                    {
                        _pendingConnectionLogs.Clear();
                    }

                    ProxyBridgeService.SetTrafficLoggingEnabled(false);

                    ConnectionsLog = null!;
                    FilteredConnectionsLog = null!;
                    ConnectionsLog = "";
                    FilteredConnectionsLog = "";

                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                }
                SaveConfigurationInternal();
            }
        }
    }

    private bool _closeToTray = true;
    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    private readonly Loc _loc = Loc.Instance;
    public Loc Loc => _loc;

    private string _currentLanguage = "en";
    private string _englishCheckmark = "✓";
    private string _chineseCheckmark = "";
    private string _russianCheckmark = "";

    public string EnglishCheckmark
    {
        get => _englishCheckmark;
        set => SetProperty(ref _englishCheckmark, value);
    }

    public string ChineseCheckmark
    {
        get => _chineseCheckmark;
        set => SetProperty(ref _chineseCheckmark, value);
    }

    public string RussianCheckmark
    {
        get => _russianCheckmark;
        set => SetProperty(ref _russianCheckmark, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool IsXRayRunning
    {
        get => _isXRayRunning;
        private set => SetProperty(ref _isXRayRunning, value);
    }

    public bool IsXRayStopped => !_isXRayRunning;

    public string XRayStatusText
    {
        get => _xRayStatusText;
        private set => SetProperty(ref _xRayStatusText, value);
    }

    public bool IsRoutingRunning
    {
        get => _isRoutingRunning;
        private set
        {
            if (SetProperty(ref _isRoutingRunning, value))
                OnPropertyChanged(nameof(IsRoutingStopped));
        }
    }

    public bool IsRoutingStopped => !_isRoutingRunning;

    public bool IsAutoStartXRayEnabled => _xRayConfig.AutoStartXRay;

    public bool AutoStartRouting
    {
        get => _autoStartRouting;
        set
        {
            if (SetProperty(ref _autoStartRouting, value))
                SaveConfigurationInternal();
        }
    }

    public ICommand ShowProxySettingsCommand { get; }
    public ICommand ShowProxyRulesCommand { get; }
    public ICommand ShowAboutCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ToggleDnsViaProxyCommand { get; }
    public ICommand ToggleLocalhostViaProxyCommand { get; }
    public ICommand ToggleTrafficLoggingCommand { get; }
    public ICommand ToggleCloseToTrayCommand { get; }
    public ICommand ToggleStartWithWindowsCommand { get; }
    public ICommand CloseDialogCommand { get; }
    public ICommand ClearConnectionsLogCommand { get; }
    public ICommand ClearActivityLogCommand { get; }
    public ICommand SearchConnectionsCommand { get; }
    public ICommand SearchActivityCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand SaveNewRuleCommand { get; }
    public ICommand CancelAddRuleCommand { get; }
    public ICommand StartRoutingCommand { get; }
    public ICommand StopRoutingCommand { get; }
    public ICommand ToggleAutoStartRoutingCommand { get; }
    public ICommand ShowXRaySettingsCommand { get; }
    public ICommand StartXRayCommand { get; }
    public ICommand StopXRayCommand { get; }
    public ICommand ToggleAutoStartXRayCommand { get; }

    public MainWindowViewModel()
    {
        ShowProxySettingsCommand = new RelayCommand(async () =>
        {
            var window = new ProxySettingsWindow();

            var viewModel = new ProxySettingsViewModel(
                initialType: _currentProxyType,
                initialIp: _currentProxyIp,
                initialPort: _currentProxyPort,
                initialUsername: _currentProxyUsername,
                initialPassword: _currentProxyPassword,
                onSave: (type, ip, port, username, password) =>
                {
                    if (_proxyService != null && ushort.TryParse(port, out ushort portNum))
                    {
                        if (_proxyService.SetProxyConfig(type, ip, portNum, username, password))
                        {

                            _currentProxyType = type;
                            _currentProxyIp = ip;
                            _currentProxyPort = port;
                            _currentProxyUsername = username;
                            _currentProxyPassword = password;

                            SaveConfigurationInternal();
                        }
                        else
                        {
                            QueueActivityLog("ERROR: Failed to set proxy config");
                        }
                    }
                    window.Close();
                },
                onClose: () =>
                {
                    window.Close();
                },
                proxyService: _proxyService
            );

            window.DataContext = viewModel;

            if (_mainWindow != null)
            {
                await window.ShowDialog(_mainWindow);
            }
        });

        ShowProxyRulesCommand = new RelayCommand(async () =>
        {
            var window = new ProxyRulesWindow();

            var viewModel = new ProxyRulesViewModel(
                proxyRules: ProxyRules,
                onAddRule: (rule) =>
                {
                    if (_proxyService != null)
                    {
                        uint ruleId = _proxyService.AddRule(
                            rule.ProcessName,
                            rule.TargetHosts,
                            rule.TargetPorts,
                            rule.Protocol,
                            rule.Action);
                        if (ruleId > 0)
                        {
                            rule.RuleId = ruleId;
                            rule.Index = ProxyRules.Count + 1;
                            ProxyRules.Add(rule);
                            SaveConfigurationInternal();
                        }
                        else
                        {
                            QueueActivityLog("ERROR: Failed to add rule");
                        }
                    }
                },
                onClose: () =>
                {
                    window.Close();
                },
                proxyService: _proxyService,
                onConfigChanged: SaveConfigurationInternal
            );

            window.DataContext = viewModel;
            viewModel.SetWindow(window);

            if (_mainWindow != null)
            {
                await window.ShowDialog(_mainWindow);
            }
        });

        ShowAboutCommand = new RelayCommand(async () =>
        {
            var viewModel = new AboutViewModel(() => { });

            var window = new Views.AboutWindow
            {
                DataContext = viewModel
            };

            if (_mainWindow != null)
            {
                await window.ShowDialog(_mainWindow);
            }
        });

        CheckForUpdatesCommand = new RelayCommand(async () =>
        {
            var updateWindow = new UpdateCheckWindow();
            var viewModel = new UpdateCheckViewModel(() => updateWindow.Close());
            updateWindow.DataContext = viewModel;

            if (_mainWindow != null)
            {
                await updateWindow.ShowDialog(_mainWindow);
            }
        });

        ToggleDnsViaProxyCommand = new RelayCommand(() =>
        {
            DnsViaProxy = !DnsViaProxy;
        });

        ToggleLocalhostViaProxyCommand = new RelayCommand(() =>
        {
            LocalhostViaProxy = !LocalhostViaProxy;
        });

        ToggleTrafficLoggingCommand = new RelayCommand(() =>
        {
            IsTrafficLoggingEnabled = !IsTrafficLoggingEnabled;
        });

        ToggleCloseToTrayCommand = new RelayCommand(() =>
        {
            CloseToTray = !CloseToTray;
            SaveConfigurationInternal();
        });

        ToggleStartWithWindowsCommand = new RelayCommand(() =>
        {
            StartWithWindows = !StartWithWindows;
            var settings = _settingsService.LoadSettings();
            settings.StartWithWindows = StartWithWindows;
            _settingsService.SaveSettings(settings);
            _settingsService.SetStartupWithWindows(StartWithWindows);
        });

        CloseDialogCommand = new RelayCommand(CloseDialogs);

        ClearConnectionsLogCommand = new RelayCommand(() =>
        {
            lock (_connectionLogLock)
            {
                _pendingConnectionLogs.Clear();
            }

            ConnectionsLog = null!;
            ConnectionsSearchText = null!;
            FilteredConnectionsLog = null!;

            ConnectionsLog = "";
            ConnectionsSearchText = "";
            FilteredConnectionsLog = "";

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        });

        ClearActivityLogCommand = new RelayCommand(() =>
        {
            lock (_activityLogLock)
            {
                _pendingActivityLogs.Clear();
            }

            ActivityLog = "";
            ActivitySearchText = "";
            FilteredActivityLog = "";

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        });

        SearchConnectionsCommand = new RelayCommand(() =>
        {
            FilteredConnectionsLog = FilterLog(_connectionsLog, _connectionsSearchText);
        });

        SearchActivityCommand = new RelayCommand(() =>
        {
            FilteredActivityLog = FilterLog(_activityLog, _activitySearchText);
        });

        AddRuleCommand = new RelayCommand(() =>
        {
            IsAddRuleViewOpen = true;
            NewProcessName = "";
            NewProxyAction = "PROXY";
        });

        SaveNewRuleCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(NewProcessName))
            {
                return;
            }

            var rule = new ProxyRule
            {
                ProcessName = NewProcessName,
                TargetHosts = "*",
                TargetPorts = "*",
                Protocol = "TCP",
                Action = NewProxyAction,
                IsEnabled = true
            };

            if (_proxyService != null)
            {
                var ruleId = _proxyService.AddRule(NewProcessName, "*", "*", "TCP", NewProxyAction);
                if (ruleId > 0)
                {
                    rule.RuleId = ruleId;
                    ProxyRules.Add(rule);
                    SaveConfigurationInternal();
                    IsAddRuleViewOpen = false;
                    NewProcessName = "";
                }
                else
                {
                    QueueActivityLog("ERROR: Failed to add rule");
                }
            }
        });

        CancelAddRuleCommand = new RelayCommand(() =>
        {
            IsAddRuleViewOpen = false;
            NewProcessName = "";
        });

        StartRoutingCommand = new RelayCommand(() =>
        {
            if (_isRoutingRunning) return;
            IsRoutingRunning = StartRoutingInternal();
        });

        StopRoutingCommand = new RelayCommand(() =>
        {
            if (!_isRoutingRunning) return;
            StopRoutingInternal();
            IsRoutingRunning = false;
        });

        ToggleAutoStartRoutingCommand = new RelayCommand(() =>
        {
            AutoStartRouting = !AutoStartRouting;
        });

        ShowXRaySettingsCommand = new RelayCommand(async () =>
        {
            var window = new Views.XRaySettingsWindow();
            var viewModel = new XRaySettingsViewModel(
                initial: _xRayConfig,
                onSave: (cfg) =>
                {
                    _xRayConfig = cfg;
                    SaveConfigurationInternal();
                    OnPropertyChanged(nameof(IsAutoStartXRayEnabled));
                    window.Close();
                },
                onCancel: () => window.Close()
            );
            window.DataContext = viewModel;

            if (_mainWindow != null)
                await window.ShowDialog(_mainWindow);
        });

        ToggleAutoStartXRayCommand = new RelayCommand(() =>
        {
            _xRayConfig.AutoStartXRay = !_xRayConfig.AutoStartXRay;
            SaveConfigurationInternal();
            OnPropertyChanged(nameof(IsAutoStartXRayEnabled));
        });

        StartXRayCommand = new RelayCommand(async () =>
        {
            if (_isXRayRunning) return;

            // If xray binary is missing, offer to download it
            if (Services.XRayService.FindXRayExecutable(_xRayConfig.XRayPath) == null)
            {
                var downloadWindow = new Views.XRayDownloadWindow();
                var downloadVm     = new XRayDownloadViewModel();
                downloadWindow.DataContext = downloadVm;

                if (_mainWindow != null)
                    await downloadWindow.ShowDialog(_mainWindow);

                if (downloadVm.DownloadedPath == null)
                    return; // user cancelled or download failed

                _xRayConfig.XRayPath = downloadVm.DownloadedPath;
                SaveConfigurationInternal();
            }

            // Save current proxy settings to restore later
            _savedProxyType     = _currentProxyType;
            _savedProxyIp       = _currentProxyIp;
            _savedProxyPort     = _currentProxyPort;
            _savedProxyUsername = _currentProxyUsername;
            _savedProxyPassword = _currentProxyPassword;

            if (_xRayService.Start(_xRayConfig))
            {
                if (_proxyService != null &&
                    ushort.TryParse(_xRayConfig.LocalPort, out ushort xRayPort))
                {
                    _proxyService.SetProxyConfig("SOCKS5", "127.0.0.1", xRayPort, "", "");
                }

                IsXRayRunning = true;
                OnPropertyChanged(nameof(IsXRayStopped));
                XRayStatusText = $"XRay: Running  ·  SOCKS5 :{_xRayConfig.LocalPort}  ·  HTTP :{_xRayConfig.HttpPort}";
            }
        });

        StopXRayCommand = new RelayCommand(() =>
        {
            if (!_isXRayRunning) return;

            _xRayService.Stop();
            IsXRayRunning = false;
            OnPropertyChanged(nameof(IsXRayStopped));
            XRayStatusText = "XRay: Stopped";

            // Restore previous proxy settings
            if (_proxyService != null &&
                !string.IsNullOrEmpty(_savedProxyIp) &&
                ushort.TryParse(_savedProxyPort, out ushort savedPort))
            {
                _proxyService.SetProxyConfig(
                    _savedProxyType, _savedProxyIp, savedPort,
                    _savedProxyUsername, _savedProxyPassword);
            }
        });

        _xRayService.LogReceived += msg =>
        {
            lock (_activityLogLock)
            {
                _pendingActivityLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            }
        };

        _xRayService.Stopped += _ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_isXRayRunning)
                {
                    IsXRayRunning = false;
                    OnPropertyChanged(nameof(IsXRayStopped));
                    XRayStatusText = "XRay: Stopped (crashed)";
                }
            });
        };
    }

    public void ChangeLanguage(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode)) return;

        _currentLanguage = languageCode;
        EnglishCheckmark = languageCode == "en" ? "✓" : "";
        ChineseCheckmark = languageCode == "zh" ? "✓" : "";
        RussianCheckmark = languageCode == "ru" ? "✓" : "";

        var config = ConfigManager.LoadConfig();
        config.Language = languageCode;
        ConfigManager.SaveConfig(config);

        _loc.CurrentCulture = new System.Globalization.CultureInfo(languageCode);
    }



    private void CloseDialogs()
    {
        IsProxyRulesDialogOpen = false;
        IsProxySettingsDialogOpen = false;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var settingsService = new SettingsService();
            var settings = settingsService.LoadSettings();
            if (!settings.CheckForUpdatesOnStartup)
                return;

            var updateService = new UpdateService();
            var versionInfo = await updateService.CheckForUpdatesAsync();

            if (versionInfo.IsUpdateAvailable && _mainWindow != null)
            {
                var notificationWindow = new UpdateNotificationWindow();
                var viewModel = new UpdateNotificationViewModel(() => notificationWindow.Close(), versionInfo);
                notificationWindow.DataContext = viewModel;

                _ = notificationWindow.ShowDialog(_mainWindow);
            }
        }
        catch { }
    }

    private bool StartRoutingInternal()
    {
        try
        {
            if (_proxyService == null)
            {
                _proxyService = new ProxyBridgeService();
                _proxyService.LogReceived += msg =>
                {
                    lock (_activityLogLock)
                        _pendingActivityLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                };
                _proxyService.ConnectionReceived += (processName, pid, destIp, destPort, proxyInfo) =>
                {
                    if (!_isTrafficLoggingEnabled) return;
                    if (_connectionLogTimer?.IsEnabled == false) _connectionLogTimer.Start();
                    var entry = $"[{DateTime.Now:HH:mm:ss}] {processName} (PID:{pid}) -> {destIp}:{destPort} via {proxyInfo}\n";
                    lock (_connectionLogLock)
                        _pendingConnectionLogs.Add(entry);
                };
            }

            _proxyService.SetDnsViaProxy(_dnsViaProxy);
            _proxyService.SetLocalhostViaProxy(_localhostViaProxy);

            if (!string.IsNullOrEmpty(_currentProxyIp) &&
                ushort.TryParse(_currentProxyPort, out ushort portNum))
            {
                _proxyService.SetProxyConfig(
                    _currentProxyType, _currentProxyIp, portNum,
                    _currentProxyUsername, _currentProxyPassword);
            }

            if (!_proxyService.Start())
            {
                QueueActivityLog("ERROR: Failed to start routing service");
                return false;
            }

            foreach (var rule in ProxyRules)
            {
                uint ruleId = _proxyService.AddRule(
                    rule.ProcessName, rule.TargetHosts,
                    rule.TargetPorts, rule.Protocol, rule.Action);
                if (ruleId > 0)
                {
                    rule.RuleId = ruleId;
                    rule.Index  = ProxyRules.IndexOf(rule) + 1;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            QueueActivityLog($"ERROR: {ex.Message}");
            return false;
        }
    }

    private void StopRoutingInternal()
    {
        try { _proxyService?.Dispose(); } catch { }
        _proxyService = null;
    }

    private async Task TryAutoStartXRayAsync()
    {
        // Brief delay so the main window is fully shown before we start
        await Task.Delay(800);

        if (_isXRayRunning) return;

        var cfg = _xRayConfig;
        if (string.IsNullOrWhiteSpace(cfg.ServerAddress) ||
            string.IsNullOrWhiteSpace(cfg.Uuid) ||
            string.IsNullOrWhiteSpace(cfg.PublicKey))
        {
            QueueActivityLog("XRay auto-start skipped: server is not configured");
            return;
        }

        if (Services.XRayService.FindXRayExecutable(cfg.XRayPath) == null)
        {
            QueueActivityLog("XRay auto-start skipped: xray binary not found. Configure the path in XRay Settings.");
            return;
        }

        _savedProxyType     = _currentProxyType;
        _savedProxyIp       = _currentProxyIp;
        _savedProxyPort     = _currentProxyPort;
        _savedProxyUsername = _currentProxyUsername;
        _savedProxyPassword = _currentProxyPassword;

        bool started = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            started = _xRayService.Start(cfg);
        });

        if (started)
        {
            if (_proxyService != null && ushort.TryParse(cfg.LocalPort, out ushort xRayPort))
                _proxyService.SetProxyConfig("SOCKS5", "127.0.0.1", xRayPort, "", "");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsXRayRunning = true;
                OnPropertyChanged(nameof(IsXRayStopped));
                XRayStatusText = $"XRay: Running  ·  SOCKS5 :{cfg.LocalPort}  ·  HTTP :{cfg.HttpPort}";
            });
        }
    }

    public void Cleanup()
    {
        try { SaveConfigurationInternal(); } catch { }
        try { _xRayService.Dispose(); } catch { }
        try { _proxyService?.Dispose(); _proxyService = null; } catch { }
    }

    private string FilterLog(string log, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return log;

        var sb = new StringBuilder(log.Length / 2);
        var lines = log.Split('\n');

        foreach (var line in lines)
        {
            if (line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(line);
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private void LoadConfiguration()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            StartWithWindows = settings.StartWithWindows && _settingsService.IsStartupEnabled();

            var config = ConfigManager.LoadConfig();

            _currentProxyType = ValidationHelper.DefaultIfEmpty(config.ProxyType, "SOCKS5");
            _currentProxyIp = ValidationHelper.DefaultIfEmpty(config.ProxyIp, "");
            _currentProxyPort = ValidationHelper.DefaultIfEmpty(config.ProxyPort, "");
            _currentProxyUsername = config.ProxyUsername ?? "";
            _currentProxyPassword = config.ProxyPassword ?? "";

            DnsViaProxy = config.DnsViaProxy;
            LocalhostViaProxy = config.LocalhostViaProxy;
            CloseToTray = config.CloseToTray;
            IsTrafficLoggingEnabled = config.IsTrafficLoggingEnabled;

            if (!string.IsNullOrWhiteSpace(config.Language))
            {
                _currentLanguage = config.Language;
                _loc.CurrentCulture = new System.Globalization.CultureInfo(config.Language);
                EnglishCheckmark = config.Language == "en" ? "✓" : "";
                ChineseCheckmark = config.Language == "zh" ? "✓" : "";
                RussianCheckmark = config.Language == "ru" ? "✓" : "";
            }

            _autoStartRouting = config.AutoStartRouting;

            if (config.XRay != null)
                _xRayConfig = config.XRay;

            if (config.ProxyRules != null && config.ProxyRules.Count > 0)
            {
                foreach (var ruleConfig in config.ProxyRules)
                {
                    if (string.IsNullOrWhiteSpace(ruleConfig.ProcessName))
                        continue;

                    var rule = new ProxyRule
                    {
                        ProcessName = ruleConfig.ProcessName,
                        TargetHosts = ValidationHelper.DefaultIfEmpty(ruleConfig.TargetHosts),
                        TargetPorts = ValidationHelper.DefaultIfEmpty(ruleConfig.TargetPorts),
                        Protocol = ValidationHelper.DefaultIfEmpty(ruleConfig.Protocol, "TCP"),
                        Action = ValidationHelper.DefaultIfEmpty(ruleConfig.Action, "PROXY"),
                        IsEnabled = ruleConfig.IsEnabled
                    };
                    ProxyRules.Add(rule);
                }
            }

            QueueActivityLog("Configuration loaded successfully");
        }
        catch (Exception ex)
        {
            QueueActivityLog($"Failed to load configuration: {ex.Message}");
        }
    }

    private void SaveConfigurationInternal()
    {
        Task.Run(() => SaveConfigurationInternalAsync());
    }

    private void SaveConfigurationInternalAsync()
    {
        try
        {
            var config = new AppConfig
            {
                ProxyType = _currentProxyType,
                ProxyIp = _currentProxyIp,
                ProxyPort = _currentProxyPort,
                ProxyUsername = _currentProxyUsername,
                ProxyPassword = _currentProxyPassword,
                DnsViaProxy = _dnsViaProxy,
                LocalhostViaProxy = _localhostViaProxy,
                IsTrafficLoggingEnabled = _isTrafficLoggingEnabled,
                Language = _currentLanguage,
                CloseToTray = _closeToTray,
                AutoStartRouting = _autoStartRouting,
                ProxyRules = ProxyRules.Select(r => new ProxyRuleConfig
                {
                    ProcessName = r.ProcessName,
                    TargetHosts = r.TargetHosts,
                    TargetPorts = r.TargetPorts,
                    Protocol = r.Protocol,
                    Action = r.Action,
                    IsEnabled = r.IsEnabled
                }).ToList(),
                XRay = _xRayConfig
            };

            ConfigManager.SaveConfig(config);
        }
        catch { }
    }

    private void QueueActivityLog(string message)
    {
        lock (_activityLogLock)
        {
            _pendingActivityLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
    }
}

public class ProxyRule : ViewModelBase
{
    private string _processName = "*";
    private string _targetHosts = "*";
    private string _targetPorts = "*";
    private string _protocol = "TCP";
    private string _action = "PROXY";
    private bool _isEnabled = true;
    private bool _isSelected = false;
    private int _index;
    private uint _ruleId;

    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public uint RuleId
    {
        get => _ruleId;
        set => SetProperty(ref _ruleId, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    public string TargetHosts
    {
        get => _targetHosts;
        set => SetProperty(ref _targetHosts, value);
    }

    public string TargetPorts
    {
        get => _targetPorts;
        set => SetProperty(ref _targetPorts, value);
    }

    public string Protocol
    {
        get => _protocol;
        set => SetProperty(ref _protocol, value);
    }

    public string Action
    {
        get => _action;
        set => SetProperty(ref _action, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
