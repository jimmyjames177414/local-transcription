---
title: [Short descriptive title of the feature]
version: [optional version number]
date_created: [YYYY-MM-DD]
last_updated: [YYYY-MM-DD]
confidence_level: [1-10 scale of confidence in one-pass implementation success]
---
# Implementation Plan: <feature>

## Goal

**Story Goal**: [Specific, measurable end state of what needs to be built]

**Deliverable**: [Concrete artifact - service class, engine hook, UI view, CLI/MCP command, etc.]

**Success Definition**: [How a user or system can verify the feature works as intended]

## Why

- [Business value and user impact]
- [Integration with existing features]
- [Problems this solves and for whom]

## What

[User-visible behavior and technical requirements]

### Success Criteria

- [ ] [Specific measurable outcomes]

## All Needed Context

### Documentation & References

```yaml
# IMPORTANT: Only add references critical for implementation
# MUST READ - Include these in your context window when near implementation
- url: [Complete URL with section anchor]
  why: [Specific methods/concepts needed for implementation]
  critical: [Key insights that prevent common implementation errors]

- file: [exact/path/to/pattern/file.cs]
  why: [Specific pattern to follow - class structure, error handling, etc.]
  pattern: [Brief description of what pattern to extract]
  gotcha: [Known constraints or limitations to avoid]

- docfile: [docs/AGENT.md]
  why: [Custom documentation for complex integration patterns]
  section: [Specific section if document is large]
```

### Current Codebase tree (run `tree` or list `src/` to get an overview of the codebase)

```text

```

### Desired Codebase tree with files to be added and responsibility of file

```text

```

### Known Gotchas of our codebase & Library Quirks

> Only include non-obvious details that are critical to avoid common pitfalls.

```text
# CRITICAL: [Component name] requires [specific setup]
# Example: The WPF UI, CLI, and MCP server must all call the shared Engine — never duplicate transcription logic
# Example: Transcription must stay fully offline; the agent sidecar is opt-in and off by default
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
## Example is for a .NET service feature - adapt as needed
Task 1: CREATE src/LocalTranscriber.<Project>/{Feature}Models.cs
  - IMPLEMENT: {Feature}Request, {Feature}Result record/class types
  - FOLLOW pattern: src/LocalTranscriber.<Project>/ExistingModels.cs (validation approach)
  - NAMING: PascalCase for types and public members
  - PLACEMENT: Feature-specific model file in the owning project

Task 2: CREATE src/LocalTranscriber.<Project>/{Feature}Service.cs
  - IMPLEMENT: {Feature}Service class with async methods
  - FOLLOW pattern: src/LocalTranscriber.Engine/ (service structure, error handling)
  - NAMING: {Feature}Service class, async Task-returning methods
  - DEPENDENCIES: Import models from Task 1
  - PLACEMENT: Service layer in the owning project

Task 3: MODIFY src/LocalTranscriber.Engine/ (or the relevant front-end project)
  - INTEGRATE: Wire the new service through the shared Engine so UI/CLI/MCP all reuse it
  - FIND pattern: existing Engine orchestration
  - PRESERVE: Existing behavior and the single-shared-Engine rule

Task 4: CREATE tests/LocalTranscriber.<Project>.Tests/{Feature}ServiceTests.cs
  - IMPLEMENT: Unit tests for all service methods (happy path, edge cases, error handling)
  - FOLLOW pattern: tests/LocalTranscriber.Engine.Tests/ (fixture usage, assertion patterns)
  - NAMING: {Method}_{Scenario}_{Expectation}
  - COVERAGE: All public methods with positive and negative test cases
  - PLACEMENT: Test project mirroring the code under test
```

### Implementation Patterns & Key Details

```csharp
// Show critical patterns and gotchas - keep concise, focus on non-obvious details

// Example: Service method pattern
public async Task<{Feature}Result> {Feature}OperationAsync({Feature}Request request, CancellationToken ct)
{
    // PATTERN: Input validation first (follow existing service pattern)
    // GOTCHA: [Component-specific constraint or requirement]
    // CRITICAL: Keep transcription offline; reuse the shared Engine — no duplicated logic
    // PATTERN: Error handling approach (reference existing service pattern)

    return new {Feature}Result(/* ... */);
}
```

## E2E Validation Plan
- [ ] [Scenarios you will want to test. Don't include startup commands, focus on validation steps]

### Anti-Patterns to Avoid

- ❌ Don't create new patterns when existing ones work
- ❌ Don't ignore failing tests - fix them
- ❌ Don't duplicate transcription logic across UI/CLI/MCP — call the shared Engine
- ❌ Don't route transcription through the cloud or make the agent sidecar on by default
- ❌ Don't hardcode values that should be config
- ❌ Don't catch all exceptions - be specific
