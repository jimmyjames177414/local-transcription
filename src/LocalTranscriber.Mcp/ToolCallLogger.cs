namespace LocalTranscriber.Mcp;

/// <summary>
/// Appends one line per MCP tool call to a local log file. Never logs transcript text.
/// </summary>
public sealed class ToolCallLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public ToolCallLogger(string? logPath = null)
    {
        _logPath = logPath ?? Path.Combine(LocalTranscriber.Shared.AppPaths.LogsDir, "mcp-tool-calls.log");
        string? dir = Path.GetDirectoryName(Path.GetFullPath(_logPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Log(string toolName, string details = "")
    {
        lock (_lock)
        {
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:o} {toolName} {details}{Environment.NewLine}");
        }
    }
}
