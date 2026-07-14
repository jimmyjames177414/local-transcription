# Decompose

## Core move

Break the problem into parts and expose what's hidden before solving. Most wrong answers come from solving the wrong problem.

## Map the problem

Ask only what matters for this specific task:

- What is the actual goal vs the stated goal? (Are they the same?)
- What are the entities, boundaries, and relationships involved?
- What is being treated as fixed that might not be?
- What constraints are hard (must), soft (should), or assumed (untested)?
- What mechanism links cause to effect?
- What hidden state, timing, ordering, or feedback loop matters?
- What would a domain expert notice that a generalist would miss?

If any assumption is load-bearing and unverified, go back to `investigate` — don't trust reasoning alone for facts you can check.

## Reframe when the frame is wrong

Use sparingly — only when the current frame hides the useful answer:

- Change the unit of analysis (user → event, file → module, request → state machine)
- Separate the proxy metric from the real goal
- Split by actor, time, interface, or failure mode
- Ask what the current frame makes impossible to see
- Ask what stays true if the surface details change

A problem statement is not the problem. It is one representation. Try another if this one isn't productive.

## Good vs bad output

Bad decomposition:
> "The system has a frontend, backend, and database. The frontend sends requests to the backend. The backend queries the database."

Good decomposition:
> "The stated goal is 'faster page loads' but the actual goal is 'reduce checkout abandonment.' The hard constraint is no infrastructure changes until Q3. The assumed constraint is 'we need SSR' — but the slow pages are all behind auth, so SSR gives no SEO benefit. The minimal working set: lazy-load the product image carousel (accounts for 60% of page weight) and defer analytics scripts."

The difference: exposes the real goal, challenges assumptions, identifies what actually needs to change.

## Identify the minimal working set

After mapping the problem: what is the smallest set of changes, checks, or decisions that addresses the actual goal?

- What can be deferred without risk?
- What must be done now because other things depend on it?
- What looks necessary but is actually a preference?

Strip to the essential. The best decomposition makes the problem smaller, not bigger.

## Failure checks

- Describing parts without their relationships
- Accepting the user's frame without questioning whether it fits
- Building a model too complex to act on
- Staying abstract when the answer requires reading specific sources
- Treating every constraint as hard when some are preferences
- Adding scope that wasn't in the original problem

## Done when

The problem structure, hard constraints, key assumptions, and minimal working set are clear enough to either act or generate approaches. Carry forward the frame, constraints, and any assumption that would change the approach if wrong.
