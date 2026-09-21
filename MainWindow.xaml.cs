using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveCaptionViewer.Models;
using LiveCaptionViewer.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;

namespace LiveCaptionViewer;

public partial class MainWindow : Window
{
    // ─── Win32 hotkeys ────────────────────────────────────────────────────────
    //   Home       → capture segment + always send behavioral prompt to AI
    //   Vol Up     → same as Home  (headset: WM_APPCOMMAND + LL keyboard hook)
    //   End        → capture segment + always send STAR prompt to AI
    //   Vol Down   → same as End   (headset: WM_APPCOMMAND + LL keyboard hook)
    //   PageUp     → capture segment, display only — do NOT send to AI
    //   Mute       → same as PageUp (headset: WM_APPCOMMAND + LL keyboard hook)
    //   PageDown   → send Reminder.txt to AI (rule-reset reminder)
    //   Insert     → Tech Interview: capture external browser screenshot
    //   Delete     → capture segment + always send live-coding prompt to AI
    //   Ctrl+W     → toggle click-through mode (passes mouse clicks to app below)
    private const int WM_HOTKEY      = 0x0312;
    private const int WM_APPCOMMAND  = 0x0319;
    private const int APPCOMMAND_VOLUME_UP   = 10;
    private const int APPCOMMAND_VOLUME_DOWN = 9;
    private const int APPCOMMAND_VOLUME_MUTE = 8;
    private const int HOTKEY_ID_CAPTURE        = 0xC001;
    private const int HOTKEY_ID_CLEAR_UP       = 0xC002;  // PageUp
    private const int HOTKEY_ID_STAR_SEND      = 0xC003;
    private const int HOTKEY_ID_TECH_CAPTURE   = 0xC004;
    private const int HOTKEY_ID_TECH_SEND      = 0xC005;
    private const int HOTKEY_ID_CLEAR_DOWN     = 0xC006;  // PageDown
    private const int HOTKEY_ID_CLICKTHROUGH   = 0xC007;  // Ctrl+W
    private const uint MOD_NONE      = 0x0000;
    private const uint MOD_ALT       = 0x0001;
    private const uint MOD_CTRL      = 0x0002;
    private const uint VK_HOME       = 0x24;
    private const uint VK_END        = 0x23;
    private const uint VK_PRIOR      = 0x21;   // PageUp
    private const uint VK_NEXT       = 0x22;   // PageDown
    private const uint VK_INSERT     = 0x2D;
    private const uint VK_DELETE     = 0x2E;
    private const uint VK_W          = 0x57;
    private const uint VK_Q          = 0x51;
    private const uint VK_VOLUME_UP   = 0xAF;
    private const uint VK_VOLUME_DOWN = 0xAE;
    private const uint VK_VOLUME_MUTE = 0xAD;

    // ─── Low-level keyboard hook (for headset volume buttons) ────────────────
    private const int WH_KEYBOARD_LL  = 13;
    private const int WM_KEYDOWN      = 0x0100;
    private const int WM_SYSKEYDOWN   = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
                                                   IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ─── DWM rounded corners (Windows 11) ────────────────────────────────────
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREF = 33;
    private const int DWMWCP_ROUND             = 2;

    // ─── WebView2 child-HWND transparency ────────────────────────────────────
    // AllowsTransparency="True" makes the WPF shell see-through via
    // UpdateLayeredWindow (confirmed working).  WebView2 lives in a separate
    // child HWND outside that surface.  We find it by class name and apply
    // WS_EX_LAYERED + LWA_ALPHA directly to it.
    //
    // Why class name ("Chrome_WidgetWin_0"):  Chromium always uses this class
    // for its root widget regardless of GPU/rendering flags.  The widget may
    // be nested inside an HwndHost wrapper, so walking only immediate children
    // by size (previous approach) can miss it.  EnumChildWindows searches the
    // full descendant tree; we stop at the FIRST match and never touch any of
    // Chrome's internal child HWNDs — that avoids the double-alpha stacking
    // that previously made the browser invisible.
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private const int  GWL_EXSTYLE     = -20;
    private const int  WS_EX_LAYERED   = 0x00080000;
    private const int  WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA       = 0x00000002;

    private bool _clickThroughEnabled;

    // Cached HWND of Chrome's root widget (Chrome_WidgetWin_0).
    private IntPtr _webViewHwnd = IntPtr.Zero;

    // ─── Services ────────────────────────────────────────────────────────────
    private readonly LiveCaptionsCaptureService _captureService = new();
    private readonly SegmentCaptureTracker _tracker = new();
    private BrowserController? _browser;
    private ChatGptWebAutomationService? _chatGpt;
    private ClaudeWebAutomationService? _claude;
    private ProxySettings _proxySettings = ProxySettingsService.Load();

    // ─── Caption state ───────────────────────────────────────────────────────
    private DateTime? _lastCaptureTime;

    // ─── Live caption bar state ───────────────────────────────────────────────
    /// <summary>Most-recent raw text from the capture service.</summary>
    private string _latestCaptionText = "";
    /// <summary>
    /// Snapshot of caption text at the last segment capture.
    /// null  = no capture has happened yet → show all text dimmed.
    /// ""    = capture happened but caption was empty/cleared → next text is all gold.
    /// "..." = normal → text up to this is dimmed, everything after is gold.
    /// </summary>
    private string? _captionBarBase = null;

    private static readonly System.Windows.Media.SolidColorBrush _captionBaseBrush
        = new(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x55));
    private static readonly System.Windows.Media.SolidColorBrush _captionPendingBrush
        = new(System.Windows.Media.Color.FromRgb(0xFF, 0xD0, 0x60));
    private static readonly System.Windows.Media.SolidColorBrush _captionWaitBrush
        = new(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x55));

    // ─── ChatGPT send guard ───────────────────────────────────────────────────
    // Prevents a second Home press from sending while the first is still in flight.
    private bool _isSending;

    // ─── Hotkey infra ────────────────────────────────────────────────────────
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _hotkeyRegistered;               // Home → capture + send
    private bool _clearUpHotkeyRegistered;        // PageUp → capture only
    private bool _clearDownHotkeyRegistered;      // PageDown → Reminder.txt
    private bool _starHotkeyRegistered;            // End → capture + STAR send
    private bool _techCaptureHotkeyRegistered;     // Insert → Tech Interview capture
    private bool _techSendHotkeyRegistered;        // Delete → Tech Interview segment send
    private bool _clickThroughHotkeyRegistered;
    // Low-level hook handle — intercepts headset Vol Up / Vol Down globally
    private LowLevelKeyboardProc? _llKeyboardProc;
    private IntPtr _llKeyboardHook = IntPtr.Zero;

    // =========================================================================
    // Construction / lifecycle
    // =========================================================================

    public MainWindow()
    {
        InitializeComponent();

        _captureService.CaptionUpdated += OnCaptionUpdated;
        _captureService.StatusChanged += OnStatusChanged;
        _captureService.ErrorOccurred += OnErrorOccurred;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        UpdateStatusUi(CaptureStatus.Idle, null);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadProxySettingsIntoUi();
        await InitializeBrowserAsync("https://chatgpt.com");
        UpdateNavButtons();

        // Apply initial WPF opacity.
        ApplyWindowOpacity(OpacitySlider.Value);

        // Tech Interview tab — wire combo box source.
        InitTechInterviewTab();

        UpdateSegmentUi();
    }

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

        _webViewHwnd = IntPtr.Zero;
        _ = SetupWebView2TransparencyAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // Ask DWM for OS-level rounded corners (Windows 11; no-op on older OS)
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREF, ref round, sizeof(int));

        _hotkeyRegistered              = RegisterHotKey(_hwnd, HOTKEY_ID_CAPTURE,      MOD_NONE, VK_HOME);
        _clearUpHotkeyRegistered       = RegisterHotKey(_hwnd, HOTKEY_ID_CLEAR_UP,    MOD_NONE, VK_PRIOR);
        _clearDownHotkeyRegistered     = RegisterHotKey(_hwnd, HOTKEY_ID_CLEAR_DOWN,  MOD_NONE, VK_NEXT);
        _starHotkeyRegistered          = RegisterHotKey(_hwnd, HOTKEY_ID_STAR_SEND,    MOD_NONE, VK_END);
        _techCaptureHotkeyRegistered   = RegisterHotKey(_hwnd, HOTKEY_ID_TECH_CAPTURE, MOD_NONE, VK_INSERT);
        _techSendHotkeyRegistered      = RegisterHotKey(_hwnd, HOTKEY_ID_TECH_SEND,    MOD_NONE, VK_DELETE);
        _clickThroughHotkeyRegistered  = RegisterHotKey(_hwnd, HOTKEY_ID_CLICKTHROUGH, MOD_CTRL, VK_W);

        // Install global LL keyboard hook to catch headset Volume Up / Down.
        // RegisterHotKey cannot intercept multimedia/APPCOMMAND keys from headsets.
        _llKeyboardProc = LowLevelKeyboardCallback;
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        using var mod  = proc.MainModule!;
        _llKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyboardProc,
                                           GetModuleHandle(mod.ModuleName), 0);
        UpdateSegmentUi();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Headset volume buttons send WM_APPCOMMAND when the app window is focused.
        if (msg == WM_APPCOMMAND)
        {
            int cmd = (int)((lParam.ToInt64() >> 16) & 0xFFF);
            if (cmd == APPCOMMAND_VOLUME_UP)
            {
                _ = Dispatcher.InvokeAsync(CaptureAndSendAsync);
                handled = true;
                return (IntPtr)1;
            }
            if (cmd == APPCOMMAND_VOLUME_DOWN)
            {
                _ = CaptureAndSendStarAsync();
                handled = true;
                return (IntPtr)1;
            }
            if (cmd == APPCOMMAND_VOLUME_MUTE)
            {
                _ = Dispatcher.InvokeAsync(CaptureOnlyAsync);
                handled = true;
                return (IntPtr)1;
            }
        }

        if (msg != WM_HOTKEY) return IntPtr.Zero;

        int hotkeyId = wParam.ToInt32();

        switch (hotkeyId)
        {
            case HOTKEY_ID_CAPTURE:
                _ = Dispatcher.InvokeAsync(CaptureAndSendAsync);
                handled = true;
                break;

            case HOTKEY_ID_CLEAR_UP:
                _ = Dispatcher.InvokeAsync(CaptureOnlyAsync);
                handled = true;
                break;

            case HOTKEY_ID_CLEAR_DOWN:
                _ = SendReminderAsync();
                handled = true;
                break;

            case HOTKEY_ID_CLICKTHROUGH:
                _ = Dispatcher.InvokeAsync(() =>
                {
                    ClickThroughButton_Click(this, new RoutedEventArgs());
                });
                handled = true;
                break;

            case HOTKEY_ID_STAR_SEND:
                _ = CaptureAndSendStarAsync();
                handled = true;
                break;

            case HOTKEY_ID_TECH_CAPTURE:
                _ = Dispatcher.InvokeAsync(RunTechInterviewBrowserCaptureAsync);
                handled = true;
                break;

            case HOTKEY_ID_TECH_SEND:
                _ = CaptureAndSendTechAsync();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    // ── Low-level keyboard callback (headset Vol Up / Vol Down) ─────────────
    // Called for every key event system-wide while the hook is installed.
    // Intercepts VK_VOLUME_UP / VK_VOLUME_DOWN before the OS processes them,
    // fires the same actions as Home / End, and suppresses the volume change.
    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 &&
            (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == VK_VOLUME_UP)
            {
                _ = Dispatcher.InvokeAsync(CaptureAndSendAsync);
                return (IntPtr)1;   // suppress — do not change volume
            }
            if (kb.vkCode == VK_VOLUME_DOWN)
            {
                _ = CaptureAndSendStarAsync();
                return (IntPtr)1;   // suppress — do not change volume
            }
            if (kb.vkCode == VK_VOLUME_MUTE)
            {
                _ = Dispatcher.InvokeAsync(CaptureOnlyAsync);
                return (IntPtr)1;   // suppress — do not toggle mute
            }
        }
        return CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        try { await _captureService.StopAsync(); }
        catch { /* shutting down */ }

        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_CAPTURE);
            _hotkeyRegistered = false;
        }
        if (_clearUpHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_CLEAR_UP);
            _clearUpHotkeyRegistered = false;
        }
        if (_clearDownHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_CLEAR_DOWN);
            _clearDownHotkeyRegistered = false;
        }
        if (_starHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_STAR_SEND);
            _starHotkeyRegistered = false;
        }
        if (_techCaptureHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_TECH_CAPTURE);
            _techCaptureHotkeyRegistered = false;
        }
        if (_techSendHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_TECH_SEND);
            _techSendHotkeyRegistered = false;
        }
        if (_clickThroughHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID_CLICKTHROUGH);
            _clickThroughHotkeyRegistered = false;
        }
        // Low-level hook cleanup
        if (_llKeyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llKeyboardHook);
            _llKeyboardHook = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
        _captureService.Dispose();

        try { Browser.Dispose(); }
        catch { /* ignored */ }
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

    // =========================================================================
    // Capture controls
    // =========================================================================

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _captureService.Start();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _captureService.StopAsync();
        StartButton.IsEnabled = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        RecordedBox.Clear();
        TiSegmentBox.Clear();
        _tracker.Reset();
        _lastCaptureTime = null;
        UpdateSegmentUi();
    }

    // ─── Tech Interview tab ──────────────────────────────────────────────────
    private readonly TechInterviewCaptureService _interviewService = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<BrowserInfo> _browsers = new();

    private void InitTechInterviewTab()
    {
        BrowserComboBox.ItemsSource = _browsers;
    }

    private void RefreshBrowsers_Click(object sender, RoutedEventArgs e)
    {
        _browsers.Clear();
        foreach (var w in _interviewService.GetAllVisibleWindows())
            _browsers.Add(w);

        TechInterviewStatusLabel.Text = _browsers.Count switch
        {
            0 => "No visible application windows found. Try again.",
            1 => "1 window found. Select it and click Capture.",
            _ => $"{_browsers.Count} windows found. Select one and click Capture."
        };

        CaptureBrowserButton.IsEnabled = BrowserComboBox.SelectedItem is BrowserInfo;
    }

    private void BrowserComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        CaptureBrowserButton.IsEnabled = BrowserComboBox.SelectedItem is BrowserInfo;
    }

    private async void CaptureBrowser_Click(object sender, RoutedEventArgs e)
    {
        await RunTechInterviewBrowserCaptureAsync();
    }

    /// <summary>
    /// Tech Interview capture — used by the Capture button and the global Insert hotkey.
    /// </summary>
    private async Task RunTechInterviewBrowserCaptureAsync()
    {
        if (BrowserComboBox.SelectedItem is not BrowserInfo info)
        {
            TechInterviewStatusLabel.Text =
                "Insert / Capture: select a window in the dropdown first (click Refresh).";
            return;
        }

        CaptureBrowserButton.IsEnabled = false;
        CaptureBrowserButton.Content   = "Capturing…";
        TechInterviewStatusLabel.Text  = $"Capturing {info.ProcessName} ({info.WindowTitle})…";

        try
        {
            BitmapSource? shot = await _interviewService.CaptureScreenshotAsync(info.Hwnd);
            CapturedScreenshotImage.Source = shot;

            TechInterviewStatusLabel.Text =
                $"Captured at {DateTime.Now:HH:mm:ss}  —  {info.ProcessName} ({info.WindowTitle})";

            if (shot is not null)
                await AttachScreenshotToActiveAiAsync(shot);
        }
        catch (Exception ex)
        {
            TechInterviewStatusLabel.Text = $"Capture failed: {ex.Message}";
        }
        finally
        {
            CaptureBrowserButton.Content   = "Capture";
            CaptureBrowserButton.IsEnabled = BrowserComboBox.SelectedItem is BrowserInfo;
        }
    }

    /// <summary>
    /// Persists the captured <see cref="BitmapSource"/> to a temp PNG file and
    /// pushes it into whichever AI site (ChatGPT or Claude) is currently loaded
    /// in the embedded WebView2.
    /// </summary>
    private async Task AttachScreenshotToActiveAiAsync(BitmapSource shot)
    {
        WebChatAutomationBase? service = GetActiveWebChatService();
        if (service is null)
        {
            TechInterviewStatusLabel.Text =
                "Captured — open ChatGPT or Claude in the embedded browser to auto-attach the screenshot.";
            return;
        }

        // Save the bitmap to %TEMP%.  The CDP DOM.setFileInputFiles call below
        // takes an absolute path, so we need a real file on disk.
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"livecaption_capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(shot));
            using (var fs = File.Create(tempPath))
                encoder.Save(fs);
        }
        catch (Exception ex)
        {
            TechInterviewStatusLabel.Text = $"Could not save screenshot: {ex.Message}";
            return;
        }

        string aiName = service is ChatGptWebAutomationService ? "ChatGPT" : "Claude";
        var progress  = new Progress<string>(s =>
            TechInterviewStatusLabel.Text = $"[{aiName}] {s}");

        try
        {
            string result = await service.UploadFileOnlyAsync(tempPath, progress);
            TechInterviewStatusLabel.Text =
                $"Screenshot attached to {aiName} at {DateTime.Now:HH:mm:ss}.  ({result})";
        }
        catch (Exception ex)
        {
            TechInterviewStatusLabel.Text = $"[{aiName}] attach failed: {ex.Message}";
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: this event fires during XAML init (before the HWND exists)
        // because of Value="1" in the slider definition.  Skip until the window
        // has been fully created — OnWindowLoaded re-applies the current value.
        if (_hwnd != IntPtr.Zero)
            ApplyWindowOpacity(e.NewValue);

        int pct = (int)Math.Round(e.NewValue * 100);
        if (OpacityLabel is not null)
            OpacityLabel.Text = $"{pct}%";
        if (OpacitySlider is not null)
            OpacitySlider.ToolTip = $"Opacity: {pct}%";
    }

    // =========================================================================
    // Transparency
    // =========================================================================

    /// <summary>
    /// Waits until WebView2's child HWND exists, then marks it as a child
    /// layered window so <see cref="ApplyWindowOpacity"/> can set its alpha.
    /// Called once after the browser finishes initialising.
    /// </summary>
    private async Task SetupWebView2TransparencyAsync()
    {
        // Poll until WebView2 creates its child HWND (usually < 2 s).
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            IntPtr found = FindWebView2ChildHwnd();
            if (found != IntPtr.Zero)
            {
                _webViewHwnd = found;
                // Add WS_EX_LAYERED once; we only update alpha after this.
                int ex = GetWindowLong(_webViewHwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0)
                    SetWindowLong(_webViewHwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                // Apply the current slider value immediately.
                ApplyWindowOpacity(OpacitySlider.Value);
                return;
            }
        }
    }

    /// <summary>
    /// Searches all descendants of the main HWND for Chrome's root widget
    /// (<c>Chrome_WidgetWin_0</c>) and returns the first match.
    ///
    /// Chromium always names its top-level widget class
    /// <c>Chrome_WidgetWin_0</c> regardless of GPU / rendering flags.
    /// Using <see cref="EnumChildWindows"/> instead of walking only
    /// immediate children lets us find it even when it is nested inside
    /// WPF's HwndHost wrapper HWND.  We stop at the very first match so
    /// none of Chrome's internal HWNDs (Chrome_RenderWidgetHostHWND, etc.)
    /// receive their own LWA_ALPHA — that prevents the double-alpha stacking
    /// that previously made the browser invisible.
    /// </summary>
    private IntPtr FindWebView2ChildHwnd()
    {
        IntPtr found = IntPtr.Zero;
        var nameBuf = new System.Text.StringBuilder(128);
        EnumChildWindows(_hwnd, (child, _) =>
        {
            nameBuf.Clear();
            GetClassName(child, nameBuf, nameBuf.Capacity);
            if (nameBuf.ToString() == "Chrome_WidgetWin_0")
            {
                found = child;
                return false; // stop — only the root widget, not its children
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Applies see-through transparency to the entire window.
    /// <list type="bullet">
    ///   <item>WPF visual tree: <c>Window.Opacity</c> via
    ///     <c>AllowsTransparency="True"</c> → <c>UpdateLayeredWindow</c>
    ///     → dark shell + controls fade uniformly.</item>
    ///   <item>WebView2 HWND: <c>SetLayeredWindowAttributes(LWA_ALPHA)</c>
    ///     directly on the child HWND so the browser content fades too.</item>
    /// </list>
    /// Both the WPF chrome and the browser become see-through at the same
    /// rate, revealing the applications positioned behind this window.
    /// </summary>
    private void ApplyWindowOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0.05, 1.0);

        // WPF visual tree (AppShell border, panels, text boxes, labels)
        this.Opacity = clamped;

        // WebView2 child HWND (separate from WPF surface)
        if (_webViewHwnd != IntPtr.Zero && IsWindow(_webViewHwnd))
        {
            byte alpha = (byte)Math.Round(clamped * 255);
            SetLayeredWindowAttributes(_webViewHwnd, 0, alpha, LWA_ALPHA);
        }
        else if (_webViewHwnd != IntPtr.Zero)
        {
            // HWND became stale — re-search on next idle tick
            _webViewHwnd = IntPtr.Zero;
            _ = SetupWebView2TransparencyAsync();
        }
    }

    // ── Interview round selector ──────────────────────────────────────────────

    private void InterviewRound_Checked(object sender, RoutedEventArgs e)
    {
        // Reserved for future use: adapt prompts or UI based on the selected round.
        // The selected round name is: (sender as RadioButton)?.Content?.ToString()
    }

    // ── Pre-interview Send ────────────────────────────────────────────────────

    private void PromptModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool isIndividual = PromptModeToggle.IsChecked == true;
        PromptModeToggle.Content     = isIndividual ? "Individual" : "Full";
        PromptModeToggle.Foreground  = isIndividual
            ? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Gray;
        PromptModeToggle.BorderBrush = isIndividual
            ? System.Windows.Media.Brushes.DodgerBlue
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
    }

    /// <summary>
    /// Maps the currently-selected round radio button to the matching
    /// Individual Prompt V2 .md file path (relative to the executable).
    /// </summary>
    private string GetIndividualPromptFilePath()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Individual Prompt V2");
        string file;
        if      (RoundRecruiter.IsChecked      == true) file = "round_prompt_recruiter.md";
        else if (RoundHiringManager.IsChecked  == true) file = "round_prompt_hiring_manager.md";
        else if (RoundTechnical.IsChecked      == true) file = "round_prompt_technical_screen.md";
        else if (RoundSystemDesign.IsChecked   == true) file = "round_prompt_system_design.md";
        else if (RoundMlSystemDesign.IsChecked == true) file = "round_prompt_system_design.md";
        else if (RoundBehavioral.IsChecked     == true) file = "round_prompt_behavioral.md";
        else if (RoundCultureFit.IsChecked     == true) file = "round_prompt_behavioral.md";
        else if (RoundPeer.IsChecked           == true) file = "round_prompt_behavioral.md";
        else if (RoundBarRaiser.IsChecked      == true) file = "round_prompt_bar_raiser.md";
        else if (RoundSkipLevel.IsChecked      == true) file = "round_prompt_exec_leadership.md";
        else if (RoundExecutive.IsChecked      == true) file = "round_prompt_exec_leadership.md";
        else if (RoundFinal.IsChecked          == true) file = "round_prompt_final_round.md";
        else if (RoundPresentation.IsChecked   == true) file = "round_prompt_final_round.md";
        else if (RoundCaseStudy.IsChecked      == true) file = "round_prompt_hiring_manager.md";
        else if (RoundResearch.IsChecked       == true) file = "round_prompt_technical_screen.md";
        else if (RoundCode.IsChecked           == true) file = "round_prompt_coding.md";
        else if (RoundDiagram.IsChecked        == true) file = "round_prompt_system_design.md";
        else                                            file = "round_prompt_recruiter.md";
        return Path.Combine(dir, file);
    }

    /// <summary>
    /// Replaces the MY MATERIALS placeholders in an individual round prompt
    /// with the values from the Pre-interview text fields.
    /// </summary>
    private string FillIndividualPromptPlaceholders(string prompt)
    {
        static string Sub(string text, string pattern, string value,
                          RegexOptions opts = RegexOptions.None)
        {
            if (string.IsNullOrWhiteSpace(value)) return text;
            return Regex.Replace(text, pattern, _ => value.Trim(), opts);
        }

        // SENIORITY / DOMAIN / COMPANY STAGE (defaults: Staff / AI/ML in the template)
        prompt = Sub(prompt,
            @"SENIORITY: Staff\s+DOMAIN: AI/ML\s+COMPANY STAGE: \[fill\]",
            $"SENIORITY: {PiSeniority?.Text}   DOMAIN: {PiDomain?.Text}   COMPANY STAGE: {PiCompanyStage?.Text}");

        // COMPANY / PRODUCT / TEAM FOCUS / TECH STACK
        prompt = Sub(prompt,
            @"COMPANY / PRODUCT / TEAM FOCUS / TECH STACK: \[fill from research\]",
            $"Company: {PiCompany?.Text}\nProduct: {PiProduct?.Text}\nTeam Focus: {PiTeamFocus?.Text}" +
            $"\nKnown Tech Stack: {PiKnownTechStack?.Text}\nRecent News: {PiRecentNews?.Text}" +
            $"\nInterviewer Background: {PiInterviewerBackground?.Text}");

        // COMPANY CULTURE + VALUES
        prompt = Sub(prompt,
            @"COMPANY CULTURE \+ VALUES: \[what they genuinely value\. every culture/behavioral answer reflects this without reciting it\]",
            $"COMPANY CULTURE + VALUES: {PiCompanyValues?.Text}");

        // MY DIFFERENTIATION
        prompt = Sub(prompt,
            @"MY DIFFERENTIATION: \[what makes me better than the typical candidate for this role\]",
            $"MY DIFFERENTIATION: {PiMyDifferentiation?.Text}");

        // MY THROUGH-LINE
        prompt = Sub(prompt,
            @"MY THROUGH-LINE: \[.*?\]",
            $"MY THROUGH-LINE: {PiMyThroughLine?.Text}",
            RegexOptions.Singleline);

        // RESUME and JOB DESCRIPTION sit on the same line: "RESUME: [paste]   JOB DESCRIPTION: [paste]"
        prompt = Sub(prompt, @"RESUME: \[paste\]",
            $"RESUME: {PiResumeSummary?.Text}");
        prompt = Sub(prompt, @"JOB DESCRIPTION: \[paste\]",
            $"JOB DESCRIPTION: {PiJobDescription?.Text}");

        // PROJECT STORIES
        prompt = Sub(prompt,
            @"PROJECT STORIES \(2 to 3, each with real numbers\): \[paste\. setting and users, the business problem, what I built and my role, the decision I made and what I rejected, real before/after metrics\]",
            $"PROJECT STORIES (2 to 3, each with real numbers): {PiProjectStories?.Text}");

        // SYSTEM DESIGN SCENARIOS
        prompt = Sub(prompt,
            @"SYSTEM DESIGN SCENARIOS \(1 to 3, each with real numbers\): \[paste\. what it does and its scale, the architecture I designed, key components and why, tradeoffs, how I handled scale and failure, real throughput/latency/cost\]",
            $"SYSTEM DESIGN SCENARIOS (1 to 3, each with real numbers): {PiSystemDesignScenarios?.Text}");

        return prompt;
    }

    private async void PiSendButton_Click(object sender, RoutedEventArgs e)
    {
        // ── Individual mode: load the round-specific prompt file ──────────────
        if (PromptModeToggle?.IsChecked == true)
        {
            string indPath = GetIndividualPromptFilePath();
            if (!File.Exists(indPath))
            {
                MessageBox.Show(
                    $"Individual prompt file not found:\n{indPath}",
                    "Pre-interview Send", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string indPrompt = await File.ReadAllTextAsync(indPath);
            indPrompt = FillIndividualPromptPlaceholders(indPrompt);
            if (!string.IsNullOrWhiteSpace(indPrompt))
                await SendPromptToAiAsync(indPrompt);
            return;
        }

        // ── Full mode: use Prompt.txt (original behaviour) ────────────────────
        // Locate Prompt.txt next to the executable (copied by the build).
        string promptPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Prompt.txt");

        if (!File.Exists(promptPath))
        {
            MessageBox.Show(
                "Prompt.txt not found next to the executable.",
                "Pre-interview Send", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string prompt = await File.ReadAllTextAsync(promptPath);

        // Replace a pattern only when the UI field has a non-empty value.
        // Using a MatchEvaluator lambda avoids issues with '$' in replacement text.
        static string Sub(string text, string pattern, string value,
                          RegexOptions opts = RegexOptions.None)
        {
            if (string.IsNullOrWhiteSpace(value)) return text;
            string v = value.Trim();
            return Regex.Replace(text, pattern, _ => v, opts);
        }

        var sl = RegexOptions.Singleline;

        // ── Verification block (lines 7-15) ─────────────────────────────────────
        // "- Seniority / Domain / Company / Company Stage: [what you read]"
        prompt = Sub(prompt,
                     @"- Seniority / Domain / Company / Company Stage: \[what you read\]",
                     $"- Seniority / Domain / Company / Company Stage: {PiSeniority?.Text} / {PiDomain?.Text} / {PiCompany?.Text} / {PiCompanyStage?.Text}");
        // "- Key Skills (top 5) / Key Projects: [from resume]"
        prompt = Sub(prompt,
                     @"- Key Skills \(top 5\) / Key Projects: \[from resume\]",
                     $"- Key Skills (top 5) / Key Projects: {PiKeySkillsDetected?.Text} / {PiKeyProjectsDetected?.Text}");
        // "- JD Focus Areas (top 3): [from JD]"
        prompt = Sub(prompt,
                     @"- JD Focus Areas \(top 3\): \[from JD\]",
                     $"- JD Focus Areas (top 3): {PiJdFocusAreasDetected?.Text}");
        // "- My Differentiation: [what makes me unique vs a typical candidate at this level]"
        prompt = Sub(prompt,
                     @"- My Differentiation: \[what makes me unique vs a typical candidate at this level\]",
                     $"- My Differentiation: {PiMyDifferentiation?.Text}");
        // "- My Through-Line: [the ONE impression ... propose one from my materials]"
        prompt = Sub(prompt,
                     @"- My Through-Line: \[the ONE impression every interviewer should describe in debrief\. propose one from my materials\]",
                     $"- My Through-Line: {PiMyThroughLine?.Text}");

        // ── IDENTITY & CONTEXT block ───────────────────────────────────────────
        // "SENIORITY: Staff   DOMAIN: AI/ML   COMPANY STAGE: [Startup / Growth / Enterprise / Big Tech / AI-first]"
        prompt = Sub(prompt,
                     @"SENIORITY: Staff\s+DOMAIN: AI/ML\s+COMPANY STAGE: \[Startup / Growth / Enterprise / Big Tech / AI-first\]",
                     $"SENIORITY: {PiSeniority?.Text}   DOMAIN: {PiDomain?.Text}   COMPANY STAGE: {PiCompanyStage?.Text}");

        // ── COMPANY CONTEXT block — 7 fields ─────────────────────────────────
        // "Company / Product / Team Focus / Known Tech Stack / Company Values / Interviewer Background / Recent News: [fill]"
        prompt = Sub(prompt,
                     @"Company / Product / Team Focus / Known Tech Stack / Company Values / Interviewer Background / Recent News: \[fill\]",
                     $"Company: {PiCompany?.Text}\nProduct: {PiProduct?.Text}\nTeam Focus: {PiTeamFocus?.Text}\nKnown Tech Stack: {PiKnownTechStack?.Text}\nCompany Values: {PiCompanyValues?.Text}\nInterviewer Background: {PiInterviewerBackground?.Text}\nRecent News: {PiRecentNews?.Text}");
        // "COMPANY CULTURE + VALUES: [what this company genuinely values, from research...]"
        prompt = Sub(prompt,
                     @"COMPANY CULTURE \+ VALUES: \[what this company genuinely values, from research\. every culture/behavioral answer reflects this without reciting it back\]",
                     $"COMPANY CULTURE + VALUES: {PiCompanyValues?.Text}");
        // "Typical Candidate Profile: [what a typical Staff AI/ML candidate looks like for this role]"
        prompt = Sub(prompt,
                     @"Typical Candidate Profile: \[what a typical Staff AI/ML candidate looks like for this role\]",
                     $"Typical Candidate Profile: {PiTypicalCandidateProfile?.Text}");
        // "MY DIFFERENTIATION: [what makes me specifically better than that profile for THIS role]"
        prompt = Sub(prompt,
                     @"MY DIFFERENTIATION: \[what makes me specifically better than that profile for THIS role\]",
                     $"MY DIFFERENTIATION: {PiMyDifferentiation?.Text}");
        // "MY THROUGH-LINE: [the ONE impression every interviewer should independently describe in debrief...]"
        prompt = Sub(prompt,
                     @"MY THROUGH-LINE: \[the ONE impression every interviewer should independently describe in debrief,.*?\]",
                     $"MY THROUGH-LINE: {PiMyThroughLine?.Text}", sl);

        // ── Section bodies ────────────────────────────────────────────────────
        // "RESUME SUMMARY: [paste resume bullets]"
        prompt = Sub(prompt,
                     @"RESUME SUMMARY: \[paste resume bullets\]",
                     $"RESUME SUMMARY: {PiResumeSummary?.Text}");
        // "JOB DESCRIPTION: [paste key JD requirements]"
        prompt = Sub(prompt,
                     @"JOB DESCRIPTION: \[paste key JD requirements\]",
                     $"JOB DESCRIPTION: {PiJobDescription?.Text}");
        // PROJECT STORIES block
        prompt = Sub(prompt,
                     @"PROJECT STORIES \(from the project-story companion\. 2 to 3, each with real numbers\): \[paste\..*?leadership answers\]",
                     $"PROJECT STORIES (from the project-story companion. 2 to 3, each with real numbers): {PiProjectStories?.Text}", sl);
        // SYSTEM DESIGN SCENARIOS block
        prompt = Sub(prompt,
                     @"SYSTEM DESIGN SCENARIOS \(from the system-design companion\. 1 to 3, each with real numbers\): \[paste\..*?""how did you build"" answers\]",
                     $"SYSTEM DESIGN SCENARIOS (from the system-design companion. 1 to 3, each with real numbers): {PiSystemDesignScenarios?.Text}", sl);

        if (string.IsNullOrWhiteSpace(prompt)) return;
        await SendPromptToAiAsync(prompt);
    }

    // ── Custom title-bar handlers ─────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// Toggles click-through mode by adding/removing <c>WS_EX_TRANSPARENT</c>.
    ///
    /// When ON: mouse clicks pass straight through this window to the app
    /// behind it — the overlay stays visible but is completely non-interactive.
    /// When OFF: normal interaction is restored.
    ///
    /// Note: <c>WS_EX_TRANSPARENT</c> must be combined with <c>WS_EX_LAYERED</c>
    /// (already present via <c>AllowsTransparency="True"</c>) to work correctly.
    /// </summary>
    private void ClickThroughButton_Click(object sender, RoutedEventArgs e)
    {
        _clickThroughEnabled = !_clickThroughEnabled;

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (_clickThroughEnabled)
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
        else
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);

        ClickThroughButton.Content        = _clickThroughEnabled ? "🖱 Pass ✓" : "🖱 Pass";
        ClickThroughButton.Foreground     = _clickThroughEnabled
            ? System.Windows.Media.Brushes.LimeGreen
            : System.Windows.Media.Brushes.Gray;
        ClickThroughButton.BorderBrush    = _clickThroughEnabled
            ? System.Windows.Media.Brushes.LimeGreen
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
        ClickThroughButton.ToolTip        = _clickThroughEnabled
            ? "Click-Through ON — clicks pass to app below  (Ctrl+W to toggle off)"
            : "Click-Through OFF — clicks land on this app  (Ctrl+W to toggle)";
    }



    // =========================================================================
    // Caption capture (service callbacks)
    // =========================================================================

    private void OnCaptionUpdated(object? sender, CapturedCaption caption)
    {
        // Never block the capture worker thread — Dispatcher.Invoke can deadlock.
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _latestCaptionText = caption.Text ?? "";
                    _tracker.UpdateLiveText(_latestCaptionText);

                    string flat = _latestCaptionText.Replace('\r', ' ').Replace('\n', ' ').Trim();

                    LiveCaptionLabel.Inlines.Clear();

                    if (string.IsNullOrWhiteSpace(flat))
                    {
                        LiveCaptionLabel.Inlines.Add(
                            new System.Windows.Documents.Run("(waiting for caption…)")
                            { Foreground = _captionWaitBrush });
                    }
                    else
                    {
                        bool isNew = _captionBarBase == null ||
                                     flat != _captionBarBase.Replace('\r', ' ').Replace('\n', ' ').Trim();

                        if (isNew)
                        {
                            LiveCaptionLabel.Inlines.Add(
                                new System.Windows.Documents.Run(flat)
                                { Foreground = _captionPendingBrush, FontWeight = FontWeights.SemiBold });
                        }
                        else
                        {
                            LiveCaptionLabel.Inlines.Add(
                                new System.Windows.Documents.Run(flat)
                                { Foreground = _captionBaseBrush });
                        }
                    }

                    // Always scroll to the bottom so the newest words are visible.
                    LiveCaptionScroll.ScrollToEnd();
                }
                catch { /* never let bar updates break the capture pipeline */ }
            }, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            ReportCapturePipelineError(ex);
        }
    }

    private void OnStatusChanged(object? sender, CaptureStatus status)
    {
        try
        {
            Dispatcher.BeginInvoke(
                () => UpdateStatusUi(status, null),
                DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            ReportCapturePipelineError(ex);
        }
    }

    private void OnErrorOccurred(object? sender, Exception ex)
    {
        try
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    UpdateStatusUi(CaptureStatus.Error, ex.Message);
                    ResetCaptureButtonsAfterWorkerStop();
                },
                DispatcherPriority.Normal);
        }
        catch (Exception dispatchEx)
        {
            ReportCapturePipelineError(dispatchEx);
        }
    }

    private void ReportCapturePipelineError(Exception ex)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateStatusUi(CaptureStatus.Error, ex.Message);
                ResetCaptureButtonsAfterWorkerStop();
            });
        }
        catch
        {
            // App is shutting down.
        }
    }

    private void ResetCaptureButtonsAfterWorkerStop()
    {
        StartButton.IsEnabled = true;
        StopButton.IsEnabled  = false;
    }

    // =========================================================================
    // Home / End — segment capture (+ optional or forced send to AI)
    // =========================================================================

    private void ApplyCapturedSegmentToUi(string segment)
    {
        string displayText = string.IsNullOrEmpty(segment)
            ? "No new segment captured."
            : segment;

        RecordedBox.Text       = displayText;
        RecordedBox.CaretIndex = displayText.Length;
        RecordedScroll.ScrollToEnd();

        TiSegmentBox.Text       = displayText;
        TiSegmentBox.CaretIndex = displayText.Length;
        TiSegmentScroll.ScrollToEnd();

        // Snapshot the current live text so the bar highlights only new words.
        _captionBarBase = _latestCaptionText;

        _lastCaptureTime = DateTime.Now;
        UpdateSegmentUi();
    }

    /// <summary>
    /// Sends a complete prompt string to the active AI (ChatGPT / Claude).
    /// </summary>
    private async Task SendPromptToAiAsync(string fullPrompt)
    {
        if (string.IsNullOrWhiteSpace(fullPrompt)) return;
        if (_isSending) return;

        WebChatAutomationBase? service = GetActiveWebChatService();
        if (service is null)
        {
            SetSendStatus("Navigate to ChatGPT (chatgpt.com) or Claude (claude.ai) first.", isError: true);
            return;
        }

        _isSending = true;
        SetSendStatus($"Sending to {service.ServiceName}…", isError: false);

        try
        {
            string result = await service.SendPromptAsync(fullPrompt);
            bool ok = result.EndsWith("Sent.");
            SetSendStatus(result, isError: !ok);
        }
        catch (Exception ex)
        {
            SetSendStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            _isSending = false;
        }
    }

    /// <summary>
    /// PageUp: captures and displays the current segment but does NOT send to AI.
    /// Advances the tracker read-head so the next capture starts fresh.
    /// </summary>
    private Task CaptureOnlyAsync()
    {
        string segment = _tracker.CaptureNextSegment();
        ApplyCapturedSegmentToUi(segment);
        return Task.CompletedTask;
    }
    /// Resets the tracker read-head, wipes the segment boxes, and updates status.
    /// </summary>
    private Task ClearSegmentAsync()
    {
        _tracker.Reset();
        _captionBarBase = null;

        RecordedBox.Text        = string.Empty;
        TiSegmentBox.Text       = string.Empty;
        GptSendStatusLabel.Text = string.Empty;
        _lastCaptureTime        = null;
        UpdateSegmentUi();

        return Task.CompletedTask;
    }

    /// <summary>
    /// PageDown: reads Reminder.txt and sends it to AI as a rule-reset nudge.
    /// </summary>
    private async Task SendReminderAsync()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reminder.txt");
        if (!File.Exists(path))
        {
            await Dispatcher.InvokeAsync(() =>
                SetSendStatus("Reminder.txt not found next to the executable.", isError: true));
            return;
        }
        string reminder = await File.ReadAllTextAsync(path);
        if (!string.IsNullOrWhiteSpace(reminder))
            await SendPromptToAiAsync(reminder);
    }

    /// <summary>Returns the round name matching the currently selected radio button.</summary>
    private string GetSelectedRoundName()
    {
        if (RoundRecruiter.IsChecked       == true) return "Recruiter Screen";
        if (RoundHiringManager.IsChecked   == true) return "Hiring Manager";
        if (RoundTechnical.IsChecked       == true) return "Technical Screen";
        if (RoundSystemDesign.IsChecked    == true) return "System Design";
        if (RoundMlSystemDesign.IsChecked  == true) return "ML System Design";
        if (RoundBehavioral.IsChecked      == true) return "Behavioral";
        if (RoundCultureFit.IsChecked      == true) return "Culture Fit";
        if (RoundPeer.IsChecked            == true) return "Peer";
        if (RoundBarRaiser.IsChecked       == true) return "Bar Raiser";
        if (RoundSkipLevel.IsChecked       == true) return "Skip Level";
        if (RoundExecutive.IsChecked       == true) return "Executive / VP";
        if (RoundFinal.IsChecked           == true) return "Final Round";
        if (RoundPresentation.IsChecked    == true) return "Presentation";
        if (RoundCaseStudy.IsChecked       == true) return "Case Study";
        if (RoundResearch.IsChecked        == true) return "Research";
        if (RoundCode.IsChecked            == true) return "Coding";
        return "Recruiter Screen";
    }

    /// <summary>
    /// Builds a live-question trigger per Prompt v7.6 TRIGGER COMMANDS.
    /// Individual mode → QUESTION only (no round prefix).
    /// Diagram round   → ROUND: System Design + DIAGRAM.
    /// Full mode       → ROUND + QUESTION.
    /// </summary>
    private string BuildQuestionTriggerPrompt(string segment)
    {
        if (PromptModeToggle?.IsChecked == true)
            return $"QUESTION: \"{segment}\"";

        if (RoundDiagram.IsChecked == true)
            return $"ROUND: System Design\nDIAGRAM: \"{segment}\"";

        return $"ROUND: {GetSelectedRoundName()}\nQUESTION: \"{segment}\"";
    }

    /// <summary>
    /// Builds a pushback trigger per Prompt v7.6 TRIGGER COMMANDS.
    /// Diagram round → DIAGRAM PUSHBACK; others → pushed back with.
    /// </summary>
    private string BuildPushbackTriggerPrompt(string segment)
    {
        if (RoundDiagram.IsChecked == true)
            return $"DIAGRAM PUSHBACK: \"{segment}\"";

        return $"pushed back with: \"{segment}\"";
    }

    /// <summary>
    /// Home key / Vol Up: capture segment and send interview ROUND/QUESTION trigger.
    /// </summary>
    private async Task CaptureAndSendAsync()
    {
        string segment = _tracker.CaptureNextSegment();
        ApplyCapturedSegmentToUi(segment);

        if (string.IsNullOrEmpty(segment)) return;

        await SendPromptToAiAsync(BuildQuestionTriggerPrompt(segment));
    }

    /// <summary>
    /// End key / Vol Down: capture segment and send pushback trigger.
    /// </summary>
    private async Task CaptureAndSendStarAsync()
    {
        string segment = _tracker.CaptureNextSegment();
        ApplyCapturedSegmentToUi(segment);

        if (string.IsNullOrEmpty(segment)) return;

        await SendPromptToAiAsync(BuildPushbackTriggerPrompt(segment));
    }

    /// <summary>
    /// Delete key (Tech Interview): capture segment, then send with the live-coding template.
    /// </summary>
    private async Task CaptureAndSendTechAsync()
    {
        string segment = _tracker.CaptureNextSegment();
        ApplyCapturedSegmentToUi(segment);

        if (string.IsNullOrEmpty(segment)) return;

        string prompt = $"ROUND: Coding\nQUESTION: \"{segment}\"";
        await SendPromptToAiAsync(prompt);
    }

    /// <summary>
    /// Returns the web-chat automation service whose target site is currently
    /// loaded in the browser, or null if neither ChatGPT nor Claude is open.
    /// </summary>
    private WebChatAutomationBase? GetActiveWebChatService()
    {
        if (_chatGpt?.IsOnTargetSite() == true) return _chatGpt;
        if (_claude?.IsOnTargetSite()  == true) return _claude;
        return null;
    }

    private void SetSendStatus(string message, bool isError)
    {
        GptSendStatusLabel.Text = message;
        GptSendStatusLabel.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : System.Windows.Media.Brushes.LimeGreen;
    }

    // =========================================================================
    // UI helpers
    // =========================================================================

    private void UpdateSegmentUi()
    {
        Brush  fill;
        string label;

        if (!_hotkeyRegistered && !_starHotkeyRegistered && !_techSendHotkeyRegistered)
        {
            fill  = Brushes.OrangeRed;
            label = " — Hotkeys unavailable (another app may own Home, End, or Delete).";
        }
        else if (!_hotkeyRegistered)
        {
            fill  = Brushes.OrangeRed;
            label = " — Home hotkey unavailable. End (STAR) and Delete (tech) may still work.";
        }
        else if (!_starHotkeyRegistered)
        {
            fill  = Brushes.OrangeRed;
            label = " — End hotkey unavailable. Home and Delete (tech) may still work.";
        }
        else if (!_techSendHotkeyRegistered)
        {
            fill  = Brushes.OrangeRed;
            label = " — Delete hotkey unavailable. Home and End may still work.";
        }
        else if (_lastCaptureTime is null)
        {
            fill  = Brushes.Gray;
            label = IsTechInterviewTabSelected()
                ? " — Idle. Home/End/Delete = capture+send. PgUp = capture only. PgDn = Reminder."
                : " — Idle. Home/End = capture+send. PgUp = capture only. PgDn = Reminder.";
        }
        else
        {
            fill  = Brushes.LimeGreen;
            label = IsTechInterviewTabSelected()
                ? $" — Last captured {_lastCaptureTime:HH:mm:ss}. Home/End/Delete = send. PgUp = no send. PgDn = Reminder."
                : $" — Last captured {_lastCaptureTime:HH:mm:ss}. Home/End = send. PgUp = no send. PgDn = Reminder.";
        }

        // SEGMENT tab
        RecordingIndicator.Fill = fill;
        RecordingLabel.Text     = label;

        // Tech Interview tab (mirror)
        TiSegmentIndicator.Fill = fill;
        TiSegmentLabel.Text     = label;
    }

    private bool IsTechInterviewTabSelected()
    {
        if (CaptionTabControl.SelectedItem is not System.Windows.Controls.TabItem tab)
            return false;

        return tab.Header?.ToString() == "Tech Interview";
    }

    private void UpdateStatusUi(CaptureStatus status, string? errorDetail)
    {
        (string text, Brush color) = status switch
        {
            CaptureStatus.Idle =>
                ("Idle", (Brush)Brushes.Gray),
            CaptureStatus.SearchingForLiveCaptions =>
                ("Searching for Live Captions...", Brushes.Goldenrod),
            CaptureStatus.LiveCaptionsNotFound =>
                ("Live Captions not found. Open it with Win + Ctrl + L.", Brushes.OrangeRed),
            CaptureStatus.Monitoring =>
                ("Monitoring Live Captions", Brushes.LimeGreen),
            CaptureStatus.Stopped =>
                ("Stopped", Brushes.Gray),
            CaptureStatus.Error =>
                ($"Error: {errorDetail ?? "unknown"}", Brushes.Red),
            _ => ("Unknown", Brushes.Gray)
        };

        StatusLabel.Text = text;
        StatusIndicator.Fill = color;
    }

}
