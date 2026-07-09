using System.Collections.ObjectModel;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public sealed class SpeakerManagementViewModel : ObservableObject
{
    private readonly JsonSpeakerStore _store;
    private JsonSpeakerStore.StoredSpeaker? _selectedSpeaker;
    private string _renameTo = "";
    private string _statusText = "";

    public SpeakerManagementViewModel(JsonSpeakerStore? store = null)
    {
        _store = store ?? new JsonSpeakerStore();
        RefreshCommand = new RelayCommand(Refresh);
        RenameCommand = new RelayCommand(Rename, () => SelectedSpeaker is not null && !string.IsNullOrWhiteSpace(RenameTo));
        ForgetCommand = new RelayCommand(Forget, () => SelectedSpeaker is not null);
        Refresh();
    }

    public ObservableCollection<JsonSpeakerStore.StoredSpeaker> Speakers { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand ForgetCommand { get; }

    public string Note => "Voice matching arrives in a later phase. For now this list only stores names.";

    public JsonSpeakerStore.StoredSpeaker? SelectedSpeaker
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

    private void Refresh()
    {
        Speakers.Clear();
        foreach (var s in _store.List())
        {
            Speakers.Add(s);
        }
    }

    private void Rename()
    {
        if (SelectedSpeaker is null)
        {
            return;
        }

        _store.Rename(SelectedSpeaker.DisplayName, RenameTo);
        StatusText = $"Renamed '{SelectedSpeaker.DisplayName}' to '{RenameTo}'.";
        RenameTo = "";
        Refresh();
    }

    private void Forget()
    {
        if (SelectedSpeaker is null)
        {
            return;
        }

        _store.Forget(SelectedSpeaker.DisplayName);
        StatusText = $"Forgot '{SelectedSpeaker.DisplayName}'.";
        Refresh();
    }
}
