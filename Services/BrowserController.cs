using System;
using System.Threading.Tasks;
using LiveCaptionViewer.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LiveCaptionViewer.Services;

/// <summary>
/// Thin wrapper around a <see cref="WebView2"/> control that owns navigation
/// state, URL normalization, and lifecycle events.
/// </summary>
public sealed class BrowserController
{
    private readonly WebView2 _webView;
    private readonly ProxySettings _proxySettings;
    private bool _initialized;

    public event Action<string>? AddressChanged;
    public event Action? NavigationStateChanged;
    public event Action<Exception>? InitializationFailed;

    public BrowserController(WebView2 webView, ProxySettings? proxySettings = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _proxySettings = proxySettings ?? ProxySettingsService.Load();
    }

    public bool IsInitialized => _initialized;

    public bool CanGoBack =>
        _initialized && _webView.CoreWebView2 is { } core && core.CanGoBack;

    public bool CanGoForward =>
        _initialized && _webView.CoreWebView2 is { } core && core.CanGoForward;

    public async Task InitializeAsync(string? homepage = null)
    {
        try
        {
            if (!WebView2EnvironmentFactory.IsRuntimeInstalled())
            {
                throw new InvalidOperationException(
                    "WebView2 Runtime is not installed. Install the Evergreen Runtime from " +
                    "https://developer.microsoft.com/microsoft-edge/webview2/");
            }

            CoreWebView2Environment env =
                await WebView2EnvironmentFactory.CreateAsync(_proxySettings);

            _webView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = WebView2EnvironmentFactory.UserDataFolder
            };

            await _webView.EnsureCoreWebView2Async(env);

            if (_webView.CoreWebView2 is null)
                throw new InvalidOperationException("WebView2 failed to initialize.");

            try
            {
                _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            catch { /* not all WebView2 versions allow runtime change */ }

            _initialized = true;

            _webView.CoreWebView2.HistoryChanged += (_, _) =>
                NavigationStateChanged?.Invoke();

            _webView.CoreWebView2.SourceChanged += (_, _) =>
                AddressChanged?.Invoke(_webView.CoreWebView2.Source ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(homepage))
                Navigate(homepage);
        }
        catch (Exception ex)
        {
            _initialized = false;
            InitializationFailed?.Invoke(ex);
        }
    }

    public void Navigate(string? input)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(input)) return;
        _webView.CoreWebView2.Navigate(NormalizeUrl(input));
    }

    public bool TryGoBack()
    {
        if (!CanGoBack) return false;
        _webView.CoreWebView2.GoBack();
        return true;
    }

    public bool TryGoForward()
    {
        if (!CanGoForward) return false;
        _webView.CoreWebView2.GoForward();
        return true;
    }

    public void Refresh()
    {
        if (!_initialized) return;
        _webView.CoreWebView2.Reload();
    }

    internal static string NormalizeUrl(string input)
    {
        string trimmed = input.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        return "https://" + trimmed;
    }
}
