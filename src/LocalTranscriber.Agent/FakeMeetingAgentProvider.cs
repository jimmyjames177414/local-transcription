using System.Text.RegularExpressions;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

/// <summary>
/// Deterministic offline provider: simple keyword rules over the rolling window.
/// No network. Used for tests, offline mode, and as the default provider.
/// </summary>
public sealed partial class FakeMeetingAgentProvider : IMeetingAgentProvider
{
    public string Name => "fake";

    [GeneratedRegex(@"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|next week|end of (?:the )?(?:week|month|quarter)|q[1-4])\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateLikeRegex();

    public Task<AgentProviderResult> AnalyzeAsync(AgentProviderRequest request, CancellationToken cancellationToken = default)
    {
        var suggestions = new List<AgentSuggestion>();

        if (request.UserQuestion is not null)
        {
            suggestions.Add(Make(request, AgentSuggestionType.SuggestedResponse, AgentSuggestionPriority.Medium,
                "Fake answer",
                $"Offline fake provider cannot really answer '{request.UserQuestion}'. Configure the OpenAI provider for real answers.",
                null));
            return Task.FromResult(new AgentProviderResult(suggestions, null));
        }

        foreach (var e in request.WindowEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = e.Text.ToLowerInvariant();

            if (text.Contains("deploy"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.ActionItem, AgentSuggestionPriority.Medium,
                    "Deployment mentioned",
                    $"{e.Speaker.DisplayName} mentioned deployment: \"{e.Text}\". Confirm owner and date.", e));
            }

            if (text.Contains("blocked") || text.Contains("blocker"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.Blocker, AgentSuggestionPriority.High,
                    "Blocker raised",
                    $"{e.Speaker.DisplayName} raised a blocker: \"{e.Text}\".", e));
            }

            if (text.Contains("who owns") || text.Contains("who is responsible"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.QuestionToAsk, AgentSuggestionPriority.Medium,
                    "Ownership question",
                    $"Ownership is unclear: \"{e.Text}\". Suggest naming an owner before moving on.", e));
            }

            if (text.Contains("risk"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.Risk, AgentSuggestionPriority.High,
                    "Risk mentioned",
                    $"{e.Speaker.DisplayName} flagged a risk: \"{e.Text}\".", e));
            }

            if (text.Contains("decision") || text.Contains("we decided") || text.Contains("let's go with"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.Decision, AgentSuggestionPriority.Medium,
                    "Decision detected",
                    $"Possible decision: \"{e.Text}\". Capture it in the decision log.", e));
            }

            if (text.Contains("follow up") || text.Contains("follow-up"))
            {
                suggestions.Add(Make(request, AgentSuggestionType.ActionItem, AgentSuggestionPriority.Low,
                    "Follow-up mentioned",
                    $"Follow-up mentioned by {e.Speaker.DisplayName}: \"{e.Text}\".", e));
            }

            if (DateLikeRegex().IsMatch(text))
            {
                suggestions.Add(Make(request, AgentSuggestionType.ActionItem, AgentSuggestionPriority.Low,
                    "Date commitment",
                    $"A date-like commitment was mentioned: \"{e.Text}\". Verify it lands on the calendar.", e));
            }
        }

        string? summaryUpdate = request.WindowEvents.Count > 0
            ? $"{request.WindowEvents.Count} recent utterances; last speaker {request.WindowEvents[^1].Speaker.DisplayName}."
            : null;

        return Task.FromResult(new AgentProviderResult(suggestions, summaryUpdate));
    }

    private static AgentSuggestion Make(AgentProviderRequest request, AgentSuggestionType type, AgentSuggestionPriority priority, string title, string message, TranscriptEvent? related)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: request.SessionId,
            CreatedAt: DateTimeOffset.Now,
            Type: type,
            Priority: priority,
            Title: title,
            Message: message,
            Source: "fake",
            RelatedSpeaker: related?.Speaker.DisplayName,
            RelatedTranscriptEventId: related?.Id,
            Confidence: 0.5);
}
