using Microsoft.Web.WebView2.Core;

namespace LiveCaptionViewer.Services;

// =============================================================================
// NOTE ON SELECTOR FRAGILITY
// ChatGPT's web UI is updated frequently. If this breaks after a deploy,
// open DevTools inside the WebView2 and update the selectors below.
// =============================================================================

/// <summary>
/// Automation service for <c>chatgpt.com</c>.
/// Extends <see cref="WebChatAutomationBase"/> with ChatGPT-specific selectors.
/// </summary>
public sealed class ChatGptWebAutomationService : WebChatAutomationBase
{
    public ChatGptWebAutomationService(CoreWebView2 core) : base(core) { }

    public override string ServiceName => "ChatGPT";

    protected override string[] TargetHosts =>
        ["chatgpt.com", "chat.openai.com"];

    // Input selectors — tried in priority order.
    protected override string InputSelectorJs => @"
        document.querySelector('#prompt-textarea')
        || document.querySelector('div[contenteditable=""true""][data-placeholder]')
        || document.querySelector('div[contenteditable=""true""]')
        || document.querySelector('textarea[data-id]')
        || document.querySelector('textarea')";

    // Send button selectors.
    protected override string SendButtonJs => @"
        document.querySelector('[data-testid=""send-button""]')
        || document.querySelector('button[aria-label*=""Send""]')
        || document.querySelector('button[aria-label*=""send""]')
        || document.querySelector('button[type=""submit""]')";

    // Network finish flag: the SSE POST that streams the reply (POST /backend-api/[f/]conversation).
    protected override string[] StreamUrlIncludes =>
        ["/backend-api/conversation", "/backend-api/f/conversation"];

    protected override string[] StreamUrlExcludes =>
        ["/prepare", "/init", "gen_title", "/implicit", "/feedback", "/stream_status",
         "/textdocs", "/attach", "/interpreter", "/delete", "/generate_autocompletions"];

    // True while ChatGPT is still streaming (avoid generic ""Stop"" — matches unrelated buttons).
    protected override string IsGeneratingJs =>
        @"!!(document.querySelector('[data-testid=""stop-button""]:not([disabled])')
            || document.querySelector('button[aria-label=""Stop streaming""]:not([disabled])')
            || document.querySelector('button[aria-label=""Stop generating""]:not([disabled])'))";

    protected override string AssistantMessageCountJs =>
        WrapExtractionScript("countAssistantContainers()");

    protected override string LastAssistantTextLengthJs =>
        WrapExtractionScript("(function(){ const t = extractAssistantText(Math.max(0, countAssistantContainers() - 1)); return t ? t.length : 0; })()");

    protected override Task<string?> ExtractLastResponseTextAsync(int assistantIndexHint) =>
        RunJsAsync(WrapExtractionScript($"extractAssistantText({assistantIndexHint})"));

    private static string WrapExtractionScript(string returnExpression) =>
        "(function() {" + ChatGptResponseExtractionScripts.ExtractAssistantText +
        " return " + returnExpression + "; })()";

    protected override string LastResponseTextJs => @"
(function() {
    function pickText(el) {
        if (!el) return '';
        return (el.innerText || el.textContent || '').trim();
    }
    let msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (!msgs.length) {
        const turns = document.querySelectorAll('[data-testid=""conversation-turn""]');
        const assistantTurns = Array.from(turns).filter(t =>
            t.querySelector('[data-message-author-role=""assistant""]')
            || t.getAttribute('data-turn') === 'assistant');
        if (assistantTurns.length) {
            const turn = assistantTurns[assistantTurns.length - 1];
            const inner = turn.querySelector('[data-message-author-role=""assistant""]') || turn;
            const block = inner.querySelector('[class*=""markdown""]')
                || inner.querySelector('.prose')
                || inner;
            const t = pickText(block);
            if (t.length) return t;
        }
    }
    const last = msgs[msgs.length - 1];
    if (!last) return null;
    const block = last.querySelector('[class*=""markdown""]')
        || last.querySelector('.prose')
        || last.querySelector('.markdown')
        || last;
    const t = pickText(block);
    return t.length ? t : null;
})()";

    // innerHTML of the last assistant message's rendered markdown block.
    protected override string LastResponseHtmlJs => @"
(function() {
    function pickHtml(el) {
        if (!el) return null;
        const block = el.querySelector('[class*=""markdown""]')
            || el.querySelector('.prose')
            || el.querySelector('.markdown')
            || el;
        return block.innerHTML || null;
    }
    let msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    if (!msgs.length) {
        const turns = document.querySelectorAll('[data-testid=""conversation-turn""]');
        const assistantTurns = Array.from(turns).filter(t =>
            t.querySelector('[data-message-author-role=""assistant""]')
            || t.getAttribute('data-turn') === 'assistant');
        if (assistantTurns.length) {
            const turn = assistantTurns[assistantTurns.length - 1];
            const inner = turn.querySelector('[data-message-author-role=""assistant""]') || turn;
            return pickHtml(inner);
        }
    }
    const last = msgs[msgs.length - 1];
    return pickHtml(last);
})()";

    // Attachment confirmation: ChatGPT shows a preview chip with a remove button
    // once the file has been accepted and indexed.
    protected override string FileAttachmentIndicatorJs => @"
        document.querySelector('[data-testid=""file-attachment""]')
        || document.querySelector('button[aria-label*=""Remove file""]')
        || document.querySelector('button[aria-label*=""Remove attachment""]')
        || document.querySelector('[class*=""FilePreview""]')
        || document.querySelector('[class*=""file-preview""]')";
}
