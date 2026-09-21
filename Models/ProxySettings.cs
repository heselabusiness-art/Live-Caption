namespace LiveCaptionViewer.Models;

/// <summary>
/// HTTP/HTTPS/SOCKS proxy configuration for the embedded WebView2 browser.
/// </summary>
public sealed class ProxySettings
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "45.80.96.62";

    public int Port { get; set; } = 29842;

    /// <summary>http, https, socks4, or socks5</summary>
    public string Scheme { get; set; } = "http";

    public string BypassList { get; set; } =
        "localhost;127.0.0.1;192.168.8.0;192.168.100.43;<local>";

    public string ProxyServer =>
        $"{Scheme}://{Host.Trim()}:{Port}";

    public string AppendToBrowserArguments(string baseArguments)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Host) || Port <= 0)
            return baseArguments;

        string bypass = string.IsNullOrWhiteSpace(BypassList)
            ? "<local>"
            : BypassList.Trim();

        return baseArguments +
               $" --proxy-server={ProxyServer}" +
               $" --proxy-bypass-list={bypass}";
    }
}
