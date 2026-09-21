using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace LiveCaptionViewer.Services;

// =============================================================================
// TechInterviewCaptureService
// Lists running web browsers and snapshots the page area of a chosen browser
// window.  The capture is cropped to the bounds of the first UI-Automation
// Document element so the resulting image excludes tab strip, address bar,
// bookmarks bar, status bar, etc.
// =============================================================================

public sealed class BrowserInfo
{
    public required string  ProcessName { get; init; }
    public required string  WindowTitle { get; init; }
    public required IntPtr  Hwnd        { get; init; }
    public required int     ProcessId   { get; init; }

    public string DisplayName => $"[{ProcessName}]  {WindowTitle}";
}

public sealed class TechInterviewCaptureService
{
    // ─── Window enumeration ───────────────────────────────────────────────────

    /// <summary>
    /// Returns every visible, user-facing top-level window on the desktop —
    /// any application, not just browsers.  The current process is excluded
    /// so the app doesn't list itself.
    /// </summary>
    public IReadOnlyList<BrowserInfo> GetAllVisibleWindows()
    {
        var pidToName = BuildPidToNameMap();
        int ownPid    = Process.GetCurrentProcess().Id;

        var list      = new List<BrowserInfo>();
        var seenHwnds = new HashSet<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd))               return true;
                if (IsCloaked(hwnd))                      return true;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)    return true;

                // Must have a non-empty title.
                int len = GetWindowTextLength(hwnd);
                if (len <= 0)                             return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title))          return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || (int)pid == ownPid)       return true;
                if (!pidToName.TryGetValue((int)pid, out var procName)) return true;
                if (!seenHwnds.Add(hwnd))                 return true;

                list.Add(new BrowserInfo
                {
                    ProcessName = procName,
                    WindowTitle = title,
                    Hwnd        = hwnd,
                    ProcessId   = (int)pid
                });
            }
            catch { /* per-window failures don't abort enumeration */ }
            return true;
        }, IntPtr.Zero);

        list.Sort((a, b) =>
        {
            int n = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            return n != 0 ? n : string.Compare(a.WindowTitle, b.WindowTitle, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    private static Dictionary<int, string> BuildPidToNameMap()
    {
        var map = new Dictionary<int, string>(capacity: 256);
        foreach (Process p in Process.GetProcesses())
        {
            try { map[p.Id] = p.ProcessName; }
            catch { /* access denied */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { /* DWM API unavailable on this OS — treat as not cloaked */ }
        return false;
    }

    // ─── Screenshot capture ───────────────────────────────────────────────────

    /// <summary>
    /// Captures a snapshot of <paramref name="hwnd"/>.
    ///
    /// 1. Calls <c>PrintWindow(PW_RENDERFULLCONTENT)</c> so even an occluded
    ///    Chromium window paints correctly (otherwise DirectComposition
    ///    surfaces come back black).
    /// 2. Asks UI-Automation for the bounds of the first
    ///    <see cref="ControlType.Document"/> descendant — that's the actual
    ///    web page area, excluding tab strip, address bar, bookmarks bar,
    ///    status bar, etc.
    /// 3. Crops the full-window bitmap to that page region.
    /// 4. Falls back to the full window if UIA can't locate a Document.
    /// </summary>
    public Task<BitmapSource?> CaptureScreenshotAsync(IntPtr hwnd) => Task.Run<BitmapSource?>(() =>
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))                  return null;
        if (!GetWindowRect(hwnd, out RECT winRect))                  return null;

        int winWidth  = winRect.Right  - winRect.Left;
        int winHeight = winRect.Bottom - winRect.Top;
        if (winWidth <= 0 || winHeight <= 0)                         return null;

        using var fullBmp = new Bitmap(winWidth, winHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fullBmp))
        {
            IntPtr hdc = g.GetHdc();
            try   { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }

        // Try to narrow to the web-page region.  If UIA can't find a Document
        // (rare exotic browser, content-process crashed, …) keep the full
        // window so the user still gets something useful.
        System.Drawing.Rectangle? pageRect = TryGetPageRectLocal(hwnd, winRect);

        Bitmap pageBmp;
        if (pageRect is { } r && r.Width > 0 && r.Height > 0)
            pageBmp = fullBmp.Clone(r, PixelFormat.Format32bppArgb);
        else
            pageBmp = (Bitmap)fullBmp.Clone();

        try
        {
            // PNG round-trip → BitmapImage so it's UI-thread-safe and frozen.
            using var ms = new MemoryStream();
            pageBmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = ms;
            img.CacheOption  = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        finally
        {
            pageBmp.Dispose();
        }
    });

    /// <summary>
    /// Returns the on-screen page-content region of <paramref name="hwnd"/>
    /// (the actual rendered web page, excluding tab strip / address bar /
    /// bookmarks bar / status bar) in window-local pixels.
    ///
    /// Two strategies — first one that yields a sane rectangle wins:
    ///
    ///   1.  Chromium's content-process HWND
    ///       (class name <c>Chrome_RenderWidgetHostHWND</c>).  This is the
    ///       widget that paints the page in Chrome / Edge / Brave / Vivaldi /
    ///       Opera / Arc.  Its <c>GetWindowRect</c> exactly matches the
    ///       rendered page — no chrome included.  This works without any
    ///       accessibility client connected, unlike UIA.
    ///
    ///   2.  UIA <see cref="ControlType.Document"/> descendant.  Used for
    ///       non-Chromium browsers (Firefox, legacy IE) where the content
    ///       isn't exposed as a separate HWND.
    ///
    /// Returns <c>null</c> if neither strategy works, in which case the
    /// caller falls back to the full window.
    /// </summary>
    private static System.Drawing.Rectangle? TryGetPageRectLocal(IntPtr hwnd, RECT winRect)
    {
        // Strategy 1: Chromium content HWND (Chrome / Edge / Brave / Arc …).
        var byHwnd = TryGetChromiumContentRectLocal(hwnd, winRect);
        if (byHwnd is not null) return byHwnd;

        // Strategy 2: UIA Document element (Firefox, legacy IE …).
        var byUia = TryGetUiaDocumentRectLocal(hwnd, winRect);
        if (byUia is not null) return byUia;

        // Strategy 3: Client area — strips the title bar, menu bar, and
        // window borders for any non-browser application.
        return TryGetClientAreaRectLocal(hwnd, winRect);
    }

    /// <summary>
    /// Returns the window's client area in window-local pixels, which
    /// excludes the title bar, border, and any non-client decorations.
    /// </summary>
    private static System.Drawing.Rectangle? TryGetClientAreaRectLocal(IntPtr hwnd, RECT winRect)
    {
        try
        {
            if (!GetClientRect(hwnd, out RECT clientLocal)) return null;
            int w = clientLocal.Right  - clientLocal.Left;
            int h = clientLocal.Bottom - clientLocal.Top;
            if (w <= 0 || h <= 0) return null;

            // ClientToScreen converts the top-left client corner to screen coords.
            var pt = new POINT { x = 0, y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return null;

            RECT screenRect = new()
            {
                Left   = pt.x,
                Top    = pt.y,
                Right  = pt.x + w,
                Bottom = pt.y + h,
            };

            return ClipRectToWindow(screenRect, winRect);
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds the visible <c>Chrome_RenderWidgetHostHWND</c> descendant of
    /// <paramref name="hwnd"/> (Chromium browsers expose one HWND per tab;
    /// only the active tab's is visible) and converts its screen rectangle
    /// into window-local pixels clipped to the window's bounds.
    /// </summary>
    private static System.Drawing.Rectangle? TryGetChromiumContentRectLocal(IntPtr hwnd, RECT winRect)
    {
        try
        {
            IntPtr content   = IntPtr.Zero;
            int    bestArea  = 0;
            var    classBuf  = new StringBuilder(64);

            EnumChildWindows(hwnd, (h, _) =>
            {
                if (!IsWindowVisible(h)) return true;

                classBuf.Clear();
                GetClassName(h, classBuf, classBuf.Capacity);
                if (!string.Equals(classBuf.ToString(),
                                   "Chrome_RenderWidgetHostHWND",
                                   StringComparison.Ordinal))
                    return true;

                // Multiple matches can exist (hidden tabs, dev tools); pick
                // the one with the largest area — that's the active page.
                if (GetWindowRect(h, out RECT cr))
                {
                    int w = cr.Right  - cr.Left;
                    int ht = cr.Bottom - cr.Top;
                    int area = w * ht;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        content  = h;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (content == IntPtr.Zero) return null;
            if (!GetWindowRect(content, out RECT r)) return null;

            return ClipRectToWindow(r, winRect);
        }
        catch { return null; }
    }

    private static System.Drawing.Rectangle? TryGetUiaDocumentRectLocal(IntPtr hwnd, RECT winRect)
    {
        try
        {
            AutomationElement? root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var docCond = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Document);
            AutomationElement? doc = root.FindFirst(TreeScope.Descendants, docCond);
            if (doc is null) return null;

            System.Windows.Rect r = doc.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;

            // Reject obviously-bogus rects (Chromium without accessibility
            // sometimes reports the Document as the entire window).
            int winW = winRect.Right  - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            if (r.Width  >= winW - 4 && r.Height >= winH - 4) return null;

            RECT screenRect = new()
            {
                Left   = (int)Math.Round(r.Left),
                Top    = (int)Math.Round(r.Top),
                Right  = (int)Math.Round(r.Right),
                Bottom = (int)Math.Round(r.Bottom)
            };
            return ClipRectToWindow(screenRect, winRect);
        }
        catch { return null; }
    }

    /// <summary>
    /// Converts an absolute screen rectangle into window-local pixels and
    /// clips against the window's bounds.  Returns null when nothing
    /// remains after clipping.
    /// </summary>
    private static System.Drawing.Rectangle? ClipRectToWindow(RECT screen, RECT winRect)
    {
        int x = screen.Left   - winRect.Left;
        int y = screen.Top    - winRect.Top;
        int w = screen.Right  - screen.Left;
        int h = screen.Bottom - screen.Top;

        int winW = winRect.Right  - winRect.Left;
        int winH = winRect.Bottom - winRect.Top;
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > winW) w = winW - x;
        if (y + h > winH) h = winH - y;
        if (w <= 0 || h <= 0) return null;

        return new System.Drawing.Rectangle(x, y, w, h);
    }

    // ─── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // ── Window enumeration ──
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int  GWL_EXSTYLE          = -20;
    private const int  WS_EX_TOOLWINDOW     = 0x00000080;
    private const int  DWMWA_CLOAKED        = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }
}
