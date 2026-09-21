/** Inject text / files into ChatGPT or Claude — port of WebChatAutomationBase. */

function detectService() {
  const host = location.hostname;
  if (host.includes("chatgpt.com") || host.includes("chat.openai.com")) return "ChatGPT";
  if (host.includes("claude.ai")) return "Claude";
  return null;
}

function findInput() {
  return (
    document.querySelector("#prompt-textarea") ||
    document.querySelector("div.ProseMirror[contenteditable='true']") ||
    document.querySelector("div[contenteditable='true'][data-placeholder]") ||
    document.querySelector("[data-placeholder][contenteditable='true']") ||
    document.querySelector("div[contenteditable='true']") ||
    document.querySelector("textarea[data-id]") ||
    document.querySelector("textarea")
  );
}

function findSendButton() {
  return (
    document.querySelector('[data-testid="send-button"]') ||
    document.querySelector('button[aria-label="Send Message"]') ||
    document.querySelector('button[aria-label*="Send"]') ||
    document.querySelector('button[aria-label*="send"]') ||
    document.querySelector('button[type="submit"]')
  );
}

function findFileInput() {
  return document.querySelector('input[type="file"]');
}

function dismissOneButtonDialog() {
  document.querySelectorAll('[role="dialog"]').forEach((dlg) => {
    dlg.querySelectorAll("button").forEach((btn) => {
      if (/^(ok|close|dismiss)$/i.test((btn.textContent || "").trim())) btn.click();
    });
  });
}

function injectText(el, text) {
  el.focus();
  if (el.isContentEditable) {
    document.execCommand("selectAll", false, null);
    document.execCommand("insertText", false, text);
    el.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true }));
  } else {
    try {
      const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
      setter.call(el, text);
    } catch {
      el.value = text;
    }
    el.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }
}

async function sendPrompt(text) {
  const service = detectService();
  if (!service) return { ok: false, message: "Not on ChatGPT or Claude." };

  const el = findInput();
  if (!el) return { ok: false, message: `[${service}] Input field not found.` };

  injectText(el, text);
  await new Promise((r) => setTimeout(r, 700));

  const btn = findSendButton();
  if (!btn) return { ok: false, message: `[${service}] Send button not found.` };
  if (btn.disabled) return { ok: false, message: `[${service}] Send button disabled.` };
  btn.click();
  return { ok: true, message: `[${service}] Sent.` };
}

async function attachDataUrl(dataUrl, filename) {
  const service = detectService();
  if (!service) return { ok: false, message: "Not on ChatGPT or Claude." };

  const input = findFileInput();
  if (!input) return { ok: false, message: `[${service}] File input not found. Is the page fully loaded?` };

  const res = await fetch(dataUrl);
  const blob = await res.blob();
  const file = new File([blob], filename || "livecaption_capture.png", { type: "image/png" });
  const dt = new DataTransfer();
  dt.items.add(file);
  input.files = dt.files;
  input.dispatchEvent(new Event("change", { bubbles: true }));
  input.dispatchEvent(new Event("input", { bubbles: true }));
  await new Promise((r) => setTimeout(r, 400));
  dismissOneButtonDialog();
  return { ok: true, message: `[${service}] Screenshot attached.` };
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "LC_SEND_PROMPT") {
    sendPrompt(msg.text).then(sendResponse);
    return true;
  }
  if (msg?.type === "LC_ATTACH_FILE") {
    attachDataUrl(msg.dataUrl, msg.filename).then(sendResponse);
    return true;
  }
  if (msg?.type === "LC_PING_AI") {
    sendResponse({ ok: !!detectService(), service: detectService() });
    return false;
  }
  return false;
});
