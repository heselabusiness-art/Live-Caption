importScripts("lib/segment-tracker.js", "lib/prompt-fill.js");

const NATIVE_HOST = "com.livecaption.assistant";
const HTTP_BRIDGE = "http://127.0.0.1:17891";

const DEFAULT_SETTINGS = {
  activePanel: "segment",
  promptModeIndividual: false,
  selectedRound: "Recruiter Screen",
  captureTabId: null
};

const DEFAULT_PI = {
  seniority: "Staff",
  domain: "AI/ML",
  company: "",
  companyStage: "",
  myDifferentiation: "",
  myThroughLine: "",
  keySkills: "",
  keyProjects: "",
  jdFocus: "",
  usedFor: "",
  outcome: "pending",
  product: "",
  teamFocus: "",
  knownTechStack: "",
  companyValues: "",
  interviewerBackground: "",
  recentNews: "",
  typicalCandidate: "",
  resumeSummary: "",
  jobDescription: "",
  projectStories: "",
  systemDesignScenarios: ""
};

const DEFAULT_OB = {
  companyRoleTeam: "",
  manager: "",
  weekNumber: "1",
  stack: "",
  hiredToDo: "",
  anchorProject: "",
  scaleFacts: "",
  gaps: "",
  whyLeft: "",
  selfIntro: "",
  processScoping: "",
  processBuildBuy: "",
  processTesting: "",
  processShipping: "",
  processIncident: "",
  processReporting: "",
  carryoverPeople: "",
  carryoverAcronyms: "",
  carryoverSystems: "",
  carryoverCommitments: "",
  carryoverWorkLog: "",
  meetingType: "MANAGER 1:1",
  meetingWho: "",
  meetingLength: "",
  workLogEntry: ""
};

const tracker = new SegmentCaptureTracker();
let latestCaption = "";
let lastCaptureTime = null;
// Sends are serialised: a second hotkey press waits for its turn instead of being dropped
// (the caption segment is already consumed by then, so a dropped send lost the question).
const MAX_QUEUED_SENDS = 3;
let sendChain = Promise.resolve();
let pendingSends = 0;
let captureRunning = false;
let captureStatus = "idle";
let lastScreenshot = null;
let nativePort = null;
let httpPollTimer = null;

// ── MV3 service worker: Chrome stops it after ~30 s of silence and starts a fresh one for the next hotkey.
// Everything in memory is gone then, so the transcript / read head / running flag are mirrored into
// chrome.storage.session and every hotkey handler waits for `ready` (state restored, capture resumed).
let persistTimer = null;
function persistSoon() {
  if (persistTimer) return;
  persistTimer = setTimeout(persistNow, 250);
}
async function persistNow() {
  clearTimeout(persistTimer);
  persistTimer = null;
  try {
    await chrome.storage.session.set({
      trackerState: tracker.serialize(),
      latestCaption,
      lastCaptureTime
    });
  } catch { /* older Chrome / quota: the next update retries */ }
}

function enableSidebar() {
  chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});
  chrome.sidePanel.setOptions({ path: "sidepanel.html", enabled: true }).catch(() => {});
}

chrome.runtime.onInstalled.addListener(() => {
  enableSidebar();
  seedStorage();
  ready = resumeCaptureIfNeeded();
});

chrome.runtime.onStartup.addListener(() => {
  enableSidebar();
  ready = resumeCaptureIfNeeded();
});

enableSidebar();
let ready = resumeCaptureIfNeeded();

// The side panel keeps the worker alive while it is open (a message every 20 s resets Chrome's idle timer),
// so the live caption bar keeps moving instead of freezing until the next hotkey.
chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "lc-panel") return;
  port.onMessage.addListener(() => {});
});

chrome.action.onClicked.addListener((tab) => {
  const windowId = tab?.windowId;
  if (windowId != null) {
    chrome.sidePanel.open({ windowId }).catch(() => {});
  }
});

async function seedStorage() {
  const cur = await chrome.storage.local.get(["settings", "preInterview", "onboarding"]);
  await chrome.storage.local.set({
    settings: { ...DEFAULT_SETTINGS, ...(cur.settings || {}) },
    preInterview: { ...DEFAULT_PI, ...(cur.preInterview || {}) },
    onboarding: { ...DEFAULT_OB, ...(cur.onboarding || {}) }
  });
}

async function getState() {
  const data = await chrome.storage.local.get(["settings", "preInterview", "onboarding"]);
  return {
    settings: { ...DEFAULT_SETTINGS, ...(data.settings || {}) },
    preInterview: { ...DEFAULT_PI, ...(data.preInterview || {}) },
    onboarding: { ...DEFAULT_OB, ...(data.onboarding || {}) }
  };
}

function broadcast(payload) {
  chrome.runtime.sendMessage(payload).catch(() => {});
}

function snapshotUi() {
  return {
    latestCaption,
    lastCaptureTime,
    captureRunning,
    captureStatus,
    lastScreenshot,
    display: tracker.getDisplayParts()
  };
}

async function findAiTab() {
  const tabs = await chrome.tabs.query({});
  return (
    tabs.find((t) => /chatgpt\.com|chat\.openai\.com/i.test(t.url || "")) ||
    tabs.find((t) => /claude\.ai/i.test(t.url || "")) ||
    null
  );
}

async function ensureAiContent(tabId) {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "LC_PING_AI" });
  } catch {
    await chrome.scripting.executeScript({
      target: { tabId },
      files: ["content/ai-page.js"]
    });
  }
}

function sendPromptToAi(text) {
  if (!text || !String(text).trim()) {
    return Promise.resolve({ ok: false, message: "Empty prompt — nothing sent." });
  }

  if (pendingSends > MAX_QUEUED_SENDS) {
    const message = `Too many prompts waiting (${MAX_QUEUED_SENDS}). Wait for the current one to finish.`;
    broadcast({ type: "LC_SEND_STATUS", message, isError: true });
    return Promise.resolve({ ok: false, message });
  }

  if (pendingSends > 0) {
    broadcast({ type: "LC_SEND_STATUS", message: `Queued — ${pendingSends} ahead…`, isError: false });
  }

  pendingSends++;
  const run = sendChain.then(() => deliverPromptToAi(text));
  sendChain = run.then(
    () => { pendingSends--; },
    () => { pendingSends--; }
  );
  return run;
}

async function deliverPromptToAi(text) {
  const tab = await findAiTab();
  if (!tab) {
    const msg = "Navigate to ChatGPT (chatgpt.com) or Claude (claude.ai) first.";
    broadcast({ type: "LC_SEND_STATUS", message: msg, isError: true });
    return { ok: false, message: msg };
  }

  broadcast({ type: "LC_SEND_STATUS", message: "Sending…", isError: false });
  try {
    await ensureAiContent(tab.id);
    const result = await chrome.tabs.sendMessage(tab.id, { type: "LC_SEND_PROMPT", text });
    const ok = !!(result && result.ok);
    broadcast({
      type: "LC_SEND_STATUS",
      message: result?.message || (ok ? "Sent." : "Send failed."),
      isError: !ok
    });
    return result || { ok, message: ok ? "Sent." : "Send failed." };
  } catch (err) {
    const message = `Error: ${err.message || err}`;
    broadcast({ type: "LC_SEND_STATUS", message, isError: true });
    return { ok: false, message };
  }
}

async function attachScreenshotToAi(dataUrl) {
  const tab = await findAiTab();
  if (!tab) {
    return { ok: false, message: "Captured — open ChatGPT or Claude to auto-attach the screenshot." };
  }
  await ensureAiContent(tab.id);
  const filename = `livecaption_capture_${Date.now()}.png`;
  return chrome.tabs.sendMessage(tab.id, { type: "LC_ATTACH_FILE", dataUrl, filename });
}

async function fetchPrompt(relPath) {
  const url = chrome.runtime.getURL(relPath);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Prompt file not found: ${relPath}`);
  return res.text();
}

function applyCaption(text) {
  latestCaption = text || "";
  tracker.updateLiveText(latestCaption);
  persistSoon();
  broadcast({
    type: "LC_CAPTION",
    text: latestCaption,
    display: tracker.getDisplayParts(),
    captureStatus,
    captureRunning
  });
}

function mapHostStatus(status) {
  if (["idle", "searching", "notfound", "monitoring", "stopped", "error"].includes(status)) {
    return status;
  }
  return "searching";
}

function handleHostMessage(msg) {
  if (!msg) return;
  if (msg.type === "caption" && captureRunning) {
    applyCaption(msg.text || "");
    return;
  }
  if (msg.type === "status") {
    captureStatus = mapHostStatus(msg.status);
    if (captureStatus === "monitoring" && msg.text) applyCaption(msg.text);
    broadcast({ type: "LC_STATUS", captureStatus, captureRunning, error: msg.error || null });
    return;
  }
  if (msg.type === "snapshot") {
    captureStatus = mapHostStatus(msg.status);
    if (msg.text) applyCaption(msg.text);
    broadcast({ type: "LC_STATUS", captureStatus, captureRunning, error: msg.error || null });
  }
}

function disconnectNative() {
  if (nativePort) {
    try { nativePort.postMessage({ action: "stop" }); } catch { /* ignore */ }
    try { nativePort.disconnect(); } catch { /* ignore */ }
    nativePort = null;
  }
}

function stopHttpPoll() {
  if (httpPollTimer) {
    clearInterval(httpPollTimer);
    httpPollTimer = null;
  }
}

async function httpPost(path) {
  const res = await fetch(`${HTTP_BRIDGE}${path}`, { method: "POST" });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

async function httpGet() {
  const res = await fetch(`${HTTP_BRIDGE}/`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

function startHttpFallback() {
  stopHttpPoll();
  httpPollTimer = setInterval(async () => {
    if (!captureRunning) return;
    try {
      handleHostMessage(await httpGet());
    } catch {
      captureStatus = "error";
      broadcast({
        type: "LC_STATUS",
        captureStatus,
        captureRunning,
        error: "Lost the Live Captions host. Run Install-NativeHost.ps1, then click Start again."
      });
    }
  }, 400);
}

async function rememberCapture(running) {
  try { await chrome.storage.session.set({ captureRunning: running }); } catch { /* older Chrome */ }
}

async function resumeCaptureIfNeeded() {
  try {
    // restore what the previous worker knew BEFORE anything else touches the tracker
    const data = await chrome.storage.session.get(["captureRunning", "trackerState", "latestCaption", "lastCaptureTime"]);
    if (data.trackerState) tracker.restore(data.trackerState);
    if (typeof data.latestCaption === "string" && !latestCaption) latestCaption = data.latestCaption;
    if (data.lastCaptureTime != null && lastCaptureTime == null) lastCaptureTime = data.lastCaptureTime;
    if (data.captureRunning && !captureRunning) await startCapture();
  } catch { /* ignore */ }
}

/**
 * A hotkey must capture what is on the Live Captions window RIGHT NOW, not whatever the last poll saw:
 * make sure capture is running (it may have stopped with the worker), then pull one fresh snapshot
 * from the host before reading the transcript.
 */
async function refreshCaptionNow() {
  await ready;
  if (!captureRunning) await startCapture();
  try {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 1500);
    try {
      const res = await fetch(`${HTTP_BRIDGE}/?fresh=1`, { signal: ctl.signal });
      if (res.ok) handleHostMessage(await res.json());
    } finally {
      clearTimeout(timer);
    }
  } catch { /* host busy / not reachable: fall back to the transcript we have */ }
}

async function startCapture() {
  captureRunning = true;
  captureStatus = "searching";
  await rememberCapture(true);
  broadcast({ type: "LC_STATUS", captureStatus, captureRunning });

  disconnectNative();
  stopHttpPoll();

  // Prefer the always-on local host (started at install / login).
  try {
    handleHostMessage(await httpPost("/start"));
    startHttpFallback();
    return;
  } catch { /* launch via native messaging instead */ }

  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST);
    nativePort.onMessage.addListener(handleHostMessage);
    nativePort.onDisconnect.addListener(() => {
      const err = chrome.runtime.lastError?.message;
      nativePort = null;
      if (!captureRunning) return;
      startHttpFallback();
      httpPost("/start").then(handleHostMessage).catch(() => {
        captureStatus = "error";
        broadcast({
          type: "LC_STATUS",
          captureStatus,
          captureRunning,
          error: err || "Live Captions host is not running. Double-click chrome-extension\\Load-Extension.cmd"
        });
      });
    });
    nativePort.postMessage({ action: "start" });
    return;
  } catch {
    nativePort = null;
  }

  captureStatus = "error";
  broadcast({
    type: "LC_STATUS",
    captureStatus,
    captureRunning,
    error: "Windows Live Captions host is not running. Double-click chrome-extension\\Load-Extension.cmd, then open Live Captions with Win+Ctrl+L."
  });
}

async function stopCapture() {
  captureRunning = false;
  captureStatus = "stopped";
  await rememberCapture(false);
  try { if (nativePort) nativePort.postMessage({ action: "stop" }); } catch { /* ignore */ }
  disconnectNative();
  try { await httpPost("/stop"); } catch { /* ignore */ }
  stopHttpPoll();
  broadcast({ type: "LC_STATUS", captureStatus, captureRunning });
}

function noCaptionMessage() {
  const why =
    captureStatus === "notfound" ? "Live Captions is not open (press Win+Ctrl+L)."
    : captureStatus === "error" ? "The Live Captions host reported an error."
    : !latestCaption ? "No caption text has been read yet - is Live Captions showing speech?"
    : "No new caption text since the last capture.";
  return `Nothing to send. ${why}`;
}

function tellEmptyCapture() {
  const message = noCaptionMessage();
  broadcast({ type: "LC_SEND_STATUS", message, isError: true });
  try {
    chrome.action.setBadgeBackgroundColor({ color: "#c0392b" });
    chrome.action.setBadgeText({ text: "0" });
    setTimeout(() => chrome.action.setBadgeText({ text: "" }), 2500);
  } catch { /* badge is best effort */ }
}

async function captureSegmentToUi() {
  await refreshCaptionNow();
  const segment = tracker.captureNextSegment();
  lastCaptureTime = Date.now();
  persistSoon();
  broadcast({
    type: "LC_SEGMENT",
    segment,
    lastCaptureTime,
    display: tracker.getDisplayParts()
  });
  return segment;
}

async function handleCaptureSend() {
  const segment = await captureSegmentToUi();
  if (!segment) return tellEmptyCapture();
  const { settings } = await getState();
  await sendPromptToAi(buildQuestionTrigger(segment, settings));
}

async function handleCapturePushback() {
  const segment = await captureSegmentToUi();
  if (!segment) return tellEmptyCapture();
  const { settings } = await getState();
  await sendPromptToAi(buildPushbackTrigger(segment, settings));
}

async function handleCaptureOnly() {
  const segment = await captureSegmentToUi();
  if (!segment) tellEmptyCapture();
}

async function handleSendReminder() {
  await ready;
  const { settings, onboarding } = await getState();
  if (settings.activePanel === "onboarding") {
    const raw = await fetchPrompt("prompts/onboarding prompt/onboarding_assistant_prompt_v3.md");
    await sendPromptToAi(fillOnboardingPrompt(raw, onboarding));
    return;
  }
  const reminder = await fetchPrompt("prompts/Reminder.txt");
  await sendPromptToAi(reminder);
}

async function handleTechSend() {
  const segment = await captureSegmentToUi();
  if (!segment) return tellEmptyCapture();
  await sendPromptToAi(buildTechTrigger(segment));
}

async function handleTechCapture(tabId) {
  const { settings } = await getState();
  const targetId = tabId || settings.captureTabId;
  if (!targetId) {
    broadcast({
      type: "LC_TECH_STATUS",
      message: "Insert / Capture: select a tab first (click Refresh)."
    });
    return { ok: false, message: "Select a tab first." };
  }

  let tab;
  try {
    tab = await chrome.tabs.get(targetId);
  } catch {
    broadcast({ type: "LC_TECH_STATUS", message: "Selected tab is gone. Refresh the list." });
    return { ok: false, message: "Selected tab is gone." };
  }

  broadcast({ type: "LC_TECH_STATUS", message: `Capturing ${tab.title}…` });
  await chrome.windows.update(tab.windowId, { focused: true });
  await chrome.tabs.update(tab.id, { active: true });
  await new Promise((r) => setTimeout(r, 350));

  try {
    const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, { format: "png" });
    lastScreenshot = dataUrl;
    broadcast({
      type: "LC_SCREENSHOT",
      dataUrl,
      message: `Captured at ${new Date().toLocaleTimeString()} — ${tab.title}`
    });
    const attach = await attachScreenshotToAi(dataUrl);
    broadcast({
      type: "LC_TECH_STATUS",
      message: attach?.message || "Screenshot captured."
    });
    return attach;
  } catch (err) {
    const message = `Capture failed: ${err.message || err}`;
    broadcast({ type: "LC_TECH_STATUS", message });
    return { ok: false, message };
  }
}

async function listCaptureTabs() {
  const tabs = await chrome.tabs.query({});
  return tabs
    .filter((t) => t.id && t.title && !t.url?.startsWith("chrome://"))
    .map((t) => ({
      id: t.id,
      windowId: t.windowId,
      title: t.title,
      url: t.url || "",
      displayName: `${t.title} — ${t.url || ""}`
    }));
}

async function sendPreInterview() {
  const { settings, preInterview } = await getState();
  if (settings.promptModeIndividual) {
    const raw = await fetchPrompt(individualPromptUrl(settings.selectedRound));
    return sendPromptToAi(fillIndividualPrompt(raw, preInterview));
  }
  const raw = await fetchPrompt("prompts/Prompt.txt");
  return sendPromptToAi(fillFullPrompt(raw, preInterview));
}

async function sendOnboardingPrompt() {
  const { onboarding } = await getState();
  const raw = await fetchPrompt("prompts/onboarding prompt/onboarding_assistant_prompt_v3.md");
  return sendPromptToAi(fillOnboardingPrompt(raw, onboarding));
}

async function sendOnboardingSetup() {
  const { onboarding } = await getState();
  const meetingType = onboarding.meetingType || "MANAGER 1:1";
  const who = (onboarding.meetingWho || "").trim() || "unspecified";
  const length = (onboarding.meetingLength || "").trim() || "unspecified";
  const week = (onboarding.weekNumber || "").trim() || "1";
  return sendPromptToAi(`SETUP: ${meetingType}, ${who}, ${length}, WEEK ${week}`);
}

async function sendOnboardingLog() {
  const { onboarding } = await getState();
  const text = (onboarding.workLogEntry || "").trim();
  if (!text) return { ok: false, message: "Work log entry is empty." };
  return sendPromptToAi(`LOG: ${text}`);
}

chrome.commands.onCommand.addListener(async (command) => {
  switch (command) {
    case "capture-send":
      await handleCaptureSend();
      break;
    case "capture-pushback":
      await handleCapturePushback();
      break;
    case "capture-only":
      await handleCaptureOnly();
      break;
    case "send-reminder":
      await handleSendReminder();
      break;
    case "tech-capture":
      await handleTechCapture();
      break;
    case "tech-send":
      await handleTechSend();
      break;
    default:
      break;
  }
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  const done = (value) => {
    sendResponse(value);
    return false;
  };

  switch (msg?.type) {
    case "LC_START":
      startCapture().then(() => sendResponse(snapshotUi()));
      return true;

    case "LC_STOP":
      stopCapture().then(() => sendResponse(snapshotUi()));
      return true;

    case "LC_CLEAR":
      tracker.reset();
      persistSoon();
      latestCaption = "";
      lastCaptureTime = null;
      lastScreenshot = null;
      broadcast({ type: "LC_CLEARED" });
      return done({ ok: true });

    case "LC_GET_STATE":
      getState().then((state) => sendResponse({ ...state, ui: snapshotUi() }));
      return true;

    case "LC_LIST_TABS":
      listCaptureTabs().then(sendResponse);
      return true;

    case "LC_ACTION_HOME":
      handleCaptureSend().then(sendResponse);
      return true;
    case "LC_ACTION_END":
      handleCapturePushback().then(sendResponse);
      return true;
    case "LC_ACTION_PAGEUP":
      handleCaptureOnly().then(sendResponse);
      return true;
    case "LC_ACTION_PAGEDOWN":
      handleSendReminder().then(sendResponse);
      return true;
    case "LC_ACTION_INSERT":
      handleTechCapture(msg.tabId).then(sendResponse);
      return true;
    case "LC_ACTION_DELETE":
      handleTechSend().then(sendResponse);
      return true;

    case "LC_SEND_PRE":
      sendPreInterview().then(sendResponse);
      return true;
    case "LC_SEND_ONBOARDING":
      sendOnboardingPrompt().then(sendResponse);
      return true;
    case "LC_SEND_SETUP":
      sendOnboardingSetup().then(sendResponse);
      return true;
    case "LC_SEND_LOG":
      sendOnboardingLog().then(sendResponse);
      return true;

    case "LC_OPEN_URL":
      chrome.tabs.create({ url: msg.url });
      return done({ ok: true });

    default:
      return false;
  }
});
