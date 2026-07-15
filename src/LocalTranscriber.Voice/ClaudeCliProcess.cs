using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace LocalTranscriber.Voice;

/// <summary>A request to launch the Claude Code CLI for one turn.</summary>
public sealed record ClaudeProcessRequest(string ExecutablePath, string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>
/// A running Claude CLI child process, abstracted so <see cref="ClaudeCliConversation"/> can be unit
/// tested against a captured stream-json fixture without spawning the real executable.
/// </summary>
public interface IClaudeProcess : IAsyncDisposable
{
    /// <summary>Yields stdout lines (one JSON object per line) until the process closes stdout.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for exit and returns the exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Everything the process wrote to stderr (available once it has exited).</summary>
    string StandardError { get; }

    /// <summary>Kills the process tree (used for Stop/Dispose and mid-turn cancel).</summary>
    void Kill();
}

/// <summary>
/// Default <see cref="IClaudeProcess"/>: a redirected <see cref="Process"/>. Reads stdout line by
/// line (modelled on TranscriptEventTailer's async read loop) and drains stderr on a pump task so a
/// full stderr buffer can never deadlock the stdout read.
/// </summary>
internal sealed class ClaudeCliProcess : IClaudeProcess
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;

    public ClaudeCliProcess(ClaudeProcessRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Force wsl.exe's own messages to UTF-8 (it defaults to UTF-16 when stdout is redirected,
        // which would corrupt the stream-json parse). Ignored by the native-Windows claude.exe.
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _process = new Process { StartInfo = psi };
        _process.Start();
        _stderrPump = Task.Run(async () =>
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                }
            }
        });
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? line;
        while ((line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            yield return line;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        return _process.ExitCode;
    }

    public string StandardError
    {
        get { lock (_stderr) { return _stderr.ToString(); } }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        _process.Dispose();
    }
}
