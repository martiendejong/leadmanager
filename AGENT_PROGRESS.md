# Agent Progress

## 2026-08-27 - task 839
Done: added deploy-time version tracking - root VERSION file (1.0.0), matching csproj
<Version>, version field on GET /api/health, and scripts/bump-version.ps1 to bump both
files + create a vX.Y.Z git tag on release. Documented in RELEASING.md.
Verified: build clean; ran bump-version.ps1 in an isolated scratch copy (VERSION ->
1.0.1, csproj synced, commit + tag created, `git describe --tags` resolved it); started
the API locally and GET /api/health returned {"version":"1.0.0"}.
Left: nothing - JengoAGI's VersionTrackingService will pick up the git tag (or VERSION
file as fallback) on the next check.
