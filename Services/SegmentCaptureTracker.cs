using System;
using System.Text;

namespace LiveCaptionViewer.Services;

/// <summary>
/// Mirrors the segment timeline in <c>live.py</c> (lines 293–313, 126–138):
/// <list type="bullet">
///   <item>Committed prefix — lines that scrolled off the visible window.</item>
///   <item>Current buffer — full Live Captions snapshot, overwritten each poll.</item>
///   <item>Read head — byte offset; <see cref="CaptureNextSegment"/> reads from here to EOF.</item>
/// </list>
/// When the first visible line changes (and there are more than 10 lines), the old
/// first line is appended to the committed section — same as
/// <c>file.write(first_line + "\n")</c> in Python.
/// </summary>
public sealed class SegmentCaptureTracker
{
    private const int MinLinesForScrollCommit = 10;

    private readonly object _sync = new();
    private readonly StringBuilder _committed = new();

    private string _current = string.Empty;
    private string _firstLine = string.Empty;
    private int _readHead;

    /// <summary>
    /// Called on every caption poll (~500 ms). Pass the raw caption text (newlines preserved).
    /// </summary>
    public void UpdateLiveText(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return;

        lock (_sync)
        {
            string text = snapshot.TrimEnd();
            if (text == _current) return;

            string[] lines = SplitLines(text);

            // live.py: if len(lines) > 10 and first_line != lines[0]: commit first_line
            if (lines.Length > MinLinesForScrollCommit)
            {
                string newFirstLine = lines[0].Trim();
                if (!string.IsNullOrEmpty(_firstLine)
                    && !string.Equals(_firstLine, newFirstLine, StringComparison.Ordinal))
                {
                    CommitFirstLine(_firstLine);
                }

                if (!string.IsNullOrEmpty(newFirstLine))
                    _firstLine = newFirstLine;
            }

            // live.py: seek(pos); write(text); truncate()
            _current = text;

            string virtualFile = BuildVirtualFile();
            if (_readHead > virtualFile.Length)
                _readHead = virtualFile.Length;
        }
    }

    /// <summary>
    /// Returns text spoken since the previous capture (live.py <c>read_text_from_file</c>).
    /// </summary>
    public string CaptureNextSegment()
    {
        lock (_sync)
        {
            string virtualFile = BuildVirtualFile();

            if (_readHead >= virtualFile.Length)
                return string.Empty;

            string segment = virtualFile.Substring(_readHead).Trim();
            _readHead = virtualFile.Length;
            return segment;
        }
    }

    /// <summary>
    /// Returns the display split for the live caption bar:
    /// base = text already captured (read-head passed); pending = new text since last capture.
    /// </summary>
    public (string Base, string Pending) GetDisplayParts()
    {
        lock (_sync)
        {
            string virtualFile = BuildVirtualFile();
            if (string.IsNullOrEmpty(virtualFile))
                return (string.Empty, string.Empty);

            int head = Math.Min(_readHead, virtualFile.Length);
            return (virtualFile[..head], virtualFile[head..]);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _committed.Clear();
            _current = string.Empty;
            _firstLine = string.Empty;
            _readHead = 0;
        }
    }

    private void CommitFirstLine(string line)
    {
        string trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (_committed.Length > 0)
            _committed.AppendLine();

        _committed.Append(trimmed);
    }

    /// <summary>Virtual file = committed prefix + current buffer snapshot.</summary>
    private string BuildVirtualFile()
    {
        if (_committed.Length == 0) return _current;
        if (string.IsNullOrEmpty(_current)) return _committed.ToString();

        // Committed lines end with newline; buffer is the full visible text.
        return _committed.ToString() + _current;
    }

    private static string[] SplitLines(string text)
    {
        return text.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.None);
    }
}
