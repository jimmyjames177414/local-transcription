using System.Text.Json;

namespace LocalTranscriber.Voice;

public enum RealtimeVoiceEventKind
{
    Other,
    OutputAudioDelta,
    AudioTranscriptDelta,
    AudioTranscriptDone,
    SpeechStarted,
    ResponseDone,
    FunctionCallDone,
    Error,
    InputAudioTranscriptionDone
}

/// <summary>
/// A mapped server event. <see cref="Text"/> carries base64 PCM for audio deltas, the caption
/// text for transcript events, the arguments JSON for function calls, and the message for
/// errors; null otherwise. <see cref="ItemId"/> is the conversation item id (present on audio
/// deltas) needed for barge-in truncation. <see cref="CallId"/>/<see cref="ToolName"/> are set
/// on function-call events.
/// </summary>
public sealed record RealtimeVoiceServerEvent(
    RealtimeVoiceEventKind Kind,
    string? Text,
    string? ItemId = null,
    string? CallId = null,
    string? ToolName = null);

/// <summary>Maps raw OpenAI Realtime (GA) server events to the kinds the voice session handles.</summary>
public static class RealtimeVoiceEventMapper
{
    public static RealtimeVoiceServerEvent Map(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "response.output_audio.delta":
                case "response.audio.delta":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.OutputAudioDelta, GetString(root, "delta"), GetString(root, "item_id"));

                case "response.output_audio_transcript.delta":
                case "response.audio_transcript.delta":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.AudioTranscriptDelta, GetString(root, "delta"));

                case "response.output_audio_transcript.done":
                case "response.audio_transcript.done":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.AudioTranscriptDone,
                        GetString(root, "transcript") ?? GetString(root, "text"));

                case "conversation.item.input_audio_transcription.completed":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.InputAudioTranscriptionDone,
                        GetString(root, "transcript"));

                case "input_audio_buffer.speech_started":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.SpeechStarted, null);

                case "response.function_call_arguments.done":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.FunctionCallDone,
                        GetString(root, "arguments"),
                        CallId: GetString(root, "call_id"),
                        ToolName: GetString(root, "name"));

                case "response.done":
                case "response.completed":
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.ResponseDone, null);

                case "error":
                    string? message = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : json;
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.Error, message);

                default:
                    return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.Other, null);
            }
        }
        catch (JsonException)
        {
            return new RealtimeVoiceServerEvent(RealtimeVoiceEventKind.Other, null);
        }
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
