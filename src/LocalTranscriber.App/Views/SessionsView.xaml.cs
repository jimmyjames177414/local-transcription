using System.Windows.Controls;

namespace LocalTranscriber.App.Views;

public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        InitializeComponent();
    }

    private MainWindow? Shell => DataContext as MainWindow;

    private void RenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter && shell.SessionsPanel.CommitRenameCommand.CanExecute(null))
        {
            shell.SessionsPanel.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            shell.SessionsPanel.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }
}
