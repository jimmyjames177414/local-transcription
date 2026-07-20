using System.Text;
using System.Text.Json;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Voice;

/// <summary>
/// One-shot meeting-notes document generation over the currently configured assistant backend
/// (OpenAI realtime / Claude CLI / hybrid). There is no dedicated one-shot text API in the app, so
/// this drives the streaming <see cref="IRealtimeVoiceConversation"/> as a single bounded turn:
/// connect, send the full prompt, accumulate the reply, and tear down. The "be concise" system
/// prompt each backend bakes in cannot be overridden through the interface, so the caller neutralises
/// it inside <paramref name="fullPrompt"/>.
/// </summary>
public sealed class MeetingNotesGenerator
{
    private readonly Func<AppConfig, SecretsService?, RealtimeVoiceFactory.Resolution> _sessionFactory;

    /// <param name="sessionFactory">
    /// Optional seam so tests can inject a fake <see cref="IRealtimeVoiceConversation"/>. Receives the
    /// already-prepared generation config (voice off, realtime enabled). Defaults to the real backend.
    /// </param>
    public MeetingNotesGenerator(
        Func<AppConfig, SecretsService?, RealtimeVoiceFactory.Resolution>? sessionFactory = null)
    {
        _sessionFactory = sessionFactory ?? ((cfg, sec) => AgentConversationFactory.Create(
            cfg, sec, transcriptJsonlPath: null, tools: null, toolHandler: null, notesFilePath: null));
    }

    /// <summary>
    /// Generates a complete notes document from <paramref name="fullPrompt"/>. Throws with a clear
    /// message when the backend is unavailable, reports an error, times out, or returns no content.
    /// </summary>
    public async Task<string> GenerateAsync(
        string fullPrompt,
        AppConfig config,
        SecretsService? secrets = null,
        int timeoutSeconds = 240,
        CancellationToken ct = default)
    {
        var genConfig = CloneForGeneration(config);
        var resolution = _sessionFactory(genConfig, secrets);
        if (resolution.Session is null)
        {
            throw new InvalidOperationException(resolution.Notice ?? "Notes generation is unavailable.");
        }

        var session = resolution.Session;
        var buffer = new StringBuilder();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? errorMessage = null;

        void OnText(object? _, string delta)
        {
            lock (buffer) buffer.Append(delta);
        }
        void OnCompleted(object? _, EventArgs __) => done.TrySetResult(true);
        // Mandatory: the CLI backend does not raise ResponseCompleted on error/timeout, so without
        // this a failure would hang until our own timeout fires.
        void OnError(object? _, string message)
        {
            errorMessage = message;
            done.TrySetResult(false);
        }

        session.AssistantTextAvailable += OnText;
        session.ResponseCompleted += OnCompleted;
        session.ErrorOccurred += OnError;

        try
        {
            // Connect first (SendUserTextAsync throws if the transport isn't up) and keep the connect
            // outside the timeout window — an OpenAI cold connect can retry several times.
            await session.StartAsync(ct).ConfigureAwait(false);
            await session.SendUserTextAsync(fullPrompt, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var registration = timeoutCts.Token.Register(() =>
            {
                // The token passed into the backend has limited reach mid-turn on OpenAI; cancellation
                // must go through CancelTurn (and, ultimately, dispose in the finally block).
                try { session.CancelTurn(); } catch { /* best effort */ }
                done.TrySetCanceled(timeoutCts.Token);
            });
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            bool completed;
            try
            {
                completed = await done.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Notes generation timed out after {timeoutSeconds}s.");
            }

            if (!completed)
            {
                throw new InvalidOperationException(errorMessage ?? "Notes generation failed.");
            }

            string result;
            lock (buffer) result = buffer.ToString();
            result = result.Trim();
            if (result.Length == 0)
            {
                throw new InvalidOperationException("Notes generation returned no content.");
            }
            return result;
        }
        finally
        {
            session.AssistantTextAvailable -= OnText;
            session.ResponseCompleted -= OnCompleted;
            session.ErrorOccurred -= OnError;
            await session.DisposeAsync().ConfigureAwait(false); // dispose calls StopAsync internally
        }
    }

    /// <summary>
    /// Deep-clones the caller's config (JSON round-trip — never mutate the shared POCO) and forces the
    /// settings a bounded one-shot needs: realtime enabled (the factory hard-requires it), voice off
    /// (text-only output — no mic, no playback), no spoken replies, and no required context files
    /// (context is already embedded in the prompt; OpenAI would otherwise re-inject it).
    /// </summary>
    private static AppConfig CloneForGeneration(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config);
        var clone = JsonSerializer.Deserialize<AppConfig>(json)
            ?? throw new InvalidOperationException("Failed to clone configuration for notes generation.");

        clone.Agent.Realtime.Enabled = true;
        clone.Agent.Realtime.VoiceMode = "off";
        clone.Agent.Realtime.SpeakReplies = false;
        clone.Agent.RequiredContextFiles = new List<string>();
        return clone;
    }
}
