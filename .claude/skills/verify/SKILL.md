---
name: verify
description: Run unit tests across all services to verify changes are correct. Linting is currently skipped repo-wide because lint configs are not yet mature.
---

Run the following checks in order, stopping on first failure. Report results for each step.

Linting is intentionally skipped for every service below — the project's lint configs are not yet mature enough to gate verification on. Re-enable per service once its `npm run lint` runs cleanly on `main`.

## 1. chat-service (TypeScript)

```bash
cd services/chat-service && npx jest --maxWorkers=4 --collect-coverage
# lint skipped — config not yet mature
```

## 2. conversation-service (TypeScript)

```bash
cd services/conversation-service && npx jest --maxWorkers=4 --collect-coverage
# lint skipped — config not yet mature
```

## 3. mind-service (TypeScript)

```bash
cd services/mind-service && npx jest --maxWorkers=4 --collect-coverage --forceExit
# lint skipped — config not yet mature
```

## 4. workspace-service (TypeScript)

```bash
cd services/workspace-service && npx jest --maxWorkers=4 --collect-coverage
# lint skipped — config not yet mature
```

## 5. people-org-mcp-server (TypeScript)

```bash
cd services/mcp-servers/people-org-mcp-server && npx jest --maxWorkers=4 --collect-coverage
# lint skipped — config not yet mature
```

## 6. ragingest-service (Python)

No declared test runner — skip. Lint also skipped (config not yet mature). Re-evaluate once a `pytest` / `ruff` setup lands.

## 7. reporting (Node CLI cron)

```bash
cd reporting && npx jest --maxWorkers=4
# lint skipped — no proper lint script defined
```

## 8. spa-source (Angular 20)

```bash
cd spa/spa-source && npx ng test --code-coverage --watch=false --browsers=ChromeHeadless
# lint skipped — config not yet mature
```

## 9. spa-web (Angular 20 / Amethyst)

```bash
cd spa-amethyst/src/spa-web && npx ng test --code-coverage --watch=false --browsers=ChromeHeadless
# lint skipped — config not yet mature
```

If all checks pass, report success. If any fail, show the relevant errors and suggest fixes.
