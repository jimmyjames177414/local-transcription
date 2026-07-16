namespace LocalTranscriber.App.ViewModels;

/// <summary>Controls how far a speaker rename applies in the transcript.</summary>
public enum RenameScope
{
    /// <summary>Rename every occurrence of this speaker across the session (existing behavior).</summary>
    All,
    /// <summary>Rename only the single clicked line — no enrollment, no global change.</summary>
    ThisOne
}

/// <summary>Input to the rename dialog: who is being renamed and how many times they appear.</summary>
public sealed record SpeakerRenameRequest(
    string CurrentName,
    IReadOnlyList<string> Suggestions,
    int OccurrenceCount);

/// <summary>Output from the rename dialog: the new name and which lines to update.</summary>
public sealed record SpeakerRenameResult(string NewName, RenameScope Scope);
