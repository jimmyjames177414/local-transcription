---
name: pr-review
description: Full ADO pull-request code review — fetch diff, run comprehensive checklist across correctness, concurrency, S3 scalability, orchestration deep-dive, test quality, deployment safety, common/ hygiene, and Terraform; post inline comments with severity; optionally apply minor fixes and approve. Trigger when user says "review PR", "review this PR", "code review PR #<ID>", or pastes an ADO PR URL. Args: PR number or full ADO URL.
---

# /pr-review — ADO pull request code review

## Invocation

```
/pr-review 273770
/pr-review https://dev.azure.com/accenturecio26/Flywheel_9497/_git/skyline_AIR9497/pullrequest/273770
```

Parse the PR ID from the argument. If none provided, ask.

---

## Step 1 — Fetch PR metadata + existing threads (parallel)

```
mcp__azure-devops__repo_get_pull_request_by_id(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>,
  includeChangedFiles: true
)

mcp__azure-devops__repo_list_pull_request_threads(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>
)
```

From metadata, record:
- PR title, description, author, source branch, target branch
- Full changed-file list (`changedFilesSummary.changeEntries[].item.path`)
- Reviewer votes

From threads:
- **Active** threads = open, unresolved
- **Fixed** threads = previously raised and addressed

**Re-review detection** — check `isDraft` from PR metadata first:
- `isDraft: false` AND prior comments from `r.h.kumar.mishra@accenture.com` exist → this is a **re-review**; lead Step 3 by verifying each prior issue is genuinely fixed, then scan for new issues
- `isDraft: true` → always treat as a **fresh review**, regardless of prior comments. If active threads from `r.h.kumar.mishra@accenture.com` exist, prepend a passive note to the Step 6 summary: *"⚠️ N active thread(s) from a prior draft review exist — listed for awareness but not treated as formal findings: [thread titles]"*. Do not enter re-review mode.

**Set trigger flags** from the changed-file list — these unlock mandatory extra sections in Step 3:

| Flag | Fires when any changed file matches |
|------|-------------------------------------|
| `ORCHESTRATION` | `intelligence/index.ts`, `orchestration-service.ts`, `plugin*.ts`, `tool-router*.ts`, `streaming*.ts`, `chat-service/src/services/`, `chat-service/src/routes/` |
| `S3_HOT_PATH` | `listObjectsWithMetadata`, `ListObjectsV2`, `getObjectsWithMetadataAndContent`, `getFile`, `checkChat`, `checkChatFromAgent`, `cache-manager.ts` |
| `AUTH` | `dsAuth`, `dirAuth`, `stgDirAuth`, `OBO`, `pbac`, `checkChat`, `checkChatFromAgent`, `mind-service.ts`, `access-control` |
| `DB_WRITE` | `postgres_iam_wrapper.ts`, `pg-data-helper.ts`, `migrations/`, `messageService/index.ts`, `sqs-body-enums.ts`, `sqs-interface.ts` |
| `COMMON` | any file under `common/src/` |
| `INFRA` | any `.tf`, `.tfvars`, `.yml` in a pipeline or infra directory, or any file named `azure-pipelines*.yml` anywhere in the repo |
| `SPA` | any file under `spa/` or `spa-amethyst/` |

---

## Step 2 — Fetch the diff

```
mcp__azure-devops__repo_get_pull_request_changes(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>,
  includeDiffs: true,
  includeLineContent: true
)
```

Result is always large (saved to tool-results file). **Never read inline.** Use `grep` + `Read(offset, limit)` in ~300-line chunks, or delegate to a subagent with explicit instruction to read **all** lines before concluding.

---

## Step 3 — Comprehensive analysis

Run every section. Do not skip based on PR description — the description is written by the author. Each section: mark ✅ checked / ⚠️ findings.

---

### A. Correctness

- **Logic errors**: wrong conditions, inverted booleans, off-by-one, wrong variable used
- **Null/undefined not guarded**: any `T | null | undefined` value used before a null check
- **Negative array index**: `arr[-1]` is always `undefined` in JS/TS — use `arr[arr.length - 1]`
- **Missing `return` after Express response**: `res.status(X).json(...)` without `return` causes fall-through and double-header sends
- **Removed behavior**: read every `originalLines` hunk. Deleted route handlers, removed guards, dropped validations — confirm each is intentional
- **Behavior regression on error paths**: if old code threw (5xx) and new code swallows (401), or vice versa, this is a user-visible regression. Must be explicitly documented in the PR description even if the new behavior is correct (e.g., fail-closed on DB error returning 401 instead of 500)
- **Case-sensitive EID comparison**: `array.includes(username)` or `=== username` without `.toLocaleLowerCase()` silently denies valid users whose EIDs are stored lowercase. Look for inconsistency — `checkIfUserIsMindUser` already normalizes; any new check must too
- **JSON.parse on external/LLM/MCP responses**: must be wrapped in `try/catch` with a fallback. A malformed MCP server response body throws `SyntaxError` into the `.then()` chain and silently kills it. Also: attempt `{...}` regex extraction before falling back to raw parse for LLM JSON responses — some models ignore `response_format: json_object` and add a preamble
- **`async` IIFE return values discarded**: `async () => { ... }()` inside `startActiveSpan` or similar — the Promise is discarded and rejections become unhandled. Add `.catch()`
- **`argObj` mutations must precede `wasString` re-stringify**: in `processFunctionResponse`, if `wasString=true`, `argObj` is re-stringified at a specific line; any new fields added after that line are silently dropped from the emitted payload
- **`max_tokens: null` sent to Azure OpenAI**: the value is rejected by Azure; omit the field entirely to use the model default

---

### B. Error handling precision

- **Over-broad error catches on S3**: `error?.name === 'NoSuchKey' || error?.['$metadata']?.httpStatusCode === 404` — the `|| httpStatusCode === 404` branch catches `NoSuchBucket` and other 404-adjacent errors that should not be silently swallowed. AWS SDK v3 reliably emits a typed `NoSuchKey` error; the name check alone is sufficient and safe. Remove the `||` clause
- **`finally` block side-effects**: any S3 write, masterlist update, or external call in a `finally` block runs even when the `try` block throws. If the DB batch fails, the S3 update still proceeds, leaving stores diverged. Move the external write to after the awaited operation succeeds
- **Fail-open vs fail-closed**: Redis read errors → fail-open (proceed to DB). DB errors on access-control paths → fail-closed (deny, return empty arrays). Verify the code matches the PR description's claimed behavior
- **`event.headers.authorization` null guard**: missing authorization header must return 401 before any token decoding or downstream call — an unguarded `undefined.split(' ')` crashes the handler

---

### C. Concurrency & atomicity

- **Non-atomic read-modify-write (TOCTOU)**: `getSettings()` → modify → `updateSettings()` without a DB-level atomic op. Two concurrent requests read the same state, each appends, second write clobbers first. Fix: per-element DB SQL (`@>` guard for append, `jsonb_agg` filter for remove)
- **Full-array JSONB overwrite**: `jsonb_set(col, '{key}', $2::jsonb)` replaces the entire array. Last writer wins under any concurrency. Per-element operations are always required when multiple writers exist
- **Backfill race**: fire-and-forget SQS that sends a snapshot of data read moments earlier. Safe only with `WHERE col IS NULL` guard so it fires once on uninitialized data and skips if a concurrent write already set the value
- **Fire-and-forget SQS without `.catch`**: every `sendSqsDbMessage(...)` call must have `?.catch(err => console.error(...))`. A silent failure leaves DB permanently stale with no observable signal
- **Agent transfer stack — duplicate target check**: blindly pushing on every transfer signal creates A→B→A infinite loops. If the target `gptID` is already in the stack, pop down to that position instead of pushing
- **Token cache null guard**: if the token provider returns `null` without throwing, caching it creates a self-reinforcing loop: `isTokenExpired(null) → true → re-fetch → null cached again`. Guard: `if (token) { cache.set(...) }`

---

### D. S3 scalability & hot-path caching ⚡ (mandatory when `S3_HOT_PATH` or `AUTH` flag set)

**S3 is not a database. Per-request S3 reads on hot paths cause throttling, cost spikes, and cascading Lambda failures at scale.**

1. **`ListObjectsV2` / `listObjectsWithMetadata` on every request**: listing to perform access checks (`administrators/`, `editors/`, `testers/` prefixes) on every chat request is the worst pattern. The migration path is: DB query + Redis cache with TTL. Any new list-based access check is a blocker
2. **`getFile` / `GetObject` for existence only**: use `HeadObject` if content is not needed. Better: migrate the existence flag to DB
3. **Per-request S3 reads without Redis cache**: any value that is stable per-agent/user over a short window must be: `redis.get(key)` → on miss, S3/DB → `redis.set(key, value, 'EX', TTL)`. TTL must be explicit (baseline: 60 s for access control)
4. **Redis cache with missing or zero TTL**: `redis.set(key, value)` without `'EX', N` caches indefinitely — stale data served permanently after an update
5. **DS auth and DIR auth paths bypassing cache**: `checkChat` and `checkChatFromAgent` gate every user message. Both DS and DIR (staging directory) auth entry points must route through the same `getAgentAccessControlCached()` / `cache-manager.ts` function. A separate S3 code path for either auth type means every message from those users hits S3 directly. Verify: grep for `listObjectsWithMetadata` inside `checkChat`, `checkChatFromAgent`, and their callers in both auth paths
6. **S3 retry token budget exhaustion in bulk loops**: 1 `ListObjectsV2` + N `GetObject`/`HeadObject` calls per cache miss per user (where N can be 2000+) exhausts the AWS SDK retry token budget (shared per Lambda process); subsequent S3 calls in that invocation fail with `No retry token available`. Every S3 call inside a `Promise.all` bulk loop must have a `try/catch`
7. **Cache invalidation coordinated miss bursts**: `invalidateDraftAgentsCacheForTeam()` wipes every team member's cache on every save. For large teams, the resulting simultaneous cache misses can push S3 burst rates past the throttle threshold. Consider staggered invalidation or per-user TTL jitter

---

### E. Orchestration service deep dive 🔬 (mandatory when `ORCHESTRATION` flag set)

**The orchestration layer is the most critical code path — a defect here breaks chat for all users on all agents. Always run every item below.**

1. **Terminal stream signals — all four must emit on every code path**: every path that calls `readable.push(null)` (stream end) must also emit `appendSuggestions`, `pushTokenUsage`, `pushContextCompaction`, and `consumeTokens`. Missing any one leaves the client UI stale (context bar frozen, token ring never updates)

2. **prunedMessages vs fullMessagesArray in CLARIFY/prefilter path**: passing `fullMessagesArray` to `updateHistories` doubles flow-reinforcement messages on every subsequent turn because `cleanSystemMessages` does not strip the agent's custom system prompt or flow reinforcement. The CLARIFY path must always use `prunedMessages`

3. **MCP tools must be stored in a class field, not a local variable**: if `filteredSkills` is null (prefilter disabled), recursive LLM calls fall back to `mind.skills`, silently dropping all MCP tool definitions after the first call. The full resolved tool set must be stored as a class field and referenced as `filteredSkills ?? resolvedFunctions ?? mind.skills` on every recursive call

4. **Tool-call streaming integrity**: `delta.arguments` chunks arrive in fragments — verify:
   - Empty string chunks are not skipped
   - Chunks are concatenated by index, not position
   - `finish_reason: tool_calls` does not flush before all argument chunks arrive
   - Resulting JSON is valid after concatenation

5. **A2A sub-agent propagation**:
   - `conversationId` from the parent must propagate to the sub-agent call — never generate a new one
   - Sub-agent XML tags (`<thinking>`, `<prefilterInfo>`, `<tokenUsage>`) must be stripped from the result before feeding back to the parent LLM — literal models (GPT-5.4+) attempt to parse them as structured output
   - `CURRENT_SESSION_ID` expansion must fall back to `args.gptID` when schema has no `hardCodedValue` — otherwise the composite session key evaluates to `undefined-undefined`
   - Preview mode must use a stable non-empty `conversationId` (e.g., `preview-${uuidv4()}`) reused per session — passing `''` short-circuits sub-agent statefulness
   - `addArchivedMessages` must guard `!conversationId.startsWith('preview-')` to prevent preview sessions from polluting the production OpenSearch vector store
   - `childAgentNeedsDsAuth` must guard on `DR-` prefix before scanning — a published child agent (no `DR-` prefix) whose draft has `ds_auth_enabled: true` should not contaminate the orchestrator's token

6. **Stream termination on every path**: all code paths (error, empty response, tool-call-only) must reach the SSE termination signal (`data: [DONE]`). An unterminated stream hangs the client indefinitely. Trace every `return` and `throw` in the orchestrator to confirm each one emits `[DONE]`

7. **429 retry before stream pipe**: retrying is safe only if status = 429 arrives before any response body chunk. Check `response.status` immediately after `fetch()` resolves, before any piping or `body.getReader()` call

8. **Plugin / skill routing**: for any added/removed/renamed plugin:
   - Routing table / registry updated
   - No trigger keyword or route prefix collision with existing plugins
   - Skill output not double-wrapped in the stream

9. **Feature flags gate backend behavior, not just UI emission**: `showContextCompaction` (LD flag) controlled SSE tag emission only — the actual LLM summarisation call fired for every agent regardless. The LD flag must gate the operation itself, not just the output. UI-only LD flags must never be assumed to gate backend behavior

10. **Model routing**:
    - Fallback chain does not loop (A → B → A)
    - Model-specific limits (context window, max output tokens) applied before the call, not caught after a failure
    - `max_tokens: null` omitted entirely for Azure OpenAI (field is rejected)
    - New model gated behind LD flag, flag checked before invocation

11. **WorkflowExecutor / sub-orchestrator XML stripping**: any path that calls `OrchestrationService.startOrchestration()` and exposes the result to the SSE client or parent LLM must strip all XML instrumentation tags (`<thinking>`, `<tokenUsage>`, etc.) before exposure

---

### F. Auth & token routing (mandatory when `AUTH` flag set)

- **OBO token tenant derivation**: OBO login URL must be derived from the target tenant, not `logInUrl` (which points to the DS tenant in dev/local). Using the wrong tenant produces `AADSTS50020`
- **Client secrets keyed by app registration, not environment**: `SKYLINEWS_CLIENT_SECRET` may hold a different app's secret in dev/local. OBO for a specific app registration needs its own dedicated env var (e.g., `SKYLINEWS_CLIENT_SECRET_STG_DIR`) that is stable across all environments
- **Scope map validation before S3 write**: broker client scope maps must be validated (non-empty client ID key, array-typed scopes) before every S3 write. Blank keys or non-array scopes throw `TypeError: scopesArr is not iterable` at runtime
- **DS/DIR auth mutual exclusivity**: `ds_auth_enabled` and `stg_dir_auth_enabled` are mutually exclusive. Enabling one must clear the other at both the editor layer and the runtime compute layer. Runtime: force the non-winner to `false` after both flags are computed
- **All token-gated entry points check the auth flag**: `testConnection()`, `discoverTools()`, and any other function that calls a gated API must select the correct token using the same `dsAuthEnabled` / `stgDirAuthEnabled` flag as the main chat path. These functions bypass the streamer entirely and require their own token-selection logic
- **DS/DIR auth entry points both route through cache**: verify both auth paths call `getAgentAccessControlCached()` — not separate direct S3 reads (see D.5)
- **Action `onload` must not inherit agent-level auth for pre-existing actions**: only new actions should inherit `dsAuthEnabled` from the agent form; existing actions re-opened in the editor must not be contaminated by the current form state

---

### G. Test quality

Coverage thresholds apply (conversation-service: 98%). Test issues block merge.

1. **New non-trivial files with zero tests**: any new `.ts` with multiple branches and no `.spec.ts` is a blocker. Minimum coverage:
   - Happy path
   - Each error/failure branch (Redis miss, DB error, S3 miss, bad input)
   - Cache hit vs cache miss
   - Fail-open vs fail-closed paths

2. **Stale mocks that no longer reflect the implementation**: when code migrates from S3 to DB, tests that still set up `mockS3.listObjectsWithMetadata.mockResolvedValueOnce(...)` pass because the mock is configured but never called. The test proves nothing. Stale mocks must be removed

3. **Tests validating shapes that no real dependency emits**: e.g., `{ name: 'NotFound', httpStatusCode: 404 }` for an AWS SDK v3 error that actually emits `{ name: 'NoSuchKey', $metadata: { httpStatusCode: 404 } }`. The test validates a fiction. Find and flag these

4. **Missing branch coverage on cache layers**: for any new caching code, tests must exist for: Redis hit, Redis miss, Redis error (fail-open), DB error (fail-closed), malformed JSON in cache

5. **`hasNextAgent` must be computed, not hardcoded**: `hasNextAgent: false` hardcoded in SPA services means agent transfers never fire regardless of backend signals. Any boolean derived from stream content must be computed from that content

---

### H. SQS four-file pattern (mandatory when `DB_WRITE` flag set)

Adding a new `SQSAction` requires **all four** of the following — missing any one causes the message to be silently dropped or the consumer to crash:

1. `common/src/enums/sqs-body-enums.ts` — add enum value
2. `common/src/model/sqs-interface.ts` — add `SQSBody` union variant
3. `common/src/utility/postgres_iam_wrapper.ts` — add interface method + implementation
4. `services/mind-service/src/messageService/index.ts` — add router `case`

Check all four are present. If any is missing, flag as 🔴 CRITICAL.

**user-service SQS write pattern** (when any file under `user-service/` is changed): user-service uses the same two-Lambda pattern. The HTTP API instantiates `UserRepositoryPostgre(wrapper, USER_WRITE_MODE.QUEUE)`; the consumer Lambda instantiates it with `USER_WRITE_MODE.DIRECT`. Instantiating with `DIRECT` in the HTTP path bypasses SQS entirely with no error — writes never reach Postgres. Any PR adding a new write path to user-service must confirm `USER_WRITE_MODE.QUEUE` is used in the API controller and that the consumer Lambda handles the new message type.

---

### I. `common/` hygiene (mandatory when `COMMON` flag set)

`common/` is the shared package for all services. Changes here affect every consumer.

- **New reusable utility not placed in `common/`**: any new DB client, HTTP wrapper, S3 helper, Redis client, or cross-service model that more than one service could use must go in `common/src/utility/` (or `common/src/model/` for types). Never duplicate per service
- **New package dependency added to `common/` but not to consuming services**: `common/` has its own `package.json` and `node_modules`. When a new npm package is added to `common/`, it must also be added to the `package.json` of every service that imports from `common/src/` — each service bundles its own `node_modules`. Missing this causes runtime `Cannot find module` errors post-deploy
- **New enum not in `common/src/enums/`**: shared enums (SQS actions, DB types, error codes) belong in `common/` not in service-specific files
- **New config key not centralized**: if two or more services will read the same config key (e.g., a Redis key prefix, a flag name), it belongs in `common/src/config/`
- **Import path stability**: `../../../common/src/...` must remain the stable relative path. No aliases, barrel files, or restructuring without updating all consumers
- **Interface added to service instead of `common/src/model/`**: any interface that is passed across service boundaries belongs in `common/src/model/`
- **`postgres_iam_wrapper.ts` interface + implementation always paired**: adding a method to `IPostgresIamWrapper` without a corresponding implementation in `PostgresIamWrapper` (or vice versa) causes a TypeScript compile error in consumers

---

### J. Deployment safety & data migration

1. **Data format changes on existing DB rows**: new serialization format (e.g., Postgres array literal `{a,b,c}` → JSON string `["a","b"]`) breaks rows written before deployment. The new reader must handle both formats, or the table must be confirmed empty at deploy time. A defensive dual-format parser is the safe default

2. **Column/type changes without migration**: NOT NULL column without default, type change on existing column, removed column still referenced. Cross-reference column definitions in the diff with all usages

3. **Stored procedure vs application logic drift**: if a stored proc writes in one format and the application reads in another, a partial rollout fails. Flag any change that touches both

4. **Deployment order dependency**: if service A must deploy before service B (DB migration before code using the new column), it must be called out explicitly in the PR description

5. **Undocumented behavior regressions**: any change to what HTTP status code, error message, or response shape a caller receives is a behavior regression. Must be tagged `[BEHAVIOR CHANGE]` in the PR description if missing

6. **New features must be gated behind a LaunchDarkly boolean flag defaulting to `false`**: backend endpoints can exist but must not be called from the SPA while the flag is off. The flag must be checked in both SPAs independently using `Boolean(flags['key'])`. The flag must gate the backend operation, not just the UI emission

---

### K. Cross-service coupling

- **Direct SQL across service boundaries**: one service querying another's owned tables (e.g., mind-service doing `SELECT FROM users WHERE oid = $1`). Schema changes in the owning service silently break the consumer. Fix: add an HTTP endpoint in the owning service
- **Service-to-service HTTP without timeout**: any `fetch(url)` calling another internal service must have an explicit `AbortController` with a timeout. Without one, a hung target holds the Lambda slot indefinitely. Established baselines: primary chat path ≥100 s, continuation ≥30 s, auth/token ≥15 s, skill calls ≥60 s
- **Vertex/Gemini `https.request` without `req.setTimeout`**: unlike `fetch`, `https.request` has no built-in timeout. `req.setTimeout(N, () => req.destroy())` is required or the Lambda hangs until the hard kill
- **PostgreSQL connection without `connectionTimeoutMillis` and `statement_timeout`**: without these, a partitioned RDS instance holds the Lambda slot for the full Linux TCP timeout (2–5 min), causing cascading OOM during incidents

---

### L. Infra / Terraform (mandatory when `INFRA` flag set)

- **`terraform fmt` must pass**: run `terraform fmt -check` in every directory containing modified `.tf` or `.tfvars` files. If the diff shows un-formatted HCL (misaligned `=`, wrong indentation), it must be corrected before merge. This is non-negotiable
- **`terraform validate` must pass**: verify the plan compiles without errors
- **No commented-out Terraform blocks without justification**: a commented-out `resource`, `variable`, `module`, or `output` block must have an accompanying comment explaining **why** it is commented out (not what it does). No justification = flag for removal or uncomment. Valid justifications: "disabled until X is provisioned", "reserved for DR config", "left for rollback reference — remove after N date"
- **`.tfvars` variable names must match `variables.tf` declarations**: mismatched names are silently ignored by Terraform; values appear to be set but the variable receives its default
- **No hardcoded AWS account IDs, region names, or ARNs that should be variables**: any literal `123456789012`, `us-east-1`, or `arn:aws:...` that varies by environment must be a variable
- **`sensitive = true` on secrets**: any variable holding a password, token, key, or secret must declare `sensitive = true` to prevent it from appearing in plan output
- **New resources must carry standard tags**: environment, service, team — whatever the team's tag policy requires. Check existing resources in the same file for the established tag set and replicate it
- **Pipeline YAML changes**: any new pipeline stage or job must not disable existing security gates (policy checks, approval gates, environment protections). Flag any `condition: always()` or `continueOnError: true` added to critical stages
- **🔴 Terraform state path / backend config changes**: Any diff touching `backend.tf`, the `backend { }` block inside any `*.tf` file, `key =` / `bucket =` / `region =` inside a backend config block, `-backend-config` arguments in CI/CD pipeline YAML (`.yml`/`.yaml` under `infra/` or `services-infra/` or any pipeline directory), or any `.tfvars` file that supplies backend-related variables — flag as **CRITICAL**. Changing the tfstate S3 key on a live environment causes Terraform to read from an empty state and plan recreation of all existing resources (100–200+), resulting in mass "ResourceInUseException / already exists" errors, partial side-effects written to AWS but tracked in the wrong state file, and complex recovery requiring either `terraform import` of every resource or a state file restore. The PR must explicitly document why the path changed, confirm the target state file already exists and contains the current resource inventory, and include a rollback plan.

---

### M. SPA / Angular (mandatory when `SPA` flag set)

- **`bypassSecurityTrustHtml` requires DOMPurify first**: Angular's sanitizer is fully bypassed. LLM output piped through `marked()` then `bypassSecurityTrustHtml()` without `DOMPurify.sanitize()` is an XSS vulnerability — a prompt injection can execute arbitrary HTML/JS in the user's browser
- **Draft vs published config pairs**: any config field that affects live agent behavior needs both a `show*` (draft editor) and `show*Published` (published view) variant. A single toggle without the published counterpart is silently ignored on the live agent
- **Numeric config fields validated server-side with bounds**: `compactionThreshold = 0` silently triggered compaction every turn. Server must reject out-of-range values with a 400 — never trust the SPA to enforce bounds
- **LD flag off must stop the backend operation, not just hide the UI**: the SPA hiding a feature behind a disabled LD flag does not prevent direct API calls. The backend must independently check the flag or the per-agent config field before executing the feature

---

### N. OpenSearch hygiene

- **kNN filters must use `term` on `.keyword`, not `match`**: `match` is a post-filter (HNSW traverses all documents first); `term` on `.keyword` is a pre-filter that restricts traversal scope. The performance difference is dramatic (0.2 s vs 54 s p95 on 1.2M documents)
- **Verify `.keyword` sub-field exists before deploying**: dynamic mapping in OpenSearch 2.x auto-adds `.keyword` for strings, but confirm via `GET index/_mapping`; if missing, `term` queries silently return 0 results
- **Every inserted document must include `created_at`**: without a timestamp, per-document retention via `delete_by_query` is impossible and the index grows unbounded
- **OpenSearch client must set `requestTimeout` and `sniffOnStart: false`**: without `requestTimeout`, the client hangs on a pressured cluster; `sniffOnStart: false` prevents VPC-breaking discovery calls
- **`conversationId` must be guarded against `undefined` before any OS insert**: `undefined` stringified becomes the literal `"undefined"`, producing garbage documents with IDs like `"undefined-DR-abc"` that degrade kNN performance permanently
- **ISM is index-level, not document-level**: using ISM for per-document retention on a shared index will delete the entire index when the age threshold is hit. The only correct mechanism for document-level expiry is `delete_by_query` on a `created_at` field

---

### O. CLAUDE.md architectural violations

- **mind-service direct DB writes**: all writes to PostgreSQL in mind-service must go through `sendSqsDbMessage` → Lambda → `postgres_iam_wrapper`. Direct `postgres_iam_wrapper` calls from any mind-service API-path code (controllers, services, or helpers) are a CLAUDE.md violation
- **`queryRead` for guard reads before writes**: `queryRead` hits the read replica. A guard read via `queryRead` immediately followed by a write sees stale data under replication lag. Use `queryWrite` / primary for any read that gates a write decision
- **`dotenv.config()` without `{ path: 'src/.env' }`**: bare `dotenv.config()` resolves to the service root (not `src/`) and silently loads nothing. Every service entry point must specify the path explicitly
- **Admin read endpoints without the same guard as write endpoints**: returning any data (even trimmed) to non-admins on an admin-only path allows auth-bypass. The 401 guard fires before any S3 or DB read
- **Blanket `requireAdmin` on routers containing auth-check endpoints**: routes like `/checkadmin`, `/checkapprovers`, `/CheckOrchestratorAdmin` must be accessible to non-admins to determine their own role. Blanket middleware on the parent router breaks the self-check flow

---

### P. Performance & efficiency

- **Sequential `await` on independent operations**: two or more `await` calls whose results do not depend on each other should be `Promise.all([...])`. Sequential independent I/O (S3, DB, HTTP) adds the full duration of each call to latency
- **`mrdrHeaders` / token + secret fetches that were parallelized and then reverted**: independent token fetch + secret fetch must remain in `Promise.all`. On cold starts, sequential is slower; on warm starts, the token is cached (near-instant) but the pattern degrades if caching is ever disabled
- **N+1 query pattern**: looping over a result set and making a DB/S3/HTTP call per item. Replace with a batch operation or `WHERE id = ANY($1::uuid[])` SQL
- **Unnecessary `ListObjectsV2` for point lookups**: listing a prefix to find one object when `GetObject` with the full key (catching `NoSuchKey`) is cheaper and faster
- **`pendingSandboxFiles` and `extractS3DownloadLinks` both pushing the same images**: two parallel code paths appending to the same stream from the same file set causes duplicate image events. Deduplicate before the second push

---

### Q. Code clarity & PR description completeness

- **Misleading log prefixes**: `console.error('[checkChat] ...')` inside a function called from multiple callers — the prefix should name the function, not its most common caller
- **Duplicate export alias without TODO**: `export const cacheKeys = CACHE_KEYS` — add `// TODO: migrate callers to CACHE_KEYS directly, then remove this alias` so it does not persist forever
- **New file not listed in PR description's "Files Changed"**: reviewers scanning the description may miss it. Flag it
- **`any` type on function parameters where a concrete type exists**: `(mind: any) => mind.id` when the type is already defined upstream
- **Config persistence functions whitelist-filtering new fields**: if `saveMCPConfig` (or similar) filters to an explicit field list and a new field is added to the interface but not to the filter, it is silently dropped on every save. Always verify whitelist/allowlist functions include every new field

---

## Step 4 — Verify findings before posting

**Never post based on pattern match alone.** Always verify in the raw diff:

```bash
grep -n "<symbol>" <tool-results-diff-file.txt> | head -60
Read(<diff-file>, offset=<line>, limit=40)
```

Key traps:
- `modifiedLineNumberStart` in diff JSON = actual file line number for inline comment position
- `originalLines` hunks show deleted code — always read for removed-behavior findings
- Check the controller **and** its helper file (`settings.ts` often delegates to `settings-helper.ts`)
- `postgres_iam_wrapper.ts` is 2000+ lines; new functions are always appended at end
- Fetch the actual file from the PR branch for anything you cannot confirm from the diff alone: `mcp__azure-devops__repo_get_file_content(repositoryId, project, path, version: <branch>, versionType: "Branch")`

Verdict per finding:
- `CONFIRMED` — directly observable in diff, no ambiguity
- `PLAUSIBLE` — likely real but depends on runtime state or code outside the diff
- `REFUTED` — disproved; drop silently

Post only CONFIRMED and PLAUSIBLE. **When not sure: post a 🔵 clarification comment phrased as a question, never an assertion.** Never suppress a genuine concern because you lack 100% certainty.

---

## Step 5 — Present findings for approval, then post

**STOP before posting. Always show the findings table to the user first and wait for explicit approval.**

Present a summary table:

| # | Severity | File | Line | One-line summary |
|---|----------|------|------|-----------------|
| 1 | 🟠 MAJOR | ... | ... | ... |

Then ask: **"Which of these should I post? (all / 1,2,3 / none)"**

Only call `repo_create_pull_request_thread` after the user explicitly says which findings to post. A bare "go ahead", "post all", or specific item numbers is sufficient.

---

### Posting inline comments

```
mcp__azure-devops__repo_create_pull_request_thread(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>,
  filePath: "/path/to/file.ts",           // repo-relative, leading slash
  rightFileStartLine: <modifiedLineNumberStart>,
  rightFileStartOffset: 1,
  rightFileEndLine: <modifiedLineNumberEnd>,
  rightFileEndOffset: 100,
  status: "Active",
  content: "<markdown>"
)
```

### Comment format

```
<emoji> **[<LABEL>] <short title>**

<1-2 sentence description of the defect and the exact condition that triggers it>

**Failure scenario**: <concrete input / concurrent state> → <wrong output, crash, or regression>

**Fix**: <direction with SQL/code snippet — not a full rewrite>
```

After posting each thread, immediately reply to it using `repo_reply_to_comment` with a ready-to-use Claude fix prompt (all severities except 🔵 INFO):

```
**Ready-to-use fix prompt:**

\`\`\`
A PR review flagged a potential issue in <file>, around line <N>: <one-sentence description of the concern>.

Before making any changes: read <file> (and any directly related files) and assess whether this issue is actually present as described. If it is not present, or the suggested fix would be incorrect given the actual code, say so and stop. Only apply a fix if you confirm the issue exists and the fix is sound.

If confirmed: <specific description of what to change and where>.
\`\`\`
```

### Severity

| Emoji | Label | When |
|-------|-------|------|
| 🔴 | CRITICAL | Data loss, permanent corruption, security boundary crossed, SQS four-file pattern incomplete |
| 🟠 | MAJOR | Incorrect behavior under realistic load/errors, untested non-trivial new file, stale mocks proving nothing |
| 🟡 | MINOR | Architectural coupling, deployment safety concern, EID normalization, `finally` side-effect, performance, clarity |
| 🔵 | INFO / ? | Uncertain — phrased as a question, not an assertion. Post for genuine concerns even when not fully sure |

> **Post-merge**: if the PR is already merged but you find an issue, post it as `[POST-MERGE NOTE]`. The team needs to know and can schedule a follow-up fix.

---

## Step 6 — Summary comment

```
mcp__azure-devops__repo_create_pull_request_thread(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>,
  content: "<summary>"
)
```

```markdown
**Review summary — <PR title>**

<1-sentence overall assessment>

| Label | Count | Topics |
|-------|-------|--------|
| 🔴 CRITICAL | N | ... |
| 🟠 MAJOR | N | ... |
| 🟡 MINOR | N | ... |
| 🔵 INFO | N | ... |

**Sections checked**: [list all that were triggered or explicitly checked]

<Closing: LGTM / needs changes / LGTM pending minor fix>
```

**Re-review**: lead with a verification table showing each prior thread and whether it is Fixed ✅ / Partially fixed ⚠️ / Still open 🔴.

---

## Step 7 — Apply minor fixes (only when asked or clearly cosmetic)

Only apply directly if **all** are true:
1. User said "fix it" / "apply the fix" — or the finding is purely cosmetic (log text, comment wording) with zero behavior risk
2. ≤ 3 lines, unambiguous, no design judgment required
3. Source branch is or can be checked out locally

```bash
git fetch origin <branch>
git checkout <branch>
# edit
git add <file>
git commit -m "<fix>\n\nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push origin <branch>
git checkout -
```

For anything requiring design judgment → post the finding, let the author implement.

---

## Step 8 — Approve (only when explicitly asked, no open CRITICAL/MAJOR)

```
mcp__azure-devops__repo_vote_pull_request(
  repositoryId: "skyline_AIR9497",
  project: "Flywheel_9497",
  pullRequestId: <ID>,
  vote: "Approved"                  // or "ApprovedWithSuggestions" if MINOR items remain
)
```

Never approve autonomously. If CRITICAL or MAJOR items remain open, vote `ApprovedWithSuggestions` and explain why.

---

## ADO project split — never mix

| Operation | project | repositoryId |
|-----------|---------|--------------|
| PR, threads, diff, file content, vote | `Flywheel_9497` | `skyline_AIR9497` |
| Work items (stories, tasks, bugs) | `AmethystStudio_PR1346` | — |

---

### R. No hardcoding — values must follow established patterns (check every PR)

**The correct pattern for this codebase is `config.ts` with env-keyed blocks, NOT env vars.**

Lambda functions have a 4 KB total environment variable limit — you cannot put all config there. The established project pattern is an env-keyed config object (e.g., `configMap[ENV]`) in a `config.ts` file inside the service, with the active environment selected at startup from a single env var (`NODE_ENV` / `ENVIRONMENT`). **This pattern is correct. Do not flag values in `config.ts` files as hardcoding violations.**

**What IS a hardcoding violation:**

**1. Business logic bypasses config.ts entirely**
- A model ID, URL, index name, bucket name, or workflow name inlined directly inside a handler, service, controller, or helper — not in any config file. The value must live in the service's `config.ts` (or `common/src/config/`) and be imported by the business logic, never written inline.
- Example: `const modelId = "amazon.nova-canvas-v1:0"` inside `image_generator.ts` — this should be `import { AppConfig } from '../config'; ... AppConfig[env].imageModelId`

**2. Same value in two or more `config.ts` files (DRY violation)**
- If an identical value appears in both `mind-service/src/config.ts` and `common/src/config/email-token-config.ts`, one is the source of truth and the other must import it. Having two declarations guarantees they drift.
- Specifically: OAuth tenant/client IDs, service account email, Lambda URLs, and Cognito pool IDs that appear identically across multiple service config files must be consolidated into `common/src/config/`.

**3. Routing on raw agent ID / GPT ID string literals in orchestration**
- `if (gptId === 'DR-abc123')` in orchestration or routing logic — makes code fragile to renames, untestable. Route via capability flags or config properties, not by ID.
- Skill/plugin names used in `earlyExitFunctions` or dispatch tables should be exported constants, not scattered string literals.

**4. LaunchDarkly flag keys as raw strings**
- Flag keys must be declared as named constants (e.g., `const LD_FLAGS = { showCompaction: 'show-context-compaction' }`). A typo in a flag key causes a silent no-op.

**5. Redis cache keys as inline strings**
- Must come from a centralized `CacheKeys` constants object. A key defined in two places with a subtle difference creates two cache populations that never invalidate each other.

**6. AWS resource IDs in Terraform without data sources**
- AWS account IDs in `.tf` files: use `data.aws_caller_identity.current.account_id`, never a 12-digit literal.
- AWS region in `.tf` files: use `data.aws_region.current.name`, never `"us-east-1"` inline.
- Lambda ARNs: use Terraform cross-module references or `aws_lambda_function.<name>.arn`, never string literals.

**Pattern check**: grep the diff for values appearing **outside** a `config.ts` or `*-config.ts` file: bare `https://`, model strings (`gpt-`, `claude-`, `gemini-`, `nova-canvas`), index names, bucket names, `DR-` prefix in a condition **in orchestration or routing files only**, LD flag key as a raw string, `arn:aws:` in `.tf`. Inside a config file: only flag if the same value is also present in another config file (DRY violation).

---

### S. Service-specific constraints (check against the changed service)

**chat-service**
- `streamifyResponse` is the only exported Lambda handler — non-streaming test utilities must go through `indexTests.ts`, never import `src/index.ts` directly
- `loadLLMConfigurations()` runs at module init from S3; if S3 is unreachable it falls back to hardcoded defaults silently — new LLM config paths must have test coverage for the fallback branch
- ESLint complexity ceiling is **10** (`eslintConfig.rules.complexity` in `package.json`) — any new function that pushes cyclomatic complexity above 10 violates the configured rule. Note: linting is not currently enforced in CI per CLAUDE.md; flag as 🟡 for future enforcement, not as a CI blocker
- `earlyExitFunctions` list gates which skills skip subsequent tool calls — verify this list when adding new skills; a skill added incorrectly will prevent chained tool usage
- Skill duplication: some skills exist in both `intelligence/search/` (legacy) and `intelligence/skills/Amethyst/` (current) — always confirm which is actually invoked via `intelligence/functions/index.json` before editing either
- MCP tool list is not static — new MCP servers are picked up dynamically from S3 on warm Lambda; do not cache or assume a fixed tool list between requests
- Coverage thresholds: branches 33%, functions/lines/statements 52% — falling below fails CI

**mind-service**
- `/mind/api/token-exchange` uses a route-scoped `STRATEGY_TEAMS` passport strategy registered **before** global auth middleware in `app.ts` — inserting global auth before it will break Teams OBO token exchange
- New Lambda jobs (`src/{name}Service/index.ts`) require a matching ncc target in both `package.json` scripts and `cio-bundler.js` — missing either means the Lambda is never bundled/deployed
- Health probe is served at the legacy `/conversation/api/health` path (historical reason) — do not rename or move this route
- Soft-delete and hard-delete are separate Lambdas on separate schedules — keep their schedules coordinated to avoid orphaned S3 objects or stale Postgres records
- `controllers/migration.ts` is a one-off data migration — do not add new endpoints there or treat its API as stable
- Coverage thresholds: branches 80%, functions/lines/statements 90%

**conversation-service**
- Coverage threshold **98% on all metrics** — even a single uncovered branch fails CI; every new handler must have full test coverage
- Health route is mounted **before** `passport.initialize()` in `app.ts` — never insert authenticated routes above this line
- CORS allowlist: `localhost`, `127.0.0.1`, `accenture.com`, `*.accenture.com` — adding an origin outside this regex causes a 403-style CORS error in production
- `swaggerMetadata.json` must stay in sync with route signatures — divergence breaks Swagger UI with stale endpoint definitions; update it for every new or renamed route
- Two Lambda artifacts from one source tree (`dist/` + `dist_deleteconversation/`) — a new entry point must be mirrored in `cio-bundler.js`
- `deleteConversationService` is a separate async sweeper Lambda — never call it inline from API handlers

**user-service**
- `eid` must always come from the JWT via `getUsernameFromJWT()` — never from the request body or path params; an `eid` request parameter is a security flaw
- Write-mode mismatch is silent and data-losing: HTTP Lambda uses `USER_WRITE_MODE.QUEUE`; consumer Lambda uses `USER_WRITE_MODE.DIRECT` — instantiating `UserRepositoryPostgre` in `DIRECT` mode in the HTTP path bypasses SQS entirely with no error
- `updateUser` deep-merges `text_to_speech`; a shallow spread drops `voice`/`speed` fields — test partial-update scenarios explicitly
- User row is lazily created on first GET via `getOrCreateUser` — new settings fields added to the `User` model require a corresponding default value in `DEFAULT_USER_SETTINGS`
- In-memory settings cache is per-Lambda-instance, not shared — stale settings can be served up to 300 s after an async write commits on a different instance; do not depend on cross-instance cache invalidation
- Two separate deployables (HTTP API Lambda + SQS consumer Lambda) — HTTP API can only enqueue; without the consumer Lambda wired, writes never reach Postgres

**model-evaluation-service**
- All routes mount under `/modeval/api/` (confirmed: `src/routes.ts`, `src/app.ts`) — a new route mounted at `/api/` alone is wrong and will 404 in production; always verify the prefix
- `src/sqsWorker.ts` exposes `/healthz` on `HEALTHZ_PORT` (env var, default 3201) as a minimal HTTP server for Kubernetes liveness/readiness probes — changes to worker startup or shutdown sequence (`healthzServer.listen` / `healthzServer.close`) affect cluster health signaling directly; do not remove or delay the healthz server
- Two separate deployable targets built via `tsc`: `dist/apiServer.js` (`start:api`) and `dist/sqsWorker.js` (`start:worker`) — a new entry point requires both a `package.json` script and coverage in the `tsconfig` output; missing either means the target is never deployed
- No `coverageThreshold` is configured — CI will not fail on coverage drops; do not add significant untested code paths and assume CI will catch regressions; flag missing test coverage manually

**common/**
- Two Postgres wrappers: `postgres_wrapper.ts` = basic auth; `postgres_iam_wrapper.ts` = RDS IAM auth via `@aws-sdk/rds-signer` — deployed environments use IAM; using the wrong one falls back to basic auth silently
- `S3Wrapper` / `SQSWrapper` are singletons via module caching — in tests always mock with `aws-sdk-client-mock`, never by replacing the singleton reference
- `OBO_SERVICES` in `config/mindConfig.ts` is the canonical audience list for OBO — adding a new downstream service requiring OBO requires a `common/` edit plus redeploy of all consumers
- `amethyst-constants.ts` exists in both `constant/` and `enums/` — they are different files with different exports; confirm the import path resolves to the intended file
- SQL files loaded as raw strings at runtime — bundlers (ncc) may not copy `.sql` assets automatically; verify asset copy steps after adding a new `.sql` file or bumping ncc
- `dist/` build is for type-checking only — consumers import from `src/`; never route a runtime import through `dist/`
- New SDK wrappers must expose a singleton via `getInstance()` — connection-pooled clients must not be instantiated per-request
- Coverage thresholds: branches 33%, functions/lines/statements 46% (low baseline) — do not lower; raise when adding new utility coverage

**SQS write pattern constraints — mind-service only** (when `DB_WRITE` flag set)
- `DB_SQS_ENABLED !== "true"` silently drops all writes — verify this env var is set in the target environment's `.env` before assuming any data persists via SQS
- `SPECIAL_BYPASS_ACTIONS` always fire regardless of `DB_SQS_ENABLED` — do not add new critical writes to this bypass list without explicit review
- SQS message size limit is 256 KB enforced in `sqsHelper` — large new fields may need the same stripping logic as `instructions`; verify the payload size in tests
- SQS `visibility_timeout` (600 s) must be ≥ Lambda timeout (300 s) — if Lambda timeout is raised, the SQS tfvars must be raised proportionally

**spa-source / spa-web**
- `npm test` runs in watch mode and never exits — always use `npm run test:ci` or `npm run test:headless` in CI pipelines
- Two parallel SPA codebases (`spa/spa-source/` and `spa-amethyst/src/spa-web/`) do not share code — navigation/branding/feature fixes must be applied to both independently unless the PR explicitly targets only one
- Edge lambda bundles (`eso-shim`, `secure-headers`) have strict CloudFront size and Node version constraints — never import SPA-level or service-level shared code into them; keep self-contained
- `versionHighlights.json` schema must remain stable — it is read at runtime to display "what's new" toasts; schema changes break deployed SPAs that cached the old shape

---

### T. Package version hygiene (check when `package.json` files are modified)

> **Note**: the conflict table below is a snapshot — verify against current `package.json` files before citing; do not treat it as ground truth.

These version mismatches exist in the repo today — any PR touching dependencies must not widen them further:

| Package | Conflict | Risk |
|---------|----------|------|
| `@opensearch-project/opensearch` | `common` ^3.x vs `chat-service`/`mind-service` ^2.x (v3 API is breaking) | Silent runtime failures on OpenSearch calls |
| `openai` | `common`/`chat-service` ^6.x vs `user-service` ^5.x | API shape mismatch if user-service consumes common openai utilities |
| `@types/express` | `user-service` ^5.x vs all others ^4.x | Breaking `Request`/`Response` type changes; mixing in `common/` types fails compile |
| `typescript` | `common`/`chat-service` 5.1.6 vs `mind-service`/`conversation-service` 5.3.3 vs `user-service` ^5.7.2 | Types valid on 5.7 may not compile on 5.1; `common/` type changes must be tested against oldest consumer |
| `pg` | `common`/`user-service` ^8.20 vs `chat-service`/`mind-service` ^8.13 | Minor API differences; Postgres helpers in `common/` should be tested against the older version |
| `passport` | `chat-service` ^0.6 vs others 0.7 | Passport 0.7 changed session/req.logIn behavior |
| `express` in user-service | `^4.18.2` in `dependencies` AND `^4.21.2` in `devDependencies` | npm resolves unpredictably; one declaration only |
| `rimraf` | `chat-service` ^5.x vs `mind-service`/`conversation-service` 6.x; in user-service production deps (should be dev) | No runtime impact but wrong placement |

**Missing transitive declarations** — packages consumed via `common/` but not declared in consuming service `package.json`:
- `@aws-sdk/rds-signer` — used in `postgres_iam_wrapper.ts` but not declared in `chat-service`, `mind-service`, `conversation-service`, or `user-service`
- `@aws-sdk/client-sqs` — used via `SQSWrapper` in `common/` but missing from `user-service` direct dependencies

Any PR that: (a) adds a new package to `common/`, (b) upgrades a package that has a cross-service version split, or (c) adds a new import of a common utility — must verify that the consuming services' `package.json` files are updated accordingly.

---

### U. PR process checklist

Before approving, verify:
- [ ] PR description links `AB#<work-item-id>` for ADO traceability
- [ ] All new/changed files listed in the PR description (especially new files not shown in "Files Changed" summary)
- [ ] Behavior regressions (error code changes, removed routes, changed defaults) documented with `[BEHAVIOR CHANGE]` tag
- [ ] Deployment order called out if services must deploy in sequence
- [ ] Plan file moved from `plans/drafts/` to `plans/done/` (if this PR closes a planned feature)
- [ ] New features gated behind an LD flag defaulting to `false`
- [ ] `terraform fmt -check` passes for any `.tf` / `.tfvars` changes
- [ ] No Terraform backend path / tfstate `key` changes without explicit documentation of why and a rollback plan
- [ ] No new hardcoded URLs, model names, agent IDs, ARNs, or account IDs

---

### V. Clean code principles — DRY, KISS, YAGNI, SOLID

**DRY — Don't Repeat Yourself**
- **Identical or near-identical logic in multiple files**: the same SQL query, HTTP call, error handler, or transformation copied across services. Fix: promote to `common/src/utility/` or extract into a shared helper in the same service. The bar: if a bug in the shared logic would require fixing it in N > 1 places, it must be deduplicated.
- **Same constant defined in two files**: URL, index name, queue name, feature flag key defined once in `chat-service` and again in `mind-service` — when one changes, the other drifts silently. Fix: single authoritative declaration in `common/src/config/` or `common/src/enums/`.
- **Duplicated config structure across environment blocks**: env-keyed objects (`local`/`dev`/`stage`/`prod`) where 3 of 4 blocks have the same literal value — extract the shared value to a top-level constant and reference it from each block.
- **Duplicated infrastructure wrapper**: e.g., `mind-service/src/helpers/aws_s3_wrapper.ts` re-implementing a subset of `common/src/utility/aws_s3_wrapper.ts`. CLAUDE.md explicitly prohibits this. Fix: delete the service-local copy and import from `common/`.
- **Type definitions duplicated**: an interface or type already defined in `common/src/model/` reproduced in a service's own `types/` folder. Even if the shapes currently match, divergence over time is guaranteed.

**KISS — Keep It Simple**
- **Unnecessary abstraction wrapping a single call**: a function `getX()` that does nothing except call `this.repo.getX()` with no transformation, no error handling, no logging — adds a navigation hop with no benefit. Flatten unless the layer will earn its keep (e.g., it enforces a consistent error shape across all repos).
- **Overly complex branching for simple outcomes**: deeply nested `if-else if-else if` where `switch` + early returns or a small lookup map is clearer. Cyclomatic complexity > 10 violates the configured lint rule in chat-service only (post as 🟡); post as 🔵 INFO if seen in other services where no limit is configured.
- **One-use helper functions**: a `formatXForY()` utility called exactly once, where inlining it would be clearer. Do not extract unless it is reused or genuinely isolates complexity.
- **Async where sync is sufficient**: marking a function `async` just to avoid a refactor, then `await`ing its single expression — adds a microtask tick and makes the call chain harder to follow. Remove `async`/`await` when there is no actual async operation.
- **Returning `Promise.resolve(undefined)` explicitly**: write `return;` or let the function return `undefined` naturally — the explicit wrapping adds nothing.
- **Nested ternaries deeper than 1 level**: `a ? b ? c : d : e` — expand to an `if-else` block.

**YAGNI — You Aren't Gonna Need It**
- **Dead parameter**: a function parameter that is received but never used in the body. Either it was used by a previous implementation or was added for a "future" use case. Remove it unless the caller contract requires it (interface implementation, event handler shape).
- **Commented-out code blocks without a clear TODO**: dead code preserved "just in case." Delete it — git history is the archive.
- **Future-proofing flags / extension points** added in this PR with no current usage: `if (config.featureX)` where `featureX` is never set anywhere in the PR. These slow down readers and create a false impression of capability.
- **Over-engineered factory / strategy pattern for a use case that currently has one variant**: abstract factory whose `createX()` always returns the same concrete class. YAGNI until the second variant materializes.
- **Extra fields on DB models / SQS payloads for "future use"**: columns or payload keys that are always `undefined`/`null`. They bloat every payload crossing the SQS size limit and every SELECT result. Ship only what is used.

**SOLID — where violations are measurable**
- **Single Responsibility**: a class/module that does authentication AND business logic AND DB persistence. Flag when a single TypeScript class has >3 distinctly different concerns — suggest splitting.
- **Open/Closed**: routing code that must be modified every time a new skill/model/auth type is added (vs. a registry pattern where you just add an entry). Flag when a new PR adds the fourth `if (type === '...')` branch to the same function — that's when a dispatch table or registry pattern pays off.
- **Dependency Inversion**: `new ConcreteService()` inside a business logic function makes unit testing impossible without module mocking. Flag when a PR under `services/` instantiates a concrete infrastructure client (S3, SQS, Postgres) mid-function rather than injecting it via constructor/parameter.
- **Interface Segregation**: an interface with 15 methods passed to a function that uses 2. Flag only when the mismatch causes test setup pain (tests must stub 13 no-ops) — suggest a narrower interface or type.

**Practical patterns to grep in the diff**
- Duplicate SQL / query strings: `grep -E "SELECT|UPDATE|INSERT|DELETE" diff | sort | uniq -d`
- Duplicate constants: same string literal appearing ≥ 3 times in the diff: `grep -oE '"[A-Za-z_-]{4,}"' diff | sort | uniq -c | sort -rn | head -20`
- Commented-out code: `grep -E "^\+\s*\/\/" diff | grep -v "^+++" | head -30`
- Dead parameters: function signatures in modified files → verify each param is used in the body
- Unused imports: `grep "^+import " diff` then check if the imported symbol appears elsewhere in the changed lines

> ⚠️ **Clean-code refactors must not introduce regressions.** When flagging a DRY/KISS/YAGNI violation, always note: (a) whether the duplication is *exactly identical* (safe to deduplicate) or *subtly different* (merging may change behavior), and (b) whether the deduplication path requires full unit-test coverage of the shared code before it can be safely extracted. A flag that says "these two functions are almost identical — but one validates `eid` and the other doesn't" is a 🔴 correctness finding, not a 🟡 style suggestion. Never recommend deduplication unless you have confirmed the code paths are semantically identical. If in doubt, post as 🔵 for the author to evaluate.

---

## Quick-reference checklist (scan every PR)

| Check | What to grep / look for | Risk |
|-------|------------------------|------|
| S3 list on hot path | `listObjectsWithMetadata`, `ListObjectsV2` in request handlers | Throttling, Lambda cascade, cost |
| DS/DIR auth bypasses cache | `checkChat`/`checkChatFromAgent` not via `getAgentAccessControlCached` | Every DS/DIR user hits S3 per message |
| Redis cache missing TTL | `redis.set(key, value)` without `'EX', N` | Stale data forever |
| Non-atomic RMW | `getX()` then `setX()` no transaction | Concurrent writes lose data |
| Full-array JSONB overwrite | `jsonb_set(col, '{key}', $2::jsonb)` on array column | Last writer wins |
| Replica lag guard read | `queryRead` before a write decision | Stale guard, wrong write |
| Cross-service DB access | `FROM users`, `FROM conversations` in wrong service | Schema coupling |
| Backfill no WHERE IS NULL | SQS snapshot without null guard | Overwrites concurrent writes |
| SQS four-file pattern | Enum + SQSBody + wrapper + messageService | Missing any = silent drop |
| `finally` side-effect | S3/masterlist write inside `finally` | Runs even when try throws |
| Missing `return` after response | `res.json(...)` no `return` | Double header send |
| Over-broad S3 error catch | `\|\| httpStatusCode === 404` alongside name check | Swallows NoSuchBucket |
| Stale test mocks | S3 mocks for code migrated to DB | Test proves nothing |
| New file, zero tests | Non-trivial `.ts` with no `.spec.ts` | Untested branches |
| EID case mismatch | `.includes(username)` without `.toLocaleLowerCase()` | Valid user denied |
| Data format migration | New serialization on existing DB rows | Old rows fail on first read |
| Terraform not formatted | `terraform fmt -check` | Style gate fails |
| Terraform commented blocks | Resource/variable/module block commented without justification | Dead code |
| 🔴 Terraform state path change | `backend.tf`, `key =` in backend block, `-backend-config` in pipeline YAML, backend-related `.tfvars` | Mass "already exists" errors on next apply; partial side-effects; recovery needs terraform import or state restore |
| Stream terminal signals | 4 signals on every `push(null)` path | UI stale, context bar frozen |
| prunedMessages in CLARIFY | `fullMessagesArray` passed to `updateHistories` | Doubled flow-reinforcement |
| MCP tools class field | `filteredSkills` local var drops on recursion | Tools lost mid-conversation |
| Negative array index | `arr[-1]` → undefined | Silent wrong value |
| Token cache null guard | `cache.set(null)` → infinite re-fetch loop | Broken auth indefinitely |
| Fetch without AbortController | External HTTP call no timeout | Lambda hangs indefinitely |
| PG connection no timeout | Missing `connectionTimeoutMillis` | Lambda OOM cascade on RDS partition |
| LD flag gates backend | Flag only hides UI, backend still runs | Feature runs for all agents regardless |
| `bypassSecurityTrustHtml` | LLM content without DOMPurify first | XSS via prompt injection |
| `max_tokens: null` | Sent to Azure OpenAI | Request rejected by Azure |
| Draft+published config pair | New feature field has draft variant only | Silently ignored on live agents |
| Admin read same gate as write | Read endpoint before 401 check | Auth bypass via direct API call |
| OBO wrong tenant | `logInUrl` used for DIR-tenant OBO | AADSTS50020 |
| Scope map validated before save | `scopesArr` not validated before S3 write | TypeError at runtime |
| DS/DIR mutual exclusivity | Both toggles active simultaneously | Auth contamination in orchestrator |
| argObj mutation before re-stringify | New fields added after `wasString` line | Fields silently dropped |
| Transfer stack duplicate check | Push without checking existing stack | A→B→A infinite loop |
| Sub-agent XML tag stripping | `<thinking>` etc. fed back to parent LLM | Literal models break |
| Preview conversationId stable | Passing `''` as conversationId to sub-agent | Sub-agent stateless every turn |
| OS kNN term not match | `match` filter on kNN query | 54 s p95 instead of 0.2 s |
| OS `created_at` on every doc | New OS insert without timestamp | Index grows unbounded |
| OS `conversationId` defined | `undefined` stringified to `"undefined"` | Garbage docs degrade kNN |
| common/ new dep both pkgs | Added to `common/package.json` only | Runtime `Cannot find module` |
| common/ new util not in service | Utility in one service, others will need it | Duplication + drift |
| Value bypasses config.ts | Model ID, URL, bucket name inline in handler/service, not in config.ts | Correct pattern: config.ts with env-keyed blocks |
| Config value duplicated | Same value in ≥2 config files (service + common) | Guaranteed drift; consolidate to common/src/config/ |
| Routing on agent ID literal | `if (gptId === 'DR-...')` in orchestration | Fragile to renames; route via capability flags |
| Hardcoded ARN / account ID in .tf | 12-digit number or `arn:aws:` literal | Use data.aws_caller_identity; never a literal |
| Hardcoded region in .tf | `"us-east-1"` literal in Terraform | Use data.aws_region.current.name |
| Hardcoded LD flag key | Flag key as raw string, not named constant | Typo = silent no-op |
| Hardcoded Redis key | Inline key string, not from CacheKeys constant | Two populations, no cross-invalidation |
| pkg version widened | New or upgraded dep already split across services | Cross-service runtime mismatch |
| Missing transitive dep | Package from `common/` not in service `package.json` | ncc bundle missing dep at runtime |
| DB_SQS_ENABLED unset (mind-service only) | SQS writes that pass with no env var | Silent data loss in target env |
| SQS payload >256 KB | Large field added to SQS body without stripping | Message dropped by SQS |
| EID from request body | `req.body.eid` or path param instead of JWT | Impersonation vulnerability |
| Coverage threshold drop | Branch/fn/line below service threshold | CI fails; bad precedent |
| DRY: logic duplicated | Same query/transform/error handler in ≥2 files | Bug fixed in one place, silently broken in another |
| DRY: constant in ≥2 files | Same URL/index/flag key defined twice | Drift between the two when one is updated |
| DRY: infra wrapper duplicated | Service-local S3/SQS wrapper duplicating `common/` | Region, credentials, retry logic diverge over time |
| KISS: unnecessary abstraction | Wrapper function that only calls through, no transformation | Dead navigation hop; complexity for no value |
| KISS: async with no await | `async` fn with no await expression | Needless microtask tick; misleads callers |
| KISS: nested ternary | `a ? b ? c : d : e` | Unreadable; expand to if-else |
| YAGNI: dead param | Function param never used in body | Caller confusion; signals incomplete refactor |
| YAGNI: commented code | `// old code...` block without TODO | Clutter; remove and trust git history |
| YAGNI: unused future flag | Config field/code path that is always undefined | Payload bloat; dead branches in logic |
| SOLID: new 4th branch | 4th `if (type === '...')` in a dispatch function | Registry/table pattern needed to keep Open/Closed |
| SOLID: concrete instantiation | `new S3Client()` mid-function, not injected | Untestable without module mocking |
