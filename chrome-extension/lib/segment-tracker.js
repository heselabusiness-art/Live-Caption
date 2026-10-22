/**
 * Port of SegmentCaptureTracker.cs - "what was said since the last capture?".
 *
 * Live Captions only shows a ROLLING window of the conversation: new words appear at the end, old
 * lines drop off the top, and the recogniser keeps revising the last few words. The tracker therefore
 * keeps its own transcript of every word heard and merges each snapshot into it by ALIGNING the
 * snapshot with the end of the transcript (word by word), instead of trusting character offsets into
 * the window (an offset points at different words once the window has scrolled, which made a
 * hotkey capture come back empty or cut in the middle of a sentence).
 *
 *   transcript  [w0 w1 w2 ... wN]      every word heard so far, with the whitespace that preceded it
 *   read head   index of the first word not captured yet
 *   snapshot    the window text right now; its first words are already in the transcript (alignment),
 *               its last words are new, and the last few transcript words may be revised by it.
 */
class SegmentCaptureTracker {
  /** the recogniser may still revise this many words at the end of the transcript */
  static UNSTABLE_TAIL = 6;
  /** how far before the transcript end a snapshot may start (it is a window over recent words) */
  static SLACK = 25;
  static MAX_WORDS = 20000;
  static KEEP_CAPTURED_WORDS = 400;

  constructor() {
    this._words = [];
    this._head = 0;
    this._last = "";
  }

  static normalize(word) {
    const n = word.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "");
    return n || word.toLowerCase();
  }

  static tokenize(text) {
    const out = [];
    const re = /(\s*)(\S+)/g;
    let m;
    while ((m = re.exec(text)) !== null) out.push({ ws: m[1], w: m[2], n: SegmentCaptureTracker.normalize(m[2]) });
    return out;
  }

  /** Called on every caption update with the full visible caption text (line breaks kept). */
  updateLiveText(snapshot) {
    if (!snapshot || !String(snapshot).trim()) return;
    const text = String(snapshot).replace(/\s+$/, "");
    if (text === this._last) return;
    this._last = text;

    const sw = SegmentCaptureTracker.tokenize(text);
    if (!sw.length) return;
    const tw = this._words;

    if (tw.length === 0) {
      sw[0].ws = "";
      this._words = sw;
      return;
    }

    const o = this._align(sw);
    if (o < 0) {
      // nothing in common with the end of the transcript: the window was cleared / a new passage started
      sw[0].ws = "\n";
      for (const w of sw) tw.push(w);
    } else if (o + sw.length >= tw.length) {
      // the snapshot reaches past the transcript end: replace the tail from the aligned word on (this also applies revisions)
      sw[0].ws = tw[o].ws;
      tw.length = o;
      for (const w of sw) tw.push(w);
    }
    // else: the snapshot is an older / shorter view of text we already have - nothing new in it

    if (this._head > this._words.length) this._head = this._words.length;
    this._trim();
  }

  /** Text spoken since the previous capture. */
  captureNextSegment() {
    if (this._head >= this._words.length) return "";
    const segment = this._join(this._head, this._words.length).trim();
    this._head = this._words.length;
    return segment;
  }

  /** Display split for the caption bar: base = already captured, pending = new since the last capture. */
  getDisplayParts() {
    if (!this._words.length) return { base: "", pending: "" };
    return {
      base: this._join(0, this._head),
      pending: this._join(this._head, this._words.length)
    };
  }

  reset() {
    this._words = [];
    this._head = 0;
    this._last = "";
  }

  /** Plain-data state, so the transcript survives a service-worker restart. */
  serialize() {
    return { words: this._words.map((x) => [x.ws, x.w]), head: this._head, last: this._last };
  }

  restore(state) {
    if (!state || !Array.isArray(state.words)) return;
    this._words = state.words.map(([ws, w]) => ({ ws, w, n: SegmentCaptureTracker.normalize(w) }));
    this._head = Math.min(Math.max(0, state.head | 0), this._words.length);
    this._last = typeof state.last === "string" ? state.last : "";
  }

  _join(from, to) {
    let s = "";
    for (let i = from; i < to; i++) s += this._words[i].ws + this._words[i].w;
    return s;
  }

  /**
   * Where does the snapshot start inside the transcript? Returns a word index, or -1 when the snapshot
   * has nothing in common with the end of the transcript.
   *
   * A candidate offset o is valid when every word of the overlap matches, except that the last
   * UNSTABLE_TAIL transcript words may differ (revisions) and the snapshot's first word may be a cut-off
   * suffix of a transcript word. Among valid candidates the one with the most matching words wins.
   */
  _align(sw) {
    const tw = this._words;
    const nT = tw.length;
    const nS = sw.length;
    const U = SegmentCaptureTracker.UNSTABLE_TAIL;
    let best = -1;
    let bestScore = 0;

    for (let o = Math.max(0, nT - nS - SegmentCaptureTracker.SLACK); o < nT; o++) {
      const L = Math.min(nS, nT - o);
      let matches = 0;
      let valid = true;
      for (let j = 0; j < L; j++) {
        const a = tw[o + j].n;
        const b = sw[j].n;
        if (a === b || (j === 0 && a.length > b.length && a.endsWith(b))) matches++;
        else if (o + j < nT - U) {
          valid = false; // a mismatch in the stable part: this is not where the snapshot starts
          break;
        }
      }
      if (!valid) continue;
      if (matches < Math.ceil(L * 0.6)) continue; // (mismatches are only tolerated inside the unstable tail)
      if (matches > bestScore) {
        best = o;
        bestScore = matches;
      }
    }
    return best;
  }

  _trim() {
    const max = SegmentCaptureTracker.MAX_WORDS;
    if (this._words.length <= max) return;
    const keep = SegmentCaptureTracker.KEEP_CAPTURED_WORDS;
    const drop = Math.max(this._head - keep, this._words.length - max);
    if (drop <= 0) return;
    this._words.splice(0, drop);
    this._head = Math.max(0, this._head - drop);
  }
}

if (typeof globalThis !== "undefined") {
  globalThis.SegmentCaptureTracker = SegmentCaptureTracker;
}
// CommonJS export so the logic can be unit-tested with `node --test` (ignored in the browser).
if (typeof module !== "undefined" && module.exports) {
  module.exports = { SegmentCaptureTracker };
}
