# AGENTS.md

## Project scope

Meeting Evidence Recorder is a local-first cross-platform desktop recorder for Windows and Apple Silicon macOS.

It records screen, system audio, microphone audio, and user-triggered timestamped screenshots. It produces a self-contained Meeting Evidence Bundle for downstream processing by Meeting Recording Processor (MRP).

Recorder does **not** own ASR, OCR, diarization, summarization, or final-document generation.

## Sources of truth

Use this order when instructions conflict:

1. Current code and tests.
2. `AGENTS.md`.
3. `docs/PRD.md`.
4. `docs/SDD.md`.
5. `docs/EVIDENCE_BUNDLE_SPEC.md`.
6. Current PR/work-packet acceptance criteria.
7. `docs/DELIVERY_MODEL.md`, `docs/EVIDENCE_MODEL.md`, `docs/RELEASE_MODEL.md`.

`docs/kickstart/**` is generic methodology reference, not project-specific product truth.

## Invariants

Preserve these unless the task explicitly reopens them:

- One canonical recording timeline aligns video, audio, screenshots, and events.
- Screenshot events use the timestamp of the saved recording frame, not wall-clock or hotkey callback time.
- System audio and microphone remain distinct internal sources even when MVP output is mixed audio.
- Recorder output is raw evidence; interpretation belongs downstream.
- Evidence Bundle paths are bundle-relative and safe.
- `status = completed` is written only after final media and referenced evidence validate.
- Recorder remains local-first and does not upload meeting content.
- Shared core stays platform-neutral; Windows/macOS capture details stay behind platform abstractions.

## Working rule

Start with the smallest safe harness:

1. Inspect only the directly relevant code, tests, and contract docs.
2. Reconcile the current repository with the requested observable outcome.
3. Make the smallest coherent change that proves that outcome.
4. Avoid speculative refactors, premature abstractions, unrelated cleanup, and broad test rewrites.
5. Add a specialist only when the task genuinely requires repeated domain reasoning or shared-contract interpretation.

Do not create a large permanent task inventory. Generate the next bounded work item from current repository state.

## Validation

Run focused checks first, then broader checks only when justified.

A test is evidence only if the old defect would fail it.

For platform-specific claims, use platform-relevant evidence. Do not claim Windows/macOS parity from shared unit tests alone.

Report:
- what was validated;
- what was not run;
- known gaps or assumptions.

## Handoff

Keep handoffs compact:

- objective;
- changed surfaces;
- relevant files;
- key decisions;
- validation evidence;
- checks not run;
- known gaps;
- reviewer focus.

Reference paths, tests, logs, and commit SHAs instead of copying project history.

## Git safety

For agent-controlled publication:

- branch from an explicit fetched base;
- push an explicit feature ref;
- never force-push or rewrite `main`;
- stop on unexpected divergence;
- create PRs with explicit head/base.

Merge authority is separate from implementation authority for meaningful PRs.

## Release boundary

Merging to `main` is integration, not release.

Follow `docs/RELEASE_MODEL.md` for release promotion.
