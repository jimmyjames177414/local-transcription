# Sessions Screen, Review Mode & Integrations (Claude Design 4j–4m)

## Context

The previous plan (UI redesign + minutes integration, Part A+B) is fully implemented and verified — 157 tests green on `agent-upgrade`, uncommitted. The user then extended the Claude Design project (same project `5919500c-5082-4a63-b68c-8490f0fedca8`, turn 4) with four new screens, refreshed at `plans/design-reference.html`:

- **4j Sessions** (lines 642–739): new 4th nav tab — searchable, day-grouped session list (title, time·duration, colored participant chips, ✎ notes badge, minutes-sync badge, file size) + detail pane (rename ✎, Load session / Open folder / Export ▾ / 🗑, read-only transcript preview, notes summary rail).
- **4k Delete confirm** (741–770): borderless dialog listing exact files + sizes, "voice memory is kept" note, Cancel + **hold-to-delete** (~800 ms fill, Esc cancels).
- **4l Review mode** (772–868): loading a past session puts Meeting in "◷ Viewing archive" — review banner (Close / Start new recording), read-only transcript with in-transcript search (n/m + ▲▼), assistant "grounded on this archive" with **clickable timestamp citations**, notes panel editable against that session's file.
- **4m Integrations** (870–988): Settings › Integrations — Minutes card (enable toggle, meetings folder, "what's included", status strip + Sync all now), MCP server info row.

**User-confirmed scope:** INCLUDE session titles (schema change) and clickable citations. DEFER consent-basis start flow, "N agents reading live" badge; Live-feed row is a static path display only.

**Verified backend facts:**
- `transcript_events` has proper columns (speaker_name, text… indexed by session_id) — DISTINCT/LIKE queries work; no FTS needed.
- Schema = CREATE IF NOT EXISTS constant in `SqliteDatabase.EnsureInitialized()`; no migration mechanism — add `title TEXT` via `pragma_table_info` check + `ALTER TABLE`.
- `ISessionStore` (Create/End/Get/List) and `ITranscriptEventStore` (Insert/ListBySession) have NO delete/search/title methods.
- `SessionRecord` is positional — adding `string? Title = null` as last param keeps all construction sites compiling (verified sites: RealTranscriptionEngine:130,554; FakeTranscriptionEngine:73; 3 test files).
- Voice grounding: `AgentPanelViewModel` gets `Func<string?> currentTranscriptPath` → `TranscriptJsonlPath`, tail `FromStart=true` (checkpoint ignored) — an archived jsonl grounds the assistant with **zero Voice changes**; but path is captured at connect, so stop the voice session on load/close.
- Interface fakes to extend: `MinutesExportServiceTests.FakeSessionStore/FakeEventStore`, `MinutesExportHookTests.FakeEventStore`.

Every stage ends buildable, runnable (`dotnet run --project src/LocalTranscriber.App`), tests green.

---

## Stage 1 — Storage: title, delete, search, summaries

- **`SqliteDatabase.cs`**: add `title TEXT` to sessions CREATE; in `EnsureInitialized()` run tolerant migration — `SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name='title'` → 0 ⇒ `ALTER TABLE sessions ADD COLUMN title TEXT`. No migrations framework.
- **`StorageModels.cs`**: `SessionRecord(..., string? Title = null)`; new `SessionSummary(SessionRecord Session, int EventCount, IReadOnlyList<string> SpeakerNames)`.
- **`StoreInterfaces.cs`**: `ISessionStore` += `UpdateTitleAsync(id, title)`, `DeleteAsync(id)`, `ListSummariesAsync()`. `ITranscriptEventStore` += `DeleteBySessionAsync(id)`, `SearchSessionIdsAsync(text)` (LIKE w/ `%_\` escaping).
- **`SqliteStores.cs`**: title in Create/Get/List/ReadSession (nullable read). `ListSummariesAsync` = **two queries** (sessions + `SELECT session_id, speaker_name FROM transcript_events GROUP BY session_id, speaker_name`), joined in memory — avoids GROUP_CONCAT separator ambiguity, O(1) round-trips.
- **New `Storage/SessionDeletionService.cs`** (ctor pattern of MinutesExportService): `ListFilesAsync(id)` → existing txt/jsonl/`notes-{id}.md` with sizes; `DeleteAsync(id, alsoRemoveMinutes)` → files (ignore missing) → `DeleteBySessionAsync` → `ISessionStore.DeleteAsync`; minutes removal globs `*-meeting-{id}*.md` (all matches — UniquePath may have created `-2.md`). Make `MinutesExporter`'s folder expansion `public static ResolveFolder` (shared with sync badges + Integrations).
- **Title flows into exports**: `MinutesExportService.ExportAsync` → `title ?? session.Title`. Leave the engine stop-hook untitled (KISS — rename happens after meetings; Sync-all republishes titled).
- **Extend interface fakes** in MinutesExportServiceTests/MinutesExportHookTests (NotSupportedException stubs where unused).
- **Tests** (`SqliteStoreTests` + new `SessionDeletionServiceTests`): old-schema DB migrates; title round-trip + UpdateTitle; delete removes only target session's rows; search match/no-match/metacharacters; summaries (distinct speakers, counts, zero-event sessions listed); deletion service file removal + glob + missing-file tolerance; export title fallback.

## Stage 2 — Sessions screen

- **Nav**: `AppScreen` → Meeting=0, **Sessions=1**, Speakers=2, Settings=3. MainWindow.xaml: insert ListBoxItem + `SessionsView` (param 1), bump Speakers/Settings params to 2/3. **Sweep all hardcoded indices**: grep `SelectedScreenIndex` + `ConverterParameter=` in App (known: MeetingView "✎ name"→Speakers nav, OpenAssistantSettings_Click `SelectedScreenIndex = 2`→3).
- **New `ViewModels/SessionsViewModel.cs`** (+ `SessionListItemViewModel`): self-composes stores from config (like SpeakerManagementViewModel); ctor takes `Func<bool> isRecording`.
  - `Items` filtered collection; `SearchText` debounced ~250 ms (in-memory over Title/SpeakerNames + `SearchSessionIdsAsync` union); `Filters` = All / With notes / Synced / Not synced; `FooterText` ("N sessions · X MB in output\transcripts ↗").
  - `RefreshCommand`: one `ListSummariesAsync()` + per-item file probes (FileInfo sizes, `File.Exists(notes-{id}.md)`, minutes glob in `MinutesExporter.ResolveFolder(...)` → "min ✓"/"min ⟳ pending"; badge hidden when integration disabled).
  - Item fields: Title (fallback "Meeting {HH:mm}"), TimeAndDuration, DayGroup (TODAY/YESTERDAY/date), Participants (3–4 + "+N"; **name-hash colors, not SpeakerPalette** — palette is session-scoped and Reset on StartAsync), HasNotes, MinutesBadge, FileSizeText.
  - Detail: lazy on selection — `PreviewRows` (first ~30 events via ListBySessionAsync, reuse transcript row template), `NotesSummary` (NotesDocument.Parse), `MetaText`; rename (IsRenaming/EditTitle/CommitRenameCommand → UpdateTitleAsync); `LoadCommand` (canExecute `!isRecording() && Selected != null`) raises `LoadRequested(SessionRecord, events)`; OpenFolder/Export (MinutesExportService)/`DeleteRequested` event.
- **New `Views/SessionsView.xaml`**: 400px list (CollectionViewSource + PropertyGroupDescription("DayGroup") + GroupStyle headers) / detail pane per 4j. **Extract the transcript row DataTemplate from MeetingView into `Themes/Controls.xaml`** as shared `TranscriptRowTemplate` (DRY, two consumers).
- **Shell**: `SessionsPanel` property; refresh on tab entry (`SelectedScreenIndex == Sessions`) and when `IsRecording` flips false.
- **Verify**: fake session appears under TODAY with sizes/badges; rename survives restart; search hits titles/participants/transcript text; Load disabled while recording.

## Stage 3 — Delete confirm dialog

- **New `Views/Dialogs/DeleteSessionDialog.xaml(.cs)`** modeled on VoiceModeConsentDialog: title `Delete "{title}"?`, warning, mono file list from `SessionDeletionService.ListFilesAsync`, green "voice memory kept" line, optional "Also remove from meetings folder" checkbox (visible only when a minutes file exists), Cancel (`IsCancel=True` → Esc) + **hold-to-delete**: red button w/ fill Rectangle (ScaleX 0→1 DoubleAnimation 800 ms); `DialogResult=true` ONLY on animation Completed; down starts + captures mouse, up/leave/lost-capture snaps back; never on Click (keyboard space must not insta-delete).
- **Wiring**: MainWindow handles `DeleteRequested` → dialog → `SessionDeletionService.DeleteAsync` → refresh; if the deleted session is loaded in review, close review first.
- **Verify**: listed files match disk; short press cancels; Esc cancels; post-delete rows+files gone, speakers intact; minutes file removed only when checked.

## Stage 4 — Review mode

- **`MainWindowViewModel`**: `IsReviewing`, `ReviewTitle/ReviewMetaText/ReviewFooterText`, `ReviewSessionId`, `_reviewJsonlPath`; `LoadArchive(record, events)` (guard IsRecording; Clear + SpeakerPalette.Reset + fill Transcript; switch to Meeting screen); `CloseReviewCommand`; `StartAsync` closes review first; `ShowIdlePanel` also requires `!IsReviewing`; `ShowIdleStatusPill => !IsRecording && !IsReviewing`; `GroundingJsonlPath => IsReviewing ? _reviewJsonlPath : CurrentJsonlPath`.
- **Grounding**: MainWindow wires AgentPanel with `() => Session.GroundingJsonlPath`; on load/close **stop any running voice session** (add `AgentPanelViewModel.StopVoiceIfRunningAsync()`) so the next message reconnects grounded on the right file (path captured at connect). Assistant header gains "grounded on this archive" badge on `Session.IsReviewing`.
- **Header/banner**: blue "◷ Viewing archive" pill; review banner row above Meeting's columns (meta + ✕ Close session + ● Start new recording bound to StartCommand).
- **Transcript search**: in review, header swaps to search TextBox + "n/m" + ▲▼; VM `ReviewSearchText`/`MatchCountText`/Next/Prev + `ScrollToRowRequested(int)`; MeetingView code-behind scrolls (`ScrollIntoView` + ContainerFromIndex) with a ~1.5 s highlight. Footer swaps to `ReviewFooterText` + Export ▾. Row-level highlight only (no per-word mark — KISS).
- **Citations**: new `Behaviors/TimestampInlineBehavior.cs` — attached `FormattedText` on TextBlock: regex `\b\d{2}:\d{2}:\d{2}\b` → Runs + styled Hyperlinks invoking attached `CitationCommand`; linkify only when `IsStreaming == false` (avoid per-token Inline rebuilds). Handler → `NavigateToTimestamp(hhmmss)`: first row with `Time >= hhmmss` (fixed-width string compare) → same scroll+highlight. Works in live mode too.
- **Notes**: `LoadRequested` → `_notesService.StartSession(record.Id); Notes.Reload();` (loads existing file — already supported); close → `StartSession("")` + Reload; new recording repoints via existing SessionId hook.
- **Verify**: full loop — load → pill/banner/read-only transcript → search navigates → citation click jumps+highlights → note lands in that session's notes file → Start new recording exits review into a clean live session; idle panel doesn't flash on close.

## Stage 5 — Settings › Integrations

- **`SettingsViewModel`**: `Sections` = General, Audio, Models, Assistant & privacy, **Integrations**, Advanced (audit `NavigateToSettings` indices: Audio=1, Models=2 unaffected; MeetingView assistant-settings jump uses section 3 — unaffected). New: `MinutesStatusText` ("✓ connected · last publish: {title} · {HH:mm} · N synced" — glob vs summaries, computed on section open), `SyncAllNowCommand`/`IsSyncing`.
- **`MinutesExportService.ExportMissingAsync()`** (Storage): sessions minus glob matches (skip status "recording"), export each — reused by CLI.
- **`SettingsView.xaml`**: Integrations panel (param 4), Advanced bumped to 5; **move** minutes toggle/folder from Advanced into the Minutes card (per 4m: header w/ "min" glyph + enable toggle, folder + Browse via OpenFolderDialog handler, static "What's included" checklist, static Live-feed path display, status strip + Sync all now). MCP row = static info card with connect hint — no fake "running" probe.
- **Verify**: section order per 4m; save persists; Sync all publishes only missing; Sessions badges flip to "min ✓" after sync.

## Stage 6 — CLI/MCP parity (cheap subset)

- CLI `sessions` line format includes Title; `export-minutes` already titled via service.
- New CLI `delete-session --session <id> --yes [--keep-minutes]` (SessionDeletionService; `--yes` required, no interactive confirm).
- New CLI `sync-minutes` (ExportMissingAsync).
- MCP `list_sessions` includes titles. **Deliberately skip** MCP delete_session (destructive, no confirm affordance) and rename commands (YAGNI).

---

## Risks / gotchas
1. `AppScreen` renumbering — sweep hardcoded `SelectedScreenIndex` and `ConverterParameter=` values in App.
2. Voice grounding captured at connect — must stop voice on archive load/close.
3. Archived rows re-read via SQLite lose `IsKnown` (uncertain tints won't render in review/preview) — accepted, don't fix.
4. SpeakerPalette is session-global — list chips use name-hash colors; LoadArchive blocked while recording sidesteps live conflicts.
5. Two-query summaries (not GROUP_CONCAT) — comma-safe speaker names.
6. Hold-to-delete: DialogResult only on animation Completed; guard LostMouseCapture; never on Click.
7. Citations: no Inline rebuild while streaming.
8. Every `SqliteDatabase` instance re-runs schema+migration — pragma check is idempotent/fast.
9. Delete-with-minutes must remove ALL `*-meeting-{id}*.md` matches (UniquePath suffixes).
10. `ShowIdlePanel` now has three inputs — verify no flash on review close.

## Out of scope
Consent-basis start flow; "N agents reading live" badge; functional live-feed toggle (static path only); FTS5; per-word search highlighting; MCP delete/rename tools; persisting IsKnown/confidence for archived labels; migrations framework.

## Verification (end-to-end)
1. `dotnet build` + `dotnet test` at every stage gate (157 + new tests).
2. Old DB opens (title migration); fresh fake session appears in Sessions grouped/sized/badged.
3. Rename → persists, shows in CLI `sessions` + exported frontmatter.
4. Review loop (load/search/citation/note/close/start-new) per Stage 4.
5. Delete loop per Stage 3 (files+rows gone, speakers kept).
6. Integrations: toggle honored by stop-hook, Sync all → badges update, Advanced no longer hosts minutes fields.
