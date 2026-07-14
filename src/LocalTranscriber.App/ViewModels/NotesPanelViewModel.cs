using System.Diagnostics;
using System.IO;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.App.Services;

namespace LocalTranscriber.App.ViewModels;

/// <summary>Notes panel: a live view of the session's markdown notes file.</summary>
public sealed class NotesPanelViewModel : ObservableObject
{
    private readonly NotesService _notes;
    private readonly SynchronizationContext? _uiContext;
    private string _content = "";
    private string _autoSavedText = "";
    private bool _suppressSave;
    private bool _isDirty;

    public NotesPanelViewModel(NotesService notes)
    {
        _notes = notes;
        _uiContext = SynchronizationContext.Current;
        _notes.Changed += OnNotesChanged;
        _content = _notes.Content;
        OpenNotesFileCommand = new RelayCommand(OpenNotesFile);
    }

    /// <summary>The raw markdown content of the notes file. Two-way bound to the editable text area.</summary>
    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value) && !_suppressSave)
            {
                _isDirty = true;
            }
        }
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(_content);

    public string AutoSavedText
    {
        get => _autoSavedText;
        private set => SetProperty(ref _autoSavedText, value);
    }

    public RelayCommand OpenNotesFileCommand { get; }

    /// <summary>Called when a session starts so the panel re-reads the new session's file.</summary>
    public void Reload()
    {
        _suppressSave = true;
        Content = _notes.Content;
        _suppressSave = false;
        _isDirty = false;
    }

    /// <summary>Persists any unsaved user edits. Called when the text area loses focus.</summary>
    public async Task FlushAsync()
    {
        if (!_isDirty)
        {
            return;
        }

        _isDirty = false;
        await _notes.WriteAsync(_content).ConfigureAwait(false);
    }

    private void OnNotesChanged(string markdown)
        => PostToUi(() =>
        {
            _suppressSave = true;
            Content = markdown;
            _suppressSave = false;
            _isDirty = false;
            AutoSavedText = _notes.LastSavedAt is { } t ? $"saved {t.ToLocalTime():HH:mm:ss}" : "";
            OnPropertyChanged(nameof(IsEmpty));
        });

    private void OpenNotesFile()
    {
        try
        {
            string path = _notes.FilePath;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(path)!) { UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
