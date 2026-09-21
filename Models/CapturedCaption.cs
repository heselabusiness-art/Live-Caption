using System;

namespace LiveCaptionViewer.Models;

/// <summary>
/// A single snapshot of caption text observed from the Live Captions window.
/// </summary>
public sealed class CapturedCaption
{
    public string Text { get; }
    public DateTime CapturedAt { get; }

    public CapturedCaption(string text)
    {
        Text = text;
        CapturedAt = DateTime.Now;
    }

    public override string ToString() => Text;
}
