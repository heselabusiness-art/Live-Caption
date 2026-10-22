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

public partial class MainWindow : Window
{
    // ─── Window shell state ──────────────────────────────────────────────────
    private IntPtr _hwnd;
    private HwndSource? _source;
    private WindowEffects? _effects;

    // ─── Capture services ────────────────────────────────────────────────────
    private readonly LiveCaptionsCaptureService _captureService = new();
    private readonly SegmentCaptureTracker _tracker = new();

    // =========================================================================
    // Construction / lifecycle
    // =========================================================================

    public MainWindow()
    {
        InitializeComponent();
        InitIngestionSteps();

        _captureService.CaptionUpdated += OnCaptionUpdated;
        _captureService.StatusChanged += OnStatusChanged;
        _captureService.ErrorOccurred += OnErrorOccurred;
        _captureService.Diagnostic += msg => Log.Information("Capture: {Message}", msg);

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        UpdateStatusUi(CaptureStatus.Idle, null);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadProxySettingsIntoUi();
            // Capture starts with the app (before the browser, which can be slow): the first hotkey must not
            // depend on remembering to press Start.
            StartCaptureFromUi();

            await InitializeBrowserAsync("https://chatgpt.com");
            UpdateNavButtons();

            // Apply initial WPF opacity.
            ApplyWindowOpacity(OpacitySlider.Value);

            // Tech Interview tab — wire combo box source.
            InitTechInterviewTab();

            UpdateSegmentUi();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Window initialisation failed");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);

        _effects = new WindowEffects(_hwnd);
        _effects.ApplyRoundedCorners();

        StartHotkeys();
        UpdateSegmentUi();
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        try { await _captureService.StopAsync(); }
        catch { /* shutting down */ }

        _hotkeys?.Dispose();
        _captureService.Dispose();

        try { Browser.Dispose(); }
        catch { /* ignored */ }
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

    /// <summary>
    /// Applies see-through transparency to the whole window: the WPF visual tree via
    /// <c>Window.Opacity</c> and the WebView2 child window via its own alpha, so the browser
    /// content fades at the same rate as the shell.
    /// </summary>
    private void ApplyWindowOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0.05, 1.0);
        Opacity = clamped;
        _effects?.ApplyOpacityToWebView(clamped);
    }

    // ── Interview round selector ──────────────────────────────────────────────

    private void InterviewRound_Checked(object sender, RoutedEventArgs e)
    {
        // Reserved for future use: adapt prompts or UI based on the selected round.
        // The selected round name is: (sender as RadioButton)?.Content?.ToString()
    }

    // ── Custom title-bar handlers ─────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    // ── Collapsible captions panel ───────────────────────────────────────────

    private bool       _panelCollapsed;
    private GridLength _savedBrowserWidth = new(2, GridUnitType.Star);
    private GridLength _savedPanelWidth   = new(1, GridUnitType.Star);
    private double     _savedMinWidth;
    private double     _shrunkBy;

    // Enough for the title-bar controls (toggle, opacity, Pass, minimize, close) plus a usable
    // browser toolbar when the side panel is collapsed.
    private const double CollapsedWindowMinWidth = 560;

    private void TogglePanelButton_Click(object sender, RoutedEventArgs e)
        => SetPanelCollapsed(!_panelCollapsed);

    /// <summary>
    /// Hides/shows the right-hand captions panel. When the window is in its normal state it also
    /// shrinks/grows by the panel width, so the browser keeps its size and the whole overlay can
    /// be made narrow.
    /// </summary>
    private void SetPanelCollapsed(bool collapsed)
    {
        if (collapsed == _panelCollapsed) return;
        _panelCollapsed = collapsed;
        bool normal = WindowState == WindowState.Normal;

        if (collapsed)
        {
            _savedBrowserWidth = BrowserColumn.Width;
            _savedPanelWidth   = PanelColumn.Width;
            _shrunkBy          = PanelColumn.ActualWidth + SplitterColumn.ActualWidth;
            _savedMinWidth     = MinWidth;

            CaptionsPanel.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            PanelColumn.MinWidth     = 0;
            PanelColumn.Width        = new GridLength(0);
            SplitterColumn.Width     = new GridLength(0);
            BrowserColumn.Width      = new GridLength(1, GridUnitType.Star);

            MinWidth = CollapsedWindowMinWidth;               // lower the floor first…
            if (normal) Width = Math.Max(CollapsedWindowMinWidth, Width - _shrunkBy);
        }
        else
        {
            if (normal) Width += _shrunkBy;                   // …grow back, then restore the floor
            MinWidth = _savedMinWidth > 0 ? _savedMinWidth : 900;

            SplitterColumn.Width     = new GridLength(6);
            PanelColumn.MinWidth     = 320;
            PanelColumn.Width        = _savedPanelWidth;
            BrowserColumn.Width      = _savedBrowserWidth;
            PanelSplitter.Visibility = Visibility.Visible;
            CaptionsPanel.Visibility = Visibility.Visible;
        }

        TogglePanelButton.Icon = new Wpf.Ui.Controls.SymbolIcon(
            collapsed ? Wpf.Ui.Controls.SymbolRegular.PanelRight24
                      : Wpf.Ui.Controls.SymbolRegular.PanelRightContract24);
        TogglePanelButton.ToolTip = collapsed
            ? "Expand the captions panel"
            : "Collapse the captions panel";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// Toggles click-through mode: mouse clicks pass straight through the overlay to the app
    /// behind it, while it stays visible.
    /// </summary>
    private void ClickThroughButton_Click(object sender, RoutedEventArgs e)
    {
        if (_effects is null) return;
        bool on = _effects.ToggleClickThrough();

        ClickThroughButton.Content    = on ? "Pass ON" : "Pass";
        ClickThroughButton.Appearance = on
            ? Wpf.Ui.Controls.ControlAppearance.Success
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        ClickThroughButton.ToolTip        = on
            ? "Click-Through ON — clicks pass to app below  (Ctrl+W to toggle off)"
            : "Click-Through OFF — clicks land on this app  (Ctrl+W to toggle)";
    }
}
