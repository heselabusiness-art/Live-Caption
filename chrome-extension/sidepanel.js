const ROUNDS = [
  "Recruiter Screen", "Hiring Manager", "Technical Screen", "System Design",
  "ML System Design", "Behavioral", "Culture Fit", "Peer", "Bar Raiser",
  "Skip Level", "Executive/VP", "Final", "Presentation", "Case Study",
  "Research", "Code", "Diagram"
];

const PI_KEYS = [
  "seniority", "domain", "company", "companyStage", "myDifferentiation", "myThroughLine",
  "keySkills", "keyProjects", "jdFocus", "usedFor", "outcome", "product", "teamFocus",
  "knownTechStack", "companyValues", "interviewerBackground", "recentNews", "typicalCandidate",
  "resumeSummary", "jobDescription", "projectStories", "systemDesignScenarios"
];

const OB_KEYS = [
  "companyRoleTeam", "manager", "weekNumber", "stack", "hiredToDo",
  "anchorProject", "scaleFacts", "gaps", "whyLeft", "selfIntro",
  "processScoping", "processBuildBuy", "processTesting", "processShipping",
  "processIncident", "processReporting", "carryoverPeople", "carryoverAcronyms",
  "carryoverSystems", "carryoverCommitments", "carryoverWorkLog",
  "meetingType", "meetingWho", "meetingLength", "workLogEntry"
];

const $ = (id) => document.getElementById(id);

// Keep the background service worker alive while the panel is open (Chrome stops idle workers after ~30 s,
// which froze the live caption bar and dropped capture state between hotkey presses).
(function keepWorkerAlive() {
  let port = null;
  const connect = () => {
    try {
      port = chrome.runtime.connect({ name: "lc-panel" });
      port.onDisconnect.addListener(() => { port = null; });
    } catch { port = null; }
  };
  connect();
  setInterval(() => {
    if (!port) connect();
    try { port?.postMessage({ type: "LC_PING" }); } catch { port = null; }
  }, 20000);
})();

let settings = {
  activePanel: "segment",
  promptModeIndividual: false,
  selectedRound: "Recruiter Screen",
  captureTabId: null
};

function readFields(prefix, keys) {
  const out = {};
  for (const key of keys) {
    const el = document.getElementById(`${prefix}-${key}`);
    out[key] = el ? el.value : "";
  }
  return out;
}

function writeFields(prefix, keys, values) {
  if (!values) return;
  for (const key of keys) {
    const el = document.getElementById(`${prefix}-${key}`);
    if (el && values[key] != null) el.value = values[key];
  }
}

async function persist() {
  settings.captureTabId = Number($("tabPicker").value) || null;
  await chrome.storage.local.set({
    settings,
    preInterview: readFields("pi", PI_KEYS),
    onboarding: readFields("ob", OB_KEYS)
  });
}

function setSendStatus(message, isError) {
  const el = $("sendStatus");
  el.textContent = message || "";
  el.className = isError ? "err" : "ok";
}

function setStatus(status, error) {
  const map = {
    idle: ["Idle", ""],
    searching: ["Searching for Windows Live Captions…", "searching"],
    notfound: ["Live Captions not found. Open it with Win + Ctrl + L.", "notfound"],
    monitoring: ["Monitoring Windows Live Captions", "monitoring"],
    stopped: ["Stopped", ""],
    error: [`Error: ${error || "unknown"}`, "error"]
  };
  const [text, cls] = map[status] || ["Unknown", ""];
  $("statusLabel").textContent = text;
  $("statusDot").className = `dot ${cls}`;
  $("btnStart").disabled = status === "searching" || status === "monitoring";
  $("btnStop").disabled = !["searching", "monitoring"].includes(status);
}

function renderLiveBar(display, raw) {
  const bar = $("liveBar");
  bar.innerHTML = "";
  const pending = (display && display.pending) || raw || "";
  const base = (display && display.base) || "";
  if (!pending && !base) {
    const span = document.createElement("span");
    span.className = "base";
    span.textContent = "(waiting for caption…)";
    bar.appendChild(span);
    return;
  }
  if (base) {
    const span = document.createElement("span");
    span.className = "base";
    span.textContent = base;
    bar.appendChild(span);
  }
  if (pending) {
    const span = document.createElement("span");
    span.className = "pending";
    span.textContent = (base ? " " : "") + pending;
    bar.appendChild(span);
  }
  bar.scrollTop = bar.scrollHeight;
}

function setSegment(text, lastCaptureTime) {
  const display = text || "";
  $("segmentBox").textContent = display;
  $("tiSegmentBox").textContent = display;
  const when = lastCaptureTime ? new Date(lastCaptureTime).toLocaleTimeString() : null;
  const onboarding = settings.activePanel === "onboarding";
  const tech = settings.activePanel === "tech";
  let hint;
  if (!when) {
    hint = onboarding
      ? " — Idle. Home = THEY ASKED. PageUp = capture only. PageDown = onboarding prompt."
      : tech
        ? " — Idle. Home/End/Delete = capture+send. PageUp = capture only. PageDown = reminder."
        : " — Idle. Home/End = capture+send. PageUp = capture only. PageDown = reminder.";
  } else {
    hint = onboarding
      ? ` — Last captured ${when}. Home = THEY ASKED. PageDown = onboarding prompt.`
      : ` — Last captured ${when}.`;
  }
  $("segmentHint").textContent = hint;
  $("tiHint").textContent = hint;
  $("segDot").className = when ? "dot monitoring" : "dot";
  $("tiDot").className = when ? "dot monitoring" : "dot";
}

function selectPanel(name, skipPersist) {
  settings.activePanel = name;
  document.querySelectorAll(".tabs button").forEach((b) => {
    b.classList.toggle("active", b.dataset.panel === name);
  });
  document.querySelectorAll(".panel").forEach((p) => {
    p.classList.toggle("active", p.id === `panel-${name}`);
  });
  if (!skipPersist) persist();
}

function renderRounds() {
  const host = $("roundPanel");
  host.innerHTML = "";
  for (const name of ROUNDS) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `pill${settings.selectedRound === name ? " active" : ""}`;
    btn.textContent = name;
    btn.addEventListener("click", () => {
      settings.selectedRound = name;
      renderRounds();
      persist();
    });
    host.appendChild(btn);
  }
}

function setPromptMode(individual) {
  settings.promptModeIndividual = individual;
  const btn = $("promptModeToggle");
  btn.textContent = individual ? "Individual" : "Full";
  btn.classList.toggle("on", individual);
}

async function loadTabs() {
  const tabs = await chrome.runtime.sendMessage({ type: "LC_LIST_TABS" });
  const picker = $("tabPicker");
  picker.innerHTML = "";
  (tabs || []).forEach((t) => {
    const opt = document.createElement("option");
    opt.value = t.id;
    opt.textContent = t.displayName;
    picker.appendChild(opt);
  });
  if (settings.captureTabId) picker.value = String(settings.captureTabId);
  $("techStatus").textContent = !tabs?.length
    ? "No visible tabs found. Try again."
    : `${tabs.length} tabs found. Select one and click Capture.`;
}

async function hydrate() {
  const state = await chrome.runtime.sendMessage({ type: "LC_GET_STATE" });
  settings = { ...settings, ...(state.settings || {}) };
  writeFields("pi", PI_KEYS, state.preInterview);
  writeFields("ob", OB_KEYS, state.onboarding);
  setPromptMode(!!settings.promptModeIndividual);
  renderRounds();
  selectPanel(settings.activePanel || "segment", true);
  const ui = state.ui || {};
  setStatus(ui.captureStatus || "idle");
  renderLiveBar(ui.display, ui.latestCaption);
  setSegment(ui.display?.pending || "", ui.lastCaptureTime);
  if (ui.lastScreenshot) {
    $("screenshot").src = ui.lastScreenshot;
    $("screenshot").hidden = false;
  }
  await loadTabs();
  await persist();
}

document.querySelectorAll(".tabs button").forEach((btn) => {
  btn.addEventListener("click", () => selectPanel(btn.dataset.panel));
});

$("btnChatGpt").addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "LC_OPEN_URL", url: "https://chatgpt.com" });
});
$("btnClaude").addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "LC_OPEN_URL", url: "https://claude.ai" });
});

$("btnStart").addEventListener("click", async () => {
  await persist();
  await chrome.runtime.sendMessage({ type: "LC_START" });
});

$("btnStop").addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "LC_STOP" });
});
$("btnClear").addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ type: "LC_CLEAR" });
  setSegment("", null);
  renderLiveBar(null, "");
  $("screenshot").hidden = true;
  $("screenshot").removeAttribute("src");
});

$("btnHome").addEventListener("click", () => chrome.runtime.sendMessage({ type: "LC_ACTION_HOME" }));
$("btnEnd").addEventListener("click", () => chrome.runtime.sendMessage({ type: "LC_ACTION_END" }));
$("btnPageUp").addEventListener("click", () => chrome.runtime.sendMessage({ type: "LC_ACTION_PAGEUP" }));
$("btnPageDown").addEventListener("click", () => chrome.runtime.sendMessage({ type: "LC_ACTION_PAGEDOWN" }));
$("btnTechSend").addEventListener("click", () => chrome.runtime.sendMessage({ type: "LC_ACTION_DELETE" }));
$("btnRefreshTabs").addEventListener("click", loadTabs);
$("btnCaptureTab").addEventListener("click", async () => {
  await persist();
  chrome.runtime.sendMessage({ type: "LC_ACTION_INSERT", tabId: Number($("tabPicker").value) || null });
});

$("promptModeToggle").addEventListener("click", () => {
  setPromptMode(!settings.promptModeIndividual);
  persist();
});
$("btnPiSend").addEventListener("click", async () => {
  await persist();
  chrome.runtime.sendMessage({ type: "LC_SEND_PRE" });
});
$("btnObLoad").addEventListener("click", async () => {
  await persist();
  chrome.runtime.sendMessage({ type: "LC_SEND_ONBOARDING" });
});
$("btnObSetup").addEventListener("click", async () => {
  await persist();
  chrome.runtime.sendMessage({ type: "LC_SEND_SETUP" });
});
$("btnObLog").addEventListener("click", async () => {
  await persist();
  chrome.runtime.sendMessage({ type: "LC_SEND_LOG" });
});

document.addEventListener("input", () => persist());
document.addEventListener("change", () => persist());

chrome.runtime.onMessage.addListener((msg) => {
  switch (msg?.type) {
    case "LC_STATUS":
      setStatus(msg.captureStatus, msg.error);
      break;
    case "LC_CAPTION":
      setStatus(msg.captureStatus || "monitoring");
      renderLiveBar(msg.display, msg.text);
      break;
    case "LC_SEGMENT":
      setSegment(msg.segment === "" ? "No new segment captured." : msg.segment, msg.lastCaptureTime);
      renderLiveBar(msg.display);
      break;
    case "LC_SEND_STATUS":
      setSendStatus(msg.message, msg.isError);
      break;
    case "LC_TECH_STATUS":
      $("techStatus").textContent = msg.message || "";
      break;
    case "LC_SCREENSHOT":
      $("screenshot").src = msg.dataUrl;
      $("screenshot").hidden = false;
      $("techStatus").textContent = msg.message || "";
      break;
    case "LC_CLEARED":
      setSegment("", null);
      renderLiveBar(null, "");
      break;
    default:
      break;
  }
});

hydrate();
