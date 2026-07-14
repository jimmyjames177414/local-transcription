using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalTranscriber.App.Views.Dialogs;

public partial class SpeakerRenameDialog : Window
{
    public string NewName { get; private set; } = "";

    public SpeakerRenameDialog(string currentName, IReadOnlyList<string> suggestions)
    {
        InitializeComponent();
        PromptText.Text = $"Who is \"{currentName}\"?";
        NameBox.Focus();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        if (suggestions.Count > 0)
        {
            SuggestionsPanel.Visibility = Visibility.Visible;
            foreach (string name in suggestions)
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

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            NewName = name;
            DialogResult = true;
            Close();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        NewName = name;
        DialogResult = true;
        Close();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save_Click(sender, new RoutedEventArgs());
    }
}
