# Phase 0 contract fixtures

`valid-*` means valid at **structural/referential levels 1/2 only**. These are consumer-input
samples of a completed manifest claim, not recordings emitted by a completion API.
Every `recording.mp4` here is a 51-byte text placeholder, explicitly **invalid at media level 3**.
No test may infer readability, finalization, actual duration, audio tracks, or capture success
from these files. The validator always reports `BUNDLE_MEDIA_NOT_VALIDATED` for completed
claims. The runtime manifest writer cannot publish these samples as completed without an
external media validator, and the synthetic producer leaves them `finalizing`.

PNG assets are deterministic one-pixel synthetic images. No private meeting content exists.
Audio variants exercise metadata consistency only. `invalid-*` names identify an additional
level 1/2 defect. `incomplete-session` is accepted only for explicit inspection, with a warning.

Schemas validate document shape and lexical paths. Cross-record IDs, duration bounds,
filesystem existence, symlinks and case collisions require the bundle validator. Full
media validation and full Evidence Bundle v1 Definition of Done remain outside Phase 0.
