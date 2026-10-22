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

/// <summary>Hotkey → capture → prompt → ChatGPT/Claude page.</summary>
public partial class MainWindow
{
    // =========================================================================
    // Sending prompts to the embedded ChatGPT / Claude page
    // =========================================================================

    /// <summary>Serialises every send so a second hotkey press queues instead of being lost.</summary>
    private readonly PromptSender _sender = new();

    private TriggerContext GetTriggerContext() =>
        new(GetSelectedRoundName(), PromptModeToggle?.IsChecked == true, RoundDiagram.IsChecked == true);

    /// <summary>
    /// Sends a complete prompt string to the active AI (ChatGPT / Claude). Never fails silently:
    /// every outcome ends up in the send-status line and in the log.
    /// </summary>
    private async Task SendPromptToAiAsync(string fullPrompt)
    {
        if (string.IsNullOrWhiteSpace(fullPrompt)) return;

        WebChatAutomationBase? service = GetActiveWebChatService();
        if (service is null)
        {
            SetSendStatus("Navigate to ChatGPT (chatgpt.com) or Claude (claude.ai) first.", isError: true);
            return;
        }

        try
        {
            SendResult result = await _sender.SendAsync(
                service, fullPrompt, msg => SetSendStatus(msg, isError: false));
            SetSendStatus(result.Message, isError: !result.Ok);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Send to {Service} failed", service.ServiceName);
            SetSendStatus($"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// PageDown: reads Reminder.txt and sends it to AI as a rule-reset nudge.
    /// </summary>
    private async Task SendReminderAsync()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reminder.txt");
            if (!File.Exists(path))
            {
                SetSendStatus("Reminder.txt not found next to the executable.", isError: true);
                return;
            }

            string reminder = PromptBuilder.ExtractFullReminder(await File.ReadAllTextAsync(path));
            if (!string.IsNullOrWhiteSpace(reminder))
                await SendPromptToAiAsync(reminder);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sending the reminder failed");
            SetSendStatus($"Reminder error: {ex.Message}", isError: true);
        }
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

    /// <summary>Home key / Vol Up: capture segment and send the ROUND/QUESTION trigger.</summary>
    private Task CaptureAndSendAsync() =>
        CaptureAndSendCoreAsync(segment => PromptBuilder.BuildQuestion(segment, GetTriggerContext()));

    /// <summary>End key / Vol Down: capture segment and send the pushback trigger.</summary>
    private Task CaptureAndSendPushbackAsync() =>
        CaptureAndSendCoreAsync(segment => PromptBuilder.BuildPushback(segment, GetTriggerContext()));

    /// <summary>Delete key (Tech Interview): capture segment, then send with the live-coding template.</summary>
    private Task CaptureAndSendTechAsync() =>
        CaptureAndSendCoreAsync(PromptBuilder.BuildCoding);

    /// <summary>
    /// Shared body of every capture+send hotkey. Never fails silently: an empty capture,
    /// a busy sender, or an exception all surface in the send-status line.
    /// </summary>
    private async Task CaptureAndSendCoreAsync(Func<string, string> buildPrompt)
    {
        try
        {
            await RefreshCaptionNowAsync();
            string segment = _tracker.CaptureNextSegment();
            ApplyCapturedSegmentToUi(segment);

            if (string.IsNullOrEmpty(segment))
            {
                SetSendStatus(NoCaptionMessage().Replace("Nothing captured.", "Nothing to send."), isError: true);
                return;
            }

            Log.Information("Captured segment ({Chars} chars)", segment.Length);
            await SendPromptToAiAsync(buildPrompt(segment));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hotkey send failed");
            SetSendStatus($"Hotkey error: {ex.Message}", isError: true);
        }
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
}
