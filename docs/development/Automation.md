# Automation and Releases

Emenda uses GitHub Actions for verification and publishing. The workflows are modelled on
the maintainer's other product repositories (Lexarbor, Doctheca, StructaDoc): pull requests
are verified, `main` publishes a moving edge image, and semantic-version tags publish a
release image plus a GitHub Release.

## Workflows

| Workflow | Triggers | What it does |
|---|---|---|
| [`ci.yml`](../../.github/workflows/ci.yml) | `push` to `main`, `pull_request` to `main`, `workflow_call`, `workflow_dispatch` | Restores and builds `src/Emenda.sln` in Release, runs the Domain tests, runs the Service tests that do not require PostgreSQL (`FullyQualifiedName!~PostgresTests`), and builds the container image without pushing it. |
| [`release.yml`](../../.github/workflows/release.yml) | Tags `v*.*.*` | Validates the semantic version, re-verifies through `ci.yml`, publishes a multi-platform (amd64/arm64) GHCR image with SBOM and provenance attestation, and creates a GitHub Release with generated notes. Merges to `main` never build or publish an image. |
| [`codeql.yml`](../../.github/workflows/codeql.yml) | Push/PR to `main`, weekly schedule, manual | CodeQL analysis for `actions` and `csharp`. |
| [`dependency-review.yml`](../../.github/workflows/dependency-review.yml) | `pull_request` to `main` | Fails a pull request that introduces a high-or-higher severity dependency change. |
| [`registry-cleanup.yml`](../../.github/workflows/registry-cleanup.yml) | Weekly schedule, manual | Deletes GHCR image versions that no tag can reach after a retention window, walking the multi-platform index and attestation referrer graph so a published image is never partially deleted. Former moving tags (default `edge`) are treated as retired, so they stop keeping their old image alive. Supports a manual dry run. |

Dependabot is configured in [`.github/dependabot.yml`](../../.github/dependabot.yml) for
NuGet, the Dockerfile base images and GitHub Actions.

## Container image tags

The image name is `ghcr.io/<repository>` (lower-cased), i.e. `ghcr.io/philfanzhou/emenda`.
Images are built and published only for version tags; merges to `main` run CI and never
build an image.

| Trigger | Tags |
|---|---|
| Tag `vX.Y.Z` | `X.Y.Z`, `X.Y`, `X`, `latest` |
| Tag `vX.Y.Z-rc.N` (prerelease) | `X.Y.Z-rc.N` only; no `latest`/major/minor tags |

A stable tag is never overwritten: publishing runs are serialised with
`concurrency: release-<ref>` and a release never announces a version whose image failed to
publish.

## Release process

1. Make sure `main` is green (CI on the latest commit) and contains the release content.
2. Create an annotated tag on a commit that is contained in `main`:
   ```bash
   git tag -a v0.1.0 -m "Emenda v0.1.0"
   git push origin v0.1.0
   ```
3. `release.yml` validates the tag, re-runs CI, publishes the multi-platform image and
   creates the GitHub Release. Check the workflow run and the release page.
4. Do not move or delete a published tag. A broken release gets a new patch version.
5. Prerelease tags (`vX.Y.Z-rc.N`) publish a prerelease image and GitHub prerelease; they
   do not move `latest`.

## Local verification

The CI steps are the same commands that can be run locally:

```bash
dotnet restore src/Emenda.sln
dotnet build src/Emenda.sln --configuration Release --no-restore
dotnet test src/Tests/Emenda.Domain.Tests/Emenda.Domain.Tests.csproj --configuration Release --no-build
dotnet test src/Tests/Emenda.Service.Tests/Emenda.Service.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName!~PostgresTests"
```

Database-backed suites are gated by their own `MISTAKE_*_TEST_CONNECTION` variables and
need dedicated disposable PostgreSQL 15 containers; they are not part of the generic CI
job. The release rehearsal (`script/build-script/06-mistake.minimal-source-rehearsal.sh`)
builds and tests the exported release closure outside the repository.

## Repository settings

- The GHCR package `ghcr.io/philfanzhou/emenda` is public and can be pulled anonymously.
- CodeQL is enabled; the first run on `main` established the code-scanning results.
- `main` is protected by the required status checks `Backend build and tests`,
  `Container image build`, `Analyze actions`, `Analyze csharp` and
  `Review dependency changes`. Force pushes and deletions are disabled and repository
  administrators are also subject to the protection, so every change to `main` arrives
  through a pull request whose checks are green. Release tags are not branch pushes and
  remain unaffected.
- Dependabot is configured for NuGet, the Dockerfile base images and GitHub Actions, and
  security updates are enabled for the repository.