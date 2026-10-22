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

/// <summary>Live Captions capture: start/stop, caption bar, segment tracking and status display.</summary>
public partial class MainWindow
{
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

    // =========================================================================
    // Capture controls
    // =========================================================================

    private void StartButton_Click(object sender, RoutedEventArgs e) => StartCaptureFromUi();

    private void StartCaptureFromUi()
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

    // =========================================================================
    // Caption capture (service callbacks)
    // =========================================================================

    private void OnCaptionUpdated(object? sender, CapturedCaption caption)
    {
        // Never block the capture worker thread — Dispatcher.Invoke can deadlock.
        try
        {
            Dispatcher.BeginInvoke(() => ApplyCaptionToUi(caption.Text ?? ""), DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            ReportCapturePipelineError(ex);
        }
    }

    /// <summary>UI thread: feed new caption text to the segment tracker and the live caption bar.</summary>
    private void ApplyCaptionToUi(string captionText)
    {
        try
        {
            _latestCaptionText = captionText;
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

                LiveCaptionLabel.Inlines.Add(isNew
                    ? new System.Windows.Documents.Run(flat) { Foreground = _captionPendingBrush, FontWeight = FontWeights.SemiBold }
                    : new System.Windows.Documents.Run(flat) { Foreground = _captionBaseBrush });
            }

            // Always scroll to the bottom so the newest words are visible.
            LiveCaptionScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            // Never let bar updates break the capture pipeline.
            Log.Warning(ex, "Caption bar update failed");
        }
    }

    /// <summary>
    /// Hotkeys call this first: starts capture if it is not running (nobody should have to press Start before the
    /// first hotkey) and reads the Live Captions window at THIS instant, so the segment holds what is on screen now
    /// rather than whatever the last UI Automation event delivered.
    /// </summary>
    private async Task RefreshCaptionNowAsync()
    {
        if (!_captureService.IsRunning)
        {
            Log.Information("Hotkey pressed while capture was stopped: starting capture");
            StartCaptureFromUi();
        }

        string? text = await Task.Run(() => _captureService.ReadNow());
        if (!string.IsNullOrWhiteSpace(text))
            ApplyCaptionToUi(text);
        else
            Log.Warning("Live caption read on hotkey returned no text (status {Status})", _captureService.Status);
    }

    private void OnStatusChanged(object? sender, CaptureStatus status)
    {
        Log.Information("Capture status: {Status}", status);
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
        Log.Error(ex, "Capture service error");
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
        Log.Error(ex, "Capture pipeline error");
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
    // Segment capture / display
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
    /// PageUp: captures and displays the current segment but does NOT send to AI.
    /// Advances the tracker read-head so the next capture starts fresh.
    /// </summary>
    private async Task CaptureOnlyAsync()
    {
        try
        {
            await RefreshCaptionNowAsync();
            string segment = _tracker.CaptureNextSegment();
            ApplyCapturedSegmentToUi(segment);
            if (string.IsNullOrEmpty(segment))
                SetSendStatus(NoCaptionMessage(), isError: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture-only hotkey failed");
            SetSendStatus($"Hotkey error: {ex.Message}", isError: true);
        }
    }

    /// <summary>Why a capture came back empty - never leave the user guessing.</summary>
    private string NoCaptionMessage()
    {
        string why = _captureService.Status switch
        {
            CaptureStatus.LiveCaptionsNotFound => "Live Captions is not open (press Win + Ctrl + L).",
            CaptureStatus.Error => "Capture reported an error (see the status line).",
            _ when string.IsNullOrWhiteSpace(_latestCaptionText) => "No caption text has been read yet - is Live Captions showing speech?",
            _ => "No new caption text since the last capture."
        };
        return $"Nothing captured. {why}";
    }
    /// <summary>
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

    // =========================================================================
    // UI helpers
    // =========================================================================

    private void UpdateSegmentUi()
    {
        // The capture log ("Last captured 14:44…") is deliberately not shown. The row only
        // appears when hotkeys are broken, because then nothing else would tell you why.
        IReadOnlyList<string> failedKeys = _hotkeys?.FailedKeys ?? [];
        bool broken = failedKeys.Count > 0;

        Brush fill = broken ? Brushes.OrangeRed
                   : _lastCaptureTime is null ? Brushes.Gray
                   : Brushes.LimeGreen;

        string problem = broken
            ? $" — Hotkeys NOT working: {string.Join(", ", _hotkeys!.FailureDetails)}. " +
              "Another app or another running copy of Live Caption Viewer owns them — close it and restart."
            : "";
        Visibility labelVisibility = broken ? Visibility.Visible : Visibility.Collapsed;

        // SEGMENT tab
        RecordingIndicator.Fill      = fill;
        RecordingLabel.Text          = problem;
        RecordingLabel.Visibility    = labelVisibility;

        // Tech Interview tab (mirror)
        TiSegmentIndicator.Fill      = fill;
        TiSegmentLabel.Text          = problem;
        TiSegmentLabel.Visibility    = labelVisibility;
    }

    private bool IsTechInterviewTabSelected()
    {
        if (CaptionTabControl.SelectedItem is not System.Windows.Controls.TabItem tab)
            return false;

        return ReferenceEquals(tab, TechInterviewTab);
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
