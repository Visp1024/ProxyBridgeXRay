using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

public static class XRayDownloadService
{
    private static string BuildDownloadUrl()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86   => "32",
            Architecture.Arm64 => "arm64-v8a",
            _                  => "64",
        };
        return $"https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-{arch}.zip";
    }

    private static string GetInstallDirectory()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        // Prefer the app directory; fall back to AppData if it's read-only
        try
        {
            var probe = Path.Combine(appDir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return appDir;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProxyBridgeXRay");
        }
    }

    /// <summary>
    /// Downloads and extracts xray.exe. Reports (percent 0-100, statusText).
    /// Returns the full path to the extracted xray.exe.
    /// </summary>
    public static async Task<string> DownloadAsync(
        IProgress<(int Percent, string Status)> progress,
        CancellationToken ct)
    {
        var url     = BuildDownloadUrl();
        var tempZip = Path.Combine(Path.GetTempPath(), $"xray-dl-{Guid.NewGuid():N}.zip");
        var destDir = GetInstallDirectory();

        Directory.CreateDirectory(destDir);

        var destExe = Path.Combine(destDir, "xray.exe");

        try
        {
            progress.Report((0, "Connecting to GitHub…"));

            using var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            });
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyBridge/1.0");
            client.Timeout = TimeSpan.FromMinutes(5);

            using var response = await client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            progress.Report((3, "Downloading xray-core…"));

            // Download phase — FileStream is closed at the end of this block
            // so the zip file is fully released before extraction begins.
            {
                await using var src  = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = new FileStream(tempZip,
                    FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                var buf   = new byte[65536];
                long done = 0;
                int  read;

                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;

                    if (total > 0)
                    {
                        var pct    = (int)(3 + 87 * done / total);
                        var doneMb = done  / 1_048_576.0;
                        var totMb  = total / 1_048_576.0;
                        progress.Report((pct, $"Downloading… {doneMb:F1} MB / {totMb:F1} MB"));
                    }
                }

                await dest.FlushAsync(ct);
            } // dest and src are fully closed/disposed here

            progress.Report((90, "Extracting xray.exe…"));

            // Open zip only after the write stream is closed
            using var archive = ZipFile.OpenRead(tempZip);

            // xray releases have a flat zip — xray.exe is at the root
            var entry = archive.GetEntry("xray.exe")
                     ?? throw new InvalidOperationException(
                            "xray.exe not found in archive. " +
                            "The release layout may have changed — " +
                            "please download xray manually from github.com/XTLS/Xray-core.");

            entry.ExtractToFile(destExe, overwrite: true);

            progress.Report((100, "Done!"));
            return destExe;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
