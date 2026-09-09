# Meeting Evidence Recorder — Phase 0

Standalone .NET 10 contract foundation for the Meeting Evidence Bundle v1.
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
Runtime dependencies are standard .NET libraries only. Test packages are pinned and locked;
`JsonSchema.Net` is test-only, justified by the Phase 0 requirement to validate fixtures against
Draft 2020-12 schemas. Tests use its format assertions and do not fetch remote schemas.

`BundleValidator.Read` defaults to strict completed input. `InspectIncomplete` is explicit and
returns a non-completed warning; malformed journals still fail. `IsStructurallyValid` covers
levels 1/2 only and `MediaValidated` remains false. See [fixture limitations](tests/fixtures/README.md).

`ManifestWriter.WriteActive` rejects completed state. `CommitCompleted` requires exclusive
bundle ownership, a finalizing candidate, valid metadata/references and an explicit
`ICompletionMediaValidator`. No default successful media validator is supplied. A later media
implementation must probe finalized video, duration, timestamps and audio consistency before
this API can publish completed. Tests verify rejection without pretending synthetic text is MP4.

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

This PR follows the explicitly requested contract-only Phase 0, which is narrower than SDD
sections 94–95. It implements no recording clock/runtime, capture, UI, encoder, hotkey, or MRP
integration. It does not complete the full bundle v1 or Recorder MVP Definition of Done.
