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
/// Polls the Windows 11 Live Captions window via UI Automation and emits
/// caption updates as text changes. Designed to keep CPU low: cheap polling
/// (a few hundred ms) and string-level deduplication after whitespace
/// normalization.
/// </summary>
public sealed class LiveCaptionsCaptureService : IDisposable
{
    // The Live Captions feature ships as LiveCaptions.exe with a window class
    // name that starts with "LiveCaptionsDesktopWindow" on current Win11 builds.
    private const string LiveCaptionsProcessName = "LiveCaptions";
    private const string LiveCaptionsClassName = "LiveCaptionsDesktopWindow";
    private const string LiveCaptionsWindowName = "Live Captions";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private string _lastSnapshotText = string.Empty;
    private bool _disposed;

    public event EventHandler<CapturedCaption>? CaptionUpdated;
    public event EventHandler<CaptureStatus>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;

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

                // Inner loop: poll the same window until it disappears or we cancel.
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

                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
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
        string? pythonStyle = TryGetPrimaryTextControlName(root);
        if (!string.IsNullOrWhiteSpace(pythonStyle))
            return pythonStyle;

        return ExtractCaptionTextFallback(root);
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
