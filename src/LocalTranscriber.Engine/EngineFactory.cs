using LocalTranscriber.AI;
using LocalTranscriber.Shared;
using LocalTranscriber.Speakers;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Engine;

/// <summary>
/// Builds the fully wired real engine (whisper + sherpa-onnx + SQLite) from config.
/// </summary>
public static class EngineFactory
{
    public static RealTranscriptionEngine CreateReal(AppConfig config)
    {
        var db = new SqliteDatabase(config.DatabasePath);
        var speakerStore = new SqliteKnownSpeakerStore(db);
        var embeddingStore = new SqliteSpeakerEmbeddingStore(db);
        var aliasStore = new SqliteSpeakerAliasStore(db);

        return new RealTranscriptionEngine(
            transcription: new WhisperCppTranscriptionService(),
            diarization: new SherpaOnnxDiarizationService(),
            embedding: new SherpaOnnxEmbeddingService(),
            recognition: new SpeakerRecognitionService(speakerStore, embeddingStore, new SpeakerMemoryOptions
            {
                MatchThreshold = config.SpeakerMatchThreshold,
                UncertainThreshold = config.SpeakerUncertainThreshold
            }),
            sessionStore: new SqliteSessionStore(db),
            eventStore: new SqliteTranscriptEventStore(db),
            speakerStore: speakerStore,
            aliasStore: aliasStore,
            minutesExport: config.MinutesExport,
            notesFolder: config.Agent.AgentOutputFolder);
    }

    public static TranscriptionSessionOptions CreateSessionOptions(AppConfig config, string? outputFolder = null, bool? mic = null, bool? system = null)
    {
        string folder = outputFolder ?? config.TranscriptFolder;
        Directory.CreateDirectory(folder);
        string baseName = $"session-{DateTime.Now:yyyyMMdd-HHmmss}";

        return new TranscriptionSessionOptions
        {
            OutputTextPath = Path.Combine(folder, baseName + ".txt"),
            OutputJsonlPath = Path.Combine(folder, baseName + ".jsonl"),
            EnableMicrophone = mic ?? config.EnableMicCapture,
            EnableSystemAudio = system ?? config.EnableSystemCapture,
            MicrophoneSpeakerName = config.DefaultMicSpeakerName,
            WhisperModelPath = config.WhisperModelPath,
            SpeakerModelDir = config.SpeakerModelPath,
            ChunkSeconds = config.ChunkSeconds,
            OverlapMs = config.OverlapMs,
            FilterNonSpeech = config.FilterNonSpeech
        };
    }
}
