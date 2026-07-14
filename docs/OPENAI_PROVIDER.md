# OpenAI Text Provider

Optional. Sends the rolling transcript window (text), composed context, and running summary to an OpenAI chat model with a strict JSON schema; receives suggestions + a summary update. Never audio.

## Enable

1. Key (either):
   - environment variable `OPENAI_API_KEY`, or
   - `output/secrets.json` (dev) / `%AppData%\LocalTranscriber\secrets.json` (packaged): `{"openAIApiKey": "sk-..."}`
   The env var wins. The file location is gitignored; the key is never logged.
2. ```powershell
   localtranscriber config set agent.provider openai
   localtranscriber config set agent.openAI.enabled true
   ```
3. Verify: `localtranscriber agent test-openai --transcript "./output/transcripts/<file>.jsonl"`

## Config (`agent.openAI`)

| Key | Default | Notes |
|---|---|---|
| `enabled` | false | hard gate |
| `apiKeyEnvironmentVariable` | OPENAI_API_KEY | name of the env var to check first |
| `model` | gpt-5.4-mini | any chat model on your key; gpt-5.x/o-series param quirks handled automatically |
| `temperature` | 0.2 | ignored for models that reject it |
| `maxOutputTokens` | 700 | |

Without the key or with `enabled=false`, the agent visibly falls back to the offline fake provider — nothing breaks.
