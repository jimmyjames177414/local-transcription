using System.IO.Pipes;
using System.Text.Json;

namespace LocalTranscriber.Engine.Ipc;

public sealed record IpcRequest(string Command);

public sealed record IpcResponse(bool Ok, string? Message = null, TranscriptionSessionStatus? Status = null);

/// <summary>
/// Named-pipe control server hosted by whichever process runs an engine session
/// (WPF app or headless CLI). Lets other local processes (CLI, MCP) query status
/// and stop/pause/resume the running session. Local machine only — no network.
/// </summary>
public sealed class EngineIpcServer : IAsyncDisposable
{
    public const string DefaultPipeName = "localtranscriber-control";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITranscriptionEngine _engine;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listener;

    public EngineIpcServer(ITranscriptionEngine engine, string pipeName = DefaultPipeName)
    {
        _engine = engine;
        _pipeName = pipeName;
    }

    public void Start()
    {
        _listener = Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client vanished mid-request, or the pipe name is already owned by another host
                // (maxNumberOfServerInstances = 1). Back off so a contended name can't spin the CPU,
                // then keep listening.
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<IpcResponse> HandleAsync(string? requestLine, CancellationToken cancellationToken)
    {
        if (requestLine is null)
        {
            return new IpcResponse(false, "Empty request.");
        }

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(requestLine, Json);
        }
        catch (JsonException)
        {
            return new IpcResponse(false, "Malformed request.");
        }

        try
        {
            switch (request?.Command)
            {
                case "status":
                    return new IpcResponse(true, Status: await _engine.GetStatusAsync(cancellationToken).ConfigureAwait(false));
                case "stop":
                    await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
                    return new IpcResponse(true, "Stopped.");
                case "pause":
                    await _engine.PauseAsync(cancellationToken).ConfigureAwait(false);
                    return new IpcResponse(true, "Paused.");
                case "resume":
                    await _engine.ResumeAsync(cancellationToken).ConfigureAwait(false);
                    return new IpcResponse(true, "Resumed.");
                default:
                    return new IpcResponse(false, $"Unknown command: {request?.Command}");
            }
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        bool listenerStopped = _listener is null;
        if (_listener is not null)
        {
            try
            {
                await _listener.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                listenerStopped = true;
            }
            catch (TimeoutException)
            {
            }
        }

        // Only dispose the CTS once the listener has actually stopped — a timed-out listener may still
        // be inside WaitForConnectionAsync(_cts.Token), and disposing the source under it would throw.
        if (listenerStopped)
        {
            _cts.Dispose();
        }
    }
}

public static class EngineIpcClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Sends a command to a running session's pipe. Returns null when no session is listening.</summary>
    public static async Task<IpcResponse?> TrySendAsync(string command, string pipeName = EngineIpcServer.DefaultPipeName, int connectTimeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest(command), Json)).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<IpcResponse>(line, Json);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
