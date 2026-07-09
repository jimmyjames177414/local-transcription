---
name: deepthink
description: "Deep structured reasoning with active verification. Use when the task requires complex reasoning, design decisions, debugging, or evaluation. Combines structured thinking with active verification — every claim is checked, not just reasoned about. If the task is simple and the user has not manually invoked this skill, do not use it."
---

# DeepThink

Think better, not more. Every claim must be grounded in evidence you gathered, not assumptions you reasoned about.

## 1. Orient

Before reading any mode files, answer silently:

- **Goal**: What outcome does the user actually need? (Not the stated task — the underlying need.)
- **Type**: Classify as: `bug` | `design` | `refactor` | `research` | `decision` | `implement` | `writing` | `strategy`
- **Failure modes**: Top 3 ways this goes wrong without careful thought.
- **Unknowns**: What do I not know that I need to know?
- **Complexity**: Simple (1-2 moving parts, clear path) or complex (3+ interacting concerns, competing approaches)?

Do not confuse satisfying the user's expected framing with serving the user's actual goal.

If a missing detail would change the approach, check it before proceeding — don't assume and flag.

### Simple tasks

If the task is simple: skip modes entirely. Investigate the relevant material, verify your understanding, and act. Do not force structured thinking on a straightforward task.

### Complex tasks

Select the prescribed mode sequence for the task type:

| Type | Sequence |
|---|---|
| bug | investigate → diagnose → commit |
| design | decompose → explore → evaluate → commit |
| refactor | investigate → decompose → evaluate → commit |
| research | investigate → explore → evaluate |
| decision | decompose → explore → evaluate → commit |
| implement | investigate → decompose → commit |
| writing | decompose → explore → evaluate → commit |
| strategy | investigate → decompose → explore → evaluate → commit |

### Re-classification

If a mode reveals the task is actually a different type (e.g., investigate shows a "bug" is really a "design" problem), re-classify and switch to the correct sequence immediately. Don't grind through the wrong process.

## 2. Execute modes

Create a todo ledger with the selected modes. Read one mode file at a time.

```
[ ] investigate
[ ] decompose
[ ] commit
```

Mark each mode done when complete, then move to the next. Carry forward insights, open questions, and uncertainties between modes.

### Evidence tracking

After completing each mode, record:

- **Verified**: Facts you confirmed with tools or sources
- **Assumed**: Beliefs you hold but did not verify
- **Surprised**: Things that differed from your initial expectation

The "surprised" list is the most valuable — it marks where your initial model was wrong.

### Subagents

Use subagents when available for:
- Independent information gathering (reading docs, searching code, web research)
- Exploring structurally different approaches in parallel

Continue local critical-path work while subagents run.

## 3. Verify before delivering

Before producing your final answer:

- [ ] Every factual claim is backed by evidence you gathered (not just reasoned about)
- [ ] You identified at least one thing that differed from your initial expectation
- [ ] If you chose between approaches, you can articulate specifically why you rejected the alternative
- [ ] Your answer addresses the failure modes identified in step 1

## 4. Deliver

Lead with the conclusion, then support it:

1. **Answer/Decision** — What to do and why
2. **Evidence** — What you checked that supports this
3. **Tradeoffs** — What you gave up and why that's acceptable
4. **Risks** — What could still go wrong and how to detect it
5. **Confidence** — High (most claims verified), Medium (key claims verified, some assumed), or Low (significant assumptions remain). Base this on the actual ratio of verified to assumed evidence, not on how the answer feels.

Surface uncertainty near the claims it limits. Do not make the answer look more certain than the evidence permits.

## Rules

- **Verify, don't reason.** If you can check it, check it. Reading the source beats assuming. Running the test beats predicting. Looking it up beats guessing.
- **Minimum effective depth.** Use only the modes this problem needs. Simple problems get no modes.
- **Evidence over intuition.** Track the difference between "I checked" and "I believe."
- **Disagree when evidence says to.** Deep thinking includes independent thinking.
- **One pass per mode.** Don't re-read mode files. Extract what you need and move on.
- **Do NOT read multiple mode files at once.**
- **Follow the todo process faithfully, even when it seems slow.**
