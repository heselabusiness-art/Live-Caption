using LiveCaptionViewer.Services;

namespace LiveCaptionViewer.Tests;

public class PromptBuilderTests
{
    private static readonly TriggerContext Full = new("Hiring Manager", IndividualMode: false, DiagramRound: false);

    [Fact]
    public void Question_uses_round_and_quoted_segment()
    {
        Assert.Equal(
            "ROUND: Hiring Manager\nQUESTION: \"why this company\"",
            PromptBuilder.BuildQuestion("why this company", Full));
    }

    [Fact]
    public void Individual_mode_drops_the_round_prefix()
    {
        var ctx = Full with { IndividualMode = true };

        Assert.Equal("QUESTION: \"why this company\"", PromptBuilder.BuildQuestion("why this company", ctx));
    }

    [Fact]
    public void Diagram_round_asks_for_a_diagram()
    {
        var ctx = Full with { DiagramRound = true };

        Assert.Equal(
            "ROUND: System Design\nDIAGRAM: \"design a cache\"",
            PromptBuilder.BuildQuestion("design a cache", ctx));
        Assert.Equal("DIAGRAM PUSHBACK: \"add a queue\"", PromptBuilder.BuildPushback("add a queue", ctx));
    }

    [Fact]
    public void Pushback_and_coding_triggers()
    {
        Assert.Equal("pushed back with: \"is that scalable\"", PromptBuilder.BuildPushback("is that scalable", Full));
        Assert.Equal("ROUND: Coding\nQUESTION: \"reverse a list\"", PromptBuilder.BuildCoding("reverse a list"));
    }

    [Fact]
    public void Quotes_in_the_segment_cannot_close_the_quoted_question()
    {
        string prompt = PromptBuilder.BuildQuestion("they said \"tell me\" about “scale”", Full);

        Assert.Equal("ROUND: Hiring Manager\nQUESTION: \"they said 'tell me' about 'scale'\"", prompt);
    }

    [Fact]
    public void Line_breaks_and_repeated_spaces_become_single_spaces()
    {
        Assert.Equal("one two three", PromptBuilder.SanitizeSegment("  one\r\ntwo\n\n  three \t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Blank_segments_sanitize_to_empty(string? input)
    {
        Assert.Equal("", PromptBuilder.SanitizeSegment(input));
    }

    [Fact]
    public void Long_segments_keep_their_most_recent_words()
    {
        string old = string.Join(" ", Enumerable.Repeat("oldword", 2000));
        string segment = old + " the actual last question";

        string result = PromptBuilder.SanitizeSegment(segment);

        Assert.True(result.Length <= PromptBuilder.MaxSegmentChars + 2);
        Assert.StartsWith("… ", result);
        Assert.EndsWith("the actual last question", result);
    }

    [Fact]
    public void Reminder_block_is_extracted_between_the_rule_lines()
    {
        string file = string.Join("\n",
            "DRIFT REMINDERS",
            "",
            "════════════════════════════════════════",
            "FULL REMINDER (copy everything between the lines)",
            "════════════════════════════════════════",
            "RULES REMINDER. Silent directive.",
            "RULE 1 LENGTH.",
            "════════════════════════════════════════",
            "",
            "QUICK NUDGES",
            "TOO LONG: cut it");

        Assert.Equal("RULES REMINDER. Silent directive.\nRULE 1 LENGTH.", PromptBuilder.ExtractFullReminder(file));
    }

    [Fact]
    public void Reminder_without_a_block_is_sent_whole()
    {
        Assert.Equal("just a plain reminder", PromptBuilder.ExtractFullReminder("just a plain reminder"));
    }
}
