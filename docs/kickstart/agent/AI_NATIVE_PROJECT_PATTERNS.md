# AI-Native Project Pattern Library

## Purpose

This document defines reusable operating patterns for AI-assisted software projects.

It is intentionally domain-agnostic. Product-specific architecture belongs in the project repository, not here.

Use these patterns to bootstrap project governance, delivery, evidence, agent routing, Git safety, and release flow.

---

## Core Operating Principles

1. **Experience and product outcomes drive direction.**
2. **Long-lived specs guard contracts; they do not act as a permanent execution queue.**
3. **Current repository state outranks stale plans and historical prompts.**
4. **Deliver coherent vertical slices, not disconnected horizontal layers.**
5. **Use the smallest safe execution harness that can prove the requested change.**
6. **A claim is trustworthy only when supported by falsifiable evidence.**
7. **Implementation, merge review, Git publication, and production release are separate authorities and gates.**

---

# Pattern 01 — Baseline Specs + Current Repo Truth + Risk-Adaptive Delivery

## Intent

Keep durable product and architecture contracts without allowing stale planning artifacts to control current execution.

## Durable sources

Typical durable sources:

- project charter / PRD;
- architecture guardrails / SDD;
- ADRs;
- repository code and tests;
- agent instructions and skills.

## Execution rule

At the start of each meaningful work item, reconcile:

```text
stable intent / contracts
        +
current repository state
        +
current observable behaviour
        +
current gap
        ↓
smallest coherent PR intent
```

Do **not** treat an old task inventory as a permanent execution queue.

## Required behaviour

- Inspect current code and tests before implementing.
- Preserve stable product and architecture contracts unless the request explicitly reopens them.
- Regenerate the immediate execution plan from current evidence.
- Prefer a current PR/work-packet micro-spec over historical prompts.

## Anti-patterns

- blindly executing an old numbered task;
- allowing historical prompts to override current code;
- maintaining a huge append-only implementation queue that becomes stale;
- duplicating long-lived architectural rules inside ephemeral task prompts.

## Stop condition

Planning is sufficient when the current repository, current gap, bounded objective, constraints, and validation strategy are explicit.

---

# Pattern 02 — Coherent Vertical Slice PR

## Intent

Ensure each merge advances the product in a user-observable, internally coherent way.

## Preferred shape

```text
Visible → Operable → Coherent → Verifiable
```

A slice may include data, domain logic, rendering/UI, tests, and task semantics if those pieces are required to create one coherent outcome.

## Required behaviour

- Split work by observable outcome, not by technical layer alone.
- Each PR should have one concise outcome statement.
- Include enough cross-layer work to make the slice meaningful.
- Keep unrelated future capability out of the slice.

## Anti-patterns

```text
PR A: all data models
PR B: all domain logic
PR C: all UI
PR D: all tests
```

when no individual PR yields a coherent system state.

## Acceptance test

A reviewer should be able to describe the PR in terms of a user/system behaviour, not only in terms of a layer that was completed.

---

# Pattern 03 — Canonical Domain State → Derived Views

## Intent

Prevent drift between multiple representations of the same domain truth.

## Rule

One canonical domain state owns the meaning.

All secondary representations derive from it:

```text
Canonical domain state
├── UI / 2D view
├── 3D / renderer
├── reports / readouts
├── task / rule evaluation
└── diagnostics / exports
```

## Required behaviour

- Define one source of truth for state and invariants.
- Keep units, sign conventions, calibration, business rules, and transformations centralized.
- Derive views rather than reinterpreting semantics inside presentation layers.
- Keep physical/domain truth separate from display-only caps or formatting.

## Anti-patterns

- duplicate formulas in UI and backend;
- local "corrections" in renderer code;
- task logic maintaining its own version of domain state;
- multiple unit-conversion helpers with slightly different semantics.

## Escalation trigger

Any change to canonical state or a shared semantic contract should receive broader review than a local presentation edit.

---

# Pattern 04 — Evidence Integrity

## Intent

Ensure that tests and validation actually prove the claimed behaviour.

## Evidence test

For each important claim, ask:

1. What exactly is being claimed?
2. What observable evidence would prove it?
3. Would the old defect fail this evidence?
4. Can mocks, reloads, injected values, proxy attributes, warning suppression, or weak metrics accidentally make the evidence pass?
5. Is the test layer appropriate?

## Core rule

> A test is evidence only when the old defect would fail it.

## Common invalid evidence

- full page reload used to prove client-side lifecycle cleanup;
- directly injected state used to prove public reachability;
- prop-mirrored DOM attribute used to prove internal resource state;
- screenshot byte size used as the only rendering proof;
- broad warning suppression;
- lowering acceptance thresholds instead of fixing the defect.

## Required behaviour

- Prefer the nearest falsifiable evidence.
- Use user-reachable values and flows when proving public behaviour.
- Distinguish "check executed" from "claim proven".
- Report checks not run.

---

# Pattern 05 — Repository-Centric Durable Context

## Intent

Use the repository as durable project memory and keep agent handoffs compact.

## Durable context hierarchy

```text
Repository truth
  code · tests · instructions · skills · ADRs · commits

Current-work navigation
  PR metadata · CURRENT.md · focused logs

Agent handoff
  objective · delta · evidence · gaps · commit SHA
```

## Required behaviour

- Reference files, paths, tests, logs, and commit SHAs instead of pasting entire histories.
- Keep current-work handoff files compact and overwritten, not append-only.
- Treat implementation handoff summaries as claims, not authoritative truth.
- Reviewers must independently inspect relevant code/diff/evidence.

## Anti-patterns

- copying every prior prompt into the next prompt;
- long recursive completion reports;
- one status file per PR;
- treating `CURRENT.md` as a changelog or source of truth.

---

# Pattern 06 — Separate Implementation Authority from Merge Authority

## Intent

Avoid self-certifying merge decisions for meaningful PRs.

## Roles

### Implementation authority

May:

- implement;
- run validation;
- write handoff/status;
- claim readiness.

### Merge authority

Must:

- independently inspect the actual branch/diff;
- validate merge-critical evidence;
- challenge implementation claims;
- issue a verdict.

## Suggested verdicts

- Ready to merge
- Ready after minor fixes
- Not ready

## Use by risk

```text
Micro edit     → no independent merge review required
Focused fix    → optional unless explicitly requested
Standard PR    → independent review near merge gate
High-risk PR   → independent review required
```

## Anti-pattern

Requiring an independent reviewer after every tiny edit. Independent review is a merge-gate mechanism, not universal ceremony.

---

# Pattern 07 — Fail-Closed VCS Operations

## Intent

Make agent-driven branch publication safe and explicit.

## Branch creation

Create PR branches from an explicit fetched base without inheriting a dangerous upstream:

```bash
git fetch origin
git switch --no-track -c <feature-branch> origin/main
```

## Publication sequence

```text
validate current branch and base
        ↓
record remote main SHA
        ↓
explicit feature-ref push
        ↓
verify remote feature ref == local HEAD
        ↓
verify remote main SHA unchanged
        ↓
create PR with explicit head/base
```

Canonical explicit push shape:

```bash
git push -u origin "HEAD:refs/heads/<feature-branch>"
```

## Fail-closed conditions

Stop when:

- current branch is wrong;
- the feature ref does not match local HEAD;
- the remote branch diverged unexpectedly;
- remote main moved unexpectedly during the publication operation.

## Prohibited automatic recovery

Do not automatically:

- reset branches;
- force-push;
- force-with-lease;
- rewrite main;
- guess the desired destination from upstream configuration.

Report the before/after state and stop.

---

# Pattern 08 — Integration ≠ Review ≠ Release

## Intent

Keep continuous integration separate from production promotion.

## Model

```text
feature PRs
    ↓
main / integration branch
    ↓
reviewed releasable SHA or release branch
    ↓
explicit promotion
    ↓
production
```

## Required behaviour

- Allow `main` to continue integrating work.
- Pin a known releasable SHA or release branch while release validation is in progress.
- Treat production promotion as a deliberate operation.
- Do not define "release" as "whatever `main` currently points to".

## Benefit

Parallel work can continue without forcing production to wait for all large PRs to finish.

## Anti-patterns

- automatically merging current main into production after every feature merge;
- coupling feature integration and production deployment;
- resetting production to chase a moving main.

---

# Pattern 09 — Freeze Accepted Areas

## Intent

Protect accepted design, behaviour, and architecture from accidental rework.

## Lifecycle

```text
Explore
  ↓
Review
  ↓
Accept
  ↓
Freeze as constraint
  ↓
Expand only the remaining gap
```

## Required behaviour

- Explicitly identify accepted/frozen areas.
- Preserve them in later work unless a new requirement reopens them.
- When reopening, state why and which constraints are changing.
- Avoid broad refactoring during a bounded fix.

## Applies to

- visual design;
- interaction behaviour;
- API contracts;
- architecture;
- task semantics;
- test invariants;
- content that has been explicitly accepted.

## Anti-patterns

- "while I am here" redesigns;
- proactive generalization unrelated to the request;
- cosmetic cleanup that changes accepted behaviour.

---

# Pattern 10 — Route by Cognitive Dependency, Not File Ownership

## Intent

Use specialists only when the task depends on their reasoning, invariants, or shared contracts.

## Rule

```text
touches a domain
≠
requires that domain specialist
```

Ask:

> Does this task require domain reasoning, shared-contract interpretation, or invariant changes?

If **no**:

```text
local inspection → minimal edit → focused evidence
```

If **yes**:

```text
load the one relevant specialist → focused reasoning → implementation
```

## Examples

- changing a label inside a renderer-adjacent component: no renderer specialist;
- changing render-target ownership: renderer specialist;
- changing an already-established domain constant: possibly local/focused;
- deriving a new result from domain constraints: domain specialist.

## Anti-patterns

- assigning specialists solely by directory;
- loading every specialist "just in case";
- equating file ownership with reasoning ownership.

---

# Combined Operating Model

Use the patterns in this order when bootstrapping or planning work:

```text
1. Direction
   Pattern 01 — current repo + stable guardrails

2. Slice
   Pattern 02 — coherent vertical outcome
   Pattern 09 — preserve already accepted areas

3. Architecture
   Pattern 03 — identify canonical state and invariants

4. Harness
   Pattern 10 — route only by cognitive dependency

5. Context
   Pattern 05 — pass current delta, not full history

6. Evidence
   Pattern 04 — prove the claim with falsifiable evidence

7. Merge
   Pattern 06 — independent review when a real merge verdict is needed

8. Git publication
   Pattern 07 — explicit and fail-closed

9. Release
   Pattern 08 — deliberate promotion from a known releasable point
```

---

# Minimal Project Bootstrap

A new AI-assisted project usually does not need a large document set.

Recommended minimum:

```text
PROJECT_CHARTER.md
ARCHITECTURE_GUARDRAILS.md
DELIVERY_MODEL.md
AGENTS.md
EVIDENCE_MODEL.md
RELEASE_MODEL.md
docs/adr/
.agents/status/CURRENT.md
```

Create domain-specific skills only when repeated reasoning needs justify them.

Do not pre-manufacture a huge atomic task inventory.

Generate current work from:

```text
current repository
+ current gap
+ current product/experience target
→ bounded work item
```
