using LiveCaptionViewer.Services;

namespace LiveCaptionViewer.Tests;

public class SegmentCaptureTrackerTests
{
    [Fact]
    public void Capture_returns_text_heard_so_far()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("Tell me about yourself");

        Assert.Equal("Tell me about yourself", t.CaptureNextSegment());
    }

    [Fact]
    public void Second_capture_without_new_speech_is_empty()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("Tell me about yourself");
        t.CaptureNextSegment();

        Assert.Equal("", t.CaptureNextSegment());
    }

    [Fact]
    public void Capture_returns_only_text_added_since_the_previous_capture()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("Tell me about yourself.");
        t.CaptureNextSegment();

        t.UpdateLiveText("Tell me about yourself. Why do you want this job?");

        Assert.Equal("Why do you want this job?", t.CaptureNextSegment());
    }

    [Fact]
    public void Blank_snapshots_are_ignored()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("hello there");
        t.UpdateLiveText("   ");

        Assert.Equal("hello there", t.CaptureNextSegment());
    }

    [Fact]
    public void Lines_that_scroll_off_the_window_are_kept()
    {
        var t = new SegmentCaptureTracker();

        // Live Captions shows a rolling window. Once it holds more than 10 lines the top line
        // scrolls away; the tracker must commit it so it is still part of the next capture.
        string[] lines = Enumerable.Range(1, 12).Select(i => $"line {i}").ToArray();
        t.UpdateLiveText(string.Join("\n", lines));
        t.UpdateLiveText(string.Join("\n", lines.Skip(1).Append("line 13")));

        string segment = t.CaptureNextSegment();

        Assert.Contains("line 1", segment);
        Assert.Contains("line 13", segment);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("something said earlier");
        t.Reset();

        Assert.Equal("", t.CaptureNextSegment());
        Assert.Equal(("", ""), t.GetDisplayParts());
    }

    [Fact]
    public void Display_parts_split_captured_from_pending()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("first question");
        t.CaptureNextSegment();
        t.UpdateLiveText("first question and a follow up");

        var (captured, pending) = t.GetDisplayParts();

        Assert.Equal("first question", captured);
        Assert.Equal(" and a follow up", pending);
    }

    // ── Live Captions shows a ROLLING window; captures must follow the words, not offsets into the window ──

    private static readonly string[] Speech =
        ("tell me about a time you led a project under tight deadlines and how you handled conflict between two " +
         "senior engineers on your team and what you would do differently today given what you learned").Split(' ');

    private static string WindowOf(IReadOnlyList<string> heard, int maxChars)
    {
        var words = new List<string>();
        int n = 0;
        for (int i = heard.Count - 1; i >= 0; i--)
        {
            n += heard[i].Length + 1;
            if (n > maxChars) break;
            words.Insert(0, heard[i]);
        }
        return string.Join(' ', words);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(150)]
    [InlineData(400)]
    public void A_full_scrolling_window_still_yields_every_new_word_at_the_next_hotkey_press(int windowChars)
    {
        // REGRESSION: with a full window the old tracker returned nothing, or a sentence cut in the middle.
        var t = new SegmentCaptureTracker();
        var heard = new List<string>();
        var captures = new List<string>();

        for (int i = 0; i < Speech.Length; i++)
        {
            heard.Add(Speech[i]);
            t.UpdateLiveText(WindowOf(heard, windowChars));
            if (i == 18) captures.Add(t.CaptureNextSegment());
        }
        captures.Add(t.CaptureNextSegment());

        Assert.Equal(string.Join(' ', Speech), string.Join(' ', captures));
        Assert.Equal("", t.CaptureNextSegment());
    }

    [Fact]
    public void The_recogniser_revising_the_last_words_does_not_duplicate_or_lose_anything()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("tell me about a time");
        t.UpdateLiveText("tell me about the time you");
        t.UpdateLiveText("tell me about the time you led a project.");
        Assert.Equal("tell me about the time you led a project.", t.CaptureNextSegment());

        t.UpdateLiveText("tell me about the time you led a project. what went wrong");
        Assert.Equal("what went wrong", t.CaptureNextSegment());
    }

    [Fact]
    public void Punctuation_added_when_a_sentence_is_finalised_is_not_new_speech()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("so why do you want this job");
        Assert.Equal("so why do you want this job", t.CaptureNextSegment());

        t.UpdateLiveText("So, why do you want this job?");
        Assert.Equal("", t.CaptureNextSegment());
    }

    [Fact]
    public void A_cleared_window_with_a_new_passage_is_captured()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("first question about leadership and conflict");
        t.CaptureNextSegment();

        t.UpdateLiveText("next topic is system design for a feed ranking service");
        Assert.Equal("next topic is system design for a feed ranking service", t.CaptureNextSegment());
    }

    [Fact]
    public void Only_the_latest_sentence_left_in_the_window_recaptures_nothing_old()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("one two three four five six seven eight nine ten");
        t.CaptureNextSegment();

        t.UpdateLiveText("seven eight nine ten eleven twelve");
        Assert.Equal("eleven twelve", t.CaptureNextSegment());
    }

    [Fact]
    public void A_stale_older_read_of_the_window_never_rewinds_the_transcript()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("alpha beta gamma delta epsilon zeta eta theta");
        t.UpdateLiveText("gamma delta epsilon zeta");

        Assert.Equal("alpha beta gamma delta epsilon zeta eta theta", t.CaptureNextSegment());
    }

    [Fact]
    public void A_window_that_starts_in_the_middle_of_a_word_still_lines_up()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("we should migrate the monolith to services next quarter");
        t.CaptureNextSegment();

        t.UpdateLiveText("olith to services next quarter and add observability");
        Assert.Equal("and add observability", t.CaptureNextSegment());
    }

    [Fact]
    public void Line_breaks_inside_a_segment_are_kept()
    {
        var t = new SegmentCaptureTracker();
        t.UpdateLiveText("line one\nline two");

        Assert.Equal("line one\nline two", t.CaptureNextSegment());
    }

    [Fact]
    public void Long_sessions_stay_bounded_and_keep_working()
    {
        var t = new SegmentCaptureTracker();
        var heard = new List<string>();

        for (int i = 0; i < 30_000; i++)
        {
            heard.Add($"w{i}");
            t.UpdateLiveText(WindowOf(heard, 200));
            if (i % 50 == 0) t.CaptureNextSegment();
        }

        Assert.EndsWith("w29999", t.CaptureNextSegment());
    }

    [Fact]
    public void Fuzz_rolling_windows_revisions_and_random_hotkey_presses_never_lose_or_repeat_a_word()
    {
        var rnd = new Random(12345);

        for (int run = 0; run < 60; run++)
        {
            int count = 150 + rnd.Next(200);
            var speech = Enumerable.Range(0, count).Select(i => $"w{i}{(rnd.NextDouble() < 0.15 ? "x" : "")}").ToList();
            int window = 60 + rnd.Next(400);
            var t = new SegmentCaptureTracker();
            var heard = new List<string>();
            var captured = new List<string>();

            for (int i = 0; i < speech.Count; i++)
            {
                heard.Add(speech[i]);
                if (rnd.NextDouble() < 0.2 && heard.Count > 2)
                {
                    // an interim guess for the last word, corrected by the next update
                    var wrong = heard.Take(heard.Count - 1).Append($"guess{i}").ToList();
                    t.UpdateLiveText(WindowOf(wrong, window));
                }
                t.UpdateLiveText(WindowOf(heard, window));
                if (rnd.NextDouble() < 0.05) captured.Add(t.CaptureNextSegment());
            }
            captured.Add(t.CaptureNextSegment());

            Assert.True(
                string.Join(' ', speech) == string.Join(' ', captured.Select(c => c.Replace('\n', ' ')).Where(c => c.Length > 0)),
                $"run {run}, window {window}");
        }
    }
}
