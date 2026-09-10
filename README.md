# Meeting Evidence Recorder — Phase 0 + macOS capture slice

The repository contains the .NET 10 contract foundation for Meeting Evidence Bundle v1 and
the first native macOS recording slice.
Product and architecture live in [PRD](docs/PRD.md), [SDD](docs/SDD.md) and the
[normative bundle specification](docs/EVIDENCE_BUNDLE_SPEC.md). `docs/Spec.md` remains empty.

```sh
dotnet --info
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Core contains manifest/event models and pure contract checks. Infrastructure owns UTF-8
JSON, flushed append journals, atomic manifest replacement, safe path resolution and level
1/2 validation. Unknown optional fields and unknown event payloads are preserved. Unknown
events are skipped for screenshot processing; append order and duplicate timestamps survive.
The portable contract layer uses standard .NET libraries. The macOS runtime additionally uses
ScreenCaptureKit through the small native shim and an explicitly configured FFmpeg/FFprobe
installation. Test packages are pinned and locked;
`JsonSchema.Net` is test-only, justified by the Phase 0 requirement to validate fixtures against
Draft 2020-12 schemas. Tests use its format assertions and do not fetch remote schemas.

`BundleValidator.Read` defaults to strict completed input. `InspectIncomplete` is explicit and
returns a non-completed warning; malformed journals still fail. `IsStructurallyValid` covers
levels 1/2 only and `MediaValidated` remains false. See [fixture limitations](tests/fixtures/README.md).

`ManifestWriter.WriteActive` rejects completed state. `CommitCompleted` requires exclusive
bundle ownership, a finalizing candidate, valid metadata/references and an explicit
`ICompletionMediaValidator`. The macOS runtime supplies an FFprobe-backed implementation which
probes finalized video, duration, codecs, timestamps and audio consistency before this API can
publish completed. Contract tests still verify rejection without pretending synthetic text is
MP4.

Persistence uses a single journal writer, serializes each record before append, and flushes
to disk before reporting success. Any write failure faults that writer. Readers diagnose
malformed lines and unterminated final records without repairing source evidence. Manifests
use a same-directory temporary file, flush and rename. This provides atomic file replacement;
power-loss durability of the directory entry remains filesystem-dependent.

Paths use `/`, reject ambiguous dot segments, reserved Windows names/characters, drive/UNC
paths, trailing dots/spaces and symlink/reparse references. Resolution assumes exclusive
producer ownership or a stable consumer directory; it is not an OS-handle-based defense against
a concurrently hostile process swapping directory entries. Required assets must not differ
only by case. The schema handles lexical checks; filesystem checks remain in Infrastructure.

The spec labels nested descriptive video/audio/application/platform fields as recommended.
Their objects remain required; omitted descriptions are not fabricated. `recording.file` is
required for completed; `events_file` is required after initialization. Active/incomplete
manifests can omit unavailable final media and use null duration. Known supplied audio modes
must agree with supplied source flags; future source/output modes remain compatible.

The current implementation also contains the first native recording vertical slice for Apple
Silicon macOS. The `MeetingEvidenceRecorder.MacSmoke` development harness enumerates displays
and records a selected display plus ScreenCaptureKit system audio into a real H.264/AAC
`recording.mp4` inside a schema-v1 Evidence Bundle. The shared runtime preserves native media
timestamps on one monotonic recording timeline, uses bounded capture queues, writes recoverable
work media under `.work/`, and only publishes `completed` after an `ffprobe`-backed media
validation.

The macOS capture shim is built with the installed Xcode command-line tools and currently
requires macOS 15.0 or later because it explicitly disables microphone capture through the
available ScreenCaptureKit configuration API. Build the solution on macOS with Xcode installed.
The smoke harness requires absolute paths to FFmpeg and FFprobe (or the Homebrew defaults
`/opt/homebrew/bin/ffmpeg` and `/opt/homebrew/bin/ffprobe`):

```sh
dotnet run --project tools/MeetingEvidenceRecorder.MacSmoke -- --list-displays
dotnet run --project tools/MeetingEvidenceRecorder.MacSmoke -- \
  --record --display <display-id> --output ./bundles --duration 15
```

The work-container choice (`.work/recording.partial.mkv`) is an internal recovery detail, not
part of the Evidence Bundle schema. Microphone capture, screenshots, hotkeys, Avalonia UI,
Windows capture, packaging, signing, and MRP integration remain out of scope for this slice.
