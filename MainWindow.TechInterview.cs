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

/// <summary>Tech Interview tab: screenshot of another window attached to the active chat.</summary>
public partial class MainWindow
{
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
}
