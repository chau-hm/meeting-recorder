# New Project Kickstart — Agent Bootstrap

Use this checklist when starting a new AI-assisted software project.

The goal is to establish enough governance to work safely without over-documenting a greenfield repository.

---

## Phase 0 — Define the project boundary

Create `PROJECT_CHARTER.md` with:

```text
Problem:
Primary users:
Desired outcomes:
In scope:
Explicit non-goals:
Success signals:
Known constraints:
```

Do not write implementation detail unless it is already a fixed constraint.

---

## Phase 1 — Establish architecture guardrails

Create `ARCHITECTURE_GUARDRAILS.md`.

Define only durable rules:

- canonical domain state;
- subsystem boundaries;
- authoritative data sources;
- unit / identity / state conventions;
- persistence boundaries;
- lifecycle ownership;
- security/privacy constraints;
- known forbidden shortcuts.

Do not turn this into a complete implementation plan.

Pattern references:

- Pattern 03 — Canonical Domain State
- Pattern 09 — Freeze Accepted Areas

---

## Phase 2 — Establish the delivery model

Create `DELIVERY_MODEL.md`.

Default rules:

```text
current repo truth > historical plan

deliver coherent vertical slices

smallest safe harness first

escalate only on evidence

independent review near a real merge gate
```

Pattern references:

- Pattern 01
- Pattern 02
- Pattern 06
- Pattern 10

---

## Phase 3 — Establish agent instructions

Create `AGENTS.md`.

Minimum sections:

```text
Project scope
Sources of truth
Smallest-safe-harness rule
Specialist routing rules
Before-changing-code rules
Validation policy
Evidence-integrity rules
Handoff policy
Git publication safety
Release boundary
```

Keep instructions concise enough that agents can actually load and follow them.

---

## Phase 4 — Establish evidence rules

Create `EVIDENCE_MODEL.md`.

For each important class of claim, define the nearest valid evidence.

Examples:

```text
public UI behaviour
→ user-reachable interaction test

resource lifecycle
→ same-process lifecycle evidence

API contract
→ request/response integration evidence

data transformation
→ deterministic input/output fixture

visual rendering
→ meaningful rendered-state checks + human review when needed
```

Core question:

> Would the old defect fail this evidence?

Pattern reference:

- Pattern 04

---

## Phase 5 — Establish repository-centric handoff

Create:

```text
.agents/status/CURRENT.md
```

Use it only for current integrated work that benefits a later reviewer.

Recommended fields:

```text
Work identifier:
Branch / base / head:
Observable objective:
Changed surfaces:
Decisions:
Claimed validation:
Checks not run:
Known gaps:
Reviewer focus:
Since previous review:
```

Overwrite stale work-specific content. Do not append project history.

Pattern reference:

- Pattern 05

---

## Phase 6 — Establish safe Git publication

Document:

```text
feature branch creation
explicit ref push
remote feature-ref verification
remote-main unchanged verification
PR head/base rules
fail-closed behaviour
```

Never rely on bare `git push` for agent-controlled PR publication.

Pattern reference:

- Pattern 07

---

## Phase 7 — Establish release promotion

Create `RELEASE_MODEL.md`.

At minimum define:

```text
integration branch:
release candidate representation:
production branch/environment:
required review/validation:
promotion command/process:
rollback rule:
```

Keep integration and production promotion separate.

Pattern reference:

- Pattern 08

---

## Phase 8 — Start the first real feature

Do not create a large permanent atomic task inventory.

Instead:

```text
read current repository
        ↓
reconcile stable guardrails
        ↓
identify current product/experience gap
        ↓
define the smallest coherent vertical slice
        ↓
choose the smallest safe harness
        ↓
implement
        ↓
collect falsifiable evidence
        ↓
independent merge review only when warranted
        ↓
promote a known releasable point
```

---

## Optional specialist creation rule

Create a project-local specialist only when at least one of these is true:

- the domain has non-obvious invariants;
- mistakes repeatedly cross subsystem boundaries;
- the reasoning is expensive enough to deserve reusable instructions;
- the project needs repeated independent reasoning in that domain.

Do not create specialists merely because directories exist.

Pattern reference:

- Pattern 10

---

## Bootstrap completion test

The project is sufficiently bootstrapped when a new agent can answer:

1. What problem and user outcome does this project serve?
2. What repository sources are authoritative?
3. What state/contracts must not drift?
4. How should work be sliced?
5. When should work escalate from local edit to specialist/orchestration?
6. What evidence is valid for important claims?
7. How is context handed off durably?
8. How are PR branches published safely?
9. Who makes the merge decision?
10. How does a reviewed commit become production?

If those answers are explicit, start building. Do not add governance documents merely for completeness.
