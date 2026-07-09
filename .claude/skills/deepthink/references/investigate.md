# Investigate

## Core move

Replace assumptions with evidence. Use tools to check — don't reason about what might be true.

This is not a thinking mode. It is a doing mode. Every action below is something you execute, not something you consider.

## Actions

### Read before assuming

Before forming an opinion about anything — a file, a document, a claim, a system — read the actual source. Don't assume you know what it contains, even if you've seen similar things before.

### Search before guessing

If your plan depends on something existing, verify it:

**For code tasks:**
- `grep -rn "pattern" --include="*.ts"` to find functions, patterns, or usage
- `find . -name "*.config.*" -type f` to locate configuration
- `git log` / `git blame` to understand history and intent

**For research or non-code tasks:**
- Search the web for current data, sources, or prior art
- Check official documentation, specs, or authoritative references
- Look for counterexamples to your initial assumption

If your search returns nothing, your plan has a false assumption. Adjust before proceeding.

### Check history and context

Understanding WHY something is the way it is prevents "fixing" what's intentional.

**For code:** `git log`, `git blame`, commit messages, PR descriptions
**For everything else:** prior decisions, meeting notes, changelogs, version history, related discussions

### Verify before claiming

If you say something is true — confirm it. Run the test, check the source, read the document, verify the number. Don't state facts you haven't checked.

### Trace the flow

For any system (code, process, argument, workflow): trace the actual path from input to output. Check each step. Don't trust your mental model — verify each transition.

## What to look for

- Dependencies between things you'll change
- Patterns and conventions already established (match them)
- Existing work that already does what you're about to propose
- Recent changes that might interact with your work
- Constraints or context that affect behavior
- Contradictions between different sources (docs vs reality, stated policy vs actual practice)

Contradictions are high-value signals — they often point to the real problem or the real question.

## Good vs bad output

Bad investigate output:
> "The auth middleware probably checks roles before allowing access to admin routes."

Good investigate output:
> "Confirmed `req.user.role === 'admin'` at `src/middleware/auth.ts:34` — case-sensitive match, no normalization. `git log` shows this file was last modified 3 weeks ago in commit `e4f2a1b` to add the `/admin/billing` route."

Bad (non-code):
> "The company likely has a refund policy."

Good (non-code):
> "Refund policy found at support.example.com/refunds — 30-day window, requires original receipt, excludes digital goods. Last updated March 2025."

The difference: citations and specifics, not hedged guesses.

## Record what you find

After investigating:

- **Found**: Facts confirmed with tools or sources (with citation)
- **Missing**: Information you need but couldn't find
- **Contradictions**: Where different sources disagree with each other

## Failure checks

- Forming opinions without reading the source material first
- Assuming things from memory instead of checking
- Checking easy things while leaving the critical assumption unchecked
- Reading one source when the picture spans multiple sources
- Trusting labels, titles, or names as documentation of actual behavior
- Skipping the things that document intended behavior (tests, specs, contracts, policies)

## Done when

Your understanding is grounded in what you read, checked, and verified — not what you assumed or predicted. You know what exists, where it is, and how it currently works.
