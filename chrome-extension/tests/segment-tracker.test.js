const test = require("node:test");
const assert = require("node:assert/strict");
const { SegmentCaptureTracker } = require("../lib/segment-tracker.js");

test("capture returns what was heard, then nothing until new speech arrives", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("Tell me about yourself.");
  assert.equal(t.captureNextSegment(), "Tell me about yourself.");
  assert.equal(t.captureNextSegment(), "");

  t.updateLiveText("Tell me about yourself. Why this job?");
  assert.equal(t.captureNextSegment(), "Why this job?");
});

test("lines that scroll off the caption window are kept", () => {
  const t = new SegmentCaptureTracker();
  const lines = Array.from({ length: 12 }, (_, i) => `line ${i + 1}`);
  t.updateLiveText(lines.join("\n"));
  t.updateLiveText([...lines.slice(1), "line 13"].join("\n"));

  const segment = t.captureNextSegment();
  assert.match(segment, /line 1/);
  assert.match(segment, /line 13/);
});

test("reset forgets everything", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("something said earlier");
  t.reset();
  assert.equal(t.captureNextSegment(), "");
});

// ── Live Captions shows a ROLLING window; captures must follow the words, not offsets into the window ──

const words = (s) => s.split(/\s+/).filter(Boolean);
const windowOf = (heard, maxChars) => {
  const out = [];
  let n = 0;
  for (let i = heard.length - 1; i >= 0; i--) {
    n += heard[i].length + 1;
    if (n > maxChars) break;
    out.unshift(heard[i]);
  }
  return out.join(" ");
};
const SPEECH = words(
  "tell me about a time you led a project under tight deadlines and how you handled conflict between two senior engineers on your team and what you would do differently today given what you learned"
);

test("REGRESSION: a full, scrolling window still yields every new word at the next hotkey press", () => {
  for (const cap of [90, 150, 400]) {
    const t = new SegmentCaptureTracker();
    const heard = [];
    const caps = [];
    SPEECH.forEach((w, i) => {
      heard.push(w);
      t.updateLiveText(windowOf(heard, cap));
      if (i === 18) caps.push(t.captureNextSegment());
    });
    caps.push(t.captureNextSegment());
    assert.equal(caps.join(" "), SPEECH.join(" "), `window of ${cap} chars`);
    assert.equal(t.captureNextSegment(), "");
  }
});

test("the recogniser revising the last words does not duplicate or lose anything", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("tell me about a time");
  t.updateLiveText("tell me about the time you");
  t.updateLiveText("tell me about the time you led a project.");
  assert.equal(t.captureNextSegment(), "tell me about the time you led a project.");
  t.updateLiveText("tell me about the time you led a project. what went wrong");
  assert.equal(t.captureNextSegment(), "what went wrong");
});

test("capitalisation / punctuation added when a sentence is finalised is not new speech", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("so why do you want this job");
  assert.equal(t.captureNextSegment(), "so why do you want this job");
  t.updateLiveText("So, why do you want this job?");
  assert.equal(t.captureNextSegment(), "");
});

test("when Live Captions clears the window and starts a new passage, the new passage is captured", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("first question about leadership and conflict");
  t.captureNextSegment();
  t.updateLiveText("next topic is system design for a feed ranking service");
  assert.equal(t.captureNextSegment(), "next topic is system design for a feed ranking service");
});

test("only the latest sentence left in the window: nothing is re-captured", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("one two three four five six seven eight nine ten");
  t.captureNextSegment();
  t.updateLiveText("seven eight nine ten eleven twelve");
  assert.equal(t.captureNextSegment(), "eleven twelve");
});

test("a stale, older read of the window never rewinds the transcript", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("alpha beta gamma delta epsilon zeta eta theta");
  t.updateLiveText("gamma delta epsilon zeta");
  assert.equal(t.captureNextSegment(), "alpha beta gamma delta epsilon zeta eta theta");
});

test("a window that starts in the middle of a word still lines up", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("we should migrate the monolith to services next quarter");
  t.captureNextSegment();
  t.updateLiveText("olith to services next quarter and add observability");
  assert.equal(t.captureNextSegment(), "and add observability");
});

test("line breaks inside a segment are kept", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("line one\nline two");
  assert.equal(t.captureNextSegment(), "line one\nline two");
});

test("display parts split what was captured from what is pending", () => {
  const t = new SegmentCaptureTracker();
  t.updateLiveText("first question");
  t.captureNextSegment();
  t.updateLiveText("first question and a follow up");
  assert.deepEqual(t.getDisplayParts(), { base: "first question", pending: " and a follow up" });
});

test("state survives a service-worker restart (serialize / restore)", () => {
  const a = new SegmentCaptureTracker();
  a.updateLiveText("tell me about yourself and why now");
  a.captureNextSegment();
  a.updateLiveText("tell me about yourself and why now what is your biggest weakness");
  const b = new SegmentCaptureTracker();
  b.restore(JSON.parse(JSON.stringify(a.serialize())));
  b.updateLiveText("about yourself and why now what is your biggest weakness really");
  assert.equal(b.captureNextSegment(), "what is your biggest weakness really");
});

test("long sessions stay bounded", () => {
  const t = new SegmentCaptureTracker();
  const heard = [];
  for (let i = 0; i < 30000; i++) {
    heard.push(`w${i}`);
    t.updateLiveText(windowOf(heard, 200));
    if (i % 50 === 0) t.captureNextSegment();
  }
  assert.ok(t.serialize().words.length <= SegmentCaptureTracker.MAX_WORDS);
  assert.match(t.captureNextSegment(), /w29999$/);
});

test("fuzz: rolling windows of any size, revisions and random hotkey presses never lose or repeat a word", () => {
  let seed = 12345;
  const rnd = () => ((seed = (seed * 1664525 + 1013904223) % 4294967296) / 4294967296);
  for (let run = 0; run < 60; run++) {
    const speech = Array.from({ length: 150 + Math.floor(rnd() * 200) }, (_, i) => `w${i}${rnd() < 0.15 ? "x" : ""}`);
    const cap = 60 + Math.floor(rnd() * 400);
    const t = new SegmentCaptureTracker();
    const heard = [];
    const captured = [];
    for (let i = 0; i < speech.length; i++) {
      heard.push(speech[i]);
      if (rnd() < 0.2 && heard.length > 2) {
        // an interim guess for the last word, corrected by the next update
        const wrong = [...heard.slice(0, -1), `guess${i}`];
        t.updateLiveText(windowOf(wrong, cap));
      }
      t.updateLiveText(windowOf(heard, cap));
      if (rnd() < 0.05) captured.push(t.captureNextSegment());
    }
    captured.push(t.captureNextSegment());
    assert.equal(captured.join(" ").replace(/\s+/g, " ").trim(), speech.join(" "), `run ${run}, window ${cap}`);
  }
});
