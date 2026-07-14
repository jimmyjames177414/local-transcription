using System.ComponentModel;
using System.Windows.Media;
using LocalTranscriber.App.Services;
using LocalTranscriber.Shared;

namespace LocalTranscriber.App.ViewModels;

/// <summary>One transcript turn in the Meeting screen list. Speaker name and unknown status are
/// mutable so the UI updates live when the user names an unidentified session speaker.</summary>
public sealed class TranscriptRowViewModel : INotifyPropertyChanged
{
    private string _speakerName;
    private bool _isUnknownSpeaker;

    public TranscriptRowViewModel(TranscriptEvent e, double uncertainBelow)
    {
        Time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        IsMe = e.Source == AudioSourceType.Microphone;
        SpeakerId = e.Speaker.SpeakerId;
        _speakerName = e.Speaker.DisplayName;
        IsUncertain = e.Speaker.IsKnown && e.Speaker.Confidence is double c && c < uncertainBelow;
        ConfidenceText = IsUncertain && e.Speaker.Confidence is double conf ? conf.ToString("0.00") : "";
        _isUnknownSpeaker = !e.Speaker.IsKnown && !IsMe;
        Text = e.Text;
        SpeakerBrush = SpeakerPalette.GetBrush(e.Speaker.SpeakerId, IsMe);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Time { get; }
    public string SpeakerId { get; }
    public bool IsMe { get; }
    public bool IsUncertain { get; }
    public string UncertainMark => IsUncertain ? "?" : "";
    public string ConfidenceText { get; }
    public string Text { get; }
    private Brush _speakerBrush = null!;

    public Brush SpeakerBrush
    {
        get => _speakerBrush;
        set
        {
            if (ReferenceEquals(_speakerBrush, value)) return;
            _speakerBrush = value;
            Notify(nameof(SpeakerBrush));
        }
    }

    public string MicMark => IsMe ? "·🎙" : "";

    public string SpeakerName
    {
        get => _speakerName;
        set
        {
            if (_speakerName == value) return;
            _speakerName = value;
            Notify(nameof(SpeakerName));
        }
    }

    public bool IsUnknownSpeaker
    {
        get => _isUnknownSpeaker;
        set
        {
            if (_isUnknownSpeaker == value) return;
            _isUnknownSpeaker = value;
            Notify(nameof(IsUnknownSpeaker));
        }
    }

    /// <summary>True for any non-mic speaker — clicking their name opens the rename dialog.</summary>
    public bool CanRename => !IsMe;
}
