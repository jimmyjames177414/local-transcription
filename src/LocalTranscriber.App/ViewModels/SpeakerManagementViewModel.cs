using System.Collections.ObjectModel;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public sealed class SpeakerManagementViewModel : ObservableObject
{
    private readonly IKnownSpeakerStore _store;
    private KnownSpeaker? _selectedSpeaker;
    private string _renameTo = "";
    private string _statusText = "";

    public SpeakerManagementViewModel(IKnownSpeakerStore? store = null, ConfigService? configService = null)
    {
        if (store is null)
        {
            var config = (configService ?? new ConfigService()).Load();
            store = new SqliteKnownSpeakerStore(new SqliteDatabase(config.DatabasePath));
        }

        _store = store;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => SelectedSpeaker is not null && !string.IsNullOrWhiteSpace(RenameTo));
        ForgetCommand = new AsyncRelayCommand(ForgetAsync, () => SelectedSpeaker is not null);
        _ = RefreshAsync();
    }

    public ObservableCollection<KnownSpeaker> Speakers { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand ForgetCommand { get; }

    public string Note => "Voice embeddings are stored locally in SQLite. Enroll a speaker via: localtranscriber speakers enroll --name \"Name\" --audio <sample.wav>";

    public KnownSpeaker? SelectedSpeaker
    {
        get => _selectedSpeaker;
        set
        {
            SetProperty(ref _selectedSpeaker, value);
            RenameCommand.RaiseCanExecuteChanged();
            ForgetCommand.RaiseCanExecuteChanged();
        }
    }

    public string RenameTo
    {
        get => _renameTo;
        set
        {
            SetProperty(ref _renameTo, value);
            RenameCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var speakers = await _store.ListAsync();
            Speakers.Clear();
            foreach (var s in speakers)
            {
                Speakers.Add(s);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load speakers: {ex.Message}";
        }
    }

    private async Task RenameAsync()
    {
        if (SelectedSpeaker is null)
        {
            return;
        }

        await _store.RenameAsync(SelectedSpeaker.DisplayName, RenameTo);
        StatusText = $"Renamed '{SelectedSpeaker.DisplayName}' to '{RenameTo}'.";
        RenameTo = "";
        await RefreshAsync();
    }

    private async Task ForgetAsync()
    {
        if (SelectedSpeaker is null)
        {
            return;
        }

        await _store.ForgetAsync(SelectedSpeaker.DisplayName);
        StatusText = $"Forgot '{SelectedSpeaker.DisplayName}'.";
        await RefreshAsync();
    }
}
