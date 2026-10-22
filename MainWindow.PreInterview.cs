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

/// <summary>Pre-interview tab: ingestion stepper and the setup-prompt Send button.</summary>
public partial class MainWindow
{
    // ── Pre-interview ingestion ───────────────────────────────────────────────

    private PreInterviewFormContext ReadPreInterviewFormContext() => new()
    {
        Seniority = PiSeniority?.Text?.Trim() ?? "",
        Domain = PiDomain?.Text?.Trim() ?? "",
        Company = PiCompany?.Text?.Trim() ?? "",
        CompanyStage = PiCompanyStage?.Text?.Trim() ?? "",
        ResumeSummary = PiResumeSummary?.Text?.Trim() ?? "",
        JobDescription = PiJobDescription?.Text?.Trim() ?? "",
        InterviewerBackground = PiInterviewerBackground?.Text?.Trim() ?? ""
    };

    // ── Progress-stepper ingestion ────────────────────────────────────────────

    private readonly System.Collections.ObjectModel.ObservableCollection<IngestionStepItem> _ingestSteps = new();
    private CancellationTokenSource? _ingestCts;

    private void InitIngestionSteps()
    {
        PreInterviewIngestionStep[] order =
        [
            PreInterviewIngestionStep.CompanyResearch,
            PreInterviewIngestionStep.JdAnalysis,
            PreInterviewIngestionStep.ProjectGeneration,
            PreInterviewIngestionStep.SystemDesignGeneration
        ];

        for (int i = 0; i < order.Length; i++)
            _ingestSteps.Add(new IngestionStepItem(i + 1, order[i]));

        PiIngestSteps.ItemsSource = _ingestSteps;
    }

    private void SetIngestStatus(string message, System.Windows.Media.Brush brush)
    {
        PiIngestStatusLabel.Foreground = brush;
        PiIngestStatusLabel.Text = message;
    }

    /// <summary>Start runs the ticked steps in order; while running the same button becomes Stop.</summary>
    private async void PiIngestStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ingestCts is not null)
        {
            _ingestCts.Cancel();
            PiIngestStartButton.IsEnabled = false;
            PiIngestStartButton.Content = "Stopping…";
            return;
        }

        await RunIngestionSequenceAsync();
    }

    private async Task RunIngestionSequenceAsync()
    {
        if (_sender.IsBusy)
        {
            SetIngestStatus("Another send is already in progress.", System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        WebChatAutomationBase? service = GetActiveWebChatService();
        if (service is null)
        {
            SetIngestStatus("Open ChatGPT or Claude in the embedded browser first.",
                System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        List<IngestionStepItem> selected = _ingestSteps.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetIngestStatus("Tick at least one step.", System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        PreInterviewFormContext ctx = ReadPreInterviewFormContext();

        // Reset the stepper, then skip ticked steps whose inputs are missing.
        foreach (IngestionStepItem s in _ingestSteps)
            s.Reset();

        var runnable = new List<IngestionStepItem>();
        foreach (IngestionStepItem s in selected)
        {
            string? missing = PreInterviewIngestionPipeline.GetMissingInputs(s.Step, ctx);
            if (missing is not null)
                s.SetState(IngestionStepState.Skipped, $"Skipped — fill {missing} first.");
            else
                runnable.Add(s);
        }

        foreach (IngestionStepItem s in _ingestSteps.Where(s => !s.IsSelected))
            s.SetState(IngestionStepState.Skipped, "Not selected.");

        if (runnable.Count == 0)
        {
            SetIngestStatus("Nothing to run — fill the required inputs above.",
                System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        // Hold the chat page for the whole run so hotkey sends queue behind it.
        IDisposable? lease = await _sender.AcquireAsync();
        if (lease is null)
        {
            SetIngestStatus("Another send is already in progress.", System.Windows.Media.Brushes.OrangeRed);
            return;
        }

        _ingestCts = new CancellationTokenSource();
        CancellationToken ct = _ingestCts.Token;

        SetIngestButton(running: true);
        foreach (IngestionStepItem s in _ingestSteps)
            s.CanEdit = false;

        PiIngestOverallProgress.Maximum = runnable.Count;
        PiIngestOverallProgress.Value = 0;

        int applied = 0, failed = 0;
        bool halted = false;

        try
        {
            for (int i = 0; i < runnable.Count; i++)
            {
                IngestionStepItem item = runnable[i];
                ct.ThrowIfCancellationRequested();

                SetIngestStatus(
                    $"Step {i + 1} of {runnable.Count}: {PreInterviewIngestionPipeline.GetStepDisplayName(item.Step)}",
                    System.Windows.Media.Brushes.Goldenrod);
                SetSendStatus($"Ingestion: {PreInterviewIngestionPipeline.GetStepDisplayName(item.Step)}…",
                    isError: false);

                try
                {
                    item.SetState(IngestionStepState.Running, $"Sending prompt to {service.ServiceName}…");

                    string prompt = PreInterviewIngestionPipeline.BuildPrompt(item.Step, ctx);
                    var progress = new Progress<string>(msg =>
                        item.SetState(IngestionStepState.Running, StripServicePrefix(msg, service.ServiceName)));

                    await FocusEmbeddedBrowserForAutomationAsync();

                    string responseOrError =
                        await service.SendPromptAndWaitForResponseAsync(prompt, progress, ct);

                    // Send/wait problems come back as "[Service] …" — chat is unusable, stop the run.
                    if (responseOrError.StartsWith('[') && responseOrError.Contains("]"))
                    {
                        item.SetState(IngestionStepState.Failed, responseOrError);
                        failed++;
                        halted = true;
                        break;
                    }

                    PreInterviewIngestionResult parsed =
                        PreInterviewIngestionPipeline.ParseResponse(item.Step, responseOrError);

                    item.SetState(IngestionStepState.Running, "Waiting for your confirmation…");

                    string preview = PreInterviewIngestionPipeline.FormatVerificationPreview(parsed);
                    string stepName = PreInterviewIngestionPipeline.GetStepDisplayName(item.Step);
                    bool verify = await ConfirmIngestionAsync($"{stepName} — verify ingestion", preview);

                    if (verify)
                    {
                        ApplyPreInterviewIngestion(parsed);
                        applied++;
                        item.SetState(IngestionStepState.Done, "Applied to the form.");
                    }
                    else
                    {
                        item.SetState(IngestionStepState.Skipped, "Declined — no fields changed.");
                    }
                }
                catch (OperationCanceledException)
                {
                    item.SetState(IngestionStepState.Cancelled, "Stopped.");
                    throw;
                }
                catch (Exception ex)
                {
                    // Bad/invalid reply for this step — report it and carry on with the next one.
                    item.SetState(IngestionStepState.Failed, ex.Message);
                    failed++;
                }

                PiIngestOverallProgress.Value = i + 1;
            }

            string summary = $"Done — {applied} applied" + (failed > 0 ? $", {failed} failed" : "") + ".";
            if (halted)
                summary = $"Halted — {applied} applied, {failed} failed. Remaining steps were not run.";

            SetIngestStatus(summary,
                failed == 0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.OrangeRed);
            SetSendStatus(summary, isError: failed > 0);
        }
        catch (OperationCanceledException)
        {
            SetIngestStatus($"Stopped — {applied} applied.", System.Windows.Media.Brushes.LightGray);
        }
        finally
        {
            foreach (IngestionStepItem s in _ingestSteps)
            {
                if (s.State == IngestionStepState.Pending && s.IsSelected)
                    s.SetState(IngestionStepState.Skipped, "Not run.");
                s.CanEdit = true;
            }

            _ingestCts?.Dispose();
            _ingestCts = null;
            lease.Dispose();
            SetIngestButton(running: false);
            PiIngestStartButton.IsEnabled = true;
        }
    }

    private static string StripServicePrefix(string message, string serviceName)
    {
        string prefix = $"[{serviceName}] ";
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
    }

    private void ApplyPreInterviewIngestion(PreInterviewIngestionResult result)
    {
        static string V(Dictionary<string, string> f, string key) =>
            f.TryGetValue(key, out string? val) ? val.Trim() : "";

        var f = result.Fields;

        switch (result.Step)
        {
            case PreInterviewIngestionStep.CompanyResearch:
                PiCompanyStage.Text = V(f, "companyStage");
                PiCompany.Text = V(f, "company");
                PiProduct.Text = V(f, "product");
                PiTeamFocus.Text = V(f, "teamFocus");
                PiKnownTechStack.Text = V(f, "knownTechStack");
                PiCompanyValues.Text = V(f, "companyValues");
                PiRecentNews.Text = V(f, "recentNews");
                PiTypicalCandidateProfile.Text = V(f, "typicalCandidateProfile");
                if (!string.IsNullOrWhiteSpace(V(f, "myDifferentiation")))
                    PiMyDifferentiation.Text = V(f, "myDifferentiation");
                if (!string.IsNullOrWhiteSpace(V(f, "interviewerBackground")))
                    PiInterviewerBackground.Text = V(f, "interviewerBackground");
                break;

            case PreInterviewIngestionStep.JdAnalysis:
                PiKeySkillsDetected.Text = V(f, "keySkillsDetected");
                PiKeyProjectsDetected.Text = V(f, "keyProjectsDetected");
                PiJdFocusAreasDetected.Text = V(f, "jdFocusAreasDetected");
                if (!string.IsNullOrWhiteSpace(V(f, "myDifferentiation")))
                    PiMyDifferentiation.Text = V(f, "myDifferentiation");
                if (!string.IsNullOrWhiteSpace(V(f, "myThroughLine")))
                    PiMyThroughLine.Text = V(f, "myThroughLine");
                break;

            case PreInterviewIngestionStep.ProjectGeneration:
            {
                string story = PreInterviewIngestionPipeline.FormatStoryForFormField(
                    V(f, "storyTitle"),
                    V(f, "projectStory"));
                // ONE story only: a new ingest replaces the previous one (several stories make
                // the full prompt too long for ChatGPT's per-message limit).
                PiProjectStories.Text = story.Trim();
                break;
            }

            case PreInterviewIngestionStep.SystemDesignGeneration:
            {
                string scenario = PreInterviewIngestionPipeline.FormatStoryForFormField(
                    V(f, "scenarioTitle"),
                    V(f, "systemDesignScenario"),
                    titleLabel: "Scenario");
                PiSystemDesignScenarios.Text = scenario.Trim();
                break;
            }
        }
    }

    // ── Pre-interview Send ────────────────────────────────────────────────────

    /// <summary>Start/Stop look of the ingestion button (label, icon, accent).</summary>
    private void SetIngestButton(bool running)
    {
        PiIngestStartButton.Content    = running ? "Stop" : "Start ingestion";
        PiIngestStartButton.Icon       = new Wpf.Ui.Controls.SymbolIcon(
            running ? Wpf.Ui.Controls.SymbolRegular.Stop24 : Wpf.Ui.Controls.SymbolRegular.Play24);
        PiIngestStartButton.Appearance = running
            ? Wpf.Ui.Controls.ControlAppearance.Danger
            : Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    /// <summary>Themed verify dialog (WPF-UI); falls back to the native MessageBox if it cannot be shown.</summary>
    private async Task<bool> ConfirmIngestionAsync(string title, string preview)
    {
        if (!Dispatcher.CheckAccess())
            return await await Dispatcher.InvokeAsync(() => ConfirmIngestionAsync(title, preview));

        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = new System.Windows.Controls.ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = preview,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"),
                        FontSize = 12
                    }
                },
                PrimaryButtonText = "Apply to form",
                CloseButtonText = "Discard",
                Owner = this,
                Topmost = true,
                MinWidth = 520,
                MaxWidth = 760,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch
        {
            return System.Windows.MessageBox.Show(
                preview, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
    }
    private void PromptModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool isIndividual = PromptModeToggle.IsChecked == true;
        PromptModeToggle.Content = isIndividual ? "Individual" : "Full";
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
            $"PROJECT STORIES (1 story, with real numbers): {PreInterviewIngestionPipeline.KeepSingleStory(PiProjectStories?.Text, out _)}");

        // SYSTEM DESIGN SCENARIOS
        prompt = Sub(prompt,
            @"SYSTEM DESIGN SCENARIOS \(1 to 3, each with real numbers\): \[paste\. what it does and its scale, the architecture I designed, key components and why, tradeoffs, how I handled scale and failure, real throughput/latency/cost\]",
            $"SYSTEM DESIGN SCENARIOS (1 scenario, with real numbers): {PreInterviewIngestionPipeline.KeepSingleStory(PiSystemDesignScenarios?.Text, out _)}");

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
        // Placeholders are matched case-insensitively so harmless wording-case edits in
        // Prompt.txt ("Propose" vs "propose") don't silently stop a field from being filled.
        // A field that HAS a value but whose placeholder can't be found is recorded and
        // reported after sending, so prompt drift is visible instead of silent.
        var unmatched = new List<string>();
        var notes     = new List<string>();      // informational, shown after a successful send

        string Sub(string text, string pattern, string value,
                   RegexOptions opts = RegexOptions.None)
        {
            if (string.IsNullOrWhiteSpace(value)) return text;
            opts |= RegexOptions.IgnoreCase;
            if (!Regex.IsMatch(text, pattern, opts))
            {
                string label = Regex.Unescape(pattern.Split(':')[0]).Trim(' ', '-');
                unmatched.Add(label.Length > 40 ? label[..40] + "…" : label);
                return text;
            }
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
        // ONE story only: the full prompt is already ~125k chars, and ChatGPT rejects messages over
        // its per-message limit ("The message you submitted was too long").
        string oneStory = PreInterviewIngestionPipeline.KeepSingleStory(PiProjectStories?.Text, out int storyBlocks);
        if (storyBlocks > 1) notes.Add($"project stories: sent only the latest of {storyBlocks}");
        prompt = Sub(prompt,
                     @"PROJECT STORIES \(from the project-story companion\.[^)]*\): \[paste\..*?leadership answers\]",
                     $"PROJECT STORIES (from the project-story companion. 1 story, with real numbers): {oneStory}", sl);
        // SYSTEM DESIGN SCENARIOS block
        string oneScenario = PreInterviewIngestionPipeline.KeepSingleStory(PiSystemDesignScenarios?.Text, out int scenarioBlocks);
        if (scenarioBlocks > 1) notes.Add($"system design: sent only the latest of {scenarioBlocks}");
        prompt = Sub(prompt,
                     @"SYSTEM DESIGN SCENARIOS \(from the system-design companion\.[^)]*\): \[paste\..*?""how did you build"" answers\]",
                     $"SYSTEM DESIGN SCENARIOS (from the system-design companion. 1 scenario, with real numbers): {oneScenario}", sl);

        if (string.IsNullOrWhiteSpace(prompt)) return;
        await SendPromptToAiAsync(prompt);

        if (unmatched.Count > 0)
        {
            SetSendStatus(
                $"{GptSendStatusLabel.Text}  ⚠ Not placed (Prompt.txt wording changed): " +
                string.Join(", ", unmatched.Distinct()),
                isError: true);
        }
        else if (notes.Count > 0 && GptSendStatusLabel.Text.EndsWith("Sent."))
        {
            SetSendStatus($"{GptSendStatusLabel.Text}  ℹ {string.Join("; ", notes)}", isError: false);
        }
    }
}
