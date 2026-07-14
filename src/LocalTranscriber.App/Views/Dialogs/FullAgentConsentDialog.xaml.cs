using System.Windows;

namespace LocalTranscriber.App.Views.Dialogs;

/// <summary>
/// One-time consent for the Claude CLI full-agent mode (file edits + commands in the workspace).
/// Semantically distinct from <see cref="VoiceModeConsentDialog"/> (that gates mic streaming), so it
/// is a sibling dialog rather than a copy. DialogResult is true only when the user granted capability.
/// </summary>
public partial class FullAgentConsentDialog : Window
{
    public FullAgentConsentDialog(string workspaceFolder)
    {
        InitializeComponent();
        WorkspaceText.Text = string.IsNullOrWhiteSpace(workspaceFolder) ? "(no workspace chosen)" : workspaceFolder;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }

    private void Consent_Changed(object sender, RoutedEventArgs e)
        => AllowButton.IsEnabled = ConsentCheck.IsChecked == true;

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = ConsentCheck.IsChecked == true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
