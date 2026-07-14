using System.Windows.Controls;
using Microsoft.Win32;

namespace LocalTranscriber.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainWindow? Shell => DataContext as MainWindow;

    private void BrowseTranscriptFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose transcript folder" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true && Shell is { } shell)
        {
            shell.Settings.TranscriptFolder = dialog.FolderName;
        }
    }

    private void BrowseWhisperModel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose whisper model", Filter = "Model files (*.bin)|*.bin|All files (*.*)|*.*" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true && Shell is { } shell)
        {
            shell.Settings.WhisperModelPath = dialog.FileName;
        }
    }

    private void BrowseSpeakerModel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose speaker model", Filter = "Model files (*.onnx)|*.onnx|All files (*.*)|*.*" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true && Shell is { } shell)
        {
            shell.Settings.SpeakerModelPath = dialog.FileName;
        }
    }

    private void BrowseWorkspaceFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Claude CLI workspace folder" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true && Shell is { } shell)
        {
            shell.AgentPanel.WorkspaceFolder = dialog.FolderName;
        }
    }

    private void BrowseMinutesFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Minutes meetings folder" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true && Shell is { } shell)
        {
            shell.Settings.MinutesFolder = dialog.FolderName;
        }
    }
}
