using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LocalTranscriber.App.Services;
using LocalTranscriber.App.ViewModels;
using LocalTranscriber.Shared;

namespace LocalTranscriber.App;

public partial class MainWindow : Window
{
    private readonly NotesService _notesService;
    private readonly LocalTranscriber.Storage.ConfigService _configService;
    private bool _spaceTalkHeld;

    public MainWindowViewModel Session { get; }
    public SettingsViewModel Settings { get; }
    public SpeakerManagementViewModel SpeakerPanel { get; }
    public AgentPanelViewModel AgentPanel { get; }
    public NotesPanelViewModel Notes { get; }
    public SessionsViewModel SessionsPanel { get; }

    public MainWindow(
        MainWindowViewModel session,
        SettingsViewModel settings,
        SpeakerManagementViewModel speakerPanel,
        AgentPanelViewModel agentPanel,
        NotesPanelViewModel notes,
        SessionsViewModel sessionsPanel,
        NotesService notesService,
        LocalTranscriber.Storage.ConfigService configService)
    {
        Session = session;
        Settings = settings;
        SpeakerPanel = speakerPanel;
        AgentPanel = agentPanel;
        Notes = notes;
        SessionsPanel = sessionsPanel;
        _notesService = notesService;
        _configService = configService;

        SessionsPanel.LoadRequested += (record, events) => _ = OnLoadSessionRequestedAsync(record, events);
        AgentPanel.SaveNote = (markdown) => _notesService.WriteAsync(markdown);
        AgentPanel.ReadNote = () => _notesService.Content;
        AgentPanel.NotesFilePath = () => _notesService.FilePath;
        AgentPanel.ConsentRequested += OnConsentRequested;
        AgentPanel.FullAgentConsentRequested += OnFullAgentConsentRequested;
        SessionsPanel.DeleteRequested += item => _ = OnDeleteSessionRequestedAsync(item);
        Session.PropertyChanged += OnSessionPropertyChanged;
        Session.NavigateToSettings = section =>
        {
            Settings.SelectedSectionIndex = section;
            Session.SelectedScreenIndex = (int)AppScreen.Settings;
        };

        Session.ShowSpeakerRenameDialog = (currentName, suggestions) =>
        {
            var dlg = new Views.Dialogs.SpeakerRenameDialog(currentName, suggestions) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.NewName : null;
        };

        InitializeComponent();
        DataContext = this;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        SizeChanged += OnWindowSizeChanged;
    }

    private bool _closingConfirmed;

    /// <summary>
    /// Cancels the first close, tears the conversation/session down asynchronously, then re-closes.
    /// The DI container disposes the singleton services (including <see cref="NotesService"/>) at
    /// host teardown, so we never dispose them here.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        try
        {
            await AgentPanel.ShutdownAsync();
            await Session.ShutdownAsync();
        }
        finally
        {
            _closingConfirmed = true;
            Close();
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Session.SessionId) && Session.SessionId.Length > 0)
        {
            _notesService.StartSession(Session.SessionId);
            Notes.Reload();
        }
        else if (e.PropertyName == nameof(Session.SelectedScreenIndex)
            && Session.SelectedScreenIndex == (int)AppScreen.Sessions)
        {
            _ = SessionsPanel.RefreshAsync();
        }
        else if (e.PropertyName == nameof(Session.IsRecording))
        {
            SessionsPanel.OnRecordingStateChanged();
        }
        else if (e.PropertyName == nameof(Session.IsReviewing) && !Session.IsReviewing)
        {
            // Leaving review: voice must reconnect grounded on the live file; notes fall back
            // to the daily key (a new recording repoints them via the SessionId hook).
            _ = AgentPanel.StopVoiceIfRunningAsync();
            _notesService.StartSession("");
            Notes.Reload();
        }
    }

    /// <summary>Loads an archived session into the Meeting screen for review (design 4l).</summary>
    private async Task OnLoadSessionRequestedAsync(LocalTranscriber.Storage.SessionRecord record,
        IReadOnlyList<LocalTranscriber.Shared.TranscriptEvent> events)
    {
        // Reconnect-on-next-send grounds the assistant on the archive instead of the live file.
        await AgentPanel.StopVoiceIfRunningAsync();
        Session.LoadArchive(record, events);
        _notesService.StartSession(record.Id);
        Notes.Reload();
    }

    /// <summary>Shows the hold-to-confirm delete dialog (design 4k) and performs the deletion.</summary>
    private async Task OnDeleteSessionRequestedAsync(SessionListItemViewModel item)
    {
        try
        {
            var config = _configService.Load();
            var service = new LocalTranscriber.Storage.SessionDeletionService(config);
            var files = await service.ListFilesAsync(item.Session.Id);
            bool hasMinutes = service.FindMinutesFiles(item.Session.Id).Length > 0;

            var dialog = new Views.Dialogs.DeleteSessionDialog(
                item.Title, files, item.Summary.SpeakerNames, hasMinutes)
            { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (Session.ReviewSessionId == item.Session.Id)
            {
                Session.CloseReviewCommand.Execute(null);
            }

            await service.DeleteAsync(item.Session.Id, dialog.RemoveMinutes);
            await SessionsPanel.RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Streaming voice modes require the explicit consent moment (design 4c).</summary>
    private void OnConsentRequested(object? sender, string requestedMode)
    {
        var dialog = new Views.Dialogs.VoiceModeConsentDialog(requestedMode) { Owner = this };
        bool accepted = dialog.ShowDialog() == true;
        AgentPanel.ApplyVoiceMode(dialog.SelectedMode, grantAudioConsent: accepted);
    }

    /// <summary>First claude-cli start without stored edit/command consent (mirrors the mic-consent flow).</summary>
    private void OnFullAgentConsentRequested(object? sender, FullAgentConsentEventArgs e)
    {
        var dialog = new Views.Dialogs.FullAgentConsentDialog(e.WorkspaceFolder) { Owner = this };
        e.Granted = dialog.ShowDialog() == true;
    }

    /// <summary>Space is hold-to-talk anywhere except while typing in a text box.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.IsRepeat || Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        _spaceTalkHeld = true;
        AgentPanel.VoicePushToTalkDown();
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spaceTalkHeld)
        {
            return;
        }

        _spaceTalkHeld = false;
        AgentPanel.VoicePushToTalkUp();
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => SystemCommands.CloseWindow(this);

    /// <summary>Below ~840px the full privacy readout condenses to the mini "⛨ local" pill (4h).</summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 840;
        PrivacyReadout.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MiniPrivacyPill.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMaximizeGlyph()
        => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
}
