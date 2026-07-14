using System.Windows;
using System.Windows.Media;

namespace LocalTranscriber.App.Views.Dialogs;

/// <summary>
/// The consent moment (design 4c) shown when switching to a streaming voice mode while
/// agent.realtime.sendAudio is false. Returns the chosen mode; DialogResult is true only when
/// the user explicitly allowed mic streaming.
/// </summary>
public partial class VoiceModeConsentDialog : Window
{
    public VoiceModeConsentDialog(string requestedMode)
    {
        InitializeComponent();
        SelectedMode = requestedMode;
        (requestedMode == "continuous" ? ContinuousRadio : PushRadio).IsChecked = true;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }

    /// <summary>"hybrid", "pushToTalk", or "continuous".</summary>
    public string SelectedMode { get; private set; }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        SelectedMode = sender == HybridRadio ? "hybrid" : sender == PushRadio ? "pushToTalk" : "continuous";
        UpdateCards();
        UpdateAllowButton();
    }

    private void Consent_Changed(object sender, RoutedEventArgs e) => UpdateAllowButton();

    private void UpdateAllowButton()
    {
        // Hybrid needs no consent: picking it turns the primary action into a plain confirm.
        bool streaming = SelectedMode is "pushToTalk" or "continuous";
        AllowButton.Content = streaming ? "Allow mic streaming" : "Use Hybrid";
        AllowButton.IsEnabled = !streaming || ConsentCheck.IsChecked == true;
        ConsentBox.Opacity = streaming ? 1.0 : 0.45;
    }

    private void UpdateCards()
    {
        var accent = (Brush)FindResource("Brush.Accent");
        var normal = (Brush)FindResource("Brush.Surface.Hi");
        HybridCard.BorderBrush = SelectedMode == "hybrid" ? accent : normal;
        PushCard.BorderBrush = SelectedMode == "pushToTalk" ? accent : normal;
        ContinuousCard.BorderBrush = SelectedMode == "continuous" ? accent : normal;
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        // True only for a consented streaming mode; a hybrid pick closes as false and the
        // caller applies SelectedMode ("hybrid") without touching the consent flag.
        DialogResult = SelectedMode is "pushToTalk" or "continuous" && ConsentCheck.IsChecked == true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = "hybrid";
        DialogResult = false;
        Close();
    }
}
