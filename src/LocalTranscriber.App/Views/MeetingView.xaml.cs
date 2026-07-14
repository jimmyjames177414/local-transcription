using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LocalTranscriber.App.Behaviors;

namespace LocalTranscriber.App.Views;

public partial class MeetingView : UserControl
{
    private const double CompactWidthPx = 900;
    private bool _compact;
    private bool _compactShowingNotes;

    private MainWindow? _subscribedShell;

    public MeetingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedShell is not null)
            {
                _subscribedShell.Session.ScrollToRowRequested -= OnScrollToRowRequested;
            }
            _subscribedShell = Shell;
            if (_subscribedShell is not null)
            {
                _subscribedShell.Session.ScrollToRowRequested += OnScrollToRowRequested;
            }
        };
    }

    private MainWindow? Shell => DataContext as MainWindow;

    /// <summary>Scrolls the transcript to a row (search match / citation) and flashes it.</summary>
    private void OnScrollToRowRequested(int index)
    {
        if (index < 0 || index >= TranscriptList.Items.Count)
        {
            return;
        }

        object item = TranscriptList.Items[index];
        TranscriptList.ScrollIntoView(item);
        TranscriptList.UpdateLayout();

        if (TranscriptList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container
            && container.Content is not null
            && FindRowBorder(container) is { } border)
        {
            var original = border.Background;
            border.Background = (System.Windows.Media.Brush)FindResource("Brush.Accent.Bg");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                border.Background = original;
            };
            timer.Start();
        }
    }

    private static Border? FindRowBorder(DependencyObject root)
    {
        if (root is Border b)
        {
            return b;
        }
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindRowBorder(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void ExportReviewSession_Click(object sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell || shell.Session.ReviewSessionId is not { } sessionId)
        {
            return;
        }

        _ = ExportAsync(shell, sessionId);

        static async Task ExportAsync(MainWindow shell, string sessionId)
        {
            try
            {
                var service = new LocalTranscriber.Storage.MinutesExportService(new LocalTranscriber.Storage.ConfigService().Load());
                string path = await service.ExportAsync(sessionId);
                MessageBox.Show(Window.GetWindow(shell), $"Exported: {path}", "Minutes export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(shell), ex.Message, "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>Below ~900px the notes column folds into the chat cell behind a Chat/Notes toggle (4h).</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < CompactWidthPx;
        if (compact == _compact)
        {
            return;
        }

        _compact = compact;
        if (compact)
        {
            TranscriptCol.Width = new GridLength(1, GridUnitType.Star);
            ChatCol.Width = new GridLength(246);
            ChatCol.MinWidth = 0;
            NotesCol.Width = new GridLength(0);
            Grid.SetColumn(NotesColumn, 1);
            CompactNotesButton.Visibility = Visibility.Visible;
            CompactChatButton.Visibility = Visibility.Visible;
            ApplyCompactPanel();
        }
        else
        {
            TranscriptCol.Width = new GridLength(368);
            ChatCol.Width = new GridLength(1, GridUnitType.Star);
            ChatCol.MinWidth = 300;
            NotesCol.Width = new GridLength(248);
            Grid.SetColumn(NotesColumn, 2);
            CompactNotesButton.Visibility = Visibility.Collapsed;
            CompactChatButton.Visibility = Visibility.Collapsed;
            ChatColumn.Visibility = Visibility.Visible;
            NotesColumn.Visibility = Visibility.Visible;
        }
    }

    private void ApplyCompactPanel()
    {
        ChatColumn.Visibility = _compactShowingNotes ? Visibility.Collapsed : Visibility.Visible;
        NotesColumn.Visibility = _compactShowingNotes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactShowNotes_Click(object sender, RoutedEventArgs e)
    {
        _compactShowingNotes = true;
        ApplyCompactPanel();
    }

    private void CompactShowChat_Click(object sender, RoutedEventArgs e)
    {
        _compactShowingNotes = false;
        ApplyCompactPanel();
    }

    private void OpenAssistantSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
        {
            shell.Settings.SelectedSectionIndex = 3;
            shell.Session.SelectedScreenIndex = (int)ViewModels.AppScreen.Settings;
        }
    }

    private void OpenTranscriptsFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        string? folder = Shell?.Session.OutputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        string full = Path.GetFullPath(folder);
        if (Directory.Exists(full))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", full) { UseShellExecute = true });
        }
    }

    private void FollowLive_Click(object sender, System.Windows.RoutedEventArgs e)
        => AutoScrollBehavior.SetIsFollowing(TranscriptList, true);

    private void ChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && Shell is { } shell)
        {
            e.Handled = true;
            if (shell.AgentPanel.SendTextCommand.CanExecute(null))
            {
                shell.AgentPanel.SendTextCommand.Execute(null);
            }
        }
    }

    private void NotesTextBox_LostFocus(object sender, RoutedEventArgs e)
        => _ = (Shell?.Notes.FlushAsync());

    private void HoldToTalk_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Shell?.AgentPanel.VoicePushToTalkDown();

    private void HoldToTalk_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Shell?.AgentPanel.VoicePushToTalkUp();
}
