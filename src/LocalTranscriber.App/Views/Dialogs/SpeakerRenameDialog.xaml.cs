using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalTranscriber.App.ViewModels;

namespace LocalTranscriber.App.Views.Dialogs;

public partial class SpeakerRenameDialog : Window
{
    public SpeakerRenameResult? Result { get; private set; }

    public SpeakerRenameDialog(SpeakerRenameRequest request)
    {
        InitializeComponent();
        PromptText.Text = $"Who is \"{request.CurrentName}\"?";

        string allLabel = request.OccurrenceCount == 1
            ? $"Every \"{request.CurrentName}\" line (1 line)"
            : $"Every \"{request.CurrentName}\" line ({request.OccurrenceCount} lines)";
        ScopeAllLabel.Text = allLabel;

        NameBox.Focus();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        if (request.Suggestions.Count > 0)
        {
            SuggestionsPanel.Visibility = Visibility.Visible;
            foreach (string name in request.Suggestions)
            {
                var chip = new Button
                {
                    Content = name,
                    Margin = new Thickness(0, 0, 6, 4),
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    Tag = name,
                };
                chip.Click += Chip_Click;
                ChipsPanel.Children.Add(chip);
            }
        }
    }

    private RenameScope SelectedScope =>
        ScopeOne.IsChecked == true ? RenameScope.ThisOne : RenameScope.All;

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            // Chips always apply to all lines (they're quick-pick for a known person).
            Result = new SpeakerRenameResult(name, RenameScope.All);
            DialogResult = true;
            Close();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        Result = new SpeakerRenameResult(name, SelectedScope);
        DialogResult = true;
        Close();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save_Click(sender, new RoutedEventArgs());
    }
}
