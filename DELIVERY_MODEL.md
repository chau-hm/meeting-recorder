# Delivery Model

## Purpose

This project uses a lightweight, repository-centric delivery model. The goal is to keep work bounded and provable without turning project governance into a second product.

## Source order

For current work, prefer:

```text
current code/tests
→ AGENTS.md
→ PRD / SDD / Evidence Bundle spec
→ current PR acceptance criteria
→ delivery/evidence/release guidance
→ historical prompts and plans
```

Historical plans are context only. They do not override current repository truth.

## Work sizing

Use the smallest category that safely fits the change:

### Micro
Local edit with obvious impact and focused validation.

Examples:
- wording;
- simple configuration;
- local test correction.

### Focused
One bounded behavior crossing a small number of files/components.

Examples:
- screenshot event serialization;
- microphone selection state;
- one platform adapter behavior.

### Coordinated
A coherent vertical slice across multiple layers.

Examples:
- Windows display recording from capture through finalized Evidence Bundle;
- pause/resume across clock, audio, media writer, and metadata.

### High-risk
Changes to shared contracts, timing, recovery, media integrity, privacy, or release behavior.

Examples:
- canonical timeline semantics;
- Evidence Bundle breaking change;
- audio synchronization algorithm;
- crash-recovery strategy.

## Slice rule

Prefer coherent vertical outcomes over horizontal layer completion.

Good:

```text
User presses screenshot hotkey
→ recording frame is captured
→ PNG is persisted
→ event is appended
→ bundle validates
```

Avoid splitting that into unrelated model/UI/test PRs unless an intermediate PR is independently useful.

## Execution rule

For each work item:

1. Inspect current implementation and nearest tests.
2. Identify the observable gap.
3. Preserve accepted areas not required by the task.
4. Choose the smallest safe harness.
5. Implement the minimum coherent change.
6. Collect falsifiable evidence.
7. Expand validation only when risk/evidence requires it.

## Specialist routing

Route by cognitive dependency, not directory ownership.

Touching platform code does not automatically require a platform specialist. Use specialist reasoning when shared invariants, native API semantics, timing, media lifecycle, or other non-obvious contracts are actually changing.

## Review

- Micro: independent review usually unnecessary.
- Focused: review when risk or uncertainty warrants it.
- Coordinated: independent merge review recommended.
- High-risk: independent merge review required.

The reviewer inspects the actual diff and evidence; implementation summaries are claims, not proof.

## Handoff

Use repository artifacts as durable memory. Keep current-work handoff short and overwrite stale work-specific context rather than building an append-only project diary.
