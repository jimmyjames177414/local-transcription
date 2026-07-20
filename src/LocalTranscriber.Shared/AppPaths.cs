namespace LocalTranscriber.Shared;

/// <summary>
/// Resolves where config/data/logs live.
/// Dev checkout (LocalTranscriber.sln found upward from the working directory): ./output/...
/// Packaged install: %AppData%\LocalTranscriber and Documents\LocalTranscriber\Transcripts.
/// Override the root with the LOCALTRANSCRIBER_HOME environment variable.
/// </summary>
public static class AppPaths
{
    public static bool IsDevCheckout { get; } = FindUpward("LocalTranscriber.sln") is not null;

    private static string Root =>
        Environment.GetEnvironmentVariable("LOCALTRANSCRIBER_HOME")
        ?? (IsDevCheckout
            ? "output"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalTranscriber"));

    public static string ConfigPath => Path.Combine(Root, "config.json");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string DefaultDatabasePath => IsDevCheckout
        ? Path.Combine("output", "localtranscriber.sqlite")
        : Path.Combine(Root, "data", "localtranscriber.sqlite");

    public static string DefaultTranscriptFolder => IsDevCheckout
        ? Path.Combine("output", "transcripts")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocalTranscriber", "Transcripts");

    /// <summary>Models ship next to the executable in packaged builds.</summary>
    public static string DefaultWhisperModelPath => IsDevCheckout
        ? Path.Combine("models", "whisper", "ggml-base.en.bin")
        : Path.Combine(AppContext.BaseDirectory, "models", "whisper", "ggml-base.en.bin");

    public static string DefaultSpeakerModelDir => IsDevCheckout
        ? Path.Combine("models", "speaker")
        : Path.Combine(AppContext.BaseDirectory, "models", "speaker");

    public static AppConfig CreateDefaultConfig() => new()
    {
        TranscriptFolder = DefaultTranscriptFolder,
        DatabasePath = DefaultDatabasePath,
        WhisperModelPath = DefaultWhisperModelPath,
        SpeakerModelPath = DefaultSpeakerModelDir
    };

    private static string? FindUpward(string fileName)
    {
        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, fileName)))
                {
                    return dir.FullName;
                }
            }
        }
        catch
        {
        }
        return null;
    }
}

/// <summary>
/// Minimal structured-ish file logger. Never logs transcript text.
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static string? _path;

    private static string LogPath
    {
        get
        {
            if (_path is null)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                _path = Path.Combine(AppPaths.LogsDir, $"localtranscriber-{DateTime.Now:yyyyMMdd}.log");
            }
            return _path;
        }
    }

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, string message) => Write("ERROR", area, message);

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:o} [{level}] {area}: {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
