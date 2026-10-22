using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionViewer.Services;

/// <summary>What the trigger prompt needs to know about the Interview-round controls.</summary>
/// <param name="RoundName">Name of the selected round, e.g. "Hiring Manager".</param>
/// <param name="IndividualMode">Individual prompt mode: the round is baked into the loaded prompt.</param>
/// <param name="DiagramRound">The diagram round is selected (ASCII diagram answers).</param>
public readonly record struct TriggerContext(string RoundName, bool IndividualMode, bool DiagramRound);

/// <summary>
/// Builds the short trigger messages that follow the big setup prompt (<c>Prompt.txt</c>) and
/// prepares text for them. Pure string logic, so it is unit-tested.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// A captured segment can be far longer than one question when the capture button was not
    /// pressed for a while. Only the most recent part is kept: that is what was just asked.
    /// </summary>
    public const int MaxSegmentChars = 4000;

    /// <summary>
    /// Makes a captured caption safe to embed between double quotes in a trigger prompt:
    /// line breaks and runs of spaces become single spaces, double quotes become single quotes
    /// (a stray <c>"</c> would otherwise end the quoted question early), and over-long text is
    /// cut to its tail on a word boundary.
    /// </summary>
    public static string SanitizeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "";

        string s = segment
            .Replace('"', '\'')
            .Replace('“', '\'')   // “
            .Replace('”', '\'')   // ”
            .Replace('„', '\'');  // „
        s = Regex.Replace(s, @"\s+", " ").Trim();

        if (s.Length <= MaxSegmentChars) return s;

        string tail = s[^MaxSegmentChars..];
        int firstSpace = tail.IndexOf(' ');
        if (firstSpace > 0 && firstSpace < 80)
            tail = tail[(firstSpace + 1)..];
        return "… " + tail;
    }

    /// <summary>Home / Vol Up: <c>ROUND: …\nQUESTION: "…"</c> (see Prompt.txt TRIGGER COMMANDS).</summary>
    public static string BuildQuestion(string segment, TriggerContext ctx)
    {
        string text = SanitizeSegment(segment);

        if (ctx.IndividualMode)
            return $"QUESTION: \"{text}\"";

        if (ctx.DiagramRound)
            return $"ROUND: System Design\nDIAGRAM: \"{text}\"";

        return $"ROUND: {ctx.RoundName}\nQUESTION: \"{text}\"";
    }

    /// <summary>End / Vol Down: the interviewer pushed back on the previous answer.</summary>
    public static string BuildPushback(string segment, TriggerContext ctx)
    {
        string text = SanitizeSegment(segment);
        return ctx.DiagramRound
            ? $"DIAGRAM PUSHBACK: \"{text}\""
            : $"pushed back with: \"{text}\"";
    }

    /// <summary>Delete (Tech Interview tab): live-coding question.</summary>
    public static string BuildCoding(string segment) =>
        $"ROUND: Coding\nQUESTION: \"{SanitizeSegment(segment)}\"";

    /// <summary>
    /// Reminder.txt is a reference sheet (header + FULL REMINDER + QUICK NUDGES). Only the text
    /// between the two ═══ lines under "FULL REMINDER" is meant to be sent. If the file has no
    /// such block (e.g. an older single-text reminder), the whole file is sent unchanged.
    /// </summary>
    public static string ExtractFullReminder(string fileText)
    {
        var m = Regex.Match(fileText,
            @"FULL REMINDER[^\r\n]*\r?\n[═=]{5,}[ \t]*\r?\n(.*?)\r?\n[═=]{5,}",
            RegexOptions.Singleline);
        if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
            return m.Groups[1].Value.Trim();
        return fileText;
    }
}
