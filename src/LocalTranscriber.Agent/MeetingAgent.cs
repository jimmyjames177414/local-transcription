using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LocalTranscriber.Context;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

/// <summary>
/// Consumes live transcript events (via the tailer), keeps a rolling window and a
/// running summary, and periodically asks the configured provider for suggestions.
/// Suggestions flow to the stream and to any configured sink.
/// </summary>
public sealed class MeetingAgent : IMeetingAgent, IAsyncDisposable
{
    private readonly IMeetingAgentProvider _provider;
    private readonly IContextPackService _contextService;
    private readonly ITranscriptEventTailer _tailer;
    private readonly IAgentSuggestionSink? _sink;
    private readonly AgentResponsePolicy _policy;
    private readonly IAgentVoiceOutput _voice;

    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly Channel<AgentSuggestion> _suggestions = Channel.CreateUnbounded<AgentSuggestion>();
    private readonly TranscriptEventDeduplicator _dedup = new();

    private MeetingAgentOptions? _options;
    private CancellationTokenSource? _cts;
    private Task? _tailLoop;
    private Task? _analyzeLoop;
    private RollingTranscriptWindow? _window;
    private readonly MeetingRunningSummary _summary = new();
    private ContextPack _contextPack = ContextPack.Empty;
    private ContextComposer? _composer;
    private volatile bool _hasNewEvents;

    private MeetingAgentState _state = MeetingAgentState.NotStarted;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastAnalysisAt;
    private long _eventsSeen;
    private long _suggestionsEmitted;
    private string? _error;

    public MeetingAgent(
        IMeetingAgentProvider provider,
        IContextPackService? contextService = null,
        ITranscriptEventTailer? tailer = null,
        IAgentSuggestionSink? sink = null,
        AgentResponsePolicy? policy = null,
        IAgentVoiceOutput? voice = null)
    {
        _provider = provider;
        _contextService = contextService ?? new MarkdownContextPackService();
        _tailer = tailer ?? new TranscriptEventTailer();
        _sink = sink;
        _policy = policy ?? new AgentResponsePolicy();
        _voice = voice ?? new NoOpAgentVoiceOutput();
    }

    public AgentResponsePolicy Policy => _policy;

    public async Task StartAsync(MeetingAgentOptions options, CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is MeetingAgentState.Running or MeetingAgentState.Starting)
            {
                throw new InvalidOperationException($"Agent is already active (state: {_state}).");
            }

            if (options.Mode == AgentMode.Off)
            {
                throw new InvalidOperationException("Agent mode is Off. Choose a mode before starting.");
            }

            _state = MeetingAgentState.Starting;
            _options = options;
            _error = null;
            _eventsSeen = 0;
            _suggestionsEmitted = 0;
            _hasNewEvents = false;
            _window = new RollingTranscriptWindow(TimeSpan.FromMinutes(options.RollingWindowMinutes), options.MaxTranscriptEventsPerPrompt);

            Directory.CreateDirectory(options.AgentOutputFolder);

            var contextOptions = new ContextPackOptions
            {
                ContextFolder = options.ContextFolder,
                MaxTotalCharacters = options.MaxContextCharacters,
                RequiredFiles = options.RequiredContextFiles
            };
            _contextPack = await _contextService.LoadAsync(contextOptions, cancellationToken).ConfigureAwait(false);
            _composer = new ContextComposer(_contextService, contextOptions);

            foreach (var warning in _contextPack.Warnings)
            {
                AppLog.Warn("agent", $"context: {warning}");
            }

            _cts = new CancellationTokenSource();
            _startedAt = DateTimeOffset.Now;
            _tailLoop = Task.Run(() => TailLoopAsync(options, _cts.Token), CancellationToken.None);
            _analyzeLoop = Task.Run(() => AnalyzeLoopAsync(options, _cts.Token), CancellationToken.None);
            _state = MeetingAgentState.Running;
            AppLog.Info("agent", $"Started (provider: {_provider.Name}, mode: {options.Mode}, transcript: {options.TranscriptJsonlPath})");
        }
        catch
        {
            if (_state == MeetingAgentState.Starting)
            {
                _state = MeetingAgentState.Faulted;
            }
            throw;
        }
        finally
        {
            _control.Release();
        }
    }

    private async Task TailLoopAsync(MeetingAgentOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var tailOptions = new TranscriptTailOptions
            {
                JsonlPath = options.TranscriptJsonlPath,
                FromStart = options.TailFromStart,
                CheckpointPath = Path.Combine(options.AgentOutputFolder, "tailer-checkpoint.json")
            };

            await foreach (var e in _tailer.TailAsync(tailOptions, cancellationToken).ConfigureAwait(false))
            {
                if (!_dedup.TryAdd(e))
                {
                    continue;
                }

                _window?.Add(e);
                _lastEventAt = e.Timestamp;
                Interlocked.Increment(ref _eventsSeen);
                _hasNewEvents = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state = MeetingAgentState.Faulted;
            _error = $"Tailer failed: {ex.Message}";
            AppLog.Error("agent", _error);
        }
    }

    private async Task AnalyzeLoopAsync(MeetingAgentOptions options, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.SuggestionIntervalSeconds), cancellationToken).ConfigureAwait(false);
                if (!_hasNewEvents || options.Mode == AgentMode.Off)
                {
                    continue;
                }

                _hasNewEvents = false;
                await RunAnalysisAsync(userQuestion: null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state = MeetingAgentState.Faulted;
            _error = $"Analysis loop failed: {ex.Message}";
            AppLog.Error("agent", _error);
        }
    }

    private async Task<IReadOnlyList<AgentSuggestion>> RunAnalysisAsync(string? userQuestion, CancellationToken cancellationToken)
    {
        var emitted = new List<AgentSuggestion>();
        var window = _window?.Snapshot() ?? Array.Empty<TranscriptEvent>();
        if (window.Count == 0 && userQuestion is null)
        {
            return emitted;
        }

        // Retrieval-composed context: required summary + chunks relevant to the recent talk.
        string contextText = _contextPack.CombinedText;
        if (_composer is not null)
        {
            try
            {
                string query = string.Join(" ", window.TakeLast(20).Select(e => e.Text)) + " " + (userQuestion ?? "");
                string composed = await _composer.ComposeAsync(query, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(composed))
                {
                    contextText = composed;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("agent", $"Context retrieval failed, using full pack: {ex.Message}");
            }
        }

        var request = new AgentProviderRequest
        {
            WindowEvents = window,
            ContextSummary = contextText,
            RunningSummary = _summary.Current,
            KnownSpeakers = window.Select(e => e.Speaker.DisplayName).Distinct().ToArray(),
            Mode = _options?.Mode ?? AgentMode.SilentObserver,
            SessionId = _options?.SessionId,
            UserQuestion = userQuestion
        };

        AgentProviderResult result;
        try
        {
            result = await _provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("agent", $"Provider '{_provider.Name}' analysis failed: {ex.Message}");
            return emitted;
        }

        _lastAnalysisAt = DateTimeOffset.Now;
        _summary.Update(result.RunningSummaryUpdate);

        var mode = _options?.Mode ?? AgentMode.SilentObserver;
        foreach (var suggestion in result.Suggestions)
        {
            var decision = _policy.Decide(suggestion, mode, isAskResponse: userQuestion is not null);
            if (!decision.Store)
            {
                continue;
            }

            if (_sink is not null)
            {
                await _sink.WriteAsync(suggestion, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _suggestionsEmitted);
            emitted.Add(suggestion);

            if (decision.Show)
            {
                _suggestions.Writer.TryWrite(suggestion);
            }

            if (decision.Speak)
            {
                _ = _voice.SpeakAsync($"{suggestion.Priority} {suggestion.Type}. {suggestion.Title}. {suggestion.Message}", cancellationToken);
            }
        }

        if (_sink is not null)
        {
            await _sink.UpdateSummaryAsync(_options?.SessionId, _summary.Current, cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    /// <summary>On-demand question (HotkeyOnly / Ask). Runs an immediate analysis pass and returns what it produced.</summary>
    public Task<IReadOnlyList<AgentSuggestion>> AskAsync(string question, CancellationToken cancellationToken = default)
        => RunAnalysisAsync(question, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is MeetingAgentState.NotStarted or MeetingAgentState.Stopped)
            {
                _state = MeetingAgentState.Stopped;
                return;
            }

            _state = MeetingAgentState.Stopping;
            _cts?.Cancel();

            foreach (var task in new[] { _tailLoop, _analyzeLoop })
            {
                if (task is not null)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }

            _cts?.Dispose();
            _cts = null;
            _tailLoop = null;
            _analyzeLoop = null;

            if (_state != MeetingAgentState.Faulted)
            {
                _state = MeetingAgentState.Stopped;
            }
            AppLog.Info("agent", $"Stopped ({Interlocked.Read(ref _suggestionsEmitted)} suggestions)");
        }
        finally
        {
            _control.Release();
        }
    }

    public Task<MeetingAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MeetingAgentStatus
        {
            State = _state,
            Mode = _options?.Mode ?? AgentMode.SilentObserver,
            Provider = _provider.Name,
            TranscriptPath = _options?.TranscriptJsonlPath,
            StartedAt = _startedAt,
            LastEventAt = _lastEventAt,
            LastAnalysisAt = _lastAnalysisAt,
            EventsSeen = Interlocked.Read(ref _eventsSeen),
            SuggestionsEmitted = Interlocked.Read(ref _suggestionsEmitted),
            RunningSummary = _summary.Current,
            Error = _error
        });

    public async IAsyncEnumerable<AgentSuggestion> StreamSuggestionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _suggestions.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_suggestions.Reader.TryRead(out var suggestion))
            {
                yield return suggestion;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _suggestions.Writer.TryComplete();
        _control.Dispose();
        _voice.Dispose();
        await _tailer.DisposeAsync().ConfigureAwait(false);
    }
}
