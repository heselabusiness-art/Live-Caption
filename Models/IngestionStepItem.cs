using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LiveCaptionViewer.Services;

namespace LiveCaptionViewer.Models;

public enum IngestionStepState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>One row of the Pre-interview ingestion progress stepper.</summary>
public sealed class IngestionStepItem : INotifyPropertyChanged
{
    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly Brush Gray = Frozen("#7A7A7A");
    private static readonly Brush Gold = Frozen("#DCDCAA");
    private static readonly Brush Green = Frozen("#4EC9B0");
    private static readonly Brush Red = Frozen("#F44747");
    private static readonly Brush Text = Frozen("#9A9A9A");

    private bool _isSelected = true;
    private bool _canEdit = true;
    private IngestionStepState _state = IngestionStepState.Pending;
    private string _detail = "";

    public IngestionStepItem(int number, PreInterviewIngestionStep step)
    {
        Number = number;
        Step = step;
        Title = $"{number} · {PreInterviewIngestionPipeline.GetStepDisplayName(step)}";
        _detail = PreInterviewIngestionPipeline.GetStepHint(step);
    }

    public int Number { get; }
    public PreInterviewIngestionStep Step { get; }
    public string Title { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>False while a run is in progress (locks the checkboxes).</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set => Set(ref _canEdit, value);
    }

    public IngestionStepState State => _state;

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public string Glyph => _state switch
    {
        IngestionStepState.Running => "▶",
        IngestionStepState.Done => "✔",
        IngestionStepState.Failed => "✖",
        IngestionStepState.Skipped => "–",
        IngestionStepState.Cancelled => "■",
        _ => "○"
    };

    public Brush GlyphBrush => _state switch
    {
        IngestionStepState.Running => Gold,
        IngestionStepState.Done => Green,
        IngestionStepState.Failed => Red,
        _ => Gray
    };

    public Brush DetailBrush => _state switch
    {
        IngestionStepState.Running => Gold,
        IngestionStepState.Done => Green,
        IngestionStepState.Failed => Red,
        _ => Text
    };

    public Visibility RunningVisibility =>
        _state == IngestionStepState.Running ? Visibility.Visible : Visibility.Collapsed;

    public void SetState(IngestionStepState state, string detail)
    {
        _state = state;
        Detail = detail;
        OnChanged(nameof(State));
        OnChanged(nameof(Glyph));
        OnChanged(nameof(GlyphBrush));
        OnChanged(nameof(DetailBrush));
        OnChanged(nameof(RunningVisibility));
    }

    /// <summary>Back to Pending with the requirement hint.</summary>
    public void Reset() =>
        SetState(IngestionStepState.Pending, PreInterviewIngestionPipeline.GetStepHint(Step));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
