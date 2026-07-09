---
title: Fix agent showing fake provider and duplicate Ask responses
version: 1.0
date_created: 2026-07-09
last_updated: 2026-07-09
confidence_level: 9
---
# Implementation Plan: Fix agent "fake provider" fallback and duplicate Ask responses

## Goal

**Story Goal**: The Agent tab uses the provider the user selects (no silent fake fallback when a real
provider is intended), and each "Ask" produces exactly one answer in the suggestions list.

**Deliverable**: Fixes in [src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs](../../src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs)
plus a one-line config correction in [output/config.json](../../output/config.json).

**Success Definition**: With a valid key configured, selecting a provider and clicking **Ask** yields a
single real answer (not the "Offline fake provider cannot really answer…" text, and not shown twice).

## Why

- Two visible defects in the screenshot: every Ask answer appears twice, and answers are the offline
  fake text even though the user configured OpenAI.
- Both undermine trust in the agent sidecar and make live-meeting use unusable.

## What

Two independent root causes, diagnosed from the logs and code:

### Root cause 1 — silent fake fallback (the "it's fake" symptom)

[output/config.json](../../output/config.json) has `agent.provider = "realtime"` but
`agent.realtime.enabled = false` (only `agent.openAI.enabled = true`).
[AgentProviderFactory.Create](../../src/LocalTranscriber.Agent/AgentProviderFactory.cs) gates the realtime
provider behind `realtime.enabled`, so it returns `FakeMeetingAgentProvider` with the notice
*"Realtime provider is not enabled (agent.realtime.enabled=false). Using the offline fake provider."*
Log confirms: `12:47:37 [INFO] agent: Started (provider: fake, mode: PrivateCoach …)`.

The UI makes this easy to hit: [SelectedProvider](../../src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs)
persists `agent.provider` when the dropdown changes but never flips the matching `enabled` gate, so the
picker and the gate silently disagree and the fallback notice is easy to miss.

### Root cause 2 — duplicate Ask responses

While the agent is running, [ConsumeSuggestionsAsync](../../src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs)
continuously drains the agent's suggestion channel (`StreamSuggestionsAsync`) into the `Suggestions` list.
`AskAsync` → `RunAnalysisAsync(question)` writes each shown suggestion into **that same channel**
(`_suggestions.Writer.TryWrite`) *and* returns the list. The view-model's
[AskAsync](../../src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs) then **also** inserts the
returned answers into `Suggestions`. Net effect while the live agent is running: each answer is inserted
twice. (The one-shot path where `_agent is null` has no channel consumer, so it still needs the direct insert.)

### Success Criteria

- [ ] Clicking **Ask** with a running agent inserts each answer exactly once.
- [ ] Selecting `openai`/`realtime` in the dropdown either enables that provider or clearly surfaces the
      fallback notice, so the user is never silently downgraded to fake without knowing.
- [ ] With a valid key, Ask returns real model answers rather than the fake placeholder text.

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs
  why: AskAsync double-insert (~line 63-101), ConsumeSuggestionsAsync (~line 257-276), SelectedProvider (~line 136-144)
  pattern: PostToUi(() => Suggestions.Insert(0, ...)) is the single UI insertion point
  gotcha: The channel consumer and the Ask return value both feed Suggestions when the agent is running

- file: src/LocalTranscriber.Agent/MeetingAgent.cs
  why: RunAnalysisAsync writes decision.Show suggestions to _suggestions channel AND returns them (~line 260-274); AskAsync just calls RunAnalysisAsync (~line 277)
  pattern: channel is the live display path; return value is for callers without a consumer (one-shot/CLI/MCP)

- file: src/LocalTranscriber.Agent/AgentProviderFactory.cs
  why: provider gating — realtime/openai require the matching enabled flag + a resolvable key, else Fallback→fake
  gotcha: provider string and enabled flag are independent; a mismatch silently yields fake

- docfile: docs/REALTIME_PROVIDER.md
  section: Enable — confirms realtime needs agent.realtime.enabled=true
```

### Known Gotchas

```text
# The agent is opt-in; provider gates (openAI.enabled / realtime.enabled) are deliberate consent flags.
#   Do NOT auto-enable a cloud provider without a user action — flipping the gate must be tied to the
#   user's explicit dropdown selection, mirroring the CLI test-openai / test-realtime "consent implied" pattern.
# The one-shot Ask path (_agent is null) has no channel consumer and MUST keep its direct insert.
# Realtime GA quirks already handled by the parser (see CLAUDE.md); not in scope here.
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: FIX duplicate Ask insert — MODIFY src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs (AskAsync)
  - IMPLEMENT: Only insert returned answers directly when _agent is null (one-shot). When the live agent
    handled the ask, the channel consumer already displays them — do not insert again.
  - KEEP: StatusText update from answers.Count for both paths (count reflects what was produced).
  - PRESERVE: one-shot path behavior (direct insert + notice) unchanged.

Task 2: FIX silent provider/gate mismatch — MODIFY src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs (SelectedProvider setter)
  - IMPLEMENT: When the user selects "openai" or "realtime", also enable that provider's gate in config
    (c.Agent.OpenAI.Enabled = true / c.Agent.Realtime.Enabled = true), matching the CLI "explicit action
    implies consent" pattern. Selecting "fake" changes nothing else.
  - RATIONALE: The dropdown IS the explicit user action; a valid key is still required, so no cloud call
    happens without a key. This removes the silent picker-vs-gate disagreement.
  - ALTERNATIVE (if auto-enable is judged too implicit): instead surface resolution.Notice in StatusText
    at StartAsync when the resolved provider name != SelectedProvider, so the fallback is visible.

Task 3: CORRECT user config — MODIFY output/config.json
  - SET agent.provider to "openai" (already enabled) OR set agent.realtime.enabled=true to honor the
    current "realtime" selection. Pick based on which model the user wants for Ask.
  - NOTE: this is the immediate unblock; Task 2 prevents recurrence.

Task 4: VERIFY — dotnet test (agent + app view-model coverage)
  - RUN: dotnet build; dotnet test
  - CONFIRM: existing MeetingAgent / provider-factory tests stay green; no regression in Ask behavior.
```

### Implementation Patterns & Key Details

```csharp
// Task 1 — AskAsync: rely on the channel consumer when the live agent handled the ask.
IReadOnlyList<AgentSuggestion> answers;
bool insertDirectly;               // one-shot path has no channel consumer
if (_agent is not null)
{
    answers = await _agent.AskAsync(question);
    insertDirectly = false;        // ConsumeSuggestionsAsync already inserts these
}
else
{
    var (suggestions, notice) = await AgentOneShot.AskAsync(_configService.Load(), question, _currentTranscriptPath());
    answers = suggestions;
    insertDirectly = true;
    if (notice is not null) { StatusText = notice; }
}

if (insertDirectly)
{
    foreach (var s in answers)
        PostToUi(() => Suggestions.Insert(0, new AgentSuggestionItem(s)));
}
StatusText = answers.Count == 0 ? "Agent produced no answer." : $"Agent answered ({answers.Count}).";

// Task 2 — SelectedProvider setter: the selection is the explicit consent action.
set
{
    SetProperty(ref _selectedProvider, value);
    PersistConfig(c =>
    {
        c.Agent.Provider = value;
        if (value == "openai")   c.Agent.OpenAI.Enabled = true;
        if (value == "realtime") c.Agent.Realtime.Enabled = true;
    });
}
```

## E2E Validation Plan
- [ ] Agent running + valid key: type a question, click Ask → exactly one answer row appears.
- [ ] Answer text is a real model response, not "Offline fake provider cannot really answer…".
- [ ] Agent stopped (one-shot) + valid key: Ask still inserts exactly one answer (no regression).
- [ ] Change dropdown to openai/realtime → config gate flips true; Start log shows the chosen provider, not fake.
- [ ] No key configured: Ask still degrades gracefully to fake with a visible notice (privacy/consent intact).

### Anti-Patterns to Avoid

- ❌ Don't insert Ask answers both from the channel consumer and the return value.
- ❌ Don't enable a cloud provider without a user action — tie the gate flip to the explicit dropdown selection.
- ❌ Don't remove the one-shot direct insert (that path has no channel consumer).
- ❌ Don't touch realtime GA parsing — out of scope.
```
