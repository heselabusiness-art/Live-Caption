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

/// <summary>Global hotkeys and headset buttons → actions.</summary>
public partial class MainWindow
{
    // ─── Global hotkeys ──────────────────────────────────────────────────────
    //   Home       → capture segment + send as interview question
    //   Vol Up     → same as Home  (headset buttons)
    //   End        → capture segment + send as pushback
    //   Vol Down   → same as End   (headset buttons)
    //   PageUp     → capture segment, display only — do NOT send to AI
    //   Mute       → same as PageUp (headset buttons)
    //   PageDown   → send Reminder.txt to AI (rule-reset reminder)
    //   Insert     → Tech Interview: capture external window screenshot
    //   Delete     → capture segment + send as live-coding question
    //   Ctrl+W     → toggle click-through mode
    // The key table itself lives in HotkeyManager.
    private HotkeyManager? _hotkeys;

    private void StartHotkeys()
    {
        _hotkeys = new HotkeyManager(_hwnd, _source!, Dispatcher);
        _hotkeys.Triggered += OnHotkey;
        _hotkeys.Start();

        if (_hotkeys.FailureDetails.Count > 0)
            Log.Warning("Hotkeys not registered: {Keys}", string.Join("; ", _hotkeys.FailureDetails));

        if (_hotkeys.FailedKeys.Count > 0)
            SetSendStatus(
                $"Hotkeys not working: {string.Join(", ", _hotkeys.FailureDetails)}. " +
                "Close any other running copy of Live Caption Viewer, or use the Capture buttons.",
                isError: true);
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureAndSend:         _ = CaptureAndSendAsync(); break;
            case HotkeyAction.CaptureAndSendPushback: _ = CaptureAndSendPushbackAsync(); break;
            case HotkeyAction.CaptureOnly:            _ = CaptureOnlyAsync(); break;
            case HotkeyAction.SendReminder:           _ = SendReminderAsync(); break;
            case HotkeyAction.TechCapture:            _ = RunTechInterviewBrowserCaptureAsync(); break;
            case HotkeyAction.TechSend:               _ = CaptureAndSendTechAsync(); break;
            case HotkeyAction.ToggleClickThrough:     ClickThroughButton_Click(this, new RoutedEventArgs()); break;
        }
    }
}
