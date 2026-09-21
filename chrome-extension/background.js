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
let isSending = false;
let captureRunning = false;
let captureStatus = "idle";
let lastScreenshot = null;
let nativePort = null;
let httpPollTimer = null;

function enableSidebar() {
  chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});
  chrome.sidePanel.setOptions({ path: "sidepanel.html", enabled: true }).catch(() => {});
}

chrome.runtime.onInstalled.addListener(() => {
  enableSidebar();
  seedStorage();
  resumeCaptureIfNeeded();
});

chrome.runtime.onStartup.addListener(() => {
  enableSidebar();
  resumeCaptureIfNeeded();
});

enableSidebar();
resumeCaptureIfNeeded();

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

async function sendPromptToAi(text) {
  if (!text || !String(text).trim()) return { ok: false, message: "Empty prompt — nothing sent." };
  if (isSending) return { ok: false, message: "Already sending…" };

  const tab = await findAiTab();
  if (!tab) {
    const msg = "Navigate to ChatGPT (chatgpt.com) or Claude (claude.ai) first.";
    broadcast({ type: "LC_SEND_STATUS", message: msg, isError: true });
    return { ok: false, message: msg };
  }

  isSending = true;
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
  } finally {
    isSending = false;
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
    const data = await chrome.storage.session.get("captureRunning");
    if (data.captureRunning && !captureRunning) await startCapture();
  } catch { /* ignore */ }
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

function captureSegmentToUi() {
  const segment = tracker.captureNextSegment();
  lastCaptureTime = Date.now();
  broadcast({
    type: "LC_SEGMENT",
    segment,
    lastCaptureTime,
    display: tracker.getDisplayParts()
  });
  return segment;
}

async function handleCaptureSend() {
  const segment = captureSegmentToUi();
  if (!segment) return;
  const { settings } = await getState();
  await sendPromptToAi(buildQuestionTrigger(segment, settings));
}

async function handleCapturePushback() {
  const segment = captureSegmentToUi();
  if (!segment) return;
  const { settings } = await getState();
  await sendPromptToAi(buildPushbackTrigger(segment, settings));
}

async function handleCaptureOnly() {
  captureSegmentToUi();
}

async function handleSendReminder() {
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
  const segment = captureSegmentToUi();
  if (!segment) return;
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
