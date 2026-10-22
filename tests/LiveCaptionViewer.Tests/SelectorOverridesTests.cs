using LiveCaptionViewer.Services;

namespace LiveCaptionViewer.Tests;

public class SelectorOverridesTests
{
    [Fact]
    public void Parse_reads_each_site_and_key()
    {
        var parsed = SelectorOverrides.Parse("""
            {
              "ChatGPT": { "input": "document.querySelector('#a')", "sendButton": "document.querySelector('#b')" },
              "claude":  { "isGenerating": "false", "stopButton": "document.querySelector('#s')" }
            }
            """);

        Assert.Equal("document.querySelector('#a')", parsed["ChatGPT"].Input);
        Assert.Equal("document.querySelector('#b')", parsed["ChatGPT"].SendButton);
        Assert.Null(parsed["ChatGPT"].IsGenerating);
        Assert.Equal("false", parsed["Claude"].IsGenerating);          // site lookup is case-insensitive
        Assert.Equal("document.querySelector('#s')", parsed["Claude"].StopButton);
    }

    [Fact]
    public void Parse_ignores_blank_and_non_string_values_and_tolerates_comments()
    {
        var parsed = SelectorOverrides.Parse("""
            {
              // comment
              "ChatGPT": { "input": "  ", "sendButton": 5, "stopButton": "x", },
              "Other": 7
            }
            """);

        Assert.Null(parsed["ChatGPT"].Input);
        Assert.Null(parsed["ChatGPT"].SendButton);
        Assert.Equal("x", parsed["ChatGPT"].StopButton);
        Assert.False(parsed.ContainsKey("Other"));
    }

    [Fact]
    public void Missing_file_means_no_overrides()
    {
        var overrides = new SelectorOverrides(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        Assert.Equal(SelectorOverrides.SelectorSet.None, overrides.For("ChatGPT"));
    }

    [Fact]
    public void File_edits_are_picked_up_and_a_broken_file_keeps_the_last_good_state()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """{ "ChatGPT": { "input": "one" } }""");
            var overrides = new SelectorOverrides(path);
            Assert.Equal("one", overrides.For("ChatGPT").Input);

            File.WriteAllText(path, """{ "ChatGPT": { "input": "two" } }""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("two", overrides.For("ChatGPT").Input);

            File.WriteAllText(path, "{ not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            Assert.Equal("two", overrides.For("ChatGPT").Input);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
