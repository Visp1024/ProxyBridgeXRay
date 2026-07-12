using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ProxyBridge.GUI.Services;

public class Loc : INotifyPropertyChanged
{
    private static readonly Loc _instance = new();
    public static Loc Instance => _instance;

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (_currentCulture.Equals(value)) return;
            _currentCulture = value;

            CultureInfo.CurrentUICulture = value;
            CultureInfo.CurrentCulture = value;
            Resources.Resources.Culture = value;

            // refresh UI
            OnPropertyChanged(string.Empty);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Menu Items
    public string MenuProxy => Resources.Resources.MenuProxy;
    public string MenuProxySettings => Resources.Resources.MenuProxySettings;
    public string MenuProxyRules => Resources.Resources.MenuProxyRules;
    public string MenuLocalhostViaProxy => Resources.Resources.MenuLocalhostViaProxy;
    public string MenuEnableTrafficLogging => Resources.Resources.MenuEnableTrafficLogging;
    public string MenuLogFilters => Resources.Resources.MenuLogFilters;
    public string LogFiltersDesc1 => Resources.Resources.LogFiltersDesc1;
    public string LogFiltersDesc2 => Resources.Resources.LogFiltersDesc2;
    public string MenuAutoClearLogs => Resources.Resources.MenuAutoClearLogs;
    public string MenuSettings => Resources.Resources.MenuSettings;
    public string MenuCloseToTray => Resources.Resources.MenuCloseToTray;
    public string MenuRunAtStartup => Resources.Resources.MenuRunAtStartup;
    public string MenuLanguage => Resources.Resources.MenuLanguage;
    public string MenuAbout => Resources.Resources.MenuAbout;
    public string MenuAboutProxyBridge => Resources.Resources.MenuAboutProxyBridge;
    public string MenuCheckUpdates => Resources.Resources.MenuCheckUpdates;
    public string MenuProfile => Resources.Resources.MenuProfile;
    public string MenuNewProfile => Resources.Resources.MenuNewProfile;
    public string MenuRenameProfile => Resources.Resources.MenuRenameProfile;
    public string MenuDeleteProfile => Resources.Resources.MenuDeleteProfile;
    public string MenuSwitchProfile => Resources.Resources.MenuSwitchProfile;
    public string MenuImportProfile => Resources.Resources.MenuImportProfile;
    public string MenuExportProfile => Resources.Resources.MenuExportProfile;

    // Tabs
    public string TabConnections => Resources.Resources.TabConnections;
    public string TabActivity => Resources.Resources.TabActivity;

    // Buttons
    public string ButtonClearAllConnections => Resources.Resources.ButtonClearAllConnections;
    public string ButtonClearAllLogs => Resources.Resources.ButtonClearAllLogs;
    public string ButtonSave => Resources.Resources.ButtonSave;
    public string ButtonCancel => Resources.Resources.ButtonCancel;
    public string ButtonClose => Resources.Resources.ButtonClose;
    public string ButtonAdd => Resources.Resources.ButtonAdd;
    public string ButtonEdit => Resources.Resources.ButtonEdit;
    public string ButtonDelete => Resources.Resources.ButtonDelete;

    // Search Placeholders
    public string SearchConnectionsPlaceholder => Resources.Resources.SearchConnectionsPlaceholder;
    public string SearchActivityPlaceholder => Resources.Resources.SearchActivityPlaceholder;

    // Log Messages
    public string LogInitialized => Resources.Resources.LogInitialized;
    public string LogServiceStarted => Resources.Resources.LogServiceStarted;
    public string LogServiceStartFailed => Resources.Resources.LogServiceStartFailed;
    public string LogRestoredProxySettings => Resources.Resources.LogRestoredProxySettings;
    public string LogRestoredRules => Resources.Resources.LogRestoredRules;
    public string LogConfigSaved => Resources.Resources.LogConfigSaved;
    public string LogConfigLoadFailed => Resources.Resources.LogConfigLoadFailed;
    public string LogConfigSaveFailed => Resources.Resources.LogConfigSaveFailed;
    public string LogProxySettingsSaved => Resources.Resources.LogProxySettingsSaved;
    public string LogProxySettingsFailed => Resources.Resources.LogProxySettingsFailed;
    public string LogRuleAdded => Resources.Resources.LogRuleAdded;
    public string LogRuleAddFailed => Resources.Resources.LogRuleAddFailed;
    public string LogLanguageChanged => Resources.Resources.LogLanguageChanged;
    public string LogWithAuth => Resources.Resources.LogWithAuth;

    // Proxy Settings Window
    public string ProxySettingsTitle => Resources.Resources.WindowProxySettings;
    public string ProxyTypeLabel => Resources.Resources.LabelProxyType;
    public string ProxyIpLabel => Resources.Resources.LabelProxyIp;
    public string ProxyPortLabel => Resources.Resources.LabelProxyPort;
    public string UsernameLabel => Resources.Resources.LabelUsername;
    public string PasswordLabel => Resources.Resources.LabelPassword;
    public string RequiredFieldsNote => Resources.Resources.LabelRequiredFields;
    public string TestConnectionTitle => Resources.Resources.LabelTestConnection;
    public string TestConnectionButton => Resources.Resources.ButtonTestProxy;
    public string TargetHostLabel => Resources.Resources.LabelTargetHost;
    public string PortLabel => Resources.Resources.LabelPort;
    public string StartTestButton => Resources.Resources.ButtonStartTest;
    public string OutputLabel => Resources.Resources.LabelOutput;
    public string SaveChangesButton => Resources.Resources.ButtonSaveChanges;
    public string ProxyIpPlaceholder => Resources.Resources.PlaceholderIpAddress;
    public string ProxyPortPlaceholder => Resources.Resources.PlaceholderPort;
    public string UsernamePlaceholder => Resources.Resources.PlaceholderNoAuth;
    public string PasswordPlaceholder => Resources.Resources.PlaceholderNoAuth;

    // Proxy Rules Window
    public string ProxyRulesTitle => Resources.Resources.WindowProxyRules;
    public string EnabledLabel => Resources.Resources.LabelEnabled;
    public string ActionsLabel => Resources.Resources.LabelActions;
    public string SRLabel => Resources.Resources.LabelSR;
    public string ProcessLabel => Resources.Resources.LabelProcess;
    public string TargetHostsLabel => Resources.Resources.LabelTargetHosts;
    public string TargetPortsLabel => Resources.Resources.LabelTargetPorts;
    public string ProtocolLabel => Resources.Resources.LabelProtocol;
    public string ActionLabel => Resources.Resources.LabelAction;
    public string ApplicationsLabel => Resources.Resources.LabelApplications;
    public string BrowseButton => Resources.Resources.ButtonBrowse;
    public string ExampleApplications => Resources.Resources.ExampleApplications;
    public string ExampleTargetHosts => Resources.Resources.ExampleTargetHosts;
    public string ExampleTargetPorts => Resources.Resources.ExampleTargetPorts;
    public string ProtocolTCP => Resources.Resources.ProtocolTCP;
    public string ProtocolUDP => Resources.Resources.ProtocolUDP;
    public string ProtocolBoth => Resources.Resources.ProtocolBoth;
    public string UDPProxyNote => Resources.Resources.UDPProxyNote;
    public string ActionProxy => Resources.Resources.ActionProxy;
    public string ActionDirect => Resources.Resources.ActionDirect;
    public string ActionBlock => Resources.Resources.ActionBlock;
    public string SaveRuleButton => Resources.Resources.ButtonSaveRule;

    // XRay Menu
    public string MenuXRaySettings  => Resources.Resources.MenuXRaySettings;
    public string MenuStartXRay     => Resources.Resources.MenuStartXRay;
    public string MenuStopXRay      => Resources.Resources.MenuStopXRay;

    // XRay Settings Window
    public string WindowXRaySettings    => Resources.Resources.WindowXRaySettings;
    public string XRaySubtitle          => Resources.Resources.XRaySubtitle;
    public string ButtonSaveSettings    => Resources.Resources.ButtonSaveSettings;
    public string XRaySectionImport     => Resources.Resources.XRaySectionImport;
    public string XRayImportHint        => Resources.Resources.XRayImportHint;
    public string ButtonImport          => Resources.Resources.ButtonImport;
    public string XRaySectionServer     => Resources.Resources.XRaySectionServer;
    public string LabelServerAddress    => Resources.Resources.LabelServerAddress;
    public string PlaceholderServerAddress => Resources.Resources.PlaceholderServerAddress;
    public string XRaySectionAuth       => Resources.Resources.XRaySectionAuth;
    public string LabelFlow             => Resources.Resources.LabelFlow;
    public string LabelFullConeUdp      => Resources.Resources.LabelFullConeUdp;
    public string TooltipFullConeUdp    => Resources.Resources.TooltipFullConeUdp;
    public string LabelXudp             => Resources.Resources.LabelXudp;
    public string LabelXudp443          => Resources.Resources.LabelXudp443;
    public string Xudp443Allow          => Resources.Resources.Xudp443Allow;
    public string Xudp443Skip           => Resources.Resources.Xudp443Skip;
    public string Xudp443Reject         => Resources.Resources.Xudp443Reject;
    public string XRaySectionReality    => Resources.Resources.XRaySectionReality;
    public string LabelSni              => Resources.Resources.LabelSni;
    public string PlaceholderSni        => Resources.Resources.PlaceholderSni;
    public string XRaySniHint           => Resources.Resources.XRaySniHint;
    public string LabelFingerprint      => Resources.Resources.LabelFingerprint;
    public string LabelPublicKey        => Resources.Resources.LabelPublicKey;
    public string PlaceholderPublicKey  => Resources.Resources.PlaceholderPublicKey;
    public string LabelShortId          => Resources.Resources.LabelShortId;
    public string LabelSpiderX          => Resources.Resources.LabelSpiderX;
    public string PlaceholderOptional   => Resources.Resources.PlaceholderOptional;
    public string XRaySectionInbounds   => Resources.Resources.XRaySectionInbounds;
    public string LabelSocks5Port       => Resources.Resources.LabelSocks5Port;
    public string LabelHttpPort         => Resources.Resources.LabelHttpPort;
    public string XRayInboundsHint      => Resources.Resources.XRayInboundsHint;
    public string XRaySectionBinary     => Resources.Resources.XRaySectionBinary;
    public string LabelXRayPath         => Resources.Resources.LabelXRayPath;
    public string PlaceholderXRayPath   => Resources.Resources.PlaceholderXRayPath;
    public string XRayPathHint          => Resources.Resources.XRayPathHint;
    public string XRaySectionAutoStart  => Resources.Resources.XRaySectionAutoStart;
    public string XRayAutoStartLabel    => Resources.Resources.XRayAutoStartLabel;
    public string XRayAutoStartHint     => Resources.Resources.XRayAutoStartHint;

    // XRay Download Window
    public string XRayDownloadTitle    => Resources.Resources.XRayDownloadTitle;
    public string XRayDownloadSubtitle => Resources.Resources.XRayDownloadSubtitle;
    public string ButtonRetry          => Resources.Resources.ButtonRetry;
}
