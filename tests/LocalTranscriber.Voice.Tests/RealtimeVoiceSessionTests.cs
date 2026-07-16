using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

public class RealtimeVoiceSessionTests
{
    private static RealtimeVoiceSession NewSession(
        RealtimeVoiceOptions options,
        FakeRealtimeTransport transport,
        FakeAudioOutput? audio = null,
        string transcript = "",
        string? recorderPath = null,
        IReadOnlyList<TranscriptEvent>? tailerEvents = null)
        => new(
            options,
            () => transport,
            audio ?? new FakeAudioOutput(),
            new FakeTranscriber(transcript),
            () => new FakeRecorder(recorderPath),
            new EmptyContextService(),
            tailerEvents is null ? null : () => new FakeTailer(tailerEvents),
            // Always inject a device-free mic so tests never touch real audio hardware.
            micStreamFactory: () => new FakeMicStream(0));

    [Fact]
    public async Task Start_ConfiguresRealtimeAudioSession_TurnDetectionNullForHybrid()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Model = "gpt-realtime-2.1-mini", Mode = RealtimeVoiceMode.Hybrid, Voice = "marin" };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Contains("model=gpt-realtime-2.1-mini", transport.LastUri!.Query);
        Assert.Equal("Bearer k", transport.LastHeaders!["Authorization"]);

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"output_modalities\":[\"audio\"]", sessionUpdate);
        Assert.Contains("\"voice\":\"marin\"", sessionUpdate);
        Assert.Contains("\"rate\":24000", sessionUpdate);
        Assert.Contains("\"turn_detection\":null", sessionUpdate);
    }

    [Fact]
    public async Task Continuous_ConfiguresServerVadTurnDetection()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Continuous, VadThreshold = 0.6, VadSilenceMs = 400 };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"type\":\"server_vad\"", sessionUpdate);
        Assert.Contains("\"threshold\":0.6", sessionUpdate);
        Assert.Contains("\"silence_duration_ms\":400", sessionUpdate);
    }

    [Fact]
    public async Task Grounding_InjectsNewLinesSilently_WithoutResponseCreate()
    {
        var transport = new FakeRealtimeTransport();
        var events = new[] { TestEvents.Line("We ship Friday.", 0), TestEvents.Line("Two blockers left.", 5) };
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid, TranscriptJsonlPath = "fake.jsonl" };
        await using var session = NewSession(options, transport, tailerEvents: events);

        await session.StartAsync();

        var sent = transport.SentSnapshot();
        Assert.Contains(sent, s => s.Contains("conversation.item.create")
            && s.Contains("We ship Friday.") && s.Contains("Two blockers left."));
        Assert.DoesNotContain(sent, s => s.Contains("response.create"));
    }

    [Fact]
    public async Task Hybrid_PushToTalk_SendsLocalTranscriptTextThenResponseCreate()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport, transcript: "Book the meeting room.", recorderPath: "held.wav");

        await session.StartAsync();
        await session.OnPushToTalkDownAsync();
        await session.OnPushToTalkUpAsync();

        var sent = transport.SentSnapshot().ToList();
        int itemIndex = sent.FindIndex(s => s.Contains("conversation.item.create") && s.Contains("Book the meeting room."));
        int responseIndex = sent.FindIndex(s => s.Contains("response.create"));
        Assert.True(itemIndex >= 0, "input_text item was sent");
        Assert.True(responseIndex > itemIndex, "response.create followed the input text");
    }

    [Fact]
    public async Task Hybrid_NoAudioFramesStreamed()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport, transcript: "Hello there.", recorderPath: "held.wav");

        await session.StartAsync();
        await session.OnPushToTalkDownAsync();
        await session.OnPushToTalkUpAsync();

        Assert.DoesNotContain(transport.SentSnapshot(), s => s.Contains("input_audio_buffer.append"));
    }

    [Fact]
    public async Task ReceivedAudioDelta_IsQueuedForPlayback()
    {
        var transport = new FakeRealtimeTransport();
        var audio = new FakeAudioOutput();
        byte[] payload = { 1, 2, 3, 4, 5, 6 };
        transport.EnqueueServerEvent($"{{\"type\":\"response.output_audio.delta\",\"delta\":\"{Convert.ToBase64String(payload)}\"}}");

        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport, audio);

        await session.StartAsync();
        await WaitUntilAsync(() => !audio.Enqueued.IsEmpty, TimeSpan.FromSeconds(2));

        Assert.True(audio.Enqueued.TryDequeue(out var bytes));
        Assert.Equal(payload, bytes);
        Assert.Equal(RealtimeVoiceState.Speaking, session.State);
    }

    [Fact]
    public async Task PushToTalk_StreamsMicAppends_ThenCommitsAndRequestsResponse()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.PushToTalk };
        await using var session = new RealtimeVoiceSession(
            options,
            () => transport,
            new FakeAudioOutput(),
            new FakeTranscriber(""),
            () => new FakeRecorder(null),
            new EmptyContextService(),
            tailerFactory: null,
            // 3 frames x 2000 bytes = 6000 > the 4800-byte (100ms @ 24kHz PCM16) commit floor,
            // so the turn streams enough audio to be committed rather than skipped as too-short.
            micStreamFactory: () => new FakeMicStream(frames: 3, frameBytes: 2000));

        await session.StartAsync();
        await session.OnPushToTalkDownAsync();
        await session.OnPushToTalkUpAsync();

        var sent = transport.SentSnapshot().ToList();
        Assert.Equal(3, sent.Count(s => s.Contains("input_audio_buffer.append")));
        int commitIndex = sent.FindIndex(s => s.Contains("input_audio_buffer.commit"));
        int responseIndex = sent.FindIndex(s => s.Contains("response.create"));
        Assert.True(commitIndex >= 0, "input buffer was committed");
        Assert.True(responseIndex > commitIndex, "response.create followed the commit");
    }

    [Fact]
    public async Task Continuous_BargeIn_StopsPlaybackAndTruncatesHeardAudio()
    {
        var transport = new FakeRealtimeTransport();
        var audio = new FakeAudioOutput { PlayedMilliseconds = 1234 };
        // Assistant is speaking (audio delta with an item id), then the user starts talking.
        transport.EnqueueServerEvent("{\"type\":\"response.output_audio.delta\",\"item_id\":\"item_42\",\"delta\":\"AAAA\"}");
        transport.EnqueueServerEvent("{\"type\":\"input_audio_buffer.speech_started\"}");

        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Continuous };
        await using var session = new RealtimeVoiceSession(
            options,
            () => transport,
            audio,
            new FakeTranscriber(""),
            () => new FakeRecorder(null),
            new EmptyContextService(),
            tailerFactory: null,
            micStreamFactory: () => new FakeMicStream(frames: 0));

        await session.StartAsync();
        await WaitUntilAsync(
            () => audio.StopCalls > 0 && transport.SentSnapshot().Any(s => s.Contains("conversation.item.truncate")),
            TimeSpan.FromSeconds(2));

        var sent = transport.SentSnapshot();
        Assert.Contains(sent, s => s.Contains("response.cancel"));
        string truncate = Assert.Single(sent, s => s.Contains("conversation.item.truncate"));
        Assert.Contains("\"item_id\":\"item_42\"", truncate);
        Assert.Contains("\"content_index\":0", truncate);
        Assert.Contains("\"audio_end_ms\":1234", truncate);
    }

    [Fact]
    public async Task Start_RetriesInitialConnect_WhenFirstAttemptFails()
    {
        // Reproduces the "have to start voice twice" symptom: the first handshake is reset
        // (enterprise TLS inspection / cold connect) and the retry succeeds.
        var transport = new FakeRealtimeTransport { FailFirstNConnects = 1 };
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid, MaxReconnectAttempts = 3 };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        Assert.Equal(2, transport.ConnectCalls);
        Assert.Contains(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Equal(RealtimeVoiceState.Ready, session.State);
    }

    [Fact]
    public async Task Start_InitialConnect_PropagatesWhenAllAttemptsFail()
    {
        var transport = new FakeRealtimeTransport { FailFirstNConnects = 5 };
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid, MaxReconnectAttempts = 2 };
        await using var session = NewSession(options, transport);

        await Assert.ThrowsAsync<IOException>(() => session.StartAsync());
        Assert.Equal(2, transport.ConnectCalls);
    }

    [Fact]
    public async Task SendUserText_SendsInputTextItemThenResponseCreate_NoAudio()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport);

        await session.StartAsync();
        await session.SendUserTextAsync("What did Joe commit to?");

        var sent = transport.SentSnapshot().ToList();
        int itemIndex = sent.FindIndex(s => s.Contains("conversation.item.create") && s.Contains("What did Joe commit to?"));
        int responseIndex = sent.FindIndex(s => s.Contains("response.create"));
        Assert.True(itemIndex >= 0, "typed text was sent as input_text");
        Assert.True(responseIndex > itemIndex, "response.create followed the typed text");
        Assert.DoesNotContain(sent, s => s.Contains("input_audio_buffer.append"));
        Assert.Equal(RealtimeVoiceState.Thinking, session.State);
    }

    [Fact]
    public async Task SendUserText_Throws_WhenNotStarted()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendUserTextAsync("hello"));
    }

    [Fact]
    public async Task Hybrid_PushToTalkUp_RaisesUserTextCommitted_WithLocalSttText()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport, transcript: "Book the meeting room.", recorderPath: "held.wav");

        string? committed = null;
        session.UserTextCommitted += (_, text) => committed = text;

        await session.StartAsync();
        await session.OnPushToTalkDownAsync();
        await session.OnPushToTalkUpAsync();

        Assert.Equal("Book the meeting room.", committed);
    }

    [Fact]
    public async Task ResponseDone_RaisesResponseCompleted()
    {
        var transport = new FakeRealtimeTransport();
        transport.EnqueueServerEvent("{\"type\":\"response.done\"}");
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid };
        await using var session = NewSession(options, transport);

        int completed = 0;
        session.ResponseCompleted += (_, _) => Interlocked.Increment(ref completed);

        await session.StartAsync();
        await WaitUntilAsync(() => completed > 0, TimeSpan.FromSeconds(2));

        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Tools_AreIncludedInSessionUpdate()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions
        {
            ApiKey = "k",
            Mode = RealtimeVoiceMode.Hybrid,
            Tools = new[]
            {
                new RealtimeToolDefinition("update_notes", "Add a note.",
                    new { type = "object", properties = new { text = new { type = "string" } } })
            },
            ToolHandler = _ => Task.FromResult("{\"ok\":true}")
        };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"tools\":", sessionUpdate);
        Assert.Contains("\"name\":\"update_notes\"", sessionUpdate);
        Assert.Contains("\"tool_choice\":\"auto\"", sessionUpdate);
    }

    [Fact]
    public async Task FunctionCall_InvokesHandler_SendsOutputThenResponseCreate()
    {
        var transport = new FakeRealtimeTransport();
        transport.EnqueueServerEvent(
            "{\"type\":\"response.function_call_arguments.done\",\"call_id\":\"call_7\",\"name\":\"update_notes\",\"arguments\":\"{\\\"section\\\":\\\"risks\\\",\\\"text\\\":\\\"Staging down\\\"}\"}");

        RealtimeToolCall? received = null;
        var options = new RealtimeVoiceOptions
        {
            ApiKey = "k",
            Mode = RealtimeVoiceMode.Hybrid,
            Tools = new[] { new RealtimeToolDefinition("update_notes", "Add a note.", new { type = "object" }) },
            ToolHandler = call =>
            {
                received = call;
                return Task.FromResult("{\"ok\":true}");
            }
        };
        await using var session = NewSession(options, transport);

        await session.StartAsync();
        await WaitUntilAsync(
            () => transport.SentSnapshot().Any(s => s.Contains("function_call_output")),
            TimeSpan.FromSeconds(2));

        Assert.NotNull(received);
        Assert.Equal("update_notes", received!.Name);
        Assert.Equal("call_7", received.CallId);
        Assert.Contains("Staging down", received.ArgumentsJson);

        var sent = transport.SentSnapshot().ToList();
        int outputIndex = sent.FindIndex(s => s.Contains("function_call_output") && s.Contains("call_7"));
        int responseIndex = sent.FindLastIndex(s => s.Contains("response.create"));
        Assert.True(outputIndex >= 0, "function_call_output was sent");
        Assert.True(responseIndex > outputIndex, "response.create followed the tool output");
    }

    [Fact]
    public void Factory_OffAndConsentGates()
    {
        var noSecrets = new SecretsService(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N") + ".json"));

        // Off is no longer a blocker (text chat); the remaining gates apply in order.
        var config = new AppConfig();
        config.Agent.Realtime.VoiceMode = "off";
        config.Agent.Realtime.ApiKeyEnvironmentVariable = "LT_TEST_MISSING_KEY_" + Guid.NewGuid().ToString("N");
        var disabledResult = RealtimeVoiceFactory.Create(config, noSecrets);
        Assert.Null(disabledResult.Session);
        Assert.Contains("enabled", disabledResult.Notice!, StringComparison.OrdinalIgnoreCase);

        config.Agent.Realtime.Enabled = true;
        var noKeyResult = RealtimeVoiceFactory.Create(config, noSecrets);
        Assert.Null(noKeyResult.Session);
        Assert.Contains("no API key", noKeyResult.Notice!, StringComparison.OrdinalIgnoreCase);

        config.Agent.Realtime.VoiceMode = "continuous";
        config.Agent.Realtime.SendAudio = false;
        var consentResult = RealtimeVoiceFactory.Create(config, noSecrets);
        Assert.Null(consentResult.Session);
        Assert.Contains("sendAudio", consentResult.Notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_Off_WithKeyAndEnabled_ReturnsTextChatSession()
    {
        string secretsPath = Path.Combine(Path.GetTempPath(), "secrets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var secrets = new SecretsService(secretsPath);
            secrets.SaveOpenAIKey("test-key");

            var config = new AppConfig();
            config.Agent.Realtime.VoiceMode = "off";
            config.Agent.Realtime.Enabled = true;
            config.Agent.Realtime.ApiKeyEnvironmentVariable = "LT_TEST_MISSING_KEY_" + Guid.NewGuid().ToString("N");

            var result = RealtimeVoiceFactory.Create(config, secrets);
            Assert.NotNull(result.Session);
            Assert.Null(result.Notice);
        }
        finally
        {
            File.Delete(secretsPath);
        }
    }

    [Fact]
    public async Task Off_SessionUpdate_UsesTextModality_NoVoiceOutput()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off, Voice = "marin" };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"output_modalities\":[\"text\"]", sessionUpdate);
        Assert.DoesNotContain("\"voice\":", sessionUpdate);
    }

    [Fact]
    public async Task Off_SendUserText_SendsInputTextItemThenResponseCreate()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off };
        await using var session = NewSession(options, transport);

        await session.StartAsync();
        await session.SendUserTextAsync("Summarise the blockers.");

        var sent = transport.SentSnapshot().ToList();
        int itemIndex = sent.FindIndex(s => s.Contains("conversation.item.create") && s.Contains("Summarise the blockers."));
        int responseIndex = sent.FindIndex(s => s.Contains("response.create"));
        Assert.True(itemIndex >= 0, "typed text was sent as input_text");
        Assert.True(responseIndex > itemIndex, "response.create followed the typed text");
        Assert.Equal(RealtimeVoiceState.Thinking, session.State);
    }

    [Fact]
    public async Task Off_PushToTalk_NoOps()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off };
        await using var session = NewSession(options, transport, transcript: "should never be used", recorderPath: "held.wav");

        await session.StartAsync();
        await session.OnPushToTalkDownAsync();
        await session.OnPushToTalkUpAsync();

        var sent = transport.SentSnapshot();
        Assert.DoesNotContain(sent, s => s.Contains("input_audio_buffer"));
        Assert.DoesNotContain(sent, s => s.Contains("should never be used"));
        Assert.Equal(RealtimeVoiceState.Ready, session.State);
    }

    [Fact]
    public async Task Off_Grounding_InitialTranscriptStillSeededSilently()
    {
        var transport = new FakeRealtimeTransport();
        var events = new[] { TestEvents.Line("We ship Friday.", 0), TestEvents.Line("Two blockers left.", 5) };
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off, TranscriptJsonlPath = "fake.jsonl" };
        await using var session = NewSession(options, transport, tailerEvents: events);

        await session.StartAsync();

        var sent = transport.SentSnapshot();
        Assert.Contains(sent, s => s.Contains("conversation.item.create")
            && s.Contains("We ship Friday.") && s.Contains("Two blockers left."));
        Assert.DoesNotContain(sent, s => s.Contains("response.create"));
    }

    [Fact]
    public async Task OutputTextDelta_RaisesAssistantText_WithoutSpeaking()
    {
        var transport = new FakeRealtimeTransport();
        transport.EnqueueServerEvent("{\"type\":\"response.output_text.delta\",\"delta\":\"Hi there\"}");
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off };
        await using var session = NewSession(options, transport);

        string? received = null;
        session.AssistantTextAvailable += (_, text) => received = text;

        await session.StartAsync();
        await WaitUntilAsync(() => received is not null, TimeSpan.FromSeconds(2));

        Assert.Equal("Hi there", received);
        Assert.NotEqual(RealtimeVoiceState.Speaking, session.State);
    }

    [Fact]
    public async Task Hybrid_SpeakRepliesFalse_UsesTextModality()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Hybrid, SpeakReplies = false };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"output_modalities\":[\"text\"]", sessionUpdate);
    }

    [Fact]
    public async Task Continuous_SpeakRepliesFalse_KeepsAudioModality()
    {
        // Continuous barge-in depends on audio deltas + playback truncation, so it keeps audio
        // output even when nothing is played locally.
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Continuous, SpeakReplies = false };
        await using var session = NewSession(options, transport);

        await session.StartAsync();

        string sessionUpdate = Assert.Single(transport.SentSnapshot(), s => s.Contains("session.update"));
        Assert.Contains("\"output_modalities\":[\"audio\"]", sessionUpdate);
    }

    [Fact]
    public async Task CancelTurn_WhenThinking_SendsResponseCancel_AndReturnsReady()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off };
        await using var session = NewSession(options, transport);

        await session.StartAsync();
        await session.SendUserTextAsync("A very long question.");
        Assert.Equal(RealtimeVoiceState.Thinking, session.State);

        session.CancelTurn();
        await WaitUntilAsync(
            () => transport.SentSnapshot().Any(s => s.Contains("response.cancel")) && session.State == RealtimeVoiceState.Ready,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelTurn_WhenReady_SendsNothing()
    {
        var transport = new FakeRealtimeTransport();
        var options = new RealtimeVoiceOptions { ApiKey = "k", Mode = RealtimeVoiceMode.Off };
        await using var session = NewSession(options, transport);

        await session.StartAsync();
        Assert.Equal(RealtimeVoiceState.Ready, session.State);

        session.CancelTurn();
        await Task.Delay(150);

        Assert.DoesNotContain(transport.SentSnapshot(), s => s.Contains("response.cancel"));
        Assert.Equal(RealtimeVoiceState.Ready, session.State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
