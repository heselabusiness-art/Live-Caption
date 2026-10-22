using System.IO;
using System.Text.Json;

namespace LiveCaptionViewer.Services;

/// <summary>
/// ChatGPT and Claude change their web UI often, which silently breaks the built-in CSS selectors.
/// This lets the user repair a selector without rebuilding the app: drop a
/// <c>selectors.json</c> next to the other settings
/// (<c>%LocalAppData%\LiveCaptionViewer\selectors.json</c>) and the values replace the built-in
/// JavaScript expressions. The file is re-read whenever it changes, no restart needed.
/// </summary>
/// <example>
/// <code>
/// {
///   "ChatGPT": { "input": "document.querySelector('#prompt-textarea')" },
///   "Claude":  { "sendButton": "document.querySelector('button[aria-label=\"Send message\"]')" }
/// }
/// </code>
/// Keys: <c>input</c>, <c>sendButton</c>, <c>isGenerating</c>, <c>stopButton</c>. Each value is a
/// JavaScript expression that evaluates to the element (or, for isGenerating, a truthy value).
/// </example>
public sealed class SelectorOverrides
{
    /// <summary>Overrides for one site; a null member means "use the built-in selector".</summary>
    public sealed record SelectorSet(string? Input, string? SendButton, string? IsGenerating, string? StopButton)
    {
        public static readonly SelectorSet None = new(null, null, null, null);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveCaptionViewer",
        "selectors.json");

    private readonly string _path;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private Dictionary<string, SelectorSet> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SelectorOverrides(string? path = null) => _path = path ?? DefaultPath;

    public static SelectorOverrides Shared { get; } = new();

    /// <summary>Returns the overrides for <paramref name="service"/> ("ChatGPT" / "Claude").</summary>
    public SelectorSet For(string service)
    {
        Refresh();
        return _cache.TryGetValue(service, out SelectorSet? set) ? set : SelectorSet.None;
    }

    private void Refresh()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _cache = new(StringComparer.OrdinalIgnoreCase);
                _loadedWriteTimeUtc = DateTime.MinValue;
                return;
            }

            DateTime stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loadedWriteTimeUtc) return;

            _cache = Parse(File.ReadAllText(_path));
            _loadedWriteTimeUtc = stamp;
        }
        catch
        {
            // A half-saved or invalid file must never break sending: keep the last good state.
        }
    }

    /// <summary>Parses the JSON document. Unknown sites/keys and non-string values are ignored.</summary>
    public static Dictionary<string, SelectorSet> Parse(string json)
    {
        var result = new Dictionary<string, SelectorSet>(StringComparer.OrdinalIgnoreCase);

        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

        foreach (JsonProperty site in doc.RootElement.EnumerateObject())
        {
            if (site.Value.ValueKind != JsonValueKind.Object) continue;

            result[site.Name] = new SelectorSet(
                Read(site.Value, "input"),
                Read(site.Value, "sendButton"),
                Read(site.Value, "isGenerating"),
                Read(site.Value, "stopButton"));
        }

        return result;
    }

    private static string? Read(JsonElement obj, string key)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.String)
            {
                string? v = p.Value.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
        }
        return null;
    }
}
