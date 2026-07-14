using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalTranscriber.App.Behaviors;

/// <summary>
/// Renders assistant reply text with HH:mm:ss timestamps as clickable citation links
/// (design 4l) that invoke <see cref="CitationCommandProperty"/> with the timestamp string.
/// Bind <see cref="LinkifyProperty"/> to !IsStreaming so inlines aren't rebuilt per token —
/// while false the text renders plain.
/// </summary>
public static partial class TimestampInlineBehavior
{
    private static readonly Regex TimestampRegex = new(@"\b\d{2}:\d{2}:\d{2}\b", RegexOptions.Compiled);

    public static readonly DependencyProperty FormattedTextProperty = DependencyProperty.RegisterAttached(
        "FormattedText", typeof(string), typeof(TimestampInlineBehavior),
        new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty LinkifyProperty = DependencyProperty.RegisterAttached(
        "Linkify", typeof(bool), typeof(TimestampInlineBehavior),
        new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty CitationCommandProperty = DependencyProperty.RegisterAttached(
        "CitationCommand", typeof(ICommand), typeof(TimestampInlineBehavior),
        new PropertyMetadata(null));

    public static string GetFormattedText(DependencyObject obj) => (string)obj.GetValue(FormattedTextProperty);
    public static void SetFormattedText(DependencyObject obj, string value) => obj.SetValue(FormattedTextProperty, value);
    public static bool GetLinkify(DependencyObject obj) => (bool)obj.GetValue(LinkifyProperty);
    public static void SetLinkify(DependencyObject obj, bool value) => obj.SetValue(LinkifyProperty, value);
    public static ICommand? GetCitationCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CitationCommandProperty);
    public static void SetCitationCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CitationCommandProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        string text = GetFormattedText(block);
        block.Inlines.Clear();

        if (!GetLinkify(block))
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        int position = 0;
        foreach (Match match in TimestampRegex.Matches(text))
        {
            if (match.Index > position)
            {
                block.Inlines.Add(new Run(text[position..match.Index]));
            }

            var link = new Hyperlink(new Run(match.Value))
            {
                TextDecorations = null,
                Cursor = Cursors.Hand
            };
            if (Application.Current?.TryFindResource("Brush.Accent.Text") is Brush accent)
            {
                link.Foreground = accent;
            }
            string timestamp = match.Value;
            link.Click += (_, _) => GetCitationCommand(block)?.Execute(timestamp);
            block.Inlines.Add(link);

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            block.Inlines.Add(new Run(text[position..]));
        }
    }
}
