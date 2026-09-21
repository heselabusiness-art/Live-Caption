/** Port of SegmentCaptureTracker.cs — committed prefix + current buffer + read head. */
class SegmentCaptureTracker {
  static MIN_LINES_FOR_SCROLL_COMMIT = 10;

  constructor() {
    this._committed = "";
    this._current = "";
    this._firstLine = "";
    this._readHead = 0;
  }

  updateLiveText(snapshot) {
    if (!snapshot || !String(snapshot).trim()) return;
    const text = String(snapshot).replace(/\s+$/, "");
    if (text === this._current) return;

    const lines = text.split(/\r\n|\n|\r/);
    if (lines.length > SegmentCaptureTracker.MIN_LINES_FOR_SCROLL_COMMIT) {
      const newFirstLine = (lines[0] || "").trim();
      if (this._firstLine && this._firstLine !== newFirstLine) {
        this._commitFirstLine(this._firstLine);
      }
      if (newFirstLine) this._firstLine = newFirstLine;
    }

    this._current = text;
    const virtualFile = this._buildVirtualFile();
    if (this._readHead > virtualFile.length) this._readHead = virtualFile.length;
  }

  captureNextSegment() {
    const virtualFile = this._buildVirtualFile();
    if (this._readHead >= virtualFile.length) return "";
    const segment = virtualFile.slice(this._readHead).trim();
    this._readHead = virtualFile.length;
    return segment;
  }

  getDisplayParts() {
    const virtualFile = this._buildVirtualFile();
    if (!virtualFile) return { base: "", pending: "" };
    const head = Math.min(this._readHead, virtualFile.length);
    return { base: virtualFile.slice(0, head), pending: virtualFile.slice(head) };
  }

  reset() {
    this._committed = "";
    this._current = "";
    this._firstLine = "";
    this._readHead = 0;
  }

  _commitFirstLine(line) {
    const trimmed = line.trim();
    if (!trimmed) return;
    this._committed = this._committed
      ? `${this._committed}\n${trimmed}`
      : trimmed;
  }

  _buildVirtualFile() {
    if (!this._committed) return this._current;
    if (!this._current) return this._committed;
    return this._committed + this._current;
  }
}

if (typeof globalThis !== "undefined") {
  globalThis.SegmentCaptureTracker = SegmentCaptureTracker;
}
