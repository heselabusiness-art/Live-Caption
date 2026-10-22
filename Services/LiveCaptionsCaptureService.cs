using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using LiveCaptionViewer.Models;

namespace LiveCaptionViewer.Services;

public enum CaptureStatus
{
    Idle,
    SearchingForLiveCaptions,
    LiveCaptionsNotFound,
    Monitoring,
    Stopped,
    Error
}

/// <summary>
/// Reads the Windows 11 Live Captions window via UI Automation and emits caption updates as the
/// text changes. Updates are event-driven: UI Automation property/structure change events wake
/// the reader immediately, so captions appear as soon as Windows writes them. A slow poll stays
/// on as a safety net (and as the only mechanism if event registration is refused), and
/// string-level deduplication keeps repeated reads from raising duplicate updates.
/// </summary>
public sealed class LiveCaptionsCaptureService : IDisposable
{
    // The Live Captions feature ships as LiveCaptions.exe with a window class
    // name that starts with "LiveCaptionsDesktopWindow" on current Win11 builds.
    private const string LiveCaptionsProcessName = "LiveCaptions";
    private const string LiveCaptionsClassName = "LiveCaptionsDesktopWindow";
    private const string LiveCaptionsWindowName = "Live Captions";

    /// <summary>Poll rate when no UI Automation events are available.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Safety-net poll while events are active (in case an event is ever missed).</summary>
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>Events arrive in bursts (one per word); wait briefly and read once.</summary>
    private static readonly TimeSpan EventDebounce = TimeSpan.FromMilliseconds(60);

    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _changed = new(0, 1);

    /// <summary>The Live Captions window currently being monitored (also used by <see cref="ReadNow"/>).</summary>
    private volatile AutomationElement? _window;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private string _lastSnapshotText = string.Empty;
    private bool _disposed;

    public event EventHandler<CapturedCaption>? CaptionUpdated;
    public event EventHandler<CaptureStatus>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>Informational messages for the host application's log (kept dependency-free: the native host shares this file).</summary>
    public event Action<string>? Diagnostic;

    public CaptureStatus Status { get; private set; } = CaptureStatus.Idle;
    public bool IsRunning => _workerTask is { IsCompleted: false };

    public void Start()
    {
        // Allow restart after a faulted or completed worker.
        if (IsRunning) return;
        if (_workerTask is { IsFaulted: true } or { IsCanceled: true })
        {
            _cts?.Dispose();
            _cts = null;
            _workerTask = null;
        }

        _cts = new CancellationTokenSource();
        _lastSnapshotText = string.Empty;
        var token = _cts.Token;

        // Run the polling loop off the UI thread.
        _workerTask = Task.Run(() => RunAsync(token), token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        try
        {
            _cts.Cancel();
            if (_workerTask is not null)
            {
                try { await _workerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _workerTask = null;
            UpdateStatus(CaptureStatus.Stopped);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UpdateStatus(CaptureStatus.SearchingForLiveCaptions);
                AutomationElement? window = TryFindLiveCaptionsWindow();

                if (window is null)
                {
                    UpdateStatus(CaptureStatus.LiveCaptionsNotFound);
                    await Task.Delay(SearchInterval, token).ConfigureAwait(false);
                    continue;
                }

                UpdateStatus(CaptureStatus.Monitoring);
                _window = window;

                bool eventsActive = TrySubscribe(window, out Action unsubscribe);

                // Inner loop: read the same window until it disappears or we cancel.
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string text;
                        try
                        {
                            text = ExtractCaptionText(window);
                        }
                        catch (ElementNotAvailableException)
                        {
                            // Live Captions closed or restarted; drop the reference
                            // and let the outer loop search for it again.
                            break;
                        }
                        catch (Exception ex)
                        {
                            OnError(ex);
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            string snapshot = text.TrimEnd();
                            if (!string.Equals(snapshot, _lastSnapshotText, StringComparison.Ordinal))
                            {
                                _lastSnapshotText = snapshot;

                                try
                                {
                                    CaptionUpdated?.Invoke(this, new CapturedCaption(snapshot));
                                }
                                catch (Exception ex)
                                {
                                    OnError(ex);
                                }
                            }
                        }

                        await WaitForChangeAsync(eventsActive, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _window = null;
                    unsubscribe();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            UpdateStatus(CaptureStatus.Error);
            OnError(ex);
        }
    }

    /// <summary>
    /// Reads the caption text RIGHT NOW, independent of the monitoring loop. A hotkey press uses this so it captures what
    /// Live Captions shows at that instant instead of whatever the last event / poll happened to deliver (events are debounced,
    /// and the loop may not be running at all). Returns null when the Live Captions window cannot be found or read.
    /// Safe to call from any thread; do not call it from the UI thread (UI Automation calls cross processes).
    /// </summary>
    public string? ReadNow()
    {
        try
        {
            AutomationElement? window = _window ?? TryFindLiveCaptionsWindow();
            if (window is null) return null;

            string text = ExtractCaptionText(window);
            return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"ReadNow failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Subscribes to UI Automation change events on the caption window. Returns false (and a
    /// no-op unsubscribe) when the subscription is refused, in which case polling carries on alone.
    /// </summary>
    private bool TrySubscribe(AutomationElement window, out Action unsubscribe)
    {
        var propertyHandler = new AutomationPropertyChangedEventHandler((_, _) => SignalChanged());
        var structureHandler = new StructureChangedEventHandler((_, _) => SignalChanged());

        try
        {
            Automation.AddAutomationPropertyChangedEventHandler(
                window, TreeScope.Subtree, propertyHandler,
                AutomationElement.NameProperty);
            Automation.AddStructureChangedEventHandler(window, TreeScope.Subtree, structureHandler);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"UI Automation events unavailable, falling back to polling: {ex.Message}");
            unsubscribe = static () => { };
            return false;
        }

        unsubscribe = () =>
        {
            try
            {
                Automation.RemoveAutomationPropertyChangedEventHandler(window, propertyHandler);
                Automation.RemoveStructureChangedEventHandler(window, structureHandler);
            }
            catch
            {
                // The window is already gone; nothing left to unsubscribe from.
            }
        };

        Diagnostic?.Invoke("Subscribed to Live Captions UI Automation events");
        return true;
    }

    private void SignalChanged()
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { /* a wake-up is already pending */ }
        catch (ObjectDisposedException) { /* stopped */ }
    }

    /// <summary>Sleeps until the next read is due: an event, or the poll interval.</summary>
    private async Task WaitForChangeAsync(bool eventsActive, CancellationToken token)
    {
        if (!eventsActive)
        {
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
            return;
        }

        if (await _changed.WaitAsync(SafetyPollInterval, token).ConfigureAwait(false))
            await Task.Delay(EventDebounce, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Find the Live Captions window. Prefers locating it via the process and
    /// its main window handle (fast, no full desktop tree walk), and falls back
    /// to scanning top-level windows by class / name.
    /// </summary>
    private static AutomationElement? TryFindLiveCaptionsWindow()
    {
        foreach (var proc in Process.GetProcessesByName(LiveCaptionsProcessName))
        {
            try
            {
                IntPtr handle = proc.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;

                AutomationElement? element = AutomationElement.FromHandle(handle);
                if (element is not null) return element;
            }
            catch
            {
                // try next process
            }
            finally
            {
                proc.Dispose();
            }
        }

        try
        {
            var rootCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, LiveCaptionsClassName),
                new PropertyCondition(AutomationElement.NameProperty, LiveCaptionsWindowName));

            return AutomationElement.RootElement.FindFirst(TreeScope.Children, rootCondition);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads caption text the same way as live.py <c>get_all_text_controls</c>:
    /// first <c>TextControl</c> whose <c>Name</c> length is greater than 10.
    /// Falls back to Document/TextPattern aggregation if that fails.
    /// </summary>
    private static string ExtractCaptionText(AutomationElement root)
    {
        // The caption text block has a stable AutomationId; other text on the window (headers, hints such as
        // "Ready to caption ...") must never be mistaken for speech.
        string? byId = TryGetCaptionsTextBlock(root);
        if (!string.IsNullOrWhiteSpace(byId))
            return byId;

        string? pythonStyle = TryGetPrimaryTextControlName(root);
        if (!string.IsNullOrWhiteSpace(pythonStyle))
            return pythonStyle;

        return ExtractCaptionTextFallback(root);
    }

    private const string CaptionsTextBlockId = "CaptionsTextBlock";

    private static string? TryGetCaptionsTextBlock(AutomationElement root)
    {
        try
        {
            AutomationElement? block = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, CaptionsTextBlockId));
            return block?.Current.Name;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPrimaryTextControlName(AutomationElement control)
    {
        AutomationElementCollection children;
        try
        {
            children = control.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch
        {
            return null;
        }

        foreach (AutomationElement child in children)
        {
            try
            {
                if (child.Current.ControlType == ControlType.Text)
                {
                    string? name = child.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.Length > 10)
                        return name;
                }

                string? nested = TryGetPrimaryTextControlName(child);
                if (!string.IsNullOrEmpty(nested))
                    return nested;
            }
            catch (ElementNotAvailableException)
            {
                // element went away — try siblings
            }
        }

        return null;
    }

    private static string ExtractCaptionTextFallback(AutomationElement root)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        AutomationElementCollection elements =
            root.FindAll(TreeScope.Descendants, condition);

        var sb = new StringBuilder();
        foreach (AutomationElement element in elements)
        {
            string? chunk = TryGetElementText(element);
            if (string.IsNullOrWhiteSpace(chunk)) continue;

            if (sb.Length > 0) sb.AppendLine();
            sb.Append(chunk.TrimEnd());
        }

        return sb.ToString();
    }

    private static string? TryGetElementText(AutomationElement element)
    {
        // Best: TextPattern, which exposes the document's flowing text.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textPatternObj)
            && textPatternObj is TextPattern textPattern)
        {
            try
            {
                string text = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { /* fall through */ }
        }

        // Next: ValuePattern (common on Edit controls).
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePatternObj)
            && valuePatternObj is ValuePattern valuePattern)
        {
            try
            {
                string value = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch { /* fall through */ }
        }

        // Last: the element's accessible name.
        try
        {
            return element.Current.Name;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStatus(CaptureStatus newStatus)
    {
        if (Status == newStatus) return;
        Status = newStatus;
        try
        {
            StatusChanged?.Invoke(this, newStatus);
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    private void OnError(Exception ex) => ErrorOccurred?.Invoke(this, ex);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // ignored on shutdown
        }
    }
}
