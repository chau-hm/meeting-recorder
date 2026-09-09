# Meeting Evidence Recorder

## Scope and sources

Local-first desktop evidence recorder for Windows and Apple Silicon macOS. Recorder owns
raw video/audio, screenshots and timestamped events. ASR, OCR, diarization, summaries and
documents belong downstream. No MRP runtime or shared source dependency.

Use current code/tests, this file, `docs/PRD.md`, `docs/SDD.md`, then
`docs/EVIDENCE_BUNDLE_SPEC.md` and the current work-packet acceptance criteria.
The bundle specification is the normative wire contract; report conflicts rather than
silently changing requirements. `docs/kickstart/` is methodology, not product truth.

## Boundaries and invariants

- Infrastructure references Core; Core never references filesystem implementations,
  UI, platform APIs, or encoders.
- One canonical playback timeline aligns evidence. `timestamp_ms` is integer milliseconds;
  screenshot timestamps come from the saved frame, never a callback, filename or wall clock.
- `event_id` is identity. Duplicate timestamps are valid. Preserve journal append order.
- System audio and microphone describe separate actual sources, even for mixed output.
- References are safe bundle-relative paths; bundles remain portable after copy/rename.
- `completed` is a logical commit marker after media, metadata and references validate.
  Structural fixture success must never be reported as media validation.
- Evidence remains raw and local. Do not upload meeting content.

## Working and validation

Inspect the directly relevant code, tests and contract first. Make the smallest coherent
change proving the requested observable outcome. Avoid speculative abstractions, unrelated
cleanup, empty future projects and permanent task inventories. Add specialists only for
repeated domain reasoning or shared-contract interpretation.

Run focused checks first. A test is evidence only if the defect would fail it. Report commands,
results, checks not run and genuine gaps. Platform claims require platform-relevant evidence.
Keep handoffs compact: objective, changed surfaces, decisions, files, validation and reviewer focus.

## Git and release

Branch from an explicit fetched base into a feature worktree. Push an explicit feature ref,
verify remote head and unchanged main, and create PRs with explicit head/base. Never force-push
or rewrite main; stop on unexpected divergence. Implementation authority does not grant merge
authority. Main integration is not a release; release promotion is separately authorized.
