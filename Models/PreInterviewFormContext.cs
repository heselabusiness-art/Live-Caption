namespace LiveCaptionViewer.Models;

/// <summary>Snapshot of Pre-interview tab inputs used to build ingestion prompts.</summary>
public sealed class PreInterviewFormContext
{
    public string Seniority { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Company { get; init; } = "";
    public string CompanyStage { get; init; } = "";
    public string ResumeSummary { get; init; } = "";
    public string JobDescription { get; init; } = "";
    public string InterviewerBackground { get; init; } = "";
}
