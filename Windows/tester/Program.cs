using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace RoutingTester;

internal static class Program
{
    private const string DefaultUrl   = "https://api.ipify.org/?format=json";
    private const string DefaultProxy = "127.0.0.1:12334";
    private const int    TimeoutMs    = 15000;

    private static async Task<int> Main(string[] args)
    {
        string mode    = "direct";
        string url     = DefaultUrl;
        string proxy   = DefaultProxy;
        int    rounds  = 1;
        int    delayMs = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--direct":           mode = "direct"; break;
                case "--via-proxy":        mode = "via-proxy"; break;
                case "--url":   if (i+1 < args.Length) url   = args[++i]; break;
                case "--proxy": if (i+1 < args.Length) proxy = args[++i]; break;
                case "--rounds": if (i+1 < args.Length) int.TryParse(args[++i], out rounds); break;
                case "--delay":  if (i+1 < args.Length) int.TryParse(args[++i], out delayMs); break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
            }
        }

        Console.OutputEncoding = Encoding.UTF8;
        Header(mode, url, proxy, rounds);

        int failures = 0;
        for (int n = 1; n <= rounds; n++)
        {
            if (rounds > 1) Console.WriteLine($"\n──── round {n}/{rounds} ────");
            bool ok = mode == "direct"
                ? await RunDirectAsync(url)
                : await RunViaProxyAsync(url, proxy);
            if (!ok) failures++;
            if (n < rounds && delayMs > 0) await Task.Delay(delayMs);
        }

        Console.WriteLine();
        Console.ForegroundColor = failures == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(failures == 0
            ? $"OK — {rounds}/{rounds} rounds succeeded"
            : $"FAIL — {failures}/{rounds} rounds failed");
        Console.ResetColor();
        return failures == 0 ? 0 : 1;
    }

    private static void Header(string mode, string url, string proxy, int rounds)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("RoutingTester");
        Console.ResetColor();
        Console.WriteLine($"  PID    : {Environment.ProcessId}");
        Console.WriteLine($"  Mode   : {mode}");
        Console.WriteLine($"  URL    : {url}");
        if (mode == "via-proxy") Console.WriteLine($"  Proxy  : {proxy}");
        Console.WriteLine($"  Rounds : {rounds}");
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        RoutingTester — диагностика маршрутизации ProxyBridge.

        Запуск:
          RoutingTester.exe [--direct | --via-proxy] [options]

        Режимы:
          --direct       (по умолчанию) делает HTTPS-запрос обычным сокетом.
                         Если ProxyBridge перехватывает этот процесс правилом
                         PROXY — трафик пойдёт через WinDivert → upstream proxy.
          --via-proxy    подключается напрямую к HTTP-прокси (CONNECT)
                         минуя WinDivert. Если этот режим работает, а --direct
                         нет — проблема в перехвате, а не в самом прокси.

        Опции:
          --url   <url>      целевой URL  (default https://api.ipify.org/?format=json)
          --proxy <ip:port>  адрес прокси (default 127.0.0.1:12334)
          --rounds <N>       число повторов (default 1)
          --delay  <ms>      пауза между раундами (default 0)
        """);

    // ────────────────────────── DIRECT ──────────────────────────
    private static async Task<bool> RunDirectAsync(string url)
    {
        var uri = new Uri(url);
        var totalSw = Stopwatch.StartNew();
        try
        {
            var dnsSw = Stopwatch.StartNew();
            var addrs = await Dns.GetHostAddressesAsync(uri.Host);
            dnsSw.Stop();
            Stage("DNS",      $"{uri.Host} → {string.Join(", ", (IEnumerable<IPAddress>)addrs)}", dnsSw.ElapsedMilliseconds);

            var connectSw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(addrs[0], uri.Port).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            connectSw.Stop();
            Stage("TCP",      $"{addrs[0]}:{uri.Port} ← local {tcp.Client.LocalEndPoint}", connectSw.ElapsedMilliseconds);

            Stream stream = tcp.GetStream();
            if (uri.Scheme == "https")
            {
                var tlsSw = Stopwatch.StartNew();
                var ssl = new SslStream(stream, false, (_,_,_,_) => true);
                await ssl.AuthenticateAsClientAsync(uri.Host).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                tlsSw.Stop();
                Stage("TLS",  $"{ssl.SslProtocol}, cipher {ssl.NegotiatedCipherSuite}", tlsSw.ElapsedMilliseconds);
                stream = ssl;
            }

            return await DoHttpAsync(stream, uri);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
        finally
        {
            totalSw.Stop();
            Console.WriteLine($"  Total elapsed: {totalSw.ElapsedMilliseconds} ms");
        }
    }

    // ────────────────────────── VIA-PROXY ──────────────────────────
    private static async Task<bool> RunViaProxyAsync(string url, string proxyHostPort)
    {
        var uri = new Uri(url);
        var totalSw = Stopwatch.StartNew();
        try
        {
            var (proxyHost, proxyPort) = ParseHostPort(proxyHostPort);

            var connectSw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(proxyHost, proxyPort).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            connectSw.Stop();
            Stage("TCP",  $"connected to proxy {proxyHost}:{proxyPort} ← local {tcp.Client.LocalEndPoint}", connectSw.ElapsedMilliseconds);

            var stream = tcp.GetStream();

            var connectReq = $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nProxy-Connection: keep-alive\r\n\r\n";
            var connectBytes = Encoding.ASCII.GetBytes(connectReq);
            var connSw = Stopwatch.StartNew();
            await stream.WriteAsync(connectBytes).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var connResp = await ReadHttpHeadAsync(stream).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            connSw.Stop();
            var firstLine = connResp.Split('\n')[0].Trim();
            Stage("CONNECT", firstLine, connSw.ElapsedMilliseconds);
            if (!firstLine.Contains(" 200 "))
            {
                Console.WriteLine($"  Proxy response head:\n    {connResp.Replace("\n", "\n    ").TrimEnd()}");
                return false;
            }

            Stream tunneled = stream;
            if (uri.Scheme == "https")
            {
                var tlsSw = Stopwatch.StartNew();
                var ssl = new SslStream(tunneled, false, (_,_,_,_) => true);
                await ssl.AuthenticateAsClientAsync(uri.Host).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                tlsSw.Stop();
                Stage("TLS",  $"{ssl.SslProtocol}, cipher {ssl.NegotiatedCipherSuite}", tlsSw.ElapsedMilliseconds);
                tunneled = ssl;
            }

            return await DoHttpAsync(tunneled, uri);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
        finally
        {
            totalSw.Stop();
            Console.WriteLine($"  Total elapsed: {totalSw.ElapsedMilliseconds} ms");
        }
    }

    // ────────────────────────── HTTP request over an open stream ──────────────────────────
    private static async Task<bool> DoHttpAsync(Stream stream, Uri uri)
    {
        var req = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}\r\nUser-Agent: RoutingTester/1.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        var reqBytes = Encoding.ASCII.GetBytes(req);
        var httpSw = Stopwatch.StartNew();
        await stream.WriteAsync(reqBytes).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var head = await ReadHttpHeadAsync(stream).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        httpSw.Stop();
        var first = head.Split('\n')[0].Trim();
        Stage("HTTP", first, httpSw.ElapsedMilliseconds);

        var bodySw = Stopwatch.StartNew();
        var body   = await ReadRestAsync(stream).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        bodySw.Stop();
        var preview = body.Length > 200 ? body[..200] + "…" : body;
        Stage("BODY", $"{body.Length} bytes — {preview.Replace("\r","").Replace("\n"," ")}", bodySw.ElapsedMilliseconds);

        return first.Contains(" 200 ");
    }

    // ────────────────────────── stream helpers ──────────────────────────
    private static async Task<string> ReadHttpHeadAsync(Stream s)
    {
        var sb = new StringBuilder(512);
        var buf = new byte[1];
        int lastFour = 0;
        while (true)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) break;
            sb.Append((char)buf[0]);
            lastFour = ((lastFour << 8) | buf[0]) & 0xFFFFFFF;
            // detect \r\n\r\n
            if (lastFour == 0x0D0A0D0A) break;
        }
        return sb.ToString();
    }

    private static async Task<string> ReadRestAsync(Stream s)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(buf);
            if (n == 0) break;
            ms.Write(buf, 0, n);
            if (ms.Length > 64 * 1024) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static (string host, int port) ParseHostPort(string s)
    {
        var idx = s.LastIndexOf(':');
        if (idx <= 0) throw new ArgumentException($"Bad host:port — {s}");
        return (s[..idx], int.Parse(s[(idx+1)..]));
    }

    private static void Stage(string tag, string detail, long ms)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  [{tag,-7}] ");
        Console.ResetColor();
        Console.Write(detail);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"   ({ms} ms)");
        Console.ResetColor();
    }

    private static void Fail(Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [FAIL   ] {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Console.WriteLine($"            → {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Console.ResetColor();
    }
}
