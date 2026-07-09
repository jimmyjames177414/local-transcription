namespace LocalTranscriber.Shared;

public sealed record SpeakerLabel(
    string SpeakerId,
    string DisplayName,
    bool IsKnown,
    double? Confidence = null
);
