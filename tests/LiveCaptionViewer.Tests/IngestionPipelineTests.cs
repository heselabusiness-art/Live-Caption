using LiveCaptionViewer.Models;
using LiveCaptionViewer.Services;

namespace LiveCaptionViewer.Tests;

public class IngestionPipelineTests
{
    [Fact]
    public void KeepSingleStory_keeps_the_latest_app_appended_block()
    {
        string field = "Story: first\n\nbody one\n\n---\n\nStory: second\n\nbody two";

        string kept = PreInterviewIngestionPipeline.KeepSingleStory(field, out int found);

        Assert.Equal(2, found);
        Assert.Equal("Story: second\n\nbody two", kept);
    }

    [Fact]
    public void KeepSingleStory_handles_empty_and_single_blocks()
    {
        Assert.Equal("", PreInterviewIngestionPipeline.KeepSingleStory("  ", out int none));
        Assert.Equal(0, none);

        Assert.Equal("only one", PreInterviewIngestionPipeline.KeepSingleStory("only one", out int one));
        Assert.Equal(1, one);
    }

    [Fact]
    public void ParseResponse_reads_json_from_a_fenced_block()
    {
        string reply = """
            Here you go:
            ```json
            { "keySkillsDetected": "C#", "keyProjectsDetected": "Caption app", "jdFocusAreasDetected": "Latency" }
            ```
            """;

        var result = PreInterviewIngestionPipeline.ParseResponse(PreInterviewIngestionStep.JdAnalysis, reply);

        Assert.Equal("C#", result.Fields["keySkillsDetected"]);
        Assert.Empty(result.MissingRequiredKeys);
    }

    [Fact]
    public void ParseResponse_reports_missing_required_keys()
    {
        var result = PreInterviewIngestionPipeline.ParseResponse(
            PreInterviewIngestionStep.JdAnalysis, """{ "keySkillsDetected": "C#" }""");

        Assert.Contains("keyProjectsDetected", result.MissingRequiredKeys);
        Assert.Contains("jdFocusAreasDetected", result.MissingRequiredKeys);
    }

    [Fact]
    public void ParseResponse_rejects_an_empty_reply()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PreInterviewIngestionPipeline.ParseResponse(PreInterviewIngestionStep.JdAnalysis, "  "));
    }

    [Fact]
    public void Company_research_needs_a_company_and_the_rest_need_resume_and_jd()
    {
        var empty = new PreInterviewFormContext();

        Assert.Equal("Company", PreInterviewIngestionPipeline.GetMissingInputs(PreInterviewIngestionStep.CompanyResearch, empty));
        Assert.Equal("Resume Summary + Job Description",
            PreInterviewIngestionPipeline.GetMissingInputs(PreInterviewIngestionStep.JdAnalysis, empty));
        Assert.Null(PreInterviewIngestionPipeline.GetMissingInputs(
            PreInterviewIngestionStep.JdAnalysis,
            new PreInterviewFormContext { ResumeSummary = "r", JobDescription = "j" }));
    }
}
