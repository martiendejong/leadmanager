# Releasing LeadManager

LeadManager's version is tracked in two places that are always kept in sync:

- `VERSION` (repo root) — the single source of truth.
- `src/LeadManager.Api/LeadManager.Api.csproj`'s `<Version>` — stamps the built assembly, so
  the running instance can report its own version (see `GET /api/health`).

Before deploying a release, bump the version and tag the commit:

```powershell
./scripts/bump-version.ps1              # bumps the patch version (1.0.0 -> 1.0.1)
./scripts/bump-version.ps1 -Part Minor  # or -Part Major
./scripts/bump-version.ps1 -Push        # also pushes the commit + tag to origin
```

This commits the version bump and creates a `vX.Y.Z` git tag. JengoAGI's deploy-time version
tracking reads this tag (or, failing that, the `VERSION` file) from the checked-out repo to
confirm which commit is actually running on a given environment, instead of guessing from
build timestamps and commit counts.
