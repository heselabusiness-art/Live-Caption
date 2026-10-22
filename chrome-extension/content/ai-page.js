/**
 * Inject text / files into ChatGPT or Claude — port of WebChatAutomationBase.
 *
 * The sites change their markup often. Every lookup tries a list of selectors, and any of them can be
 * replaced without editing the extension: store CSS selectors under `selectorOverrides` in
 * chrome.storage.local, e.g. (run in the service-worker console):
 *
 *   chrome.storage.local.set({ selectorOverrides: {
 *     ChatGPT: { input: "#prompt-textarea", sendButton: "button[data-testid='send-button']" },
 *     Claude:  { stopButton: "button[aria-label='Stop response']" }
 *   }})
 *
 * Keys: input, sendButton, stopButton.
 */

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const SEND_READY_MAX_MS = 30_000;
const SEND_READY_POLL_MS = 250;
const SEND_VERIFY_MAX_MS = 8_000;
const SEND_MAX_ATTEMPTS = 3;
const ENTER_FALLBACK_AFTER_MS = 2_500;
const GENERATION_GRACE_MS = 4_000;

function detectService() {
  const host = location.hostname;
  if (host.includes("chatgpt.com") || host.includes("chat.openai.com")) return "ChatGPT";
  if (host.includes("claude.ai")) return "Claude";
  return null;
}

const DEFAULT_SELECTORS = {
  input: [
    "#prompt-textarea",
    "div.ProseMirror[contenteditable='true']",
    "div[contenteditable='true'][data-placeholder]",
    "[data-placeholder][contenteditable='true']",
    "div[contenteditable='true']",
    "textarea[data-id]",
    "textarea"
  ],
  sendButton: [
    '[data-testid="send-button"]',
    "#composer-submit-button",
    'button[aria-label="Send Message"]',
    'button[aria-label="Send message"]',
    'button[aria-label*="Send"]',
    'button[aria-label*="send"]',
    'button[type="submit"]'
  ],
  // ChatGPT: avoid a generic "Stop" match, it hits unrelated buttons.
  stopButton: {
    ChatGPT: [
      '[data-testid="stop-button"]:not([disabled])',
      'button[aria-label="Stop streaming"]:not([disabled])',
      'button[aria-label="Stop generating"]:not([disabled])'
    ],
    Claude: [
      '[data-testid="stop-button"]',
      'button[aria-label="Stop response"]',
      'button[aria-label*="Stop"]'
    ]
  }
};

let userSelectors = {};

async function loadSelectorOverrides() {
  try {
    const data = await chrome.storage.local.get("selectorOverrides");
    userSelectors = data.selectorOverrides?.[detectService()] || {};
  } catch {
    userSelectors = {};
  }
}

function queryFirst(selectors) {
  for (const selector of selectors) {
    if (!selector) continue;
    try {
      const el = document.querySelector(selector);
      if (el) return el;
    } catch {
      /* invalid selector: skip it */
    }
  }
  return null;
}

function withOverride(key, defaults) {
  return queryFirst([userSelectors[key], ...defaults]);
}

const findInput = () => withOverride("input", DEFAULT_SELECTORS.input);
const findSendButton = () => withOverride("sendButton", DEFAULT_SELECTORS.sendButton);
const findStopButton = () =>
  withOverride("stopButton", DEFAULT_SELECTORS.stopButton[detectService()] || []);
const isGenerating = () => !!findStopButton();

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

const nonWhitespaceLength = (s) => String(s).replace(/\s+/g, "").length;

function composerLength(el) {
  const raw = el.value !== undefined ? el.value : el.innerText || el.textContent || "";
  return nonWhitespaceLength(raw);
}

function isDisabled(btn) {
  return btn.disabled || btn.getAttribute("aria-disabled") === "true";
}

/** Puts the text into the composer and VERIFIES it landed (the page's editor can silently drop it). */
async function injectText(el, text) {
  const expected = nonWhitespaceLength(text);
  const holdsText = () => composerLength(el) >= expected * 0.95;

  el.focus();

  if (el.isContentEditable) {
    const sel = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(el);
    sel.removeAllRanges();
    sel.addRange(range);

    // 1) The editor's own insert pipeline.
    document.execCommand("insertText", false, text);
    el.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true, inputType: "insertText", data: text }));
    await sleep(60);
    if (holdsText()) return true;

    // 2) Simulated paste: ProseMirror (both sites) handles paste events natively.
    el.focus();
    range.selectNodeContents(el);
    sel.removeAllRanges();
    sel.addRange(range);
    const data = new DataTransfer();
    data.setData("text/plain", text);
    el.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
    await sleep(120);
    return holdsText();
  }

  try {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
    setter.call(el, text);
  } catch {
    el.value = text;
  }
  el.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true }));
  el.dispatchEvent(new Event("change", { bubbles: true }));
  await sleep(60);
  return holdsText();
}

/** True once the reply started streaming or the composer was cleared. */
async function waitForSubmission() {
  const start = Date.now();
  while (Date.now() - start < SEND_VERIFY_MAX_MS) {
    await sleep(SEND_READY_POLL_MS);
    if (isGenerating()) return true;
    const el = findInput();
    if (el && composerLength(el) === 0) return true;
  }
  return false;
}

function pressEnter(el) {
  el.focus();
  const init = { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true, cancelable: true };
  el.dispatchEvent(new KeyboardEvent("keydown", init));
  el.dispatchEvent(new KeyboardEvent("keypress", init));
  el.dispatchEvent(new KeyboardEvent("keyup", init));
}

/** Waits for a still-streaming previous reply, then stops it so the new question goes out at once. */
async function clearGeneration() {
  if (!isGenerating()) return true;

  const grace = Date.now() + GENERATION_GRACE_MS;
  while (isGenerating() && Date.now() < grace) await sleep(300);
  if (!isGenerating()) return true;

  findStopButton()?.click();
  const stopDeadline = Date.now() + 5_000;
  while (isGenerating() && Date.now() < stopDeadline) await sleep(200);
  if (isGenerating()) return false;

  await sleep(250); // let the UI swap Stop back to Send
  return true;
}

async function clickSendWhenReady() {
  let last = "BTN_NOT_FOUND";
  let enterTried = false;

  for (let attempt = 1; attempt <= SEND_MAX_ATTEMPTS; attempt++) {
    const waitStart = Date.now();

    // 1) Wait for the Send button to exist AND be enabled.
    for (;;) {
      const btn = findSendButton();
      last = !btn ? "BTN_NOT_FOUND" : isDisabled(btn) ? "BTN_DISABLED" : "READY";
      if (last === "READY") break;

      // No Send button at all usually means the markup changed. Both sites submit on Enter.
      if (last === "BTN_NOT_FOUND" && !enterTried && Date.now() - waitStart >= ENTER_FALLBACK_AFTER_MS) {
        enterTried = true;
        const el = findInput();
        if (el) {
          pressEnter(el);
          if (await waitForSubmission()) return "SENT";
        }
      }

      if (Date.now() - waitStart >= SEND_READY_MAX_MS) return last;
      await sleep(SEND_READY_POLL_MS);
    }

    // 2) Click, then 3) confirm the page took the message before ever clicking again.
    findSendButton().click();
    if (await waitForSubmission()) return "SENT";
    last = "NOT_SUBMITTED";
  }

  return last;
}

async function sendPrompt(text) {
  const service = detectService();
  if (!service) return { ok: false, message: "Not on ChatGPT or Claude." };

  await loadSelectorOverrides();

  if (!(await clearGeneration())) {
    return { ok: false, message: `[${service}] The previous answer is still generating — press again when it finishes.` };
  }

  let inserted = false;
  for (let attempt = 1; attempt <= 2 && !inserted; attempt++) {
    const el = findInput();
    if (!el) {
      if (attempt === 2) return { ok: false, message: `[${service}] Input field not found.` };
      await sleep(700);
      continue;
    }
    inserted = await injectText(el, text);
    if (!inserted && attempt === 1) await sleep(700);
  }
  if (!inserted) return { ok: false, message: `[${service}] Could not put the text into the chat box.` };

  // A short question needs no settling time; a huge prompt gets longer.
  await sleep(text.length < 2_000 ? 120 : 700);

  switch (await clickSendWhenReady()) {
    case "SENT":
      return { ok: true, message: `[${service}] Sent.` };
    case "BTN_DISABLED":
      return { ok: false, message: `[${service}] Send button stayed disabled (prompt ${text.length.toLocaleString()} chars).` };
    case "BTN_NOT_FOUND":
      return { ok: false, message: `[${service}] Send button not found. Set selectorOverrides.${service}.sendButton in chrome.storage.local.` };
    default:
      return { ok: false, message: `[${service}] Send was clicked but the message was not submitted.` };
  }
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
  await sleep(400);
  dismissOneButtonDialog();
  return { ok: true, message: `[${service}] Screenshot attached.` };
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "LC_SEND_PROMPT") {
    sendPrompt(msg.text)
      .then(sendResponse)
      .catch((err) => sendResponse({ ok: false, message: `Error: ${err?.message || err}` }));
    return true;
  }
  if (msg?.type === "LC_ATTACH_FILE") {
    attachDataUrl(msg.dataUrl, msg.filename)
      .then(sendResponse)
      .catch((err) => sendResponse({ ok: false, message: `Error: ${err?.message || err}` }));
    return true;
  }
  if (msg?.type === "LC_PING_AI") {
    sendResponse({ ok: !!detectService(), service: detectService() });
    return false;
  }
  return false;
});
