using LocalTranscriber.Agent;

namespace LocalTranscriber.Agent.Tests;

public class ResponsePolicyTests
{
    private static AgentSuggestion Make(
        AgentSuggestionType type = AgentSuggestionType.Risk,
        AgentSuggestionPriority priority = AgentSuggestionPriority.Medium,
        string title = "Test",
        double? confidence = 0.8)
        => new(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.Now, type, priority, title, "Message.", "fake", Confidence: confidence);

    [Fact]
    public void ModeOff_SuppressesEverything()
    {
        var policy = new AgentResponsePolicy();
        var decision = policy.Decide(Make(priority: AgentSuggestionPriority.Critical), AgentMode.Off);
        Assert.False(decision.Store);
        Assert.False(decision.Show);
        Assert.False(decision.Speak);
    }

    [Fact]
    public void SilentObserver_ShowsButNeverSpeaks()
    {
        var policy = new AgentResponsePolicy(new AgentPolicyOptions { VoiceEnabled = true });
        var decision = policy.Decide(Make(priority: AgentSuggestionPriority.Critical), AgentMode.SilentObserver);
        Assert.True(decision.Store);
        Assert.True(decision.Show);
        Assert.False(decision.Speak);
    }

    [Fact]
    public void PrivateCoach_SpeaksHighPriority_WhenVoiceEnabled()
    {
        var policy = new AgentResponsePolicy(new AgentPolicyOptions { VoiceEnabled = true });
        Assert.True(policy.Decide(Make(priority: AgentSuggestionPriority.High, title: "A"), AgentMode.PrivateCoach).Speak);
        Assert.False(policy.Decide(Make(priority: AgentSuggestionPriority.Medium, title: "B", type: AgentSuggestionType.Decision), AgentMode.PrivateCoach).Speak);
    }

    [Fact]
    public void PrivateCoach_NeverSpeaks_WhenVoiceDisabled()
    {
        var policy = new AgentResponsePolicy(new AgentPolicyOptions { VoiceEnabled = false });
        Assert.False(policy.Decide(Make(priority: AgentSuggestionPriority.Critical), AgentMode.PrivateCoach).Speak);
    }

    [Fact]
    public void HotkeyOnly_StoresWithoutShowing_ButAsksAlwaysShow()
    {
        var policy = new AgentResponsePolicy();
        var passive = policy.Decide(Make(title: "Passive"), AgentMode.HotkeyOnly);
        Assert.True(passive.Store);
        Assert.False(passive.Show);

        var ask = policy.Decide(Make(title: "Answer"), AgentMode.HotkeyOnly, isAskResponse: true);
        Assert.True(ask.Show);
    }

    [Fact]
    public void AskResponse_SpeaksWhenVoiceEnabled_SilentWhenDisabled()
    {
        var withVoice = new AgentResponsePolicy(new AgentPolicyOptions { VoiceEnabled = true });
        Assert.True(withVoice.Decide(Make(title: "Q"), AgentMode.HotkeyOnly, isAskResponse: true).Speak);

        var noVoice = new AgentResponsePolicy(new AgentPolicyOptions { VoiceEnabled = false });
        Assert.False(noVoice.Decide(Make(title: "Q"), AgentMode.HotkeyOnly, isAskResponse: true).Speak);
    }

    [Fact]
    public void InterruptWhenImportant_OnlyShowsHighAndCritical()
    {
        var policy = new AgentResponsePolicy();
        Assert.False(policy.Decide(Make(priority: AgentSuggestionPriority.Medium, title: "M"), AgentMode.InterruptWhenImportant).Show);
        Assert.True(policy.Decide(Make(priority: AgentSuggestionPriority.High, title: "H", type: AgentSuggestionType.Blocker), AgentMode.InterruptWhenImportant).Show);
    }

    [Fact]
    public void Duplicates_AreSuppressed()
    {
        var policy = new AgentResponsePolicy();
        Assert.True(policy.Decide(Make(title: "Same thing"), AgentMode.SilentObserver).Store);
        var second = policy.Decide(Make(title: "same THING"), AgentMode.SilentObserver);
        Assert.False(second.Store);
        Assert.Equal("duplicate", second.Reason);
    }

    [Fact]
    public void Cooldown_HidesRepeatedTypes_ButStillStores()
    {
        var now = DateTimeOffset.Parse("2026-07-09T10:00:00Z");
        var policy = new AgentResponsePolicy(new AgentPolicyOptions { CooldownPerType = TimeSpan.FromSeconds(45) });

        Assert.True(policy.Decide(Make(title: "First risk"), AgentMode.SilentObserver, now: now).Show);

        var during = policy.Decide(Make(title: "Second risk"), AgentMode.SilentObserver, now: now.AddSeconds(10));
        Assert.True(during.Store);
        Assert.False(during.Show);
        Assert.Equal("cooldown", during.Reason);

        Assert.True(policy.Decide(Make(title: "Third risk"), AgentMode.SilentObserver, now: now.AddSeconds(60)).Show);
    }

    [Fact]
    public void DismissedTitles_AreStoredButNotShown()
    {
        var policy = new AgentResponsePolicy();
        policy.Deduplicator.MarkDismissed("Annoying topic");
        var decision = policy.Decide(Make(title: "Annoying topic"), AgentMode.SilentObserver);
        Assert.True(decision.Store);
        Assert.False(decision.Show);
    }

    [Fact]
    public void LowConfidence_IsStoredButNotShown()
    {
        var policy = new AgentResponsePolicy(new AgentPolicyOptions { MinimumConfidenceToShow = 0.5 });
        var decision = policy.Decide(Make(confidence: 0.2), AgentMode.SilentObserver);
        Assert.True(decision.Store);
        Assert.False(decision.Show);
    }
}
