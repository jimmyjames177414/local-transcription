using System.Windows;
using LocalTranscriber.App.Mvvm;

namespace LocalTranscriber.App.ViewModels;

public enum ChatRole
{
    User,
    Assistant
}

/// <summary>One bubble in the assistant conversation.</summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text;
    private bool _isStreaming;
    private string _metaText = "";

    public ChatMessageViewModel(ChatRole role, string text, bool isStreaming = false)
    {
        Role = role;
        _text = text;
        _isStreaming = isStreaming;
        Timestamp = DateTimeOffset.Now;
        CopyCommand = new RelayCommand(() => TrySetClipboard(Text));
    }

    public ChatRole Role { get; }
    public bool IsUser => Role == ChatRole.User;
    public DateTimeOffset Timestamp { get; }
    public RelayCommand CopyCommand { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>Small gray line under an assistant reply, e.g. "🔊 spoken privately · 10:45:40".</summary>
    public string MetaText
    {
        get => _metaText;
        set => SetProperty(ref _metaText, value);
    }

    public void Append(string delta) => Text += delta;

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another process; not worth surfacing.
        }
    }
}
