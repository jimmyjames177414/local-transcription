# Evaluate

## Core move

Compare options using weighted criteria, not impressions. The winner is the option that best satisfies the important criteria, not the one that checks the most boxes.

## 1. Define criteria

Derive criteria from the actual goal, constraints, and context. Do not import a generic rubric — every criterion must matter for this specific decision.

Common criteria families (select what's relevant):

- **Correctness**: Does it solve the actual problem? Does it handle edge cases?
- **Simplicity**: Fewest concepts, fewest moving parts, easiest to understand
- **Maintainability**: Can someone else understand and modify this in 6 months?
- **Performance**: Does it meet real (not hypothetical) performance requirements?
- **Robustness**: How does it handle failures, bad input, and unexpected state?
- **Reversibility**: How hard is it to undo or change course if this is wrong?
- **Effort**: How long to implement correctly, including tests?
- **Risk**: What's the blast radius if it goes wrong?
- **Fit**: Does it match existing patterns in the codebase? (Consistency has value.)

For aesthetic or design work, add:
- **Function-fit**: Does the form serve its intended function for its audience?
- **Coherence**: Do the parts reinforce each other?

## 2. Weight the criteria

Classify each as:
- **Critical (pass/fail)**: Failure here kills the option entirely
- **High weight**: Primary drivers for the decision
- **Low weight**: Nice-to-haves, tiebreakers only

State WHY certain criteria outweigh others. "Simplicity is high because this module has 5 maintainers with varying experience" is useful. "Simplicity is important" is not.

## 3. Score and compare

For each option against each weighted criterion:

- Score with evidence — cite specific aspects of the approach, not vibes
- Name the strongest objection to each option
- Note assumptions that, if wrong, would change the score

A high score on a low-weight criterion does not compensate for failure on a critical one. Don't let enthusiasm for cleverness override a critical weakness.

## Good vs bad output

Bad evaluation:
> "Option A is simpler but Option B is more scalable. Both have tradeoffs. It depends on your needs."

Good evaluation:
> "Decisive criterion: maintainability (high weight — team of 3 junior devs). Option A scores high (200 lines, no new abstractions). Option B scores higher on scalability but requires understanding the observer pattern — a complexity tax paid on every future change. Option A wins unless you expect >10x traffic growth within 6 months."

The difference: named the decisive criterion, scored with specifics, gave a clear winner with a reversal condition.

## 4. Find the decisive criterion

Often one criterion actually swings the decision. Name it explicitly. If removing that criterion would change the winner, it's the decisive one and the user should know it.

## 5. Stress-test the winner

Before committing to the leading option:

- What's the worst realistic failure mode?
- What assumption, if wrong, would make this the worst choice?
- Does the runner-up handle any critical scenario better?
- Is there a cheap way to get the runner-up's advantage without switching?

If stress-testing reveals a fatal flaw, go back to step 3 or generate a new hybrid approach.

## Failure checks

- Assigning equal weight to all criteria
- Letting secondary strengths override critical weaknesses
- Criticizing without naming the violated criterion
- Using polish, cleverness, or novelty as a proxy for quality
- Scoring everything when one criterion clearly dominates the decision
- Evaluating against hypothetical requirements instead of actual ones
- Ignoring implementation effort (the best design you can't finish is worse than the good design you can)

## Done when

The criteria, weights, scores, and decisive criterion are explicit. The winner survives stress-testing. Carry forward the judgment, the main tradeoff, and any uncertainty that should be visible in the final answer.
