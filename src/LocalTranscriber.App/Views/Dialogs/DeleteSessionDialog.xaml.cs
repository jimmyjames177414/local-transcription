using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.Views.Dialogs;

/// <summary>
/// Delete confirmation (design 4k): lists the exact files, keeps voice memory, and confirms via
/// press-and-hold (~800 ms fill) instead of type-to-confirm. Esc / releasing early cancels.
/// DialogResult is true ONLY when the hold animation completes — a plain click never deletes.
/// </summary>
public partial class DeleteSessionDialog : Window
{
    private static readonly Duration HoldDuration = new(TimeSpan.FromMilliseconds(800));
    private Storyboard? _holdStoryboard;

    public sealed record FileRow(string Name, string SizeText);

    public DeleteSessionDialog(string title, IReadOnlyList<SessionFileInfo> files,
        IReadOnlyList<string> participants, bool hasMinutesExport)
    {
        InitializeComponent();
        TitleText.Text = $"Delete \"{title}\"?";
        FileList.ItemsSource = files
            .Select(f => new FileRow(f.Name, $" · {ViewModels.SessionListItemViewModel.FormatSize(f.SizeBytes)}"))
            .ToList();
        VoiceMemoryText.Text = participants.Count > 0
            ? $"Voice memory ({string.Join(", ", participants)}) is kept — this only deletes the files above."
            : "Voice memory is kept — this only deletes the files above.";
        RemoveMinutesCheck.Visibility = hasMinutesExport ? Visibility.Visible : Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) =>
        {
            // Drag by the chrome, but never from the hold button.
            if (e.OriginalSource is not DependencyObject d || !IsDescendantOfHoldButton(d))
            {
                try { DragMove(); } catch { }
            }
        };
    }

    public bool RemoveMinutes => RemoveMinutesCheck.IsChecked == true;

    private bool IsDescendantOfHoldButton(DependencyObject d)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, HoldButton))
            {
                return true;
            }
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Hold_Down(object sender, MouseButtonEventArgs e)
    {
        HoldButton.CaptureMouse();

        var fill = new DoubleAnimation(0, 1, HoldDuration) { FillBehavior = FillBehavior.HoldEnd };
        _holdStoryboard = new Storyboard();
        _holdStoryboard.Children.Add(fill);
        Storyboard.SetTarget(fill, HoldFill);
        Storyboard.SetTargetProperty(fill, new PropertyPath("RenderTransform.ScaleX"));
        _holdStoryboard.Completed += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        _holdStoryboard.Begin();
        e.Handled = true;
    }

    private void Hold_Up(object sender, MouseButtonEventArgs e)
    {
        AbortHold();
        e.Handled = true;
    }

    private void Hold_Abort(object sender, EventArgs e) => AbortHold();

    private void AbortHold()
    {
        if (_holdStoryboard is not null)
        {
            _holdStoryboard.Stop();
            _holdStoryboard = null;
        }
        HoldFillScale.ScaleX = 0;
        if (HoldButton.IsMouseCaptured)
        {
            HoldButton.ReleaseMouseCapture();
        }
    }
}
