using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation;
using LiveCaptionViewer.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace LiveCaptionViewer.Services;

// =============================================================================
// NOTE ON SELECTOR FRAGILITY
// Both ChatGPT and Claude update their web UIs frequently.
// If automation breaks after a site deploy, open DevTools inside the WebView2
// and update the selector strings in the concrete subclass.
// =============================================================================

/// <summary>
/// Shared base for web-chat automation services.
/// File upload uses CDP <c>DOM.setFileInputFiles</c> — the same mechanism
/// Playwright and Selenium use — so no OS dialog or clipboard tricks are needed.
/// </summary>
public abstract class WebChatAutomationBase
{
    protected virtual TimeSpan SendDelay          => TimeSpan.FromMilliseconds(700);
    protected virtual int      FileUploadMaxWaitMs => 20_000;
    protected virtual int      FileUploadPollMs    => 400;

    protected readonly CoreWebView2 Core;

    private static readonly ILogger Log = AppLog.For("WebChat");

    /// <summary>
    /// When the previous reply is still streaming, wait this long for it to finish before
    /// pressing Stop. In a live interview the newest question matters more than the tail of
    /// the previous answer.
    /// </summary>
    protected virtual TimeSpan GenerationGrace => TimeSpan.FromSeconds(4);

    /// <summary>Press Stop on a reply that outlasts <see cref="GenerationGrace"/> instead of waiting up to 45 s.</summary>
    protected virtual bool InterruptGeneration => true;

    /// <summary>After this long without any Send button, fall back to pressing Enter in the composer.</summary>
    protected virtual int EnterFallbackAfterMs => 2_500;

    protected WebChatAutomationBase(CoreWebView2 core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
    }

    // ─── Abstract / virtual surface ──────────────────────────────────────────

    public abstract string ServiceName { get; }
    protected abstract string[] TargetHosts { get; }

    /// <summary>JS expression → chat text-input element.</summary>
    protected abstract string InputSelectorJs { get; }

    /// <summary>JS expression → Send button element.</summary>
    protected abstract string SendButtonJs { get; }

    /// <summary>
    /// JS expression that evaluates to <c>true</c> while the AI is still
    /// streaming a response (the stop button is visible).
    /// </summary>
    protected virtual string IsGeneratingJs =>
        @"!!(document.querySelector('[data-testid=""stop-button""]')
            || document.querySelector('button[aria-label*=""Stop""]')
            || document.querySelector('[aria-label=""Stop streaming""]'))";

    /// <summary>JS expression → the Stop button shown while a reply streams.</summary>
    protected virtual string StopButtonJs =>
        @"(document.querySelector('[data-testid=""stop-button""]')
            || document.querySelector('button[aria-label=""Stop streaming""]')
            || document.querySelector('button[aria-label=""Stop generating""]')
            || document.querySelector('button[aria-label=""Stop response""]')
            || document.querySelector('button[aria-label*=""Stop""]'))";

    // The built-in selectors can be replaced without a rebuild via selectors.json (see
    // SelectorOverrides). Everything below uses the effective expression.
    private SelectorOverrides.SelectorSet UserSelectors => SelectorOverrides.Shared.For(ServiceName);
    private string EffInputJs => Wrap(UserSelectors.Input ?? InputSelectorJs);
    private string EffSendJs => Wrap(UserSelectors.SendButton ?? SendButtonJs);
    private string EffGeneratingJs => Wrap(UserSelectors.IsGenerating ?? IsGeneratingJs);
    private string EffStopJs => Wrap(UserSelectors.StopButton ?? StopButtonJs);

    private static string Wrap(string expression) => "(" + expression + ")";

    /// <summary>
    /// JS expression returning the <c>innerHTML</c> of the last AI response
    /// block, or <c>null</c>.  Subclasses override with site-specific selectors.
    /// </summary>
    protected virtual string LastResponseHtmlJs => "null";

    /// <summary>
    /// JS expression that becomes truthy once the site shows an attachment
    /// preview chip (confirming the upload was accepted).
    /// </summary>
    protected virtual string FileAttachmentIndicatorJs =>
        @"document.querySelector('[aria-label*=""Remove""]')
          || document.querySelector('[data-testid*=""attachment""]')
          || document.querySelector('[data-testid*=""file""]')";

    // ─── Public API ───────────────────────────────────────────────────────────

    public bool IsOnTargetSite()
    {
        // Compare the real host name: a plain substring test also matches e.g. a search URL
        // that merely contains "claude.ai" in its query string.
        if (!Uri.TryCreate(Core.Source, UriKind.Absolute, out Uri? uri))
            return false;

        foreach (string host in TargetHosts)
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// CSS selector for the hidden <c>input[type="file"]</c> element.
    /// Used by <see cref="UploadFileOnlyAsync"/> for CDP-level file injection.
    /// </summary>
    protected virtual string FileInputCssSelector => @"input[type=""file""]";

    /// <summary>
    /// Uploads <paramref name="filePath"/> directly to the active site using the
    /// Chrome DevTools Protocol command <c>DOM.setFileInputFiles</c> — the same
    /// mechanism used by Playwright and Selenium.  No OS dialog, no clipboard.
    /// After setting the file, dispatches React-compatible change/input events
    /// and polls until the site shows an attachment-preview chip.
    /// </summary>
    public async Task<string> UploadFileOnlyAsync(
        string             filePath,
        IProgress<string>? progress = null)
    {
        if (!IsOnTargetSite())
            return $"Not on {ServiceName}. Navigate there first.";

        if (!System.IO.File.Exists(filePath))
            return "File not found on disk.";

        progress?.Report($"[{ServiceName}] Locating file input…");

        string selectorJson = JsString(FileInputCssSelector);

        try
        {
            // ── 1. Get the JS objectId of the file input via Runtime.evaluate ──
            string evalJson = await Core.CallDevToolsProtocolMethodAsync(
                "Runtime.evaluate",
                JsonSerializer.Serialize(new
                {
                    expression    = $"document.querySelector({selectorJson})",
                    returnByValue = false
                }));

            using var evalDoc = JsonDocument.Parse(evalJson);
            string? objectId = null;
            if (evalDoc.RootElement.TryGetProperty("result", out var resultProp) &&
                resultProp.TryGetProperty("objectId", out var oidProp))
                objectId = oidProp.GetString();

            if (string.IsNullOrEmpty(objectId))
                return $"[{ServiceName}] File input not found. Is the page fully loaded?";

            // ── 2. Inject the file directly via DOM.setFileInputFiles ──────────
            // CDP expects forward slashes on all platforms.
            string cdpPath = filePath.Replace('\\', '/');
            progress?.Report($"[{ServiceName}] Setting file via CDP…");

            // ── 2a. Arm a one-shot 'change' listener on the SAME element ────────
            // We need to know after the next CDP call whether Chromium's
            // setFileInputFiles already fired 'change' for us.  Using a
            // listener directly on the file input is fully deterministic;
            // polling for a chip is not (slow uploads like Claude render
            // their chip after CDP has long since dispatched the event, which
            // made our previous chip-poll approach fire a duplicate manual
            // change for Claude).
            await Core.CallDevToolsProtocolMethodAsync(
                "Runtime.callFunctionOn",
                JsonSerializer.Serialize(new
                {
                    objectId            = objectId,
                    functionDeclaration = @"function() {
    this.__lcChangeFired = false;
    this.__lcChangeListener = () => { this.__lcChangeFired = true; };
    this.addEventListener('change', this.__lcChangeListener, { once: true });
}",
                    returnByValue       = false
                }));

            // ── 2b. Inject the file via CDP. ─────────────────────────────────────
            await Core.CallDevToolsProtocolMethodAsync(
                "DOM.setFileInputFiles",
                JsonSerializer.Serialize(new
                {
                    objectId = objectId,
                    files    = new[] { cdpPath }
                }));

            // ── 2c. Give Chromium a brief window to auto-dispatch change. ───────
            await Task.Delay(150);

            // ── 2d. Read the listener flag back from the input itself. ──────────
            string flagJson = await Core.CallDevToolsProtocolMethodAsync(
                "Runtime.callFunctionOn",
                JsonSerializer.Serialize(new
                {
                    objectId            = objectId,
                    functionDeclaration = "function() { return !!this.__lcChangeFired; }",
                    returnByValue       = true
                }));

            bool autoDispatched = false;
            try
            {
                using var flagDoc = JsonDocument.Parse(flagJson);
                if (flagDoc.RootElement.TryGetProperty("result", out var rp) &&
                    rp.TryGetProperty("value", out var vp) &&
                    vp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    autoDispatched = vp.GetBoolean();
                }
            }
            catch { /* fall through — treat as not auto-dispatched */ }

            // ── 2e. Clean up our listener/flag from the DOM element. ────────────
            await Core.CallDevToolsProtocolMethodAsync(
                "Runtime.callFunctionOn",
                JsonSerializer.Serialize(new
                {
                    objectId            = objectId,
                    functionDeclaration = @"function() {
    if (this.__lcChangeListener) {
        try { this.removeEventListener('change', this.__lcChangeListener); } catch (e) {}
        delete this.__lcChangeListener;
    }
    delete this.__lcChangeFired;
}",
                    returnByValue       = false
                }));

            // ── 3. Dispatch 'change' manually only if CDP didn't auto-fire it. ──
            if (!autoDispatched)
            {
                await RunJsAsync($@"
(function() {{
    const el = document.querySelector({selectorJson});
    if (!el) return;
    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
}})()");
            }

            // ── 4. Dismiss any "already uploaded" alert that may appear. ────────
            await Task.Delay(400);
            await DismissOneButtonDialogAsync();

            // ── 5. Poll for attachment-preview chip ────────────────────────────
            progress?.Report($"[{ServiceName}] Waiting for upload to finish…");
            bool confirmed = await WaitForAttachmentAsync(progress);

            return confirmed
                ? $"[{ServiceName}] File uploaded successfully."
                : $"[{ServiceName}] Upload timed out — file may not have been accepted.";
        }
        catch (Exception ex)
        {
            return $"[{ServiceName}] CDP error: {ex.Message}";
        }
    }

    /// <summary>
    /// Focuses the chat text input inside the browser via JavaScript.
    /// </summary>
    public async Task FocusChatInputAsync()
    {
        await RunJsAsync($@"
(function() {{
    const el = {EffInputJs};
    if (el) {{ el.focus(); el.click(); }}
}})()");
    }

    /// <summary>
    /// Polls until the site shows an attachment-preview chip, meaning the
    /// file has been accepted.  Returns true on confirmation, false on timeout.
    /// Any one-button "already uploaded" style dialogs are auto-dismissed each cycle.
    /// </summary>
    public async Task<bool> WaitForAttachmentAsync(IProgress<string>? progress = null)
    {
        int elapsed = 0;
        while (elapsed < FileUploadMaxWaitMs)
        {
            await Task.Delay(FileUploadPollMs);
            elapsed += FileUploadPollMs;

            // Dismiss any blocking alert (e.g. ChatGPT's "already uploaded" modal)
            // before checking for the chip so it doesn't block the selector query.
            await DismissOneButtonDialogAsync();

            string? found = await RunJsAsync(
                $"!!({FileAttachmentIndicatorJs}) ? 'FOUND' : 'NOT_FOUND'");

            if (found == "FOUND") return true;

            if (elapsed % 2000 == 0)
                progress?.Report($"[{ServiceName}] Uploading… ({elapsed / 1000}s)");
        }
        return false;
    }

    /// <summary>
    /// Clicks the sole button inside any open <c>[role="dialog"]</c> whose text
    /// is "OK", "Close", or "Dismiss" — handles ChatGPT's "already uploaded" and
    /// similar one-button info modals without disrupting the main UI.
    /// </summary>
    private async Task DismissOneButtonDialogAsync()
    {
        await RunJsAsync(@"
(function() {
    const dialogs = document.querySelectorAll('[role=""dialog""]');
    for (const dlg of dialogs) {
        const btns = Array.from(dlg.querySelectorAll('button'));
        for (const btn of btns) {
            if (/^(ok|close|dismiss)$/i.test(btn.textContent.trim())) {
                btn.click();
                return;
            }
        }
    }
    const alerts = document.querySelectorAll('[role=""alert""], [class*=""toast""]');
    for (const el of alerts) {
        const msg = (el.textContent || '').toLowerCase();
        if (msg.includes('clipboard') || msg.includes(""couldn't copy"")) {
            const close = el.querySelector('button');
            if (close) close.click();
        }
    }
})()");
    }

    /// <summary>
    /// Waits until the AI has finished streaming its response.
    /// </summary>
    public async Task WaitForGenerationCompleteAsync(IProgress<string>? progress = null)
    {
        await WaitForLastResponseTextAsync(progress, baseline: null);
    }

    protected virtual string AssistantMessageCountJs => @"
(function() {
    const a = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (a.length) return a.length;
    const b = document.querySelectorAll('[data-testid=""assistant-message""]');
    if (b.length) return b.length;
    return document.querySelectorAll('.font-claude-message').length;
})()";

    protected virtual string LastAssistantTextLengthJs => @"
(function() {
    const t = (function() {
        const msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
        const last = msgs[msgs.length - 1];
        if (!last) return '';
        const block = last.querySelector('[class*=""markdown""]')
            || last.querySelector('.prose')
            || last.querySelector('.markdown')
            || last;
        return (block.innerText || block.textContent || '').trim();
    })();
    return t.length;
})()";

    private async Task<bool> IsGeneratingAsync()
    {
        string? r = await RunJsAsync($"({EffGeneratingJs}) ? 'YES' : 'NO'");
        return r == "YES";
    }

    private async Task<int> GetAssistantMessageCountAsync()
    {
        string? raw = await RunJsAsync($"(function(){{ return ({AssistantMessageCountJs}); }})()");
        return int.TryParse(raw, out int n) ? n : 0;
    }

    private async Task<int> GetLastAssistantTextLengthAsync()
    {
        string? raw = await RunJsAsync($"(function(){{ return ({LastAssistantTextLengthJs}); }})()");
        return int.TryParse(raw, out int n) ? n : 0;
    }

    public readonly record struct ResponseWaitBaseline(
        int AssistantCount,
        int LastTextLength,
        string LastExtractedSnapshot = "");

    protected virtual string ScrollLastAssistantIntoViewJs => @"
(function() {
    let msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (!msgs.length) msgs = document.querySelectorAll('[data-testid=""assistant-message""]');
    if (!msgs.length) msgs = document.querySelectorAll('.font-claude-message');
    const last = msgs[msgs.length - 1];
    if (!last) return false;
    last.scrollIntoView({ block: 'end', behavior: 'instant' });
    return true;
})()";

    private async Task<bool> IsLikelyNewAssistantReplyAsync(ResponseWaitBaseline baseline)
    {
        if (await GetAssistantMessageCountAsync() > baseline.AssistantCount)
            return true;
        if (await GetLastAssistantTextLengthAsync() > baseline.LastTextLength + 32)
            return true;
        return false;
    }

    private static bool LooksLikeIngestionPayload(string text)
    {
        string t = text.Trim();
        return t.Contains("```json", StringComparison.OrdinalIgnoreCase)
            || (t.StartsWith('{') && t.Contains('}'));
    }

    private static bool IsSubstantiallyNewResponse(string? text, ResponseWaitBaseline baseline)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        string snap = baseline.LastExtractedSnapshot.Trim();

        if (!string.IsNullOrEmpty(snap) && trimmed == snap)
            return false;

        if (string.IsNullOrEmpty(snap) && LooksLikeIngestionPayload(trimmed))
            return true;

        if (!string.IsNullOrEmpty(snap) && trimmed.Length > snap.Length + 24)
            return true;

        if (LooksLikeIngestionPayload(trimmed) && !LooksLikeIngestionPayload(snap))
            return true;

        return trimmed.Length > baseline.LastTextLength + 32;
    }

    // ─── Finish flag ──────────────────────────────────────────────────────────

    /// <summary>URL fragments of the streaming POST that carries the model's reply.</summary>
    protected virtual string[] StreamUrlIncludes => [];

    /// <summary>URL fragments of POSTs to ignore (title generation, prepare, feedback…).</summary>
    protected virtual string[] StreamUrlExcludes => [];

    /// <summary>
    /// DOM finish flag: true once the last assistant turn shows its action bar
    /// (Copy / Good response / Regenerate). Climbing stops before reaching a container
    /// that holds more than one message, so earlier turns can't trigger it.
    /// </summary>
    protected virtual string ResponseCompleteJs => @"
(function() {
    let msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (!msgs.length) msgs = document.querySelectorAll('[data-testid=""assistant-message""]');
    if (!msgs.length) msgs = document.querySelectorAll('.font-claude-message');
    const last = msgs[msgs.length - 1];
    if (!last) return false;
    let el = last;
    for (let i = 0; i < 6 && el; i++) {
        const multi = el.querySelectorAll('[data-message-author-role], .font-claude-message').length;
        if (multi > 1) break;
        const btns = el.querySelectorAll('button');
        for (const b of btns) {
            const l = ((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '')).toLowerCase();
            if (/copy|good response|bad response|regenerate|retry|read aloud/.test(l)) return true;
        }
        el = el.parentElement;
    }
    return false;
})()";

    private async Task<bool> IsDomResponseCompleteAsync(ResponseWaitBaseline b)
    {
        if (await GetAssistantMessageCountAsync() <= b.AssistantCount)
            return false;

        string? r = await RunJsAsync($"({ResponseCompleteJs}) ? 'YES' : 'NO'");
        return r == "YES";
    }

    private async Task<GenerationFinishWatcher?> TryStartFinishWatcherAsync()
    {
        try { return await GenerationFinishWatcher.StartAsync(Core, StreamUrlIncludes, StreamUrlExcludes); }
        catch { return null; }
    }

    /// <summary>
    /// Waits for the reply to finish, then returns its text. Completion is decided by a
    /// finish flag, not by guessing from text stability:
    /// <list type="number">
    /// <item>network flag — the streaming POST completed (<see cref="GenerationFinishWatcher"/>)</item>
    /// <item>DOM flag — last assistant turn shows its action bar and the stop button is gone</item>
    /// <item>content flag (last resort) — new JSON payload unchanged for several polls</item>
    /// </list>
    /// </summary>
    internal async Task<string?> WaitForLastResponseTextAsync(
        IProgress<string>? progress,
        ResponseWaitBaseline? baseline,
        GenerationFinishWatcher? watcher = null,
        CancellationToken ct = default)
    {
        ResponseWaitBaseline b = baseline
            ?? new ResponseWaitBaseline(
                await GetAssistantMessageCountAsync(),
                await GetLastAssistantTextLengthAsync());

        const int pollMs = 400;
        const int maxWaitMs = 600_000;
        var sw = Stopwatch.StartNew();
        long lastReportMs = -5_000;
        string flag = "";

        string? contentPrev = null;
        int contentStable = 0;

        progress?.Report($"[{ServiceName}] Waiting for reply…");

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            bool streamSeen = watcher?.StreamStarted == true;

            // Flag 1 — network: streaming POST finished.
            if (streamSeen && watcher!.IsFinished(TimeSpan.FromMilliseconds(1200)))
            {
                flag = "network";
                break;
            }

            bool generating = await IsGeneratingAsync();

            // Flag 2 — DOM: action bar present on the new assistant turn.
            if (!generating && await IsDomResponseCompleteAsync(b))
            {
                flag = "dom";
                break;
            }

            // Flag 3 — content (only when the network flag is unavailable).
            if (!streamSeen && !generating && sw.ElapsedMilliseconds > 10_000)
            {
                string? text = await ReadLastResponseTextAsync(b.AssistantCount, b.LastExtractedSnapshot);
                if (!string.IsNullOrWhiteSpace(text) &&
                    LooksLikeIngestionPayload(text))
                {
                    if (text == contentPrev) contentStable++;
                    else { contentStable = 0; contentPrev = text; }

                    if (contentStable >= 4)
                    {
                        flag = "content";
                        break;
                    }
                }
            }

            if (sw.ElapsedMilliseconds - lastReportMs >= 5_000)
            {
                lastReportMs = sw.ElapsedMilliseconds;
                string state = streamSeen ? "streaming" : generating ? "generating" : "waiting";
                progress?.Report($"[{ServiceName}] {state}… ({sw.ElapsedMilliseconds / 1000}s)");
            }

            await Task.Delay(pollMs, ct);
        }

        progress?.Report(string.IsNullOrEmpty(flag)
            ? $"[{ServiceName}] Timed out waiting for finish flag — reading page…"
            : $"[{ServiceName}] Finished ({flag} flag) — reading response…");

        // Let the stop button disappear and the DOM commit the final text.
        for (int i = 0; i < 25; i++)
        {
            if (!await IsGeneratingAsync()) break;
            await Task.Delay(300, ct);
        }

        // Read-back phase. Hard-bounded (≤ 45 s) and reports every attempt, so it can never
        // look frozen. The stale snapshot is skipped inside ReadLastResponseTextAsync.
        string? best = null;
        string? previous = null;
        var readSw = Stopwatch.StartNew();
        for (int i = 0; i < 40 && readSw.Elapsed < TimeSpan.FromSeconds(45); i++)
        {
            await Task.Delay(i == 0 ? 300 : 600, ct);

            string? text = await ReadLastResponseTextAsync(b.AssistantCount, b.LastExtractedSnapshot);
            progress?.Report(
                $"[{ServiceName}] Reading response… attempt {i + 1} " +
                (text is null ? "(nothing new yet)" : $"({text.Length} chars)"));

            if (text is null)
                continue;

            best = text;
            if (text == previous)
                return text;       // two identical fresh reads in a row → final

            previous = text;
        }

        // Out of time: a fresh-but-unconfirmed read is still better than nothing.
        return best;
    }

    /// <summary>Compact selector/DOM stats appended to the error when no text can be read.</summary>
    private async Task<string> DescribeDomStateAsync()
    {
        string? diag = await RunJsAsync(PageJsonScanScripts.DomDiagnostics);
        return string.IsNullOrWhiteSpace(diag) ? "no diagnostics" : diag;
    }

    /// <summary>Reads assistant text for the reply at/after <paramref name="assistantIndexHint"/>.</summary>
    protected virtual Task<string?> ExtractLastResponseTextAsync(int assistantIndexHint) =>
        GetLastResponseTextAsync();

    /// <summary>Per-strategy outcome of the most recent read (shown in errors for diagnosis).</summary>
    private string _readTrace = "";

    /// <summary>
    /// Reads the newest reply. Tries each strategy in turn and SKIPS any result that is the
    /// pre-send reply (<paramref name="staleSnapshot"/>) — so a selector that keeps returning
    /// the previous message can no longer block the page-text scanner behind it.
    /// </summary>
    private async Task<string?> ReadLastResponseTextAsync(
        int assistantIndexHint = 0,
        string? staleSnapshot = null)
    {
        await RunJsAsync($"(function(){{ return ({ScrollLastAssistantIntoViewJs}); }})()");

        var trace = new StringBuilder();

        string? Pick(string name, string? t)
        {
            if (t?.StartsWith("JS_ERROR:", StringComparison.Ordinal) == true)
            {
                trace.Append(name).Append("=js-error ");
                return null;
            }

            if (string.IsNullOrWhiteSpace(t))
            {
                trace.Append(name).Append("=empty ");
                return null;
            }

            t = t.Trim();
            if (IsStale(t, staleSnapshot))
            {
                trace.Append(name).Append("=stale ");
                return null;
            }

            trace.Append(name).Append("=ok(").Append(t.Length).Append(") ");
            return t;
        }

        // `??` is lazy: later strategies only run when earlier ones gave nothing fresh.
        string? result =
            Pick("container", await ExtractLastResponseTextAsync(assistantIndexHint))
            ?? Pick("lastMsg", await GetLastResponseTextAsync())
            ?? Pick("html", HtmlOrNull(await GetLastResponseHtmlAsync()))
            ?? Pick("pageJson", await RunJsAsync(PageJsonScanScripts.LastJsonObject))
            ?? Pick("pageJsonLoose", await RunJsAsync(PageJsonScanScripts.LastJsonObjectLoose));

        _readTrace = trace.ToString().Trim();
        return result;
    }

    private static string? HtmlOrNull(string? html) =>
        string.IsNullOrWhiteSpace(html) || html.StartsWith("JS_ERROR:", StringComparison.Ordinal)
            ? html
            : HtmlToPlainText(html);

    // ─── Staleness (is this still the previous reply?) ───────────────────────

    private static bool IsStale(string text, string? snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot) && Fingerprint(text) == Fingerprint(snapshot);

    /// <summary>
    /// Comparable identity of a reply: its last balanced <c>{ "…" }</c> object if it has one,
    /// otherwise the trimmed text. Lets "whole message" text and "JSON only" text of the same
    /// reply compare equal.
    /// </summary>
    private static string Fingerprint(string text) => (LastBalancedObject(text) ?? text).Trim();

    private static string? LastBalancedObject(string text)
    {
        var starts = new List<int>();
        foreach (Match m in Regex.Matches(text, @"\{\s*"""))
            starts.Add(m.Index);

        for (int s = starts.Count - 1, tried = 0; s >= 0 && tried < 80; s--, tried++)
        {
            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = starts[s]; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }

                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                    return text[starts[s]..(i + 1)];
            }
        }

        return null;
    }

    private static string HtmlToPlainText(string html)
    {
        string s = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = Regex.Replace(s, @"\s+\n", "\n");
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        return s.Trim();
    }

    /// <summary>
    /// Returns the <c>innerHTML</c> of the last AI response message, or
    /// <c>null</c> if nothing could be found.
    /// </summary>
    public Task<string?> GetLastResponseHtmlAsync() =>
        RunJsAsync($"(function(){{ return ({LastResponseHtmlJs}); }})()");

    /// <summary>Plain text of the last assistant message (for JSON ingestion).</summary>
    public virtual Task<string?> GetLastResponseTextAsync() =>
        RunJsAsync($"(function(){{ return ({LastResponseTextJs}); }})()");

    protected virtual string LastResponseTextJs => @"
(function() {
    function pickText(el) {
        if (!el) return '';
        return (el.innerText || el.textContent || '').trim();
    }
    let msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (!msgs.length) msgs = document.querySelectorAll('[data-testid=""assistant-message""]');
    if (!msgs.length) {
        const claude = document.querySelectorAll('.font-claude-message');
        if (claude.length) return pickText(claude[claude.length - 1]) || null;
    }
    const last = msgs[msgs.length - 1];
    if (!last) return null;
    const block = last.querySelector('[class*=""markdown""]')
        || last.querySelector('.prose')
        || last.querySelector('.markdown')
        || last.querySelector('[class*=""message""]')
        || last;
    const t = pickText(block);
    return t.length ? t : null;
})()";

    /// <summary>Sends a prompt, waits for the model to finish, returns response text.</summary>
    public async Task<string> SendPromptAndWaitForResponseAsync(
        string text,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Empty prompt — nothing sent.";

        if (!IsOnTargetSite())
            return $"Not on {ServiceName}. Navigate there first.";

        int assistantCount = await GetAssistantMessageCountAsync();
        // Snapshot through the SAME pipeline used after the finish flag, so the
        // "is this the old reply?" comparison is apples-to-apples.
        string? snapshot = await ReadLastResponseTextAsync(Math.Max(0, assistantCount - 1));
        var baseline = new ResponseWaitBaseline(
            assistantCount,
            await GetLastAssistantTextLengthAsync(),
            snapshot ?? "");

        // Start listening for the stream BEFORE sending so the request can't be missed.
        using GenerationFinishWatcher? watcher = await TryStartFinishWatcherAsync();

        string sendResult = await SendPromptAsync(text);
        if (!sendResult.EndsWith("Sent.", StringComparison.Ordinal))
            return sendResult;

        string? body = await WaitForLastResponseTextAsync(progress, baseline, watcher, ct);
        if (!string.IsNullOrWhiteSpace(body))
            return body;

        string diag = await DescribeDomStateAsync();
        return $"[{ServiceName}] Response finished but no new message text was found. " +
               $"(read: {_readTrace}; dom: {diag}; " +
               $"stream: {(watcher?.StreamStarted == true ? watcher.LastUrl : "not seen")})";
    }

    /// <summary>Injects text and clicks Send (no file).</summary>
    public async Task<string> SendPromptAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Empty prompt — nothing sent.";

        if (!IsOnTargetSite())
            return $"Not on {ServiceName}. Navigate there first.";

        var sw = Stopwatch.StartNew();
        Log.Information("[{Service}] Sending prompt ({Chars} chars)", ServiceName, text.Length);

        // While a reply streams, ChatGPT/Claude replace Send with Stop, so a hotkey pressed
        // right after the previous question would find no Send button.
        string? blocked = await ClearGenerationAsync();
        if (blocked is not null)
        {
            Log.Warning("[{Service}] {Message}", ServiceName, blocked);
            return blocked;
        }

        string result = "";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            result = await SendOnceAsync(text);
            if (result.EndsWith("Sent.", StringComparison.Ordinal) || !IsRetryable(result))
                break;

            // The composer was not ready (page still settling, focus lost): once more, fresh.
            Log.Warning("[{Service}] Attempt {Attempt} failed: {Result}", ServiceName, attempt, result);
            await Task.Delay(700);
        }

        Log.Information("[{Service}] Send finished in {Ms} ms: {Result}", ServiceName, sw.ElapsedMilliseconds, result);
        return result;
    }

    /// <summary>Only failures that happened BEFORE the text left the composer are safe to repeat.</summary>
    private static bool IsRetryable(string result) =>
        result.Contains("Could not put the text", StringComparison.Ordinal) ||
        result.Contains("Input field not found", StringComparison.Ordinal);

    private async Task<string> SendOnceAsync(string text)
    {
        string injectResult = await InjectTextAsync(text);
        if (injectResult == "INSERT_FAILED")
            return $"[{ServiceName}] Could not put the text into the chat box ({SizeNote(text)}; {await DescribeSendBlockerAsync()}).";
        if (injectResult != "OK")
            return $"[{ServiceName}] Input field not found ({injectResult}).";

        // A short question needs no settling time; a huge prompt gets the full delay.
        await Task.Delay(text.Length < 2_000 ? TimeSpan.FromMilliseconds(120) : SendDelay);

        string clickResult = await ClickSendWhenReadyAsync();
        return clickResult switch
        {
            "SENT"          => $"[{ServiceName}] Sent.",
            "BTN_DISABLED"  => $"[{ServiceName}] Send button stayed disabled for {SendReadyMaxWaitMs / 1000}s ({SizeNote(text)}; {await DescribeSendBlockerAsync()}).",
            "BTN_NOT_FOUND" => $"[{ServiceName}] Send button not found ({SizeNote(text)}; {await DescribeSendBlockerAsync()}). " +
                               $"Fix the selector in {SelectorOverrides.DefaultPath}.",
            "NOT_SUBMITTED" => $"[{ServiceName}] Send was clicked but the message was not submitted ({await DescribeSendBlockerAsync()}).",
            _               => $"[{ServiceName}] Unexpected: {clickResult}"
        };
    }

    /// <summary>
    /// Makes room for a new prompt. Returns null when the chat is idle, otherwise an error
    /// message. A reply that is still streaming after <see cref="GenerationGrace"/> is stopped
    /// (unless <see cref="InterruptGeneration"/> is off, then it is waited out for up to 45 s).
    /// </summary>
    private async Task<string?> ClearGenerationAsync()
    {
        if (!await IsGeneratingAsync())
            return null;

        if (await WaitWhileGeneratingAsync(GenerationGrace))
            return null;

        if (InterruptGeneration)
        {
            string stopped = await RunJsAsync($@"
(function() {{
    const btn = {EffStopJs};
    if (!btn) return 'NO_STOP';
    btn.click();
    return 'STOPPED';
}})()") ?? "NO_STOP";

            Log.Information("[{Service}] Previous reply still streaming: stop -> {Result}", ServiceName, stopped);

            if (stopped == "STOPPED" && await WaitWhileGeneratingAsync(TimeSpan.FromSeconds(5)))
            {
                await Task.Delay(250);   // let the UI swap Stop back to Send
                return null;
            }
        }

        if (await WaitWhileGeneratingAsync(TimeSpan.FromSeconds(45)))
            return null;

        return $"[{ServiceName}] The previous answer is still generating — press again when it finishes.";
    }

    /// <summary>
    /// "prompt 124,724 chars ≈ 31,181 tokens" — shown on failures because chat sites cap the
    /// size of a single message (≈32k tokens on some plans) and disable Send beyond it.
    /// </summary>
    private static string SizeNote(string text) =>
        $"prompt {text.Length:N0} chars ≈ {text.Length / 4:N0} tokens";

    /// <summary>Returns true once no reply is streaming; false if it is still generating after <paramref name="max"/>.</summary>
    private async Task<bool> WaitWhileGeneratingAsync(TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (await IsGeneratingAsync())
        {
            if (sw.Elapsed >= max) return false;
            await Task.Delay(500);
        }
        return true;
    }

    // ─── JS helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="text"/> into the chat composer and VERIFIES that it landed.
    ///
    /// Why this is not just <c>execCommand('insertText')</c>:
    /// that call only works reliably while the page has focus. With a global hotkey the
    /// foreground app is something else (the interview window), and after a click on the
    /// Send button the focus sits on a WPF control. The composer then silently stays empty
    /// and the Send button never enables. So we
    ///   1. make the page behave as focused (CDP focus emulation),
    ///   2. select the composer contents and insert through the browser's real editing
    ///      pipeline (CDP <c>Input.insertText</c>), which also fires the input events the
    ///      page's framework listens for,
    ///   3. check the composer really holds the text, falling back to execCommand.
    /// </summary>
    /// <summary>
    /// Puts <paramref name="text"/> into the chat composer and VERIFIES that it landed.
    ///
    /// Why this is not just <c>execCommand('insertText')</c>:
    /// that call only works reliably while the page has focus. With a global hotkey the
    /// foreground app is something else (the interview window), and after a click on the
    /// Send button the focus sits on a WPF control. The composer then silently stays empty
    /// and the Send button never enables. So we
    ///   1. make the page behave as focused (CDP focus emulation),
    ///   2. select the composer contents and insert through the browser's real editing
    ///      pipeline (CDP <c>Input.insertText</c>), which also fires the input events the
    ///      page's framework listens for,
    ///   3. check the composer really holds the text, falling back to execCommand.
    /// </summary>
    private async Task<string> InjectTextAsync(string text)
    {
        await EnsureFocusEmulationAsync();

        string kind = await RunJsAsync(PrepareComposerScript()) ?? "JS_RETURNED_NULL";
        if (kind != "CE" && kind != "TA")
            return kind;                                   // INPUT_NOT_FOUND / JS_ERROR…

        int expected = CountNonWhitespace(text);

        if (kind == "CE")
        {
            // 1) Native editing pipeline.
            try
            {
                await Core.CallDevToolsProtocolMethodAsync(
                    "Input.insertText", JsonSerializer.Serialize(new { text }));
            }
            catch { /* fall through to execCommand */ }

            if (await ComposerHoldsTextAsync(expected)) return "OK";

            // 2) Legacy path.
            await RunJsAsync(InsertViaExecCommandScript(text));
            return await ComposerHoldsTextAsync(expected) ? "OK" : "INSERT_FAILED";
        }

        await RunJsAsync(SetTextareaValueScript(text));
        return await ComposerHoldsTextAsync(expected) ? "OK" : "INSERT_FAILED";
    }

    /// <summary>Lets the page believe it is focused/visible even if another app has OS focus.</summary>
    private async Task EnsureFocusEmulationAsync()
    {
        try
        {
            await Core.CallDevToolsProtocolMethodAsync(
                "Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
        }
        catch { /* best effort — older runtimes may not support it */ }
    }

    private static int CountNonWhitespace(string s)
    {
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    /// <summary>
    /// Polls (up to ~8 s — a 100k+ character paste takes the editor a while) until the composer
    /// contains at least 95 % of the expected non-whitespace characters.
    /// </summary>
    private async Task<bool> ComposerHoldsTextAsync(int expectedNonWs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            string? raw = await RunJsAsync(ComposerLengthScript());
            if (int.TryParse(raw, out int have) && have >= expectedNonWs * 0.95)
                return true;
            if (sw.ElapsedMilliseconds >= 8_000) return false;
            await Task.Delay(200);
        }
    }

    private string PrepareComposerScript() => $@"
(function() {{
    const el = {EffInputJs};
    if (!el) return 'INPUT_NOT_FOUND';
    try {{ window.focus(); }} catch (_) {{}}
    el.focus();
    if (el.isContentEditable) {{
        const sel = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(el);
        sel.removeAllRanges();
        sel.addRange(range);
        return 'CE';
    }}
    return 'TA';
}})()";

    private string ComposerLengthScript() => $@"
(function() {{
    const el = {EffInputJs};
    if (!el) return -1;
    const t = (el.value !== undefined ? el.value : (el.textContent || '')) + '';
    return t.replace(/\s+/g, '').length;
}})()";

    private string InsertViaExecCommandScript(string text) => $@"
(function() {{
    const el = {EffInputJs};
    if (!el) return 'INPUT_NOT_FOUND';
    el.focus();
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, {JsString(text)});
    el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true }}));
    return 'OK';
}})()";

    private string SetTextareaValueScript(string text) => $@"
(function() {{
    const el = {EffInputJs};
    if (!el) return 'INPUT_NOT_FOUND';
    el.focus();
    try {{
        const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
        setter.call(el, {JsString(text)});
    }} catch (_) {{ el.value = {JsString(text)}; }}
    el.dispatchEvent(new InputEvent('input',  {{ bubbles: true, cancelable: true }}));
    el.dispatchEvent(new Event ('change', {{ bubbles: true }}));
    return 'OK';
}})()";

    /// <summary>One-line state dump used when Send can't be enabled — says WHY.</summary>
    private async Task<string> DescribeSendBlockerAsync()
    {
        string script = $@"
(function() {{
    const el = {EffInputJs};
    let chars = -1;
    if (el) chars = ((el.value !== undefined ? el.value : (el.textContent || '')) + '').replace(/\s+/g, '').length;
    const btn = {EffSendJs};
    const alerts = Array.from(document.querySelectorAll('[role=""alert""], [class*=""toast""]'))
        .map(a => (a.innerText || '').trim()).filter(t => t).slice(0, 2).join(' | ').slice(0, 160);
    return JSON.stringify({{
        composerChars: chars,
        sendButton: btn ? (btn.getAttribute('data-testid') || btn.getAttribute('aria-label') || 'button') : null,
        visibility: document.visibilityState,
        hasFocus: document.hasFocus(),
        alert: alerts
    }});
}})()";
        return await RunJsAsync(script) ?? "no page state";
    }

    // A very large prompt (Prompt.txt is ~125k chars) takes the page's editor a while to
    // register, and the Send button stays disabled until it has. A fixed delay + a single
    // click therefore "loads" the prompt but never submits it. Instead we wait until the
    // button is actually enabled, click, then CONFIRM the message was submitted.
    protected virtual int SendReadyMaxWaitMs => 30_000;
    protected virtual int SendReadyPollMs    => 250;
    protected virtual int SendVerifyMaxMs    => 8_000;
    protected virtual int SendMaxAttempts    => 3;

    private async Task<string> ClickSendWhenReadyAsync()
    {
        string last = "BTN_NOT_FOUND";

        for (int attempt = 1; attempt <= SendMaxAttempts; attempt++)
        {
            // 1) Wait for the Send button to exist AND be enabled.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            bool enterTried = false;
            while (true)
            {
                last = await RunJsAsync(SendReadyProbeScript()) ?? "JS_RETURNED_NULL";
                if (last == "READY") break;

                // No Send button at all usually means the site changed its markup. Both sites
                // submit on Enter, so try that before giving up.
                if (last == "BTN_NOT_FOUND" && !enterTried && wait.ElapsedMilliseconds >= EnterFallbackAfterMs)
                {
                    enterTried = true;
                    Log.Warning("[{Service}] Send button not found, pressing Enter instead", ServiceName);
                    if (await PressEnterInComposerAsync() && await WaitForSubmissionAsync())
                        return "SENT";
                }

                if (wait.ElapsedMilliseconds >= SendReadyMaxWaitMs) return last;
                await Task.Delay(SendReadyPollMs);
            }

            // 2) Click.
            last = await RunJsAsync(ClickSendScript()) ?? "JS_RETURNED_NULL";
            if (last != "SENT") continue;                       // button vanished/disabled again → re-wait

            // 3) Confirm submission. Only if the text is STILL sitting unsent do we loop and
            //    click again, so a slow-but-successful submit can never be sent twice.
            if (await WaitForSubmissionAsync()) return "SENT";
            last = "NOT_SUBMITTED";
        }

        return last;
    }

    /// <summary>True once the reply started streaming or the composer was cleared.</summary>
    private async Task<bool> WaitForSubmissionAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < SendVerifyMaxMs)
        {
            await Task.Delay(SendReadyPollMs);
            string? r = await RunJsAsync(SubmittedProbeScript());
            if (r == "YES") return true;
        }
        return false;
    }

    /// <summary>Focuses the composer and sends a real Enter key press through the browser's input pipeline.</summary>
    private async Task<bool> PressEnterInComposerAsync()
    {
        try
        {
            await EnsureFocusEmulationAsync();
            await RunJsAsync($"(function() {{ const el = {EffInputJs}; if (el) el.focus(); }})()");

            foreach (string type in new[] { "keyDown", "keyUp" })
            {
                // Optional CDP parameters must be omitted, not sent as null.
                var args = new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["key"] = "Enter",
                    ["code"] = "Enter",
                    ["windowsVirtualKeyCode"] = 13,
                    ["nativeVirtualKeyCode"] = 13
                };
                if (type == "keyDown")
                    args["text"] = "\r";

                await Core.CallDevToolsProtocolMethodAsync(
                    "Input.dispatchKeyEvent", JsonSerializer.Serialize(args));
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[{Service}] Enter key fallback failed", ServiceName);
            return false;
        }
    }

    private string SendReadyProbeScript() => $@"
(function() {{
    const btn = {EffSendJs};
    if (!btn) return 'BTN_NOT_FOUND';
    if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'BTN_DISABLED';
    return 'READY';
}})()";

    private string ClickSendScript() => $@"
(function() {{
    const btn = {EffSendJs};
    if (!btn)         return 'BTN_NOT_FOUND';
    if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'BTN_DISABLED';
    btn.click();
    return 'SENT';
}})()";

    private string SubmittedProbeScript() => $@"
(function() {{
    // Reply already streaming → definitely submitted.
    if ({EffGeneratingJs}) return 'YES';
    // Composer emptied → the page took the message.
    const el = {EffInputJs};
    if (!el) return 'NO';
    const txt = (el.value !== undefined ? el.value : (el.innerText || el.textContent || '')) + '';
    return txt.trim().length === 0 ? 'YES' : 'NO';
}})()";

    // ─── Utilities ────────────────────────────────────────────────────────────

    protected async Task<string?> RunJsAsync(string script)
    {
        try
        {
            string raw = await Core.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw is "null" or "undefined") return null;
            if (raw[0] != '"')
                return raw;

            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return UnescapeJsonStringLiteral(raw);
            }
        }
        catch (Exception ex) { return $"JS_ERROR:{ex.Message}"; }
    }

    private static string UnescapeJsonStringLiteral(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];
        return Regex.Unescape(raw);
    }

    protected static string JsString(string s) => JsonSerializer.Serialize(s);
}
