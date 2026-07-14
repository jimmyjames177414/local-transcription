using System.IO;

namespace LocalTranscriber.App.Services;

/// <summary>
/// Manages the session's notes markdown file. Supports atomic writes (temp-file + move) and
/// a FileSystemWatcher so external edits (user's editor, AI tool) reload the UI automatically.
/// </summary>
public sealed class NotesService : IDisposable
{
    private readonly Func<string> _folderProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _sessionKey;
    private string _content = "";
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private FileSystemWatcher? _watcher;

    public NotesService(Func<string> agentOutputFolderProvider)
    {
        _folderProvider = agentOutputFolderProvider;
        _sessionKey = DateTime.Now.ToString("yyyyMMdd");
    }

    /// <summary>Raised when the file content changes (written by us or by an external process).</summary>
    public event Action<string>? Changed;

    public DateTimeOffset? LastSavedAt { get; private set; }

    public string Content => _content;

    public string FilePath => Path.Combine(_folderProvider(), $"notes-{_sessionKey}.md");

    /// <summary>Repoints at a new session; loads its existing file if one is present.</summary>
    public void StartSession(string sessionId)
    {
        _sessionKey = string.IsNullOrWhiteSpace(sessionId) ? DateTime.Now.ToString("yyyyMMdd") : sessionId;
        _content = TryReadFile() ?? "";
        RewatchFolder();
    }

    /// <summary>Atomically overwrites the notes file and notifies listeners.</summary>
    public async Task WriteAsync(string markdown, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _content = markdown;
            _lastWrite = DateTimeOffset.Now;
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, markdown, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            LastSavedAt = DateTimeOffset.Now;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(markdown);
    }

    private void RewatchFolder()
    {
        _watcher?.Dispose();
        try
        {
            string folder = _folderProvider();
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder, $"notes-{_sessionKey}.md")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnExternalChange;
            _watcher.Created += OnExternalChange;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void OnExternalChange(object sender, FileSystemEventArgs e)
    {
        // Suppress events caused by our own WriteAsync.
        if (DateTimeOffset.Now - _lastWrite < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        string? text = TryReadFile();
        if (text is null || text == _content)
        {
            return;
        }

        _content = text;
        Changed?.Invoke(text);
    }

    private string? TryReadFile()
    {
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                string path = FilePath;
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _gate.Dispose();
    }
}
