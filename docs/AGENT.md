# Meeting Agent (Live AI Sidecar)

Optional layer that watches the live transcript event stream (`.jsonl`) and produces private suggestions: risks, blockers, decisions, action items, contradictions, questions to ask, suggested responses. Disabled by default; the offline transcriber never needs it.

## Data flow

```text
transcript.jsonl -> TranscriptEventTailer -> MeetingAgent (rolling window + running summary)
                                              |            \
                            ContextComposer (context/ .md)  -> provider (fake | openai | realtime)
                                              v
                suggestions.jsonl + SQLite + markdown  ->  UI Agent tab / CLI / MCP  (+ optional private voice)
```

The `.txt` transcript is for humans; the agent never rereads it. The tailer reads the `.jsonl` incrementally with an offset checkpoint (`output/agent/tailer-checkpoint.json`).

## Quick start

```powershell
# 1. describe your project (required file)
notepad context\codename-summary.md

# 2. offline demo with the fake provider
localtranscriber agent start-fake --transcript "./output/transcripts/<session>.jsonl"

# 3. real intelligence (uses your OpenAI key; see OPENAI_PROVIDER.md)
localtranscriber config set agent.provider openai
localtranscriber config set agent.openAI.enabled true
localtranscriber agent start --transcript "./output/transcripts/<session>.jsonl"

# ask on demand (any mode, any time)
localtranscriber agent ask "what commitments were made in the last five minutes"
```

In the app: **Agent tab** → enable, pick mode + provider, Start Agent while a recording session runs.

## Modes

| Mode | Behavior |
|---|---|
| Off | no processing |
| SilentObserver (default) | suggestions stored + shown in the list; no voice, no popups |
| PrivateCoach | as above + speaks High/Critical privately when voice is enabled |
| HotkeyOnly | collects silently; answers only when you ask |
| InterruptWhenImportant | shows only High/Critical |
| ExperimentalMeetingParticipant | not implemented — see MEETING_JOIN_MODE.md |

A response policy keeps the agent quiet: per-type cooldown (45s), duplicate suppression, dismissed-title memory, confidence floor. Everything is still stored in SQLite/`suggestions.jsonl` even when not shown.

## Outputs

`output/agent/`: `suggestions.jsonl`, `meeting-summary.md`, `action-items.md`, `risks.md`. SQLite: `agent_suggestions`, `agent_state`.

## Key config (`config.json` → `agent`)

`enabled`, `mode`, `provider` (fake/openai/realtime), `contextFolder`, `agentOutputFolder`, `rollingWindowMinutes` (5), `suggestionIntervalSeconds` (10), `maxTranscriptEventsPerPrompt` (80), `maxContextCharacters` (20000), plus `openAI`/`realtime`/`voice` blocks. Set via `localtranscriber config set agent.<path> <value>`.
