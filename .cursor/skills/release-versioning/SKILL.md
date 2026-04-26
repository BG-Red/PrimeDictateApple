---
name: release-versioning
description: Defines PrimeDictate versioning and release build policy. Use when changing version numbers, preparing tagged releases, deciding whether to publish binaries, or updating GitHub Actions build/release workflows.
---
# PrimeDictate Release Versioning

## Policy

- Use semantic versioning for `Directory.Build.props` base version: `MAJOR.MINOR.PATCH`.
- Keep `MAJOR=0` until the first stable public release.
- Treat every push build as a preview build and do not manually bump for each commit.
- Create official release binaries only from tags matching `v*` (for example `v0.2.0`).

## Current flow

1. Base version is stored in `Directory.Build.props` (`Version`).
2. GitHub Actions computes version per run:
   - Tag builds: package version equals tag version (for example `v0.2.0` -> `0.2.0`).
   - Branch/PR builds: package version is preview (`<base>-ci.<run_number>`).
3. Workflow passes version metadata to:
   - `dotnet build`
   - `scripts/Publish-Windows.ps1`
   - `scripts/Build-Installers.ps1`
4. Artifacts are uploaded with version in artifact name.

## When to bump base version

- Bump **PATCH** for bug fixes with no breaking behavior change.
- Bump **MINOR** for new features or meaningful UX/behavior changes.
- Bump **MAJOR** only for breaking changes; avoid while pre-1.0 unless intentional.

## When to publish official binaries

- Publish signed official binaries when one of these is true:
  - User-facing feature milestone is complete.
  - Important bug fix should be installable for users.
  - Security/privacy fix needs distribution.
- Skip official publish when changes are internal-only (refactor/tooling/docs) unless requested.

## Release checklist

1. Update `Directory.Build.props` to intended stable version.
2. Commit and push.
3. Create tag `v<version>` on that commit.
4. Confirm GitHub Actions build succeeds and signs outputs.
5. Share resulting installer artifact(s) or attach to GitHub Release.
