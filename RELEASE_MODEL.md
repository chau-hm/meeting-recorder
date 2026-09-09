# Release Model

## Purpose

Keep feature integration separate from user-facing release.

## Branches and release identity

```text
feature branches
    ↓
main
    ↓
reviewed release commit
    ↓
version tag
    ↓
GitHub Release artifacts
```

`main` is the integration branch. A merge to `main` is not automatically a release.

The release candidate is an exact reviewed commit SHA, optionally represented by an `-rc` tag.

The production release is an immutable version tag and its published artifacts.

## Target artifacts

Initial targets:

- Windows x64 self-contained package;
- Apple Silicon macOS self-contained `.app` package/archive.

Future platform targets do not change the release model.

## Release gate

Before promoting a release commit, require evidence appropriate to the changed surfaces.

At minimum:

- automated tests pass;
- Evidence Bundle contract tests pass;
- release build succeeds;
- Windows smoke/integration validation for Windows claims;
- macOS smoke/integration validation for macOS claims;
- no known media-integrity or privacy blocker.

When signing/notarization is introduced, successful signing/notarization becomes part of the macOS release gate.

## Promotion

Release from the exact reviewed commit.

Do not define release as “whatever `main` points to now”.

Preferred sequence:

```text
select reviewed SHA
→ run release validation
→ create version tag on that SHA
→ build artifacts from the tag
→ publish GitHub Release
→ verify published artifacts
```

Parallel work may continue on `main` while a release commit is being validated.

## Versioning

Use semantic versioning once public releases begin:

```text
MAJOR.MINOR.PATCH
```

Evidence Bundle schema version is independent from application version.

## Rollback

Desktop releases are immutable.

If a release is bad:

- keep the previous known-good release available;
- mark/withdraw the broken release if appropriate;
- fix forward with a new patch release.

Do not move an existing release tag to a different commit.

## Git safety

Release promotion must not require force-pushing, rewriting `main`, or resetting a production branch to a moving integration branch.
