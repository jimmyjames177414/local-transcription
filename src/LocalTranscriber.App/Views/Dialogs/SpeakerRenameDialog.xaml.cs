using System.Windows;
using System.Windows.Input;

namespace LocalTranscriber.App.Views.Dialogs;

public partial class SpeakerRenameDialog : Window
{
    public string NewName { get; private set; } = "";

    public SpeakerRenameDialog(string currentName)
    {
        InitializeComponent();
        PromptText.Text = $"Who is \"{currentName}\"?";
        NameBox.Text = "";
        NameBox.Focus();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
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
