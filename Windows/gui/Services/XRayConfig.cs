namespace ProxyBridge.GUI.Services;

/// <summary>
/// VLESS + REALITY tunnel configuration for the bundled XRay-core process.
/// Stored at application level inside <see cref="AppSettings"/> (settings.json),
/// independently of proxy profiles.
/// </summary>
public class XRayConfig
{
    public string ServerAddress { get; set; } = "";
    public string ServerPort    { get; set; } = "443";
    public string Uuid          { get; set; } = "";
    public string Flow          { get; set; } = "xtls-rprx-vision";
    public string Sni           { get; set; } = "";
    public string Fingerprint   { get; set; } = "chrome";
    public string PublicKey     { get; set; } = "";
    public string ShortId       { get; set; } = "";
    public string SpiderX       { get; set; } = "";
    public string LocalPort     { get; set; } = "10808";
    public string HttpPort      { get; set; } = "10809";
    public string XRayPath      { get; set; } = "";
    public bool   AutoStartXRay { get; set; } = false;
    public bool   XudpEnabled   { get; set; } = true;   // XUDP mux (Full Cone UDP). Old settings.json without the field -> true.
}
