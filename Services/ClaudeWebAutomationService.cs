using Microsoft.Web.WebView2.Core;

namespace LiveCaptionViewer.Services;

// =============================================================================
// NOTE ON SELECTOR FRAGILITY
// Claude's web UI (claude.ai) is updated frequently. If this breaks after a
// deploy, open DevTools inside the WebView2 and update the selectors below.
// Claude's composer uses the ProseMirror editor (a contenteditable div).
// =============================================================================

/// <summary>
/// Automation service for <c>claude.ai</c>.
/// Extends <see cref="WebChatAutomationBase"/> with Claude-specific selectors.
/// </summary>
public sealed class ClaudeWebAutomationService : WebChatAutomationBase
{
    public ClaudeWebAutomationService(CoreWebView2 core) : base(core) { }

    public override string ServiceName => "Claude";

    protected override string[] TargetHosts => ["claude.ai"];

    // Input selectors for Claude's ProseMirror-based composer.
    protected override string InputSelectorJs => @"
        document.querySelector('div.ProseMirror[contenteditable=""true""]')
        || document.querySelector('[data-placeholder][contenteditable=""true""]')
        || document.querySelector('div[contenteditable=""true""]')
        || document.querySelector('textarea')";

    // Send button selectors for Claude.
    protected override string SendButtonJs => @"
        document.querySelector('button[aria-label=""Send Message""]')
        || document.querySelector('button[aria-label*=""Send""]')
        || document.querySelector('button[aria-label*=""send""]')
        || document.querySelector('button[data-testid=""send-button""]')
        || document.querySelector('button[type=""submit""]')";

    // True while Claude is still streaming.
    protected override string IsGeneratingJs =>
        @"!!(document.querySelector('button[aria-label=""Stop""]')
            || document.querySelector('[data-testid=""stop-button""]'))";

    // innerHTML of the last Claude response block.
    protected override string LastResponseHtmlJs => @"
(function() {
    const msgs = document.querySelectorAll('.font-claude-message');
    const last  = msgs[msgs.length - 1];
    if (!last) return null;
    return (last.querySelector('.prose') || last).innerHTML;
})()";

    // Attachment confirmation: Claude shows a file card with a remove button
    // once the upload is complete.
    protected override string FileAttachmentIndicatorJs => @"
        document.querySelector('button[aria-label*=""Remove""]')
        || document.querySelector('[data-testid*=""remove-file""]')
        || document.querySelector('[class*=""attachment""]')
        || document.querySelector('[class*=""FilePreview""]')";
}
