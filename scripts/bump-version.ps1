<#
.SYNOPSIS
  Bumps LeadManager's version as part of a release: updates the VERSION file and the API
  csproj's <Version>, commits both, and tags the commit vX.Y.Z.

.DESCRIPTION
  Run this right before deploying a release. It keeps the two files that carry the version
  (VERSION at the repo root, and src/LeadManager.Api/LeadManager.Api.csproj's <Version>) in
  sync, then tags the commit so JengoAGI's deploy-version-tracking can tell which commit is
  actually running on a given environment.

.PARAMETER Part
  Which part of the semver to bump: Major, Minor, or Patch (default: Patch).

.PARAMETER Push
  If set, also pushes the commit and the new tag to origin.

.EXAMPLE
  ./scripts/bump-version.ps1
  Bumps the patch version (e.g. 1.0.0 -> 1.0.1), commits, and tags locally.

.EXAMPLE
  ./scripts/bump-version.ps1 -Part Minor -Push
  Bumps the minor version (e.g. 1.0.1 -> 1.1.0), commits, tags, and pushes both to origin.
#>
param(
    [ValidateSet("Major", "Minor", "Patch")]
    [string]$Part = "Patch",
    [switch]$Push
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot "VERSION"
$csprojFile = Join-Path $repoRoot "src\LeadManager.Api\LeadManager.Api.csproj"

if (-not (Test-Path $versionFile)) {
    throw "VERSION file not found at $versionFile"
}

$current = (Get-Content $versionFile -Raw).Trim()
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "VERSION file content '$current' is not a valid X.Y.Z semver string"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

switch ($Part) {
    "Major" { $major++; $minor = 0; $patch = 0 }
    "Minor" { $minor++; $patch = 0 }
    "Patch" { $patch++ }
}

$newVersion = "$major.$minor.$patch"

Set-Content -Path $versionFile -Value $newVersion -NoNewline
Add-Content -Path $versionFile -Value ""

$csprojContent = Get-Content $csprojFile -Raw
$updatedCsproj = $csprojContent -replace '<Version>[^<]*</Version>', "<Version>$newVersion</Version>"
if ($updatedCsproj -eq $csprojContent) {
    throw "Did not find a <Version> element to update in $csprojFile"
}
Set-Content -Path $csprojFile -Value $updatedCsproj -NoNewline

Write-Host "Bumped version: $current -> $newVersion"

git -C $repoRoot add $versionFile $csprojFile
git -C $repoRoot commit -m "chore: bump version to v$newVersion"
git -C $repoRoot tag "v$newVersion"

Write-Host "Committed and tagged v$newVersion"

if ($Push) {
    git -C $repoRoot push
    git -C $repoRoot push origin "v$newVersion"
    Write-Host "Pushed commit and tag to origin"
}
else {
    Write-Host "Run 'git push && git push origin v$newVersion' to publish the release."
}
