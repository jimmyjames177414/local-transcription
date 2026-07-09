# Diagnose

## Core move

Turn symptoms into competing cause hypotheses. Then find the cheapest test that distinguishes them — and run it.

Diagnosis is not guessing the most likely cause and fixing it. It is generating multiple plausible causes and systematically eliminating them.

## Build the diagnostic frame

Before hypothesizing, establish what you know:

- What is the expected behavior?
- What is the observed behavior?
- What is the exact difference?
- When did it last work correctly? What changed since then?
- How often does it fail? Always, intermittently, under specific conditions?
- What has already been tried?

Use evidence from `investigate` — don't re-derive facts you already gathered.

## Generate competing hypotheses

Produce 3+ plausible causes that explain the symptoms. Each must name a specific mechanism, not just a category.

Bad: "it might be a configuration issue"
Good: "the nginx client_max_body_size is set to 1M, rejecting uploads before they reach the app server"

Bad: "the strategy isn't working"
Good: "the pricing model assumes elastic demand, but our customer segment has fixed budgets — lowering price doesn't increase volume"

Useful hypothesis families:

- **State mismatch** — stale data, out-of-sync systems, missed update
- **Boundary/limit** — size limit, threshold, capacity, off-by-one
- **Environment** — version mismatch, different conditions in test vs production
- **Timing** — race condition, ordering dependency, delayed feedback
- **Hidden dependency** — implicit coupling, transitive relationship, shared state
- **Input assumption** — unexpected format, missing data, wrong type, edge case
- **Interaction effect** — two individually correct parts producing incorrect behavior together
- **Observation error** — measuring the wrong thing, reading stale data, testing the wrong scenario
- **Misaligned incentive** — the system works as designed but the design doesn't serve the goal
- **Wrong abstraction** — the mental model doesn't match reality

Don't anchor on the first plausible cause. The most obvious explanation is often wrong — that's why the bug wasn't caught earlier.

## Discriminate

For each hypothesis, answer:

1. What would I observe if this cause is real?
2. What would I observe if this cause is NOT real?
3. What is the cheapest check that distinguishes this from the other hypotheses?
4. Would the proposed fix remove the cause or just mask the symptom?

Then run the cheapest distinguishing check. Actually run it — search the source, read the data, check the record, trace the path, test with specific input. Don't reason about what the check would show.

## Good vs bad output

Bad diagnosis:
> "The upload might be failing due to a server configuration issue. Try checking the server settings."

Good diagnosis:
> "Hypothesis 1: nginx `client_max_body_size` is 1M (default) — checked `nginx.conf:12`, confirmed set to `1m`. Hypothesis 2: app-level validation rejects files >5MB — checked `upload.ts:28`, limit is `10mb`. Hypothesis 3: request timeout before upload completes — cheapest test: upload a 2MB file (under both limits). Result: 2MB upload succeeds. Root cause: nginx limit. Fix: set `client_max_body_size 15m` in nginx.conf."

The difference: named mechanisms, ran distinguishing tests, eliminated alternatives.

## Narrow and repeat

After each check:
- Eliminate hypotheses that the evidence contradicts
- Promote hypotheses that the evidence supports
- If all hypotheses are eliminated, generate new ones — the real cause is something you haven't considered yet

Stop when one hypothesis explains all symptoms and the evidence rules out the alternatives.

## Failure checks

- Explaining a symptom with a renamed symptom ("it fails because it's broken")
- Anchoring on the first plausible cause without generating alternatives
- Proposing a fix before confirming the mechanism
- Ignoring recent changes or environment differences
- Treating correlation as cause
- Gathering evidence without knowing what it would rule out
- Checking easy hypotheses while avoiding the hard-to-test one

## Done when

The root cause is identified with evidence that rules out the competing hypotheses. You can explain the mechanism (not just the symptom), and the proposed fix addresses the cause, not a side effect. Carry forward the confirmed cause, the evidence that confirmed it, and the fix.
