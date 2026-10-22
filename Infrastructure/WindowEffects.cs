using System.Text;
using Serilog;
using static LiveCaptionViewer.Infrastructure.NativeMethods;

namespace LiveCaptionViewer.Infrastructure;

/// <summary>
/// Window-level visual tweaks of the overlay: rounded corners, fading the WebView2 child
/// window together with the WPF shell, and click-through mode.
/// </summary>
/// <remarks>
/// <c>AllowsTransparency="True"</c> makes the WPF shell see-through, but WebView2 lives in a
/// separate child HWND outside that surface, so its alpha is set directly via
/// <c>WS_EX_LAYERED</c> + <c>LWA_ALPHA</c>. The widget is found by class name
/// (<c>Chrome_WidgetWin_0</c>) and only the first match is touched, so Chromium's internal child
/// windows never get their own alpha (that double-alpha stacking once made the browser invisible).
/// </remarks>
internal sealed class WindowEffects
{
    private static readonly ILogger Log = AppLog.For(nameof(WindowEffects));

    private readonly IntPtr _hwnd;
    private IntPtr _webViewHwnd = IntPtr.Zero;
    private double _lastOpacity = 1.0;

    public WindowEffects(IntPtr hwnd) => _hwnd = hwnd;

    public bool ClickThroughEnabled { get; private set; }

    /// <summary>Asks DWM for OS-level rounded corners (Windows 11; no-op on older versions).</summary>
    public void ApplyRoundedCorners()
    {
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREF, ref round, sizeof(int));
    }

    /// <summary>Forgets the cached WebView2 window (e.g. after the browser is re-created).</summary>
    public void ResetWebViewWindow() => _webViewHwnd = IntPtr.Zero;

    /// <summary>
    /// Waits until WebView2's child HWND exists, then marks it layered so its alpha can be set.
    /// </summary>
    public async Task AttachWebViewAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            IntPtr found = FindWebView2ChildHwnd();
            if (found == IntPtr.Zero) continue;

            _webViewHwnd = found;
            int ex = GetWindowLong(found, GWL_EXSTYLE);
            if ((ex & WS_EX_LAYERED) == 0)
                SetWindowLong(found, GWL_EXSTYLE, ex | WS_EX_LAYERED);

            ApplyOpacityToWebView(_lastOpacity);
            return;
        }

        Log.Warning("WebView2 child window not found; browser opacity cannot be changed");
    }

    /// <summary>Applies the alpha to the WebView2 child window (the WPF shell is faded by the caller).</summary>
    public void ApplyOpacityToWebView(double opacity)
    {
        _lastOpacity = opacity;
        if (_webViewHwnd == IntPtr.Zero) return;

        if (IsWindow(_webViewHwnd))
        {
            byte alpha = (byte)Math.Round(opacity * 255);
            SetLayeredWindowAttributes(_webViewHwnd, 0, alpha, LWA_ALPHA);
        }
        else
        {
            // Stale handle: look it up again.
            _webViewHwnd = IntPtr.Zero;
            _ = AttachWebViewAsync();
        }
    }

    private IntPtr FindWebView2ChildHwnd()
    {
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(128);
        EnumChildWindows(_hwnd, (child, _) =>
        {
            name.Clear();
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == "Chrome_WidgetWin_0")
            {
                found = child;
                return false; // only the root widget, not its children
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Toggles click-through by adding/removing <c>WS_EX_TRANSPARENT</c> (needs
    /// <c>WS_EX_LAYERED</c>, which <c>AllowsTransparency</c> already provides).
    /// </summary>
    public bool ToggleClickThrough()
    {
        ClickThroughEnabled = !ClickThroughEnabled;
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            ClickThroughEnabled ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT);
        return ClickThroughEnabled;
    }
}
