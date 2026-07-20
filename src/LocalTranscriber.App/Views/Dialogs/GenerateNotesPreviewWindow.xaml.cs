using System.Diagnostics;
using System.IO;
using System.Windows;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.Views.Dialogs;

/// <summary>
/// Read-only preview of a generated meeting-notes document. Saving here writes a standalone .md and
/// opens it — it never touches the live Notes panel or the minutes export files.
/// </summary>
public partial class GenerateNotesPreviewWindow : Window
{
    private readonly string _markdown;
    private readonly string _suggestedFileName;
    private bool _showingRaw;

    public GenerateNotesPreviewWindow(string markdown, string suggestedFileName)
    {
        InitializeComponent();
        _markdown = markdown;
        _suggestedFileName = suggestedFileName;
        RenderedView.Markdown = markdown;
        RawView.Text = markdown;
    }

    private void ToggleRaw_Click(object sender, RoutedEventArgs e)
    {
        _showingRaw = !_showingRaw;
        RawView.Visibility = _showingRaw ? Visibility.Visible : Visibility.Collapsed;
        RenderedView.Visibility = _showingRaw ? Visibility.Collapsed : Visibility.Visible;
        ToggleRawButton.Content = _showingRaw ? "View rendered" : "View raw";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_markdown); } catch { /* clipboard may be locked by another app */ }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string folder = MinutesExporter.ResolveFolder(new ConfigService().Load().MinutesExport.Folder);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _suggestedFileName,
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            AddExtension = true
        };
        try
        {
            if (Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }
        }
        catch { /* fall back to the OS default directory */ }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _markdown);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
