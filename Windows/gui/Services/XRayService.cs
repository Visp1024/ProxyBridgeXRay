using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProxyBridge.GUI.Services;

public class XRayService : IDisposable
{
    private Process? _process;
    private string _configFilePath = "";
    private readonly object _lock = new();
    private bool _isRunning;

    public event Action<string>? LogReceived;
    public event Action? Started;
    public event Action<int>? Stopped;

    public bool IsRunning => _isRunning;

    public bool Start(XRayConfig config)
    {
        lock (_lock)
        {
            if (_isRunning) return true;

            try
            {
                if (!int.TryParse(config.LocalPort, out int localPort)) localPort = 10808;
                if (!int.TryParse(config.HttpPort,  out int httpPort))  httpPort  = 10809;

                // Если оба inbound выключены, на всякий случай включаем SOCKS, чтобы XRay было что слушать.
                bool enableSocks = config.EnableSocks;
                bool enableHttp  = config.EnableHttp;
                if (!enableSocks && !enableHttp) enableSocks = true;

                // Проверяем занятость только тех портов, которые реально будут заняты XRay'ем.
                bool socksBlocked = enableSocks && !IsPortAvailable(localPort);
                bool httpBlocked  = enableHttp  && !IsPortAvailable(httpPort);

                if (socksBlocked || httpBlocked)
                {
                    var blocked = socksBlocked && httpBlocked
                        ? $"{localPort} and {httpPort}"
                        : socksBlocked ? $"{localPort}" : $"{httpPort}";

                    LogReceived?.Invoke($"XRay: port(s) {blocked} in use — attempting to free them…");
                    KillLingeringXRay();
                    Thread.Sleep(600);

                    socksBlocked = enableSocks && !IsPortAvailable(localPort);
                    httpBlocked  = enableHttp  && !IsPortAvailable(httpPort);

                    if (socksBlocked || httpBlocked)
                    {
                        var still = socksBlocked && httpBlocked
                            ? $"{localPort} and {httpPort}"
                            : socksBlocked ? $"{localPort}" : $"{httpPort}";

                        LogReceived?.Invoke(
                            $"XRay ERROR: port(s) {still} still occupied by another process. " +
                            $"Free the port(s) or change them in XRay settings.");
                        return false;
                    }

                    LogReceived?.Invoke("XRay: ports are now free.");
                }

                _configFilePath = Path.Combine(Path.GetTempPath(), "proxybridge_xray.json");
                File.WriteAllText(_configFilePath, GenerateXRayConfig(config), new UTF8Encoding(false));

                var xrayPath = ResolveXRayPath(config.XRayPath);
                if (xrayPath == null)
                {
                    LogReceived?.Invoke("XRay ERROR: xray executable not found. Configure path in XRay settings.");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = xrayPath,
                    Arguments = $"run -c \"{_configFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) LogReceived?.Invoke($"XRay: {e.Data}");
                };
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) LogReceived?.Invoke($"XRay: {e.Data}");
                };
                _process.Exited += (s, e) =>
                {
                    var code = _process?.ExitCode ?? -1;
                    lock (_lock)
                    {
                        _isRunning = false;
                        _process?.Dispose();
                        _process = null;
                    }
                    LogReceived?.Invoke($"XRay process exited (code: {code})");
                    Stopped?.Invoke(code);
                };

                if (!_process.Start())
                {
                    LogReceived?.Invoke("XRay ERROR: Failed to start process.");
                    _process.Dispose();
                    _process = null;
                    return false;
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _isRunning = true;

                var inboundSummary = (enableSocks, enableHttp) switch
                {
                    (true, true)  => $"SOCKS5 127.0.0.1:{config.LocalPort}, HTTP 127.0.0.1:{config.HttpPort}",
                    (true, false) => $"SOCKS5 127.0.0.1:{config.LocalPort}",
                    (false, true) => $"HTTP 127.0.0.1:{config.HttpPort}",
                    _             => "(no inbounds)"
                };
                LogReceived?.Invoke($"XRay started (PID: {_process.Id}), {inboundSummary}");
                Started?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"XRay ERROR: {ex.Message}");
                _process?.Dispose();
                _process = null;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning || _process == null) return;

            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }

            _isRunning = false;
            LogReceived?.Invoke("XRay stopped.");
        }
    }

    public static string? FindXRayExecutable(string configuredPath) =>
        ResolveXRayPath(configuredPath);

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void KillLingeringXRay()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("xray"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    private static string? ResolveXRayPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            foreach (var name in new[] { "xray.exe", "xray" })
            {
                var full = Path.Combine(trimmed, name);
                if (File.Exists(full)) return full;
            }
        }

        // Check alongside the app executable
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var localExe = Path.Combine(appDir, "xray.exe");
        if (File.Exists(localExe)) return localExe;
        var localBin = Path.Combine(appDir, "xray");
        if (File.Exists(localBin)) return localBin;

        return null;
    }

    private static string GenerateXRayConfig(XRayConfig cfg)
    {
        static string Esc(string s) => s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");

        if (!int.TryParse(cfg.LocalPort,  out int localPort))  localPort  = 10808;
        if (!int.TryParse(cfg.HttpPort,   out int httpPort))   httpPort   = 10809;
        if (!int.TryParse(cfg.ServerPort, out int serverPort)) serverPort = 443;

        bool enableSocks = cfg.EnableSocks;
        bool enableHttp  = cfg.EnableHttp;
        if (!enableSocks && !enableHttp) enableSocks = true;

        // Sniffing подменяет dest IP на реальный SNI/Host из TLS/HTTP — без этого
        // vless-сервер получает CONNECT на сырой IP и часто не может его роутить.
        const string sniffing =
            @"""sniffing"": { ""enabled"": true, ""destOverride"": [""http"", ""tls""], ""routeOnly"": false }";

        var inbounds = new System.Collections.Generic.List<string>(2);
        if (enableSocks)
        {
            inbounds.Add(
$@"    {{
      ""tag"": ""socks-in"",
      ""protocol"": ""socks"",
      ""listen"": ""127.0.0.1"",
      ""port"": {localPort},
      ""settings"": {{ ""auth"": ""noauth"", ""udp"": true }},
      {sniffing}
    }}");
        }
        if (enableHttp)
        {
            inbounds.Add(
$@"    {{
      ""tag"": ""http-in"",
      ""protocol"": ""http"",
      ""listen"": ""127.0.0.1"",
      ""port"": {httpPort},
      ""settings"": {{ ""allowTransparent"": false }},
      {sniffing}
    }}");
        }
        var inboundsJson = string.Join(",\n", inbounds);

        return
$@"{{
  ""log"": {{ ""loglevel"": ""warning"" }},
  ""inbounds"": [
{inboundsJson}
  ],
  ""outbounds"": [
    {{
      ""tag"": ""vless-out"",
      ""protocol"": ""vless"",
      ""settings"": {{
        ""vnext"": [
          {{
            ""address"": ""{Esc(cfg.ServerAddress)}"",
            ""port"": {serverPort},
            ""users"": [
              {{
                ""id"": ""{Esc(cfg.Uuid)}"",
                ""flow"": ""{Esc(cfg.Flow)}"",
                ""encryption"": ""none""
              }}
            ]
          }}
        ]
      }},
      ""streamSettings"": {{
        ""network"": ""tcp"",
        ""security"": ""reality"",
        ""realitySettings"": {{
          ""serverName"": ""{Esc(cfg.Sni)}"",
          ""fingerprint"": ""{Esc(cfg.Fingerprint)}"",
          ""publicKey"": ""{Esc(cfg.PublicKey)}"",
          ""shortId"": ""{Esc(cfg.ShortId)}"",
          ""spiderX"": ""{Esc(cfg.SpiderX)}""
        }}
      }}
    }}
  ]
}}";
    }

    public void Dispose()
    {
        Stop();
        try
        {
            if (!string.IsNullOrEmpty(_configFilePath) && File.Exists(_configFilePath))
                File.Delete(_configFilePath);
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
