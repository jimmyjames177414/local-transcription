# Commit

## Core move

Stop analyzing. Make the decision, state it clearly, and identify what could go wrong.

A decision is a commitment under uncertainty — not a ranking, not a comparison, not a hedge. The goal is to give the user something they can act on.

## Fast path

When one option clearly dominates (and it often does):

1. State the decision in one sentence
2. Name the dominant criterion and the evidence for it
3. State the main tradeoff (what you're giving up)
4. State what would change the recommendation (the reversal trigger)

Do not produce a balanced comparison when one option is clearly better. Artificial balance wastes time and obscures the answer.

## Deep path

When the choice is high-stakes, irreversible, or genuinely close:

1. **Decision**: What you recommend and the one-sentence reason
2. **Key evidence**: The 2-3 facts that most support this (from `investigate`, not from reasoning)
3. **What you rejected**: The runner-up and specifically why it lost
4. **Top risk**: The most likely way this goes wrong
5. **Reversal trigger**: What signal would mean you should reconsider?
6. **First step**: The smallest concrete action to start

## Challenge before committing

Before finalizing, state the strongest argument against your conclusion — then explain why it doesn't hold (or acknowledge where it does). This is not a formality. If you can't articulate a real objection, you haven't thought hard enough. If the objection is strong enough to change the answer, change the answer.

Bad challenge:
> "One could argue the other approach is better, but our approach is more practical."

Good challenge:
> "The strongest objection: approach B handles 10x traffic spikes without config changes, while our choice requires manual scaling. This doesn't change the recommendation because the team confirmed traffic is stable and predictable (verified in analytics dashboard), and the manual scaling path takes <5 minutes when needed."

## Useful moves

- Remove dominated options first (worse on every important criterion)
- If evidence is weak: choose a safe-to-try experiment over a big commitment
- If the future is unclear: choose the most reversible option
- If more analysis won't change the action: commit now and stop deliberating
- If two options are genuinely equal: pick the simpler one — complexity needs justification, simplicity doesn't

## Failure checks

- Hiding value judgments inside technical language
- Refusing to commit when the user needs a recommendation
- Committing when a cheap check would change the choice
- Choosing the elaborate option because it sounds more thoughtful
- Presenting uncertainty as a reason not to decide (uncertainty is the reason TO decide explicitly — implicit decisions are worse)
- Hedging with "it depends" without specifying what it depends ON
- Adding caveats that don't change the recommended action

## Done when

The decision, evidence, main tradeoff, and top risk are clear. The user can act immediately or redirect with a specific objection. Carry forward only what the user needs to act.
