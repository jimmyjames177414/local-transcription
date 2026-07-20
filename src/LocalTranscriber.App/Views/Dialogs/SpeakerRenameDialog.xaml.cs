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

        ScopeAllLabel.Text = request.OccurrenceCount == 1
            ? $"Every \"{request.CurrentName}\" line (1 line)"
            : $"Every \"{request.CurrentName}\" line ({request.OccurrenceCount} lines)";

        // Default scope by intent: naming an unidentified speaker relabels every line (and enrolls
        // their voice); correcting an already-named speaker defaults to the single clicked line.
        if (request.IsCurrentlyUnknown)
            ScopeAll.IsChecked = true;
        else
            ScopeOne.IsChecked = true;

        // The full ranked roster backs the editable box's type-ahead; chips surface only the top few.
        NameInput.ItemsSource = request.Suggestions;
        NameInput.Focus();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        if (request.Suggestions.Count > 0)
        {
            SuggestionsPanel.Visibility = Visibility.Visible;
            const int maxChips = 8;
            int shown = 0;
            foreach (string name in request.Suggestions)
            {
                if (shown >= maxChips) break;
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
                shown++;
            }

            int overflow = request.Suggestions.Count - shown;
            if (overflow > 0)
            {
                MoreHint.Text = $"+{overflow} more - type to search";
                MoreHint.Visibility = Visibility.Visible;
            }
        }
    }

    private RenameScope SelectedScope =>
        ScopeOne.IsChecked == true ? RenameScope.ThisOne : RenameScope.All;

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        // A chip is a typing shortcut: fill the name box and let the user confirm the scope + Save.
        // (Previously chips silently committed an "all lines" rename, which was surprising.)
        if (sender is Button btn && btn.Tag is string name)
        {
            NameInput.Text = name;
            NameInput.Focus();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = (NameInput.Text ?? "").Trim();
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
