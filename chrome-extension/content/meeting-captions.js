/** Poll Meet / Zoom / Teams caption regions and forward text to the background. */

function readMeetingCaptions() {
  const host = location.hostname;
  const chunks = [];

  const push = (el) => {
    const t = (el?.innerText || el?.textContent || "").trim();
    if (t) chunks.push(t);
  };

  if (host.includes("meet.google.com")) {
    document
      .querySelectorAll(
        '[jsname="tgaKEf"], .a4cQT, [data-is-persistent], [class*="caption"], [aria-label*="caption" i]'
      )
      .forEach(push);
  } else if (host.includes("zoom.us")) {
    document
      .querySelectorAll(
        '.live-transcription-subtitle, .caption-container, [class*="live-transcription"], [class*="cc-"]'
      )
      .forEach(push);
  } else if (host.includes("teams.microsoft.com") || host.includes("teams.live.com")) {
    document
      .querySelectorAll(
        '[data-tid="closed-caption-text"], [data-tid*="caption"], [class*="fui-ChatMyMessage"], [class*="caption"]'
      )
      .forEach(push);
  }

  return chunks.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

let lastSent = "";
let pollTimer = null;

function tick() {
  const text = readMeetingCaptions();
  if (!text || text === lastSent) return;
  lastSent = text;
  chrome.runtime.sendMessage({ type: "LC_MEETING_CAPTION", text }).catch(() => {});
}

function start() {
  if (pollTimer) return;
  pollTimer = setInterval(tick, 400);
}

function stop() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "LC_MEETING_START") start();
  if (msg?.type === "LC_MEETING_STOP") stop();
});

start();
