# Context Packs

Local Markdown files that teach the agent your project. The better these files, the better the suggestions — with empty placeholders the agent only sees the meeting itself.

## Layout (`context/` by default)

| File | Purpose | Weight in retrieval |
|---|---|---|
| `codename-summary.md` | REQUIRED. What the project is, current phase, goals. Always sent whole. | 1.5 |
| `decisions.md` | Standing decisions. The agent flags contradictions against these. | 1.4 |
| `risks.md` | Known risks/constraints. | 1.4 |
| `people.md` | Who owns what. Helps "ask X about Y" suggestions. | 1.2 |
| `glossary.md` | Codenames and acronyms. | 1.1 |
| `architecture.md` | Technical notes. | 1.0 |
| `meeting-notes/*.md` | Optional running notes; nested folder supported. | 1.0 |

## How retrieval works

Documents split into heading-scoped chunks (≤1200 chars). Each analysis pass scores chunks by keyword overlap with the last ~20 transcript lines (doc-type weighted) and sends: the whole `codename-summary.md` + the top 5 relevant chunks, within `maxContextCharacters`. Nothing outside the context folder is ever read; only `.md` files; traversal is blocked.

## Tools

```powershell
localtranscriber context list
localtranscriber context show decisions.md
localtranscriber context validate
localtranscriber context search "deployment freeze"
localtranscriber context chunks
```

MCP: `context_list_documents`, `context_read_document`, `context_validate`.
