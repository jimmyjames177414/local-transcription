namespace LocalTranscriber.Engine;

public enum TranscriptionSessionState
{
    NotStarted,
    Starting,
    Recording,
    Paused,
    Stopping,
    Stopped,
    Faulted
}
