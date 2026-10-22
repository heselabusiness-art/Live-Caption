using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionViewer.Infrastructure;
using LiveCaptionViewer.Models;
using LiveCaptionViewer.Services;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace LiveCaptionViewer;

/// <summary>Embedded WebView2 browser: navigation, proxy settings, ChatGPT/Claude services.</summary>
public partial class MainWindow
{
    // ─── Browser services ────────────────────────────────────────────────────
    private BrowserController? _browser;
    private ChatGptWebAutomationService? _chatGpt;
    private ClaudeWebAutomationService? _claude;
    private ProxySettings _proxySettings = ProxySettingsService.Load();

    private async Task InitializeBrowserAsync(string? homepage)
    {
        _proxySettings = ProxySettingsService.Load();

        var controller = new BrowserController(Browser, _proxySettings);
        controller.AddressChanged += url =>
            Dispatcher.Invoke(() =>
            {
                if (!AddressBar.IsKeyboardFocused) AddressBar.Text = url;
            });
        controller.NavigationStateChanged += () =>
            Dispatcher.Invoke(UpdateNavButtons);
        controller.InitializationFailed += ex =>
            Dispatcher.Invoke(() => OnBrowserInitFailed(ex));

        _browser = controller;
        await controller.InitializeAsync(homepage);

        if (controller.IsInitialized && Browser.CoreWebView2 is not null)
        {
            BrowserOverlay.Visibility = Visibility.Collapsed;
            AddressBar.IsEnabled = true;
            GoButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;

            _chatGpt = new ChatGptWebAutomationService(Browser.CoreWebView2);
            _claude  = new ClaudeWebAutomationService(Browser.CoreWebView2);
        }

        _effects?.ResetWebViewWindow();
        _ = _effects?.AttachWebViewAsync();
    }

    // =========================================================================
    // Proxy settings
    // =========================================================================

    private void LoadProxySettingsIntoUi()
    {
        _proxySettings = ProxySettingsService.Load();
        ProxyEnabledCheckBox.IsChecked = _proxySettings.Enabled;
        ProxyHostTextBox.Text = _proxySettings.Host;
        ProxyPortTextBox.Text = _proxySettings.Port.ToString();
        ProxyBypassTextBox.Text = _proxySettings.BypassList;
        SelectProxySchemeInUi(_proxySettings.Scheme);
        UpdateProxySettingsStatusLabel();
    }

    private ProxySettings ReadProxySettingsFromUi()
    {
        if (!int.TryParse(ProxyPortTextBox.Text.Trim(), out int port))
            port = 8080;

        return new ProxySettings
        {
            Enabled = ProxyEnabledCheckBox.IsChecked == true,
            Host = ProxyHostTextBox.Text.Trim(),
            Port = port,
            Scheme = GetSelectedProxySchemeFromUi(),
            BypassList = ProxyBypassTextBox.Text.Trim()
        };
    }

    private string GetSelectedProxySchemeFromUi()
    {
        string label = (ProxySchemeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)
            ?.Content?.ToString() ?? "HTTP";

        return label switch
        {
            "HTTPS"  => "https",
            "SOCKS4" => "socks4",
            "SOCKS5" => "socks5",
            _        => "http"
        };
    }

    private void SelectProxySchemeInUi(string scheme)
    {
        int index = scheme.ToLowerInvariant() switch
        {
            "https"  => 1,
            "socks4" => 2,
            "socks5" => 3,
            _        => 0
        };
        ProxySchemeComboBox.SelectedIndex = index;
    }

    private bool TrySaveProxySettingsFromUi(bool showErrors)
    {
        ProxySettings settings = ReadProxySettingsFromUi();
        if (settings.Enabled && string.IsNullOrWhiteSpace(settings.Host))
        {
            if (showErrors)
            {
                ProxySettingsStatusLabel.Foreground = System.Windows.Media.Brushes.OrangeRed;
                ProxySettingsStatusLabel.Text = "Enter a proxy IP or host when proxy is enabled.";
            }
            return false;
        }

        if (settings.Enabled && settings.Port <= 0)
        {
            if (showErrors)
            {
                ProxySettingsStatusLabel.Foreground = System.Windows.Media.Brushes.OrangeRed;
                ProxySettingsStatusLabel.Text = "Enter a valid proxy port.";
            }
            return false;
        }

        _proxySettings = settings;
        ProxySettingsService.Save(settings);
        UpdateProxySettingsStatusLabel();
        return true;
    }

    private void UpdateProxySettingsStatusLabel()
    {
        ProxySettingsStatusLabel.Foreground = System.Windows.Media.Brushes.LightGray;
        ProxySettingsStatusLabel.Text = _proxySettings.Enabled
            ? $"Saved. Active proxy: {_proxySettings.ProxyServer}"
            : "Saved. Proxy disabled — browser uses a direct connection.";
    }

    private void ProxySaveButton_Click(object sender, RoutedEventArgs e)
        => TrySaveProxySettingsFromUi(showErrors: true);

    private async void ProxyApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveProxySettingsFromUi(showErrors: true))
            return;

        ProxySettingsStatusLabel.Foreground = System.Windows.Media.Brushes.Goldenrod;
        ProxySettingsStatusLabel.Text = "Restarting app to apply proxy settings…";

        await Task.Delay(300);
        RestartApplication();
    }

    private static void RestartApplication()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            exe = System.Reflection.Assembly.GetExecutingAssembly().Location;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        });
        Application.Current.Shutdown();
    }

    // =========================================================================
    // Browser controls
    // =========================================================================

    private void BackButton_Click(object sender, RoutedEventArgs e)
        => _browser?.TryGoBack();

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
        => _browser?.TryGoForward();

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => _browser?.Refresh();

    private void GoButton_Click(object sender, RoutedEventArgs e)
        => NavigateToAddressBar();

    private void BookmarkGpt_Click(object sender, RoutedEventArgs e)
        => _browser?.Navigate("https://chatgpt.com");

    private void BookmarkClaude_Click(object sender, RoutedEventArgs e)
        => _browser?.Navigate("https://claude.ai");

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToAddressBar();
            e.Handled = true;
        }
    }

    private void NavigateToAddressBar()
    {
        if (_browser is null || !_browser.IsInitialized) return;
        _browser.Navigate(AddressBar.Text);
        Browser.Focus();
    }

    /// <summary>Focus WebView2 so ChatGPT/Claude accept automated send/read (no clipboard).</summary>
    private async Task FocusEmbeddedBrowserForAutomationAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            Activate();
            Browser.Focus();
            Keyboard.Focus(Browser);
        });
        await Task.Delay(120);
    }

    private void UpdateNavButtons()
    {
        if (_browser is null || !_browser.IsInitialized)
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            return;
        }
        BackButton.IsEnabled = _browser.CanGoBack;
        ForwardButton.IsEnabled = _browser.CanGoForward;
    }

    private void OnBrowserInitFailed(Exception ex)
    {
        AddressBar.Text = "WebView2 unavailable";
        AddressBar.IsEnabled = false;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        GoButton.IsEnabled = false;

        bool accessDenied = ex is UnauthorizedAccessException ||
            ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            ex.HResult == unchecked((int)0x80070005);

        string hint = accessDenied
            ? "WebView2 could not write its browser profile folder." + Environment.NewLine +
              Environment.NewLine +
              "Try running the app normally (not as Administrator), or delete any blocked folder at:" + Environment.NewLine +
              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptionViewer")
            : "WebView2 could not be initialized." + Environment.NewLine + Environment.NewLine +
              "Install the WebView2 Evergreen Runtime from:" + Environment.NewLine +
              "https://developer.microsoft.com/microsoft-edge/webview2/";

        BrowserOverlay.Text = hint + Environment.NewLine + Environment.NewLine + $"Details: {ex.Message}";
        BrowserOverlay.Visibility = Visibility.Visible;
    }
}
