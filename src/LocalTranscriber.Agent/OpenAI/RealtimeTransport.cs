using System.Net.WebSockets;
using System.Text;

namespace LocalTranscriber.Agent.OpenAI;

/// <summary>Connection settings shared by realtime consumers (transport URI/headers, timeouts, retry).</summary>
public sealed record RealtimeConnectionOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-realtime-2.1-mini";
    public string BaseUrl { get; init; } = "wss://api.openai.com/v1/realtime";
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxReconnectAttempts { get; init; } = 3;
}

/// <summary>Injectable websocket layer so tests never touch the network.</summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
    Task SendAsync(string json, CancellationToken cancellationToken = default);
    /// <summary>Next complete server event as JSON text; null when the socket closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken = default);
}

public sealed class ClientWebSocketRealtimeTransport : IRealtimeTransport
{
    private ClientWebSocket? _socket;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        foreach (var (name, value) in headers)
        {
            _socket.Options.SetRequestHeader(name, value);
        }
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Realtime socket is not connected.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            return null;
        }

        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        return ValueTask.CompletedTask;
    }
}
