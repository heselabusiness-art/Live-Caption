using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiveCaptionViewer.Services;

/// <summary>
/// Backend "finish flag" for web-chat replies.
/// Uses CDP <c>Network.*</c> events to track the streaming POST request that carries the
/// model's answer. When that request completes (<c>Network.loadingFinished</c>), the whole
/// reply has been delivered — no DOM guessing required.
/// Must be created and disposed on the UI thread (WebView2 requirement).
/// </summary>
internal sealed class GenerationFinishWatcher : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly string[] _include;
    private readonly string[] _exclude;
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    private CoreWebView2DevToolsProtocolEventReceiver? _onSent;
    private CoreWebView2DevToolsProtocolEventReceiver? _onFinished;
    private CoreWebView2DevToolsProtocolEventReceiver? _onFailed;

    private bool _everStarted;
    private DateTime _lastEndUtc = DateTime.MinValue;
    private bool _disposed;

    private GenerationFinishWatcher(CoreWebView2 core, string[] include, string[] exclude)
    {
        _core = core;
        _include = include;
        _exclude = exclude;
    }

    /// <summary>True once a matching streaming request has been seen.</summary>
    public bool StreamStarted
    {
        get { lock (_gate) return _everStarted; }
    }

    /// <summary>Number of matching streaming requests still in flight.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    /// <summary>URL of the most recent streaming request (diagnostics).</summary>
    public string LastUrl { get; private set; } = "";

    /// <summary>
    /// True when a stream was seen, none are in flight, and the last one ended at least
    /// <paramref name="debounce"/> ago (guards against chained requests).
    /// </summary>
    public bool IsFinished(TimeSpan debounce)
    {
        lock (_gate)
        {
            return _everStarted
                && _active.Count == 0
                && DateTime.UtcNow - _lastEndUtc >= debounce;
        }
    }

    public static async Task<GenerationFinishWatcher?> StartAsync(
        CoreWebView2 core,
        string[] include,
        string[] exclude)
    {
        if (include.Length == 0)
            return null;

        var watcher = new GenerationFinishWatcher(core, include, exclude);
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

            watcher._onSent = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            watcher._onFinished = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            watcher._onFailed = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");

            watcher._onSent.DevToolsProtocolEventReceived += watcher.OnRequestWillBeSent;
            watcher._onFinished.DevToolsProtocolEventReceived += watcher.OnLoadingEnded;
            watcher._onFailed.DevToolsProtocolEventReceived += watcher.OnLoadingEnded;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            return null;
        }
    }

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("requestId", out JsonElement idEl) ||
                !root.TryGetProperty("request", out JsonElement req))
                return;

            string method = req.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
            string url = req.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
            string? id = idEl.GetString();

            if (id is null || !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return;
            if (!Matches(url))
                return;

            lock (_gate)
            {
                _active.Add(id);
                _everStarted = true;
                LastUrl = url;
            }
        }
        catch
        {
            // Malformed event — ignore.
        }
    }

    private void OnLoadingEnded(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            string? id = doc.RootElement.TryGetProperty("requestId", out JsonElement idEl)
                ? idEl.GetString()
                : null;
            if (id is null) return;

            lock (_gate)
            {
                if (_active.Remove(id))
                    _lastEndUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // Malformed event — ignore.
        }
    }

    private bool Matches(string url)
    {
        bool included = false;
        foreach (string marker in _include)
        {
            if (url.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                included = true;
                break;
            }
        }

        if (!included) return false;

        foreach (string marker in _exclude)
        {
            if (url.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_onSent is not null) _onSent.DevToolsProtocolEventReceived -= OnRequestWillBeSent;
        if (_onFinished is not null) _onFinished.DevToolsProtocolEventReceived -= OnLoadingEnded;
        if (_onFailed is not null) _onFailed.DevToolsProtocolEventReceived -= OnLoadingEnded;

        try { _ = _core.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); }
        catch { /* browser may be closing */ }
    }
}
