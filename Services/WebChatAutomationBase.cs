using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Web.WebView2.Core;

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
        string src = Core.Source ?? string.Empty;
        foreach (string host in TargetHosts)
            if (src.Contains(host, StringComparison.OrdinalIgnoreCase))
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
    const el = {InputSelectorJs};
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
})()");
    }

    /// <summary>
    /// Waits until the AI has finished streaming its response.
    /// First polls for the stop-button to appear (generation started),
    /// then polls for it to disappear (generation finished).
    /// </summary>
    public async Task WaitForGenerationCompleteAsync(IProgress<string>? progress = null)
    {
        // ── Phase 1: wait for generation to START (up to 8 s) ────────────────
        for (int i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            string? started = await RunJsAsync($"({IsGeneratingJs}) ? 'YES' : 'NO'");
            if (started == "YES") break;
        }

        // ── Phase 2: wait for generation to END (up to 3 min) ────────────────
        int elapsed = 0;
        const int maxMs = 180_000;
        while (elapsed < maxMs)
        {
            await Task.Delay(1_000);
            elapsed += 1_000;

            string? still = await RunJsAsync($"({IsGeneratingJs}) ? 'YES' : 'NO'");
            if (still != "YES") return;   // done!

            if (elapsed % 5_000 == 0)
                progress?.Report($"[{ServiceName}] Generating… ({elapsed / 1000}s)");
        }
    }

    /// <summary>
    /// Returns the <c>innerHTML</c> of the last AI response message, or
    /// <c>null</c> if nothing could be found.
    /// </summary>
    public Task<string?> GetLastResponseHtmlAsync() =>
        RunJsAsync($"(function(){{ return ({LastResponseHtmlJs}); }})()");

    /// <summary>Injects text and clicks Send (no file).</summary>
    public async Task<string> SendPromptAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Empty prompt — nothing sent.";

        if (!IsOnTargetSite())
            return $"Not on {ServiceName}. Navigate there first.";

        string injectResult = await InjectTextAsync(text);
        if (injectResult != "OK")
            return $"[{ServiceName}] Input field not found ({injectResult}).";

        await Task.Delay(SendDelay);

        string clickResult = await ClickSendAsync();
        return clickResult switch
        {
            "SENT"          => $"[{ServiceName}] Sent.",
            "BTN_DISABLED"  => $"[{ServiceName}] Send button disabled.",
            "BTN_NOT_FOUND" => $"[{ServiceName}] Send button not found.",
            _               => $"[{ServiceName}] Unexpected: {clickResult}"
        };
    }

    // ─── JS helpers ───────────────────────────────────────────────────────────

    private async Task<string> InjectTextAsync(string text)
    {
        string jsText = JsString(text);
        string script = $@"
(function() {{
    const el = {InputSelectorJs};
    if (!el) return 'INPUT_NOT_FOUND';
    el.focus();
    if (el.isContentEditable) {{
        document.execCommand('selectAll', false, null);
        document.execCommand('insertText', false, {jsText});
        el.dispatchEvent(new InputEvent('input', {{ bubbles: true, cancelable: true }}));
    }} else {{
        try {{
            const setter = Object.getOwnPropertyDescriptor(
                HTMLTextAreaElement.prototype, 'value').set;
            setter.call(el, {jsText});
        }} catch(_) {{ el.value = {jsText}; }}
        el.dispatchEvent(new InputEvent('input',  {{ bubbles: true, cancelable: true }}));
        el.dispatchEvent(new Event ('change', {{ bubbles: true }}));
    }}
    return 'OK';
}})()";
        return await RunJsAsync(script) ?? "JS_RETURNED_NULL";
    }

    private async Task<string> ClickSendAsync()
    {
        string script = $@"
(function() {{
    const btn = {SendButtonJs};
    if (!btn)         return 'BTN_NOT_FOUND';
    if (btn.disabled) return 'BTN_DISABLED';
    btn.click();
    return 'SENT';
}})()";
        return await RunJsAsync(script) ?? "JS_RETURNED_NULL";
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    protected async Task<string?> RunJsAsync(string script)
    {
        try
        {
            string raw = await Core.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw is "null" or "undefined") return null;
            return raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) : raw;
        }
        catch (Exception ex) { return $"JS_ERROR:{ex.Message}"; }
    }

    protected static string JsString(string s) => JsonSerializer.Serialize(s);
}
