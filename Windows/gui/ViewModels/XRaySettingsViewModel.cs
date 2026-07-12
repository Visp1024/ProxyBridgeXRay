using System;
using System.Collections.Generic;
using System.Windows.Input;
using ProxyBridge.GUI.Common;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class XRaySettingsViewModel : ViewModelBase
{
    private readonly Loc _loc = Loc.Instance;
    public Loc Loc => _loc;

    private string _serverAddress = "";
    private string _serverPort = "443";
    private string _uuid = "";
    private string _flow = "xtls-rprx-vision";
    private bool _xudpEnabled = true;
    private string _sni = "";
    private string _fingerprint = "chrome";
    private string _publicKey = "";
    private string _shortId = "";
    private string _spiderX = "";
    private string _localPort = "10808";
    private string _httpPort = "10809";
    private string _xRayPath = "";

    private string _serverAddressError = "";
    private string _uuidError = "";
    private string _publicKeyError = "";
    private string _localPortError = "";
    private string _httpPortError = "";

    private bool _autoStartXRay;

    private string _importUrl = "";
    private string _importMessage = "";
    private bool _importSuccess;

    private readonly Action<XRayConfig> _onSave;
    private readonly Action _onCancel;

    public string ServerAddress
    {
        get => _serverAddress;
        set { SetProperty(ref _serverAddress, value); ServerAddressError = ""; }
    }

    public string ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
    }

    public string Uuid
    {
        get => _uuid;
        set { SetProperty(ref _uuid, value); UuidError = ""; }
    }

    public string Flow
    {
        get => _flow;
        set => SetProperty(ref _flow, value);
    }

    public bool XudpEnabled
    {
        get => _xudpEnabled;
        set => SetProperty(ref _xudpEnabled, value);
    }

    public string Sni
    {
        get => _sni;
        set => SetProperty(ref _sni, value);
    }

    public string Fingerprint
    {
        get => _fingerprint;
        set => SetProperty(ref _fingerprint, value);
    }

    public string PublicKey
    {
        get => _publicKey;
        set { SetProperty(ref _publicKey, value); PublicKeyError = ""; }
    }

    public string ShortId
    {
        get => _shortId;
        set => SetProperty(ref _shortId, value);
    }

    public string SpiderX
    {
        get => _spiderX;
        set => SetProperty(ref _spiderX, value);
    }

    public string LocalPort
    {
        get => _localPort;
        set { SetProperty(ref _localPort, value); LocalPortError = ""; }
    }

    public string HttpPort
    {
        get => _httpPort;
        set { SetProperty(ref _httpPort, value); HttpPortError = ""; }
    }

    public string XRayPath
    {
        get => _xRayPath;
        set => SetProperty(ref _xRayPath, value);
    }

    public string ServerAddressError
    {
        get => _serverAddressError;
        set => SetProperty(ref _serverAddressError, value);
    }

    public string UuidError
    {
        get => _uuidError;
        set => SetProperty(ref _uuidError, value);
    }

    public string PublicKeyError
    {
        get => _publicKeyError;
        set => SetProperty(ref _publicKeyError, value);
    }

    public string LocalPortError
    {
        get => _localPortError;
        set => SetProperty(ref _localPortError, value);
    }

    public string HttpPortError
    {
        get => _httpPortError;
        set => SetProperty(ref _httpPortError, value);
    }

    public bool AutoStartXRay
    {
        get => _autoStartXRay;
        set => SetProperty(ref _autoStartXRay, value);
    }

    public string ImportUrl
    {
        get => _importUrl;
        set { SetProperty(ref _importUrl, value); ImportMessage = ""; }
    }

    public string ImportMessage
    {
        get => _importMessage;
        set
        {
            if (SetProperty(ref _importMessage, value))
            {
                OnPropertyChanged(nameof(ShowImportSuccess));
                OnPropertyChanged(nameof(ShowImportError));
            }
        }
    }

    public bool ImportSuccess
    {
        get => _importSuccess;
        set
        {
            if (SetProperty(ref _importSuccess, value))
            {
                OnPropertyChanged(nameof(ShowImportSuccess));
                OnPropertyChanged(nameof(ShowImportError));
            }
        }
    }

    public bool ShowImportSuccess => _importSuccess && !string.IsNullOrEmpty(_importMessage);
    public bool ShowImportError => !_importSuccess && !string.IsNullOrEmpty(_importMessage);

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ImportFromUrlCommand { get; }

    public XRaySettingsViewModel(XRayConfig initial, Action<XRayConfig> onSave, Action onCancel)
    {
        _onSave = onSave;
        _onCancel = onCancel;

        ServerAddress = initial.ServerAddress;
        ServerPort = initial.ServerPort;
        Uuid = initial.Uuid;
        Flow = initial.Flow;
        Sni = initial.Sni;
        Fingerprint = initial.Fingerprint;
        PublicKey = initial.PublicKey;
        ShortId = initial.ShortId;
        SpiderX = initial.SpiderX;
        LocalPort = initial.LocalPort;
        HttpPort  = initial.HttpPort;
        XRayPath  = initial.XRayPath;
        AutoStartXRay = initial.AutoStartXRay;
        XudpEnabled = initial.XudpEnabled;

        SaveCommand = new RelayCommand(() =>
        {
            bool valid = true;

            if (string.IsNullOrWhiteSpace(ServerAddress))
            {
                ServerAddressError = "Server address is required";
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(Uuid))
            {
                UuidError = "UUID is required";
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(PublicKey))
            {
                PublicKeyError = "Public key is required";
                valid = false;
            }

            if (!ushort.TryParse(LocalPort, out _))
            {
                LocalPortError = "Invalid port (1–65535)";
                valid = false;
            }

            if (!ushort.TryParse(HttpPort, out _))
            {
                HttpPortError = "Invalid port (1–65535)";
                valid = false;
            }

            if (valid && LocalPort.Trim() == HttpPort.Trim())
            {
                HttpPortError = "Must differ from SOCKS5 port";
                valid = false;
            }

            if (!valid) return;

            _onSave(new XRayConfig
            {
                ServerAddress = ServerAddress.Trim(),
                ServerPort = ServerPort.Trim(),
                Uuid = Uuid.Trim(),
                Flow = Flow.Trim(),
                Sni = Sni.Trim(),
                Fingerprint = Fingerprint.Trim(),
                PublicKey = PublicKey.Trim(),
                ShortId = ShortId.Trim(),
                SpiderX = SpiderX.Trim(),
                LocalPort = LocalPort.Trim(),
                HttpPort  = HttpPort.Trim(),
                XRayPath  = XRayPath.Trim(),
                AutoStartXRay = AutoStartXRay,
                XudpEnabled = XudpEnabled,
            });
        });

        CancelCommand = new RelayCommand(() => _onCancel());

        ImportFromUrlCommand = new RelayCommand(() =>
        {
            if (TryParseVlessUrl(ImportUrl.Trim(), out string err))
            {
                ImportSuccess = true;
                ImportMessage = "✓ Imported successfully";
            }
            else
            {
                ImportSuccess = false;
                ImportMessage = $"✗ {err}";
            }
        });
    }

    // Raises PropertyChanged for Flow and Fingerprint so code-behind can sync ComboBoxes.
    public void NotifyComboBoxProperties()
    {
        OnPropertyChanged(nameof(Flow));
        OnPropertyChanged(nameof(Fingerprint));
    }

    private bool TryParseVlessUrl(string url, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "URL is empty";
            return false;
        }

        if (!url.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            error = "URL must start with vless://";
            return false;
        }

        try
        {
            var uri = new Uri(url);

            var uuid = uri.UserInfo;
            if (string.IsNullOrEmpty(uuid))
            {
                error = "UUID not found in URL";
                return false;
            }

            var host = uri.Host;
            if (string.IsNullOrEmpty(host))
            {
                error = "Server address not found in URL";
                return false;
            }

            var port = uri.Port > 0 ? uri.Port.ToString() : "443";

            var queryParams = ParseQueryString(uri.Query.TrimStart('?'));

            var security = queryParams.GetValueOrDefault("security", "");
            if (!security.Equals("reality", StringComparison.OrdinalIgnoreCase))
            {
                error = string.IsNullOrEmpty(security)
                    ? "Missing security=reality parameter"
                    : $"Expected security=reality, got '{security}'";
                return false;
            }

            var pbk = queryParams.GetValueOrDefault("pbk", "");
            if (string.IsNullOrEmpty(pbk))
            {
                error = "Missing public key (pbk) in URL";
                return false;
            }

            ServerAddress = host;
            ServerPort = port;
            Uuid = uuid;
            Flow = queryParams.GetValueOrDefault("flow", "xtls-rprx-vision");
            Sni = queryParams.GetValueOrDefault("sni",
                  queryParams.GetValueOrDefault("serverName", ""));
            Fingerprint = queryParams.GetValueOrDefault("fp", "chrome");
            PublicKey = pbk;
            ShortId = queryParams.GetValueOrDefault("sid", "");
            SpiderX = queryParams.GetValueOrDefault("spx", "");

            return true;
        }
        catch (Exception ex)
        {
            error = $"Parse error: {ex.Message}";
            return false;
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..idx]);
            var val = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = val;
        }

        return result;
    }
}
