# Evidence Model

## Purpose

Validation must prove the behavior being claimed. Passing tests alone is not enough if the tests do not exercise the defect or contract in question.

Core rule:

> A test is evidence only when the old defect would fail it.

## Evidence by claim

| Claim | Nearest valid evidence |
|---|---|
| Recording media is usable | Media probe plus decode/playback integration check |
| System audio is actually captured | Controlled audible fixture/tone captured through the real platform backend |
| Microphone is actually captured | Controlled microphone/input fixture through the real backend |
| Mixed audio contains both sources | Deterministic two-source fixture and output inspection |
| Audio/video stay synchronized | Long-running timestamped A/V fixture with measured drift |
| Screenshot matches recording time | Saved image derived from the recording frame plus identical normalized frame timestamp |
| Pause excludes wall-clock pause duration | Deterministic clock/media integration test |
| Evidence Bundle is portable | Copy/rename fixture and validate/import without Recorder runtime |
| Bundle paths are safe | Traversal/absolute-path negative tests |
| `completed` means durable valid output | Finalization test proving media/assets validate before status commit |
| Crash leaves recoverable evidence | Forced process termination plus recovery inspection |
| Windows behavior works | Windows platform integration evidence |
| macOS behavior works | Apple Silicon macOS platform integration evidence |
| MRP can consume the bundle | End-to-end Recorder bundle → MRP import fixture |

## Evidence strength

Prefer the nearest layer that can falsify the claim:

```text
pure domain rule
→ unit test

serialization/path contract
→ contract/component test

capture/media lifecycle
→ integration test

OS-native behavior
→ real-platform integration test

cross-system handoff
→ end-to-end fixture
```

Do not use a broader test when a focused deterministic test proves the same claim more clearly.

## Invalid or weak evidence

Do not treat these as sufficient proof:

- file existence alone as proof of valid media;
- screenshot file size as proof of correct frame content;
- callback arrival time as proof of recording-frame timestamp;
- shared unit tests as proof of native Windows/macOS capture;
- mocked media devices as proof that OS permissions/device lifecycle work;
- warning suppression as proof that a timing or resource issue is fixed;
- lowering thresholds to make drift tests pass.

## Evidence Bundle contract

Contract changes must validate at least:

- schema parsing;
- required fields;
- forward-compatible optional fields;
- unknown event handling;
- safe relative paths;
- asset references;
- status semantics;
- timestamp boundaries;
- media consistency where applicable.

Breaking changes require an explicit schema-major decision.

## Reporting

Every implementation handoff should distinguish:

### Proven
Claims backed by executed evidence.

### Not run
Checks that were intentionally skipped or unavailable.

### Known gaps
Remaining behavior that is not proven or not implemented.

Do not upgrade an assumption into a validated claim.
