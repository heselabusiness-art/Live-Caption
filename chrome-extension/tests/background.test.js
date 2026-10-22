const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

/**
 * Boots background.js in a sandbox with a fake `chrome`, the way Chrome does it: a service worker that is
 * stopped after a while and started again by the next hotkey. `storage` is shared between boots (it
 * outlives the worker); everything else is fresh.
 */
function bootWorker({ storage, host }) {
  const sent = [];
  const listeners = { command: null };
  const noop = () => {};
  const ev = () => ({ addListener: noop });
  const chrome = {
    storage: {
      session: {
        get: async (keys) => {
          const out = {};
          for (const k of [].concat(keys)) if (k in storage) out[k] = JSON.parse(JSON.stringify(storage[k]));
          return out;
        },
        set: async (o) => Object.assign(storage, JSON.parse(JSON.stringify(o)))
      },
      local: { get: async () => ({}), set: async () => {} }
    },
    runtime: {
      sendMessage: (m) => { sent.push(m); return Promise.resolve(); },
      onMessage: ev(), onInstalled: ev(), onStartup: ev(), onConnect: ev(),
      getURL: (p) => p, connectNative: () => { throw new Error("no native host in this test"); }, lastError: null
    },
    commands: { onCommand: { addListener: (fn) => { listeners.command = fn; } } },
    action: { onClicked: ev(), setBadgeText: noop, setBadgeBackgroundColor: noop },
    sidePanel: { setPanelBehavior: async () => {}, setOptions: async () => {}, open: async () => {} },
    tabs: { query: async () => [], sendMessage: async () => ({ ok: true, message: "Sent." }), get: async () => ({}) , update: async () => ({}) },
    windows: { update: async () => ({}) },
    scripting: { executeScript: async () => {} }
  };
  const fetchStub = async (url, opts = {}) => ({
    ok: true,
    status: 200,
    json: async () => ({ type: "snapshot", status: "monitoring", text: host.text, error: null }),
    text: async () => ""
  });
  const ctx = {
    chrome, fetch: fetchStub, console, AbortController, setTimeout, clearTimeout,
    setInterval: () => 1, clearInterval: noop, Promise, JSON, Date,
    importScripts: (...files) => {
      for (const f of files) vm.runInContext(fs.readFileSync(path.join(__dirname, "..", f), "utf8"), ctx, { filename: f });
    }
  };
  vm.createContext(ctx);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8"), ctx, { filename: "background.js" });
  return { sent, fire: (cmd) => listeners.command(cmd), ctx };
}

const segments = (sent) => sent.filter((m) => m.type === "LC_SEGMENT").map((m) => m.segment);
const settle = (ms = 320) => new Promise((r) => setTimeout(r, ms));

test("REGRESSION: the hotkey wakes a FRESH service worker and still captures the caption that is on screen", async () => {
  const storage = {};
  const host = { text: "Tell me about a time you led a project under pressure." };
  const w = bootWorker({ storage, host });
  await w.fire("capture-only"); // nothing was polled yet: the worker must start capture and read the host itself
  assert.deepEqual(segments(w.sent), ["Tell me about a time you led a project under pressure."]);
});

test("the read head survives a service-worker restart: the next hotkey returns only the NEW speech", async () => {
  const storage = {};
  const host = { text: "Tell me about a time you led a project under pressure." };
  const first = bootWorker({ storage, host });
  await first.fire("capture-only");
  await settle(); // state is mirrored into storage.session

  host.text = "Tell me about a time you led a project under pressure. How did you handle conflict?";
  const second = bootWorker({ storage, host }); // Chrome stopped the worker and started a new one
  await second.fire("capture-only");
  assert.deepEqual(segments(second.sent), ["How did you handle conflict?"]);
});

test("a scrolling caption window never costs words, also across restarts", async () => {
  const storage = {};
  const host = { text: "one two three four five six seven eight nine ten" };
  const a = bootWorker({ storage, host });
  await a.fire("capture-only");
  await settle();
  host.text = "five six seven eight nine ten eleven twelve thirteen"; // old lines scrolled off the window
  const b = bootWorker({ storage, host });
  await b.fire("capture-only");
  assert.deepEqual(segments(b.sent), ["eleven twelve thirteen"]);
});

test("an empty capture is never silent: the panel gets an explanation", async () => {
  const storage = {};
  const host = { text: "" };
  const w = bootWorker({ storage, host });
  await w.fire("capture-send");
  const status = w.sent.filter((m) => m.type === "LC_SEND_STATUS" && m.isError);
  assert.equal(status.length, 1);
  assert.match(status[0].message, /Nothing to send/);
});
