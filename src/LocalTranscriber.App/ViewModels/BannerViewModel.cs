using LocalTranscriber.App.Mvvm;

namespace LocalTranscriber.App.ViewModels;

public enum BannerSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>An inline banner under a panel header (design 4d): message + optional action + dismiss.</summary>
public sealed class BannerViewModel : ObservableObject
{
    public BannerViewModel(BannerSeverity severity, string text, string? actionLabel = null,
        Action? action = null, Action<BannerViewModel>? onDismiss = null)
    {
        Severity = severity;
        Text = text;
        ActionLabel = actionLabel ?? "";
        ActionCommand = action is null ? null : new RelayCommand(action);
        DismissCommand = new RelayCommand(() => onDismiss?.Invoke(this));
    }

    public BannerSeverity Severity { get; }
    public string Text { get; }
    public string ActionLabel { get; }
    public RelayCommand? ActionCommand { get; }
    public RelayCommand DismissCommand { get; }

    /// <summary>Stable key so the same condition doesn't stack duplicate banners.</summary>
    public string? Key { get; init; }
}
