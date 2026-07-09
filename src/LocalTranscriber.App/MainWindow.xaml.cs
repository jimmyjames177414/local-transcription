using System.Windows;
using System.Windows.Controls;
using LocalTranscriber.App.ViewModels;
using Microsoft.Win32;

namespace LocalTranscriber.App;

public partial class MainWindow : Window
{
    public MainWindowViewModel Session { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public SpeakerManagementViewModel SpeakerPanel { get; } = new();
    public AgentPanelViewModel AgentPanel { get; }

    public MainWindow()
    {
        AgentPanel = new AgentPanelViewModel(currentTranscriptPath: () => Session.CurrentJsonlPath);
        InitializeComponent();
        DataContext = this;
        Closing += async (_, _) =>
        {
            await AgentPanel.ShutdownAsync();
            await Session.ShutdownAsync();
        };
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose transcript output folder" };
        if (dialog.ShowDialog(this) == true)
        {
            Session.OutputFolder = dialog.FolderName;
        }
    }

    private void Preview_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.ScrollToEnd();
        }
    }
}
