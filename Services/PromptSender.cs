namespace LiveCaptionViewer.Services;

public readonly record struct SendResult(bool Ok, string Message);

/// <summary>
/// Serialises everything that types into the chat page. A hotkey pressed while the previous
/// prompt is still going out used to be dropped (after the caption segment had already been
/// consumed, so the question was lost). Now the second press waits for its turn instead.
/// </summary>
public sealed class PromptSender
{
    /// <summary>More presses than this waiting behind the current send are refused.</summary>
    public const int MaxQueued = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _queued;

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Number of sends waiting behind the one in progress.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>
    /// Sends <paramref name="prompt"/> through <paramref name="service"/> once every earlier send
    /// has finished. <paramref name="onStatus"/> receives a message when the call has to queue.
    /// </summary>
    public async Task<SendResult> SendAsync(
        WebChatAutomationBase service,
        string prompt,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        using IDisposable? lease = await AcquireAsync(onStatus, ct);
        if (lease is null)
            return new SendResult(false, $"Too many prompts waiting ({MaxQueued}). Wait for the current one to finish.");

        onStatus?.Invoke($"Sending to {service.ServiceName}…");
        string message = await service.SendPromptAsync(prompt);
        return new SendResult(message.EndsWith("Sent.", StringComparison.Ordinal), message);
    }

    /// <summary>
    /// Takes exclusive use of the chat page (also used by the multi-step ingestion run).
    /// Returns null when too many callers are already queued.
    /// </summary>
    public async Task<IDisposable?> AcquireAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (!_gate.Wait(0))
        {
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                return null;
            }

            onStatus?.Invoke($"Queued — {Queued} waiting behind the prompt being sent…");
            try { await _gate.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _queued); }
        }

        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
