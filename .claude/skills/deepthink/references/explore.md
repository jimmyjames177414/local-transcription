# Explore

## Core move

Generate structurally different approaches before choosing. Not surface variants — architecturally distinct options that make different tradeoffs.

## Generate 3+ approaches

Each approach must differ in at least one of: architecture, data flow, abstraction level, or fundamental tradeoff.

For each approach, state concisely:

1. **What it does differently** (the structural difference, not cosmetic)
2. **Key advantage** (where this approach wins)
3. **Key risk** (where this approach is weakest)
4. **What it trades away** (the cost of this approach)

Do not generate approaches you cannot briefly defend. Quantity without structural diversity is waste.

## Creative operators (use 2-4, not all)

- **Inversion**: How would you cause the problem? Reverse the mechanism.
- **Constraint removal**: Remove one constraint. What solution appears? Can you get close with the constraint restored?
- **Analogy**: What domain has solved a structurally similar problem? Map the mechanism, not the surface.
- **Subtraction**: What can be removed, automated, or made unnecessary? The best solution is often less.
- **Scale shift**: What changes at 10x or 0.1x scale? This reveals which parts of the design are load-bearing.
- **Temporal shift**: Move the intervention earlier (prevent), later (react), or make it continuous (monitor).
- **Decomposition**: Can independent parts be solved independently? Coupling is often assumed, not real.
- **Recombination**: Fuse the best mechanism from two approaches into a third.
- **Boundary search**: Check the extremes. What happens with zero items? One? A million? Adversarial input?

- **Counterfactual**: What if a key assumption is false? What world makes a bad idea good?
- **Agent shift**: View the problem as the adversary, the end user, the maintainer 2 years from now, or a complete novice.
- **Abstraction**: Strip surface details, solve the underlying pattern, then map the solution back.

If an operator fundamentally reframes the problem, step back to `decompose` before continuing — you may be solving a different problem now.

## Good vs bad output

Bad exploration:
> "Approach 1: Use Redis for caching. Approach 2: Use Memcached for caching. Approach 3: Use an in-memory LRU cache."

Good exploration:
> "Approach 1: Cache at the edge (CDN) — eliminates server hits entirely, but cache invalidation becomes the hard problem. Approach 2: Eliminate the need to cache — denormalize the query so it's fast without caching. Simpler, but increases write complexity. Approach 3: Cache at the application layer with stale-while-revalidate — users always get fast responses, data is eventually consistent. Trades strict consistency."

The difference: each approach makes a fundamentally different tradeoff, not just swaps one tool for another.

## Protect strange ideas

Don't kill unusual approaches immediately. Extract their useful mechanism first. The useful part of a bad idea is often the best part of the final solution.

Ask: "What would have to be true for this to be the right approach?" If the answer is plausible, keep it alive.

## Failure checks

- Many variants of one idea (same architecture, different naming)
- Novelty unrelated to the goal
- Adding complexity when subtraction works
- Forced contrarianism (different for the sake of different)
- Rejecting approaches without extracting their mechanism
- Generating approaches without grounding in what `investigate` found
- Ignoring existing patterns in the codebase that suggest a natural approach

## Done when

You have 3+ structurally different approaches with clear, honest tradeoffs. Each could be defended to a skeptic. Carry forward only the strongest options and the criteria needed to choose between them.
