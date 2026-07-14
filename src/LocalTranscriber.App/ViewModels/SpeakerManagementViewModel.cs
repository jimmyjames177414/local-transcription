using System.Collections.ObjectModel;
using System.Windows.Media;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.App.Services;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public enum SpeakerCardState
{
    Normal,
    Renaming,
    ConfirmForget
}

public enum SpeakerCardKind
{
    Speaker,
    Me,
    Enroll
}

/// <summary>One roster card on the Speakers screen (design 4e).</summary>
public sealed class SpeakerCardViewModel : ObservableObject
{
    private readonly SpeakerManagementViewModel _owner;
    private SpeakerCardState _state = SpeakerCardState.Normal;
    private string _renameText = "";

    public SpeakerCardViewModel(SpeakerManagementViewModel owner, SpeakerCardKind kind, string name, string subText, string speakerId = "")
    {
        _owner = owner;
        Kind = kind;
        Name = name;
        SubText = subText;
        Initials = kind == SpeakerCardKind.Me
            ? "Me"
            : new string(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])).ToArray());
        Badge = kind == SpeakerCardKind.Speaker ? SpeakerPalette.GetBrush(speakerId.Length > 0 ? speakerId : name, isMe: false)
            : kind == SpeakerCardKind.Me ? SpeakerPalette.GetBrush("", isMe: true)
            : Brushes.Transparent;

        BeginRenameCommand = new RelayCommand(() => { RenameText = Name; State = SpeakerCardState.Renaming; });
        CancelCommand = new RelayCommand(() => State = SpeakerCardState.Normal);
        ConfirmRenameCommand = new AsyncRelayCommand(
            () => _owner.RenameAsync(this),
            () => !string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != Name);
        BeginForgetCommand = new RelayCommand(() => State = SpeakerCardState.ConfirmForget);
        ConfirmForgetCommand = new AsyncRelayCommand(() => _owner.ForgetAsync(this));
    }

    public SpeakerCardKind Kind { get; }
    public string Name { get; }
    public string SubText { get; }
    public string Initials { get; }
    public Brush Badge { get; }

    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ConfirmRenameCommand { get; }
    public RelayCommand BeginForgetCommand { get; }
    public AsyncRelayCommand ConfirmForgetCommand { get; }

    public SpeakerCardState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string RenameText
    {
        get => _renameText;
        set
        {
            SetProperty(ref _renameText, value);
            ConfirmRenameCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>Speakers screen: locally-stored voice memory as roster cards.</summary>
public sealed class SpeakerManagementViewModel : ObservableObject
{
    private readonly IKnownSpeakerStore _store;
    private readonly ConfigService _configService;
    private string _statusText = "";
    private string _countText = "";

    public SpeakerManagementViewModel(IKnownSpeakerStore? store = null, ConfigService? configService = null)
    {
        _configService = configService ?? new ConfigService();
        if (store is null)
        {
            var config = _configService.Load();
            store = new SqliteKnownSpeakerStore(new SqliteDatabase(config.DatabasePath));
        }

        _store = store;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }

    public ObservableCollection<SpeakerCardViewModel> Cards { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var speakers = await _store.ListAsync();
            var config = _configService.Load();

            Cards.Clear();
            Cards.Add(new SpeakerCardViewModel(this, SpeakerCardKind.Me,
                config.DefaultMicSpeakerName, "microphone track · always you"));
            foreach (var s in speakers)
            {
                Cards.Add(new SpeakerCardViewModel(this, SpeakerCardKind.Speaker, s.DisplayName, DescribeSpeaker(s), s.Id));
            }

            Cards.Add(new SpeakerCardViewModel(this, SpeakerCardKind.Enroll, "", ""));
            CountText = $"{speakers.Count} remembered voice{(speakers.Count == 1 ? "" : "s")} · stored locally in SQLite, never uploaded";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load speakers: {ex.Message}";
        }
    }

    private static string DescribeSpeaker(KnownSpeaker s)
    {
        string seen = s.LastSeenAt is { } t
            ? t.ToLocalTime().Date == DateTime.Today ? $"seen today {t.ToLocalTime():HH:mm}" : $"seen {t.ToLocalTime():yyyy-MM-dd}"
            : "not seen yet";
        return $"{s.SampleCount} sample{(s.SampleCount == 1 ? "" : "s")} · {seen}";
    }

    internal async Task RenameAsync(SpeakerCardViewModel card)
    {
        try
        {
            string to = card.RenameText.Trim();
            await _store.RenameAsync(card.Name, to);
            StatusText = $"Renamed '{card.Name}' to '{to}' — voice sample kept, so {to} is recognized automatically next time.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
        }
    }

    internal async Task ForgetAsync(SpeakerCardViewModel card)
    {
        try
        {
            await _store.ForgetAsync(card.Name);
            StatusText = $"Forgot '{card.Name}' — the voice embedding was deleted from this PC.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Forget failed: {ex.Message}";
        }
    }
}
