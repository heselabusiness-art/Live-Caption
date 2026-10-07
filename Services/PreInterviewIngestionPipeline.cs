using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveCaptionViewer.Models;

namespace LiveCaptionViewer.Services;

public enum PreInterviewIngestionStep
{
    CompanyResearch,
    JdAnalysis,
    ProjectGeneration,
    SystemDesignGeneration
}

public sealed class PreInterviewIngestionResult
{
    public required PreInterviewIngestionStep Step { get; init; }
    public required Dictionary<string, string> Fields { get; init; }
    public string RawResponse { get; init; } = "";
    public IReadOnlyList<string> MissingRequiredKeys { get; init; } = [];
    public IReadOnlyList<string> TrimmedKeys { get; init; } = [];
}

/// <summary>
/// Loads pre-interview prompt files, appends JSON output instructions, parses GPT
/// responses, and maps keys to Pre-interview form fields.
/// </summary>
public static class PreInterviewIngestionPipeline
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string GetStepDisplayName(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.CompanyResearch => "Company Research",
        PreInterviewIngestionStep.JdAnalysis => "JD Analysis",
        PreInterviewIngestionStep.ProjectGeneration => "Project Stories",
        PreInterviewIngestionStep.SystemDesignGeneration => "System Design Scenarios",
        _ => step.ToString()
    };

    public static string GetPromptFileName(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.CompanyResearch => "Company Reseach Prompt.txt",
        PreInterviewIngestionStep.JdAnalysis => "JD analysis prompt.txt",
        PreInterviewIngestionStep.ProjectGeneration => "Project Generation Prompt.txt",
        PreInterviewIngestionStep.SystemDesignGeneration => "System Design Generation prompt.txt",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    public static string BuildPrompt(PreInterviewIngestionStep step, PreInterviewFormContext ctx)
    {
        string path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "pre interview prompt",
            GetPromptFileName(step));

        if (!File.Exists(path))
            throw new FileNotFoundException($"Ingestion prompt not found: {path}");

        string template = File.ReadAllText(path);
        string filled = FillPlaceholders(template, ctx, step);
        filled += Environment.NewLine + Environment.NewLine + GetSingleStoryOverride(step);

        string concise = GetConciseOverride(step);
        if (concise.Length > 0)
            filled += Environment.NewLine + Environment.NewLine + concise;

        return filled + Environment.NewLine + Environment.NewLine + GetJsonOutputInstruction(step);
    }

    /// <summary>Which required inputs are still empty for a step (null = ready to run).</summary>
    public static string? GetMissingInputs(PreInterviewIngestionStep step, PreInterviewFormContext ctx)
    {
        switch (step)
        {
            case PreInterviewIngestionStep.CompanyResearch:
                return string.IsNullOrWhiteSpace(ctx.Company) ? "Company" : null;

            default:
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(ctx.ResumeSummary)) missing.Add("Resume Summary");
                if (string.IsNullOrWhiteSpace(ctx.JobDescription)) missing.Add("Job Description");
                return missing.Count == 0 ? null : string.Join(" + ", missing);
        }
    }

    /// <summary>One-line "what this step needs / produces" shown on the progress stepper.</summary>
    public static string GetStepHint(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.CompanyResearch =>
            "Needs Company (JD + Seniority/Domain help). Fills company context.",
        PreInterviewIngestionStep.JdAnalysis =>
            "Needs Resume + JD. Fills skills, projects, focus areas, through-line.",
        PreInterviewIngestionStep.ProjectGeneration =>
            "Needs Resume + JD. Writes ONE project story (replaces the previous one).",
        PreInterviewIngestionStep.SystemDesignGeneration =>
            "Needs Resume + JD. Writes ONE system design scenario (replaces the previous one).",
        _ => ""
    };

    // ── Concise / anti-drift output (Company Research + JD Analysis) ──────────

    /// <summary>Word budget per field. Prompt asks for the limit; parser clamps at 1.5×.</summary>
    private static readonly Dictionary<string, int> ConciseWordLimits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Company Research
            ["companyStage"] = 6,
            ["company"] = 8,
            ["product"] = 25,
            ["teamFocus"] = 25,
            ["knownTechStack"] = 30,
            ["companyValues"] = 20,
            ["recentNews"] = 45,
            ["typicalCandidateProfile"] = 40,
            ["interviewerBackground"] = 30,
            // JD Analysis
            ["keySkillsDetected"] = 30,
            ["keyProjectsDetected"] = 40,
            ["jdFocusAreasDetected"] = 40,
            ["myThroughLine"] = 25,
            // Shared
            ["myDifferentiation"] = 40,
        };

    private static bool IsConciseStep(PreInterviewIngestionStep step) =>
        step is PreInterviewIngestionStep.CompanyResearch or PreInterviewIngestionStep.JdAnalysis;

    private static string GetConciseOverride(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.CompanyResearch => """
            ═══════════════════════════════════════
            INGESTION RUN — CONCISE CORE ONLY (ANTI-DRIFT)
            ═══════════════════════════════════════
            This run fills a small form, not a report. Do the research and analysis, but do NOT write the
            full report, tables, action plan, keyword lists, or any other section of the template above.
            Reply with ONLY the JSON block described below.
            Every value must be the shortest accurate core answer:
            • Facts only — nothing you cannot support from your research, the JD, or my resume.
              If something is unknown or unverifiable, write "Research manually". Never guess, pad, or hedge.
            • No filler, no marketing language, no restating the question, no markdown or bullet symbols inside values.
            • Stay strictly on THIS company and THIS role — no general industry commentary.
            Hard caps are listed per field in the JSON block. Values over the cap will be trimmed automatically.
            """,
        PreInterviewIngestionStep.JdAnalysis => """
            ═══════════════════════════════════════
            INGESTION RUN — CONCISE CORE ONLY (ANTI-DRIFT)
            ═══════════════════════════════════════
            This run fills a small form, not a report. Do the analysis, but do NOT write the full report,
            scorecards, gap tables, blind-spot sections, or any other section of the template above.
            Reply with ONLY the JSON block described below.
            Every value must be the shortest accurate core answer:
            • Base everything on the resume and JD I provided — never invent skills, projects, or requirements.
            • No filler, no hedging, no restating the question, no markdown or bullet symbols inside values
              (numbering like "1. … 2. … 3. …" in a single line is allowed where the field asks for it).
            • Pick only the highest-signal items; drop everything else.
            Hard caps are listed per field in the JSON block. Values over the cap will be trimmed automatically.
            """,
        _ => ""
    };

    /// <summary>Trim over-long values at a sentence boundary (or word cap). Returns trimmed keys.</summary>
    private static List<string> ClampConciseFields(
        PreInterviewIngestionStep step,
        Dictionary<string, string> fields)
    {
        var trimmed = new List<string>();
        if (!IsConciseStep(step))
            return trimmed;

        foreach (string key in fields.Keys.ToList())
        {
            if (!ConciseWordLimits.TryGetValue(key, out int limit))
                continue;

            int hardCap = limit + limit / 2;
            string clamped = ClampWords(fields[key], hardCap);
            if (clamped != fields[key])
            {
                fields[key] = clamped;
                trimmed.Add(key);
            }
        }

        return trimmed;
    }

    private static string ClampWords(string text, int maxWords)
    {
        MatchCollection words = Regex.Matches(text, @"\S+");
        if (words.Count <= maxWords)
            return text;

        string cut = text[..(words[maxWords - 1].Index + words[maxWords - 1].Length)];

        // Prefer ending on a sentence boundary if one falls in the back half of the cut.
        int lastStop = Math.Max(cut.LastIndexOf(". ", StringComparison.Ordinal),
                       Math.Max(cut.LastIndexOf(".\n", StringComparison.Ordinal),
                                cut.LastIndexOf("; ", StringComparison.Ordinal)));
        if (lastStop > cut.Length / 2)
            return cut[..(lastStop + 1)].TrimEnd();

        return cut.TrimEnd(',', ';', ':', ' ') + "…";
    }

    /// <summary>What the user must fill on the Pre-interview tab before each step.</summary>
    public static string DescribeRequiredInputs(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.CompanyResearch =>
            "Step 1 — Your inputs: Company (required). JD, Seniority, Domain recommended. Interviewer Background optional.",
        PreInterviewIngestionStep.JdAnalysis =>
            "Step 2 — Your inputs: Resume Summary + Job Description (required). Company / Seniority / Domain optional context.",
        PreInterviewIngestionStep.ProjectGeneration =>
            "Step 3 — Your inputs: Resume Summary + Job Description (required). Writes ONE project story; running again replaces it.",
        PreInterviewIngestionStep.SystemDesignGeneration =>
            "Step 4 — Your inputs: Resume Summary + Job Description (required). Writes ONE scenario; running again replaces it.",
        _ => ""
    };

    private static string GetSingleStoryOverride(PreInterviewIngestionStep step) => step switch
    {
        PreInterviewIngestionStep.ProjectGeneration => """
            ═══════════════════════════════════════
            INGESTION RUN — ONE STORY ONLY
            ═══════════════════════════════════════
            For this run, choose the single best project from my resume for this JD.
            Write ONLY that one story at full depth (same structure as one entry in the template).
            Do NOT write 2 or 3 stories. Do NOT include a story map for multiple stories.
            """,
        PreInterviewIngestionStep.SystemDesignGeneration => """
            ═══════════════════════════════════════
            INGESTION RUN — ONE SCENARIO ONLY
            ═══════════════════════════════════════
            For this run, choose the single best system from my resume for this JD.
            Write ONLY that one scenario at full depth (same structure as one entry in the template).
            Do NOT write 2 or 3 scenarios. Do NOT include a multi-scenario map.
            """,
        _ => ""
    };

    public static PreInterviewIngestionResult ParseResponse(
        PreInterviewIngestionStep step,
        string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            throw new InvalidOperationException("GPT returned an empty response.");

        string json = ExtractJsonObject(responseText);
        using JsonDocument doc = ParseJsonLenient(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("GPT JSON must be a single object.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            fields[prop.Name] = JsonElementToString(prop.Value);

        NormalizeLegacyStoryKeys(step, fields);
        List<string> trimmedKeys = ClampConciseFields(step, fields);
        var missing = GetMissingRequiredKeys(step, fields);

        return new PreInterviewIngestionResult
        {
            Step = step,
            Fields = fields,
            RawResponse = responseText,
            MissingRequiredKeys = missing,
            TrimmedKeys = trimmedKeys
        };
    }

    public static string FormatVerificationPreview(PreInterviewIngestionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{GetStepDisplayName(result.Step)} — review these values:");
        sb.AppendLine();

        foreach (var (key, value) in result.Fields.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string preview = value.Length > 220 ? value[..220] + "…" : value;
            sb.AppendLine($"• {key}:");
            sb.AppendLine($"  {preview}");
            sb.AppendLine();
        }

        if (result.TrimmedKeys.Count > 0)
        {
            sb.AppendLine("✂ Trimmed to stay concise: " + string.Join(", ", result.TrimmedKeys));
            sb.AppendLine();
        }

        if (result.MissingRequiredKeys.Count > 0)
        {
            sb.AppendLine("⚠ Missing keys (re-run ingest or edit manually after apply):");
            sb.AppendLine("  " + string.Join(", ", result.MissingRequiredKeys));
            sb.AppendLine();
        }

        sb.AppendLine("Apply these values to the Pre-interview form?");
        return sb.ToString();
    }

    private static string FillPlaceholders(
        string template,
        PreInterviewFormContext ctx,
        PreInterviewIngestionStep step)
    {
        string jobTitle = $"{ctx.Seniority} {ctx.Domain}".Trim();
        if (string.IsNullOrWhiteSpace(jobTitle))
            jobTitle = "Staff AI/ML Engineer";

        string resume = string.IsNullOrWhiteSpace(ctx.ResumeSummary)
            ? "[Resume not provided — fill RESUME SUMMARY first.]"
            : ctx.ResumeSummary.Trim();

        string jd = string.IsNullOrWhiteSpace(ctx.JobDescription)
            ? "[Job description not provided — fill JOB DESCRIPTION first.]"
            : ctx.JobDescription.Trim();

        string text = template;
        text = text.Replace("[attached]", resume, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("[Paste JD here]", jd, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("[Paste the JD here]", jd, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("[paste]", ctx.Company.Trim(), StringComparison.OrdinalIgnoreCase);
        text = text.Replace("[paste exact title]", jobTitle, StringComparison.OrdinalIgnoreCase);

        if (step == PreInterviewIngestionStep.CompanyResearch)
        {
            text = Regex.Replace(
                text,
                @"Company Name:\s*\[paste\]\s*·\s*Job Title:\s*\[paste exact title\]\s*·\s*JD URL/Text:\s*\[paste\]",
                $"Company Name: {ctx.Company.Trim()} · Job Title: {jobTitle} · JD URL/Text: {jd}",
                RegexOptions.IgnoreCase);

            if (!string.IsNullOrWhiteSpace(ctx.InterviewerBackground))
            {
                text = text.Replace(
                    "Interviewer Names: [paste if known from the calendar invite]",
                    $"Interviewer Names: {ctx.InterviewerBackground.Trim()}",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    private static string GetJsonOutputInstruction(PreInterviewIngestionStep step)
    {
        string keys = step switch
        {
            PreInterviewIngestionStep.CompanyResearch => """
                "companyStage": string (≤ 6 words — one of: Startup / Growth / Enterprise / Big Tech / AI-first),
                "company": string (official name only),
                "product": string (ONE sentence, ≤ 25 words — what they build),
                "teamFocus": string (ONE sentence, ≤ 25 words — what this team works on),
                "knownTechStack": string (comma-separated tool names only, max 12 items, ≤ 30 words, no explanations),
                "companyValues": string (comma-separated, max 5 short phrases, ≤ 20 words),
                "recentNews": string (max 2 items, each ≤ 20 words and dated; ≤ 45 words total),
                "typicalCandidateProfile": string (≤ 2 sentences, ≤ 40 words),
                "myDifferentiation": string (≤ 2 sentences, ≤ 40 words — my edge vs that profile, from my resume only),
                "interviewerBackground": string (≤ 30 words, or "Research manually")
                """,
            PreInterviewIngestionStep.JdAnalysis => """
                "keySkillsDetected": string (required — exactly 5 from the resume, one line: "1. X 2. Y 3. Z 4. W 5. V", each ≤ 6 words, ≤ 30 words total),
                "keyProjectsDetected": string (required — max 3 resume projects, one line numbered, each ≤ 12 words),
                "jdFocusAreasDetected": string (required — exactly top 3 JD requirements, one line numbered, each ≤ 12 words),
                "myDifferentiation": string (required — ≤ 2 sentences, ≤ 40 words),
                "myThroughLine": string (required — exactly ONE sentence, ≤ 25 words)
                """,
            PreInterviewIngestionStep.ProjectGeneration => """
                "storyTitle": string (short project name),
                "projectStory": string (ONE full project story only — ready to paste into PROJECT STORIES)
                """,
            PreInterviewIngestionStep.SystemDesignGeneration => """
                "scenarioTitle": string (short system name),
                "systemDesignScenario": string (ONE full scenario only — ready to paste into SYSTEM DESIGN SCENARIOS)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };

        return """
            ═══════════════════════════════════════
            MACHINE OUTPUT (REQUIRED — FINAL MESSAGE ONLY)
            ═══════════════════════════════════════
            Your VERY LAST assistant message in this reply must be BLOCK STYLE for automated ingestion:
            • Nothing before or after the block — no intro, no summary, no "here is the JSON".
            • Use exactly one fenced code block labeled json (same pattern as assistant prompt paste blocks):

            ```json
            {
            """ + keys + """
            }
            ```

            Rules:
            • All keys must be present with camelCase names exactly as shown.
            • String values are plain text (use \n for line breaks inside strings).
            • Do not click Copy; the app reads this block from the page.
            """;
    }

    private static void NormalizeLegacyStoryKeys(
        PreInterviewIngestionStep step,
        Dictionary<string, string> fields)
    {
        if (step == PreInterviewIngestionStep.ProjectGeneration &&
            !fields.ContainsKey("projectStory") &&
            fields.TryGetValue("projectStories", out string? legacyProjects))
        {
            fields["projectStory"] = TakeFirstStoryBlock(legacyProjects);
        }

        if (step == PreInterviewIngestionStep.SystemDesignGeneration &&
            !fields.ContainsKey("systemDesignScenario") &&
            fields.TryGetValue("systemDesignScenarios", out string? legacyScenarios))
        {
            fields["systemDesignScenario"] = TakeFirstStoryBlock(legacyScenarios);
        }
    }

    /// <summary>If GPT returns multiple stories, keep only the first block.</summary>
    private static string TakeFirstStoryBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string[] parts = text.Split(
            ["\n---\n", "\n\n---\n\n", "\n\nStory 2", "\n\nScenario 2"],
            StringSplitOptions.None);

        return parts[0].Trim();
    }

    public static string FormatStoryForFormField(string? title, string body, string titleLabel = "Story")
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            return "";

        title = title?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title))
            return body;

        return $"{titleLabel}: {title}{Environment.NewLine}{Environment.NewLine}{body}";
    }

    // Blocks written by earlier versions of the app were appended as
    //   <story>\n\n---\n\n<next story, starting "Story:" or "Scenario:">
    private static readonly Regex StoryBlockSeparator = new(
        @"\r?\n[ \t]*\r?\n---[ \t]*\r?\n[ \t]*\r?\n(?=(?:Story|Scenario):)",
        RegexOptions.Compiled);

    /// <summary>
    /// The prompt carries exactly ONE project story and ONE system-design scenario (more makes
    /// the message too long for ChatGPT). If a form field still holds several app-appended
    /// stories from earlier runs, keep only the most recent one.
    /// </summary>
    /// <param name="found">How many app-written story blocks the field contained.</param>
    public static string KeepSingleStory(string? text, out int found)
    {
        found = 0;
        if (string.IsNullOrWhiteSpace(text)) return "";

        string[] blocks = StoryBlockSeparator.Split(text.Trim());
        found = blocks.Length;
        return blocks[^1].Trim();
    }

    private static IReadOnlyList<string> GetMissingRequiredKeys(
        PreInterviewIngestionStep step,
        Dictionary<string, string> fields)
    {
        string[] required = step switch
        {
            PreInterviewIngestionStep.CompanyResearch =>
            [
                "companyStage", "company", "product", "teamFocus", "knownTechStack",
                "companyValues", "recentNews", "typicalCandidateProfile"
            ],
            PreInterviewIngestionStep.JdAnalysis =>
            ["keySkillsDetected", "keyProjectsDetected", "jdFocusAreasDetected"],
            PreInterviewIngestionStep.ProjectGeneration => ["projectStory"],
            PreInterviewIngestionStep.SystemDesignGeneration => ["systemDesignScenario"],
            _ => []
        };

        var missing = new List<string>();
        foreach (string key in required)
        {
            if (!fields.TryGetValue(key, out string? val) || string.IsNullOrWhiteSpace(val))
                missing.Add(key);
        }

        return missing;
    }

    private static string ExtractJsonObject(string text)
    {
        text = text.Trim();

        MatchCollection fences = Regex.Matches(
            text,
            @"```(?:json)?\s*([\s\S]*?)```",
            RegexOptions.IgnoreCase);
        for (int i = fences.Count - 1; i >= 0; i--)
        {
            string candidate = fences[i].Groups[1].Value.Trim();
            if (candidate.Contains('{', StringComparison.Ordinal))
                return ExtractBalancedJsonObject(candidate);
        }

        if (text.Contains('{', StringComparison.Ordinal))
            return ExtractBalancedJsonObject(text);

        throw new InvalidOperationException(
            "Could not find a JSON object in the GPT response. " +
            "The final message should be only a ```json code block.");
    }

    /// <summary>
    /// Strict parse first; if that fails (LLMs often emit raw line breaks/tabs inside string
    /// values), escape control characters that sit inside strings and retry once.
    /// </summary>
    private static JsonDocument ParseJsonLenient(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            var sb = new StringBuilder(json.Length + 32);
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (inStr)
                {
                    if (esc) { esc = false; sb.Append(c); continue; }
                    if (c == '\\') { esc = true; sb.Append(c); continue; }
                    if (c == '"') { inStr = false; sb.Append(c); continue; }
                    if (c == '\n') { sb.Append("\\n"); continue; }
                    if (c == '\r') { continue; }
                    if (c == '\t') { sb.Append("\\t"); continue; }
                    sb.Append(c);
                }
                else
                {
                    if (c == '"') inStr = true;
                    sb.Append(c);
                }
            }

            try
            {
                return JsonDocument.Parse(sb.ToString());
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "GPT returned a JSON block that is not valid JSON: " + ex.Message, ex);
            }
        }
    }

    private static string ExtractBalancedJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException("Could not find a JSON object in the GPT response.");

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("GPT JSON object was not closed with a matching '}'.");
    }

    private static string JsonElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Array => string.Join(
            Environment.NewLine,
            el.EnumerateArray().Select(JsonElementToString)),
        JsonValueKind.Object => el.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => el.ToString()
    };
}
