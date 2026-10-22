using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionViewer.Services;

/// <summary>
/// "What was said since the last capture?"
/// <para>
/// Live Captions only shows a ROLLING window of the conversation: new words appear at the end, old
/// lines drop off the top, and the recogniser keeps revising the last few words. The tracker therefore
/// keeps its own transcript of every word heard and merges each snapshot into it by ALIGNING the
/// snapshot with the end of the transcript, word by word, instead of trusting character offsets into
/// the window (an offset points at different words once the window has scrolled, which made a hotkey
/// capture come back empty or cut in the middle of a sentence).
/// </para>
/// <list type="bullet">
///   <item>Transcript — every word heard so far, with the whitespace that preceded it.</item>
///   <item>Read head — index of the first word that has not been captured yet.</item>
///   <item>Snapshot — the window text right now: its first words are already in the transcript
///   (alignment), its last words are new, and the last few transcript words may be revised by it.</item>
/// </list>
/// The same algorithm lives in <c>chrome-extension/lib/segment-tracker.js</c>.
/// </summary>
public sealed class SegmentCaptureTracker
{
    /// <summary>The recogniser may still revise this many words at the end of the transcript.</summary>
    private const int UnstableTail = 6;

    /// <summary>How far before the transcript end a snapshot may start (it is a window over recent words).</summary>
    private const int Slack = 25;

    private const int MaxWords = 20_000;
    private const int KeepCapturedWords = 400;

    private static readonly Regex WordRegex = new(@"(\s*)(\S+)", RegexOptions.Compiled);

    private readonly object _sync = new();
    private List<Word> _words = new();
    private int _head;
    private string _last = string.Empty;

    private sealed class Word
    {
        public string Ws = string.Empty; // whitespace before the word (line breaks are kept)
        public string Text = string.Empty;
        public string Norm = string.Empty;
    }

    /// <summary>Called on every caption update with the full visible caption text (line breaks kept).</summary>
    public void UpdateLiveText(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return;

        lock (_sync)
        {
            string text = snapshot.TrimEnd();
            if (text == _last) return;
            _last = text;

            List<Word> sw = Tokenize(text);
            if (sw.Count == 0) return;

            if (_words.Count == 0)
            {
                sw[0].Ws = string.Empty;
                _words = sw;
                return;
            }

            int o = Align(sw);
            if (o < 0)
            {
                // Nothing in common with the end of the transcript: the window was cleared / a new passage started.
                sw[0].Ws = "\n";
                _words.AddRange(sw);
            }
            else if (o + sw.Count >= _words.Count)
            {
                // The snapshot reaches the transcript end: replace the tail from the aligned word on (this also applies revisions).
                sw[0].Ws = _words[o].Ws;
                _words.RemoveRange(o, _words.Count - o);
                _words.AddRange(sw);
            }
            // else: an older / shorter view of text we already have - nothing new in it.

            if (_head > _words.Count) _head = _words.Count;
            Trim();
        }
    }

    /// <summary>Returns the text spoken since the previous capture and moves the read head to the end.</summary>
    public string CaptureNextSegment()
    {
        lock (_sync)
        {
            if (_head >= _words.Count) return string.Empty;

            string segment = Join(_head, _words.Count).Trim();
            _head = _words.Count;
            return segment;
        }
    }

    /// <summary>
    /// Display split for the live caption bar: base = text already captured, pending = new text since the last capture.
    /// </summary>
    public (string Base, string Pending) GetDisplayParts()
    {
        lock (_sync)
        {
            if (_words.Count == 0) return (string.Empty, string.Empty);
            return (Join(0, _head), Join(_head, _words.Count));
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _words = new List<Word>();
            _head = 0;
            _last = string.Empty;
        }
    }

    private string Join(int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to; i++) sb.Append(_words[i].Ws).Append(_words[i].Text);
        return sb.ToString();
    }

    private static List<Word> Tokenize(string text)
    {
        var list = new List<Word>();
        foreach (Match m in WordRegex.Matches(text))
            list.Add(new Word { Ws = m.Groups[1].Value, Text = m.Groups[2].Value, Norm = Normalize(m.Groups[2].Value) });
        return list;
    }

    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (char c in word)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.Length > 0 ? sb.ToString() : word.ToLowerInvariant();
    }

    /// <summary>
    /// Where does the snapshot start inside the transcript? Returns a word index, or -1 when the snapshot has nothing in
    /// common with the end of the transcript. A candidate offset is valid when every word of the overlap matches, except that
    /// the last <see cref="UnstableTail"/> transcript words may differ (revisions) and the snapshot's first word may be a cut-off
    /// suffix of a transcript word. Among valid candidates the one with the most matching words wins.
    /// </summary>
    private int Align(List<Word> sw)
    {
        int nT = _words.Count;
        int nS = sw.Count;
        int best = -1;
        int bestScore = 0;

        for (int o = Math.Max(0, nT - nS - Slack); o < nT; o++)
        {
            int length = Math.Min(nS, nT - o);
            int matches = 0;
            bool valid = true;

            for (int j = 0; j < length; j++)
            {
                string a = _words[o + j].Norm;
                string b = sw[j].Norm;
                if (a == b || (j == 0 && a.Length > b.Length && a.EndsWith(b, StringComparison.Ordinal)))
                    matches++;
                else if (o + j < nT - UnstableTail)
                {
                    valid = false; // a mismatch in the stable part: this is not where the snapshot starts
                    break;
                }
            }

            if (!valid) continue;
            if (matches < (int)Math.Ceiling(length * 0.6)) continue; // mismatches are only tolerated inside the unstable tail
            if (matches > bestScore)
            {
                best = o;
                bestScore = matches;
            }
        }

        return best;
    }

    private void Trim()
    {
        if (_words.Count <= MaxWords) return;

        int drop = Math.Max(_head - KeepCapturedWords, _words.Count - MaxWords);
        if (drop <= 0) return;

        _words.RemoveRange(0, drop);
        _head = Math.Max(0, _head - drop);
    }
}
