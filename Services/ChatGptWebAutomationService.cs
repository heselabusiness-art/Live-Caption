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

    // True while ChatGPT is still streaming (the stop button is present).
    protected override string IsGeneratingJs =>
        @"!!(document.querySelector('[data-testid=""stop-button""]')
            || document.querySelector('button[aria-label=""Stop streaming""]'))";

    // innerHTML of the last assistant message's rendered markdown block.
    protected override string LastResponseHtmlJs => @"
(function() {
    const msgs = document.querySelectorAll('[data-message-author-role=""assistant""]');
    const last  = msgs[msgs.length - 1];
    if (!last) return null;
    return (last.querySelector('.prose')
         || last.querySelector('.markdown')
         || last).innerHTML;
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
