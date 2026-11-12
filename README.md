# Emenda

[![CI](https://github.com/philfanzhou/Emenda/actions/workflows/ci.yml/badge.svg)](https://github.com/philfanzhou/Emenda/actions/workflows/ci.yml)
[![CodeQL](https://github.com/philfanzhou/Emenda/actions/workflows/codeql.yml/badge.svg)](https://github.com/philfanzhou/Emenda/actions/workflows/codeql.yml)
[![Release](https://github.com/philfanzhou/Emenda/actions/workflows/release.yml/badge.svg)](https://github.com/philfanzhou/Emenda/actions/workflows/release.yml)

Self-hosted, AI-driven mistake management service for learning platforms.

Emenda owns the mistake lifecycle: intake, AI-assisted image analysis, review, practice
tracking and process history. It is extracted from the [Ruoyu.Study](https://gitee.com/philfanzhou/Ruoyu.Study)
platform, where it ran as the Mistake service; the git history of that service is preserved
in this repository.

## Status

**Standalone baseline (product v0.1).** The service is extracted from Ruoyu.Study, moved to
its own product layout, renamed to Emenda, and can be deployed independently: it runs from
standard appsettings/environment configuration, requires no Consul, Loki, SignaCore or
Student service, authenticates callers with a per-caller credential and serves a
product-owned intake API. This product targets new, trusted backend callers with its own
database and object store; it is not a drop-in replacement for the current Ruoyu.Study service.

The product intake contract is [emenda-intake-v1](docs/Integration/emenda-intake-v1.json).
AI-autonomy roadmap work and platform-side cutover are outside this baseline. Legacy
Local/Platform modes and Student integration remain compatibility code, not deployment
prerequisites for new callers.

## Standalone mode

Configuration comes from `appsettings*.json` and environment variables; the product reads
no Consul KV and writes no Loki sink. Start the container with `./start.sh` (or the same
environment variables):

| Environment variable | Required | Purpose |
|---|---|---|
| `EMENDA_DB_CONNECTION` | yes | PostgreSQL connection string (`ConnectionStrings__Emenda`) |
| `EMENDA_SCHEMA_OWNERSHIP` | no | `Own` (default, product owns/migrates its dedicated schema); `Shared` is an advanced compatibility mode, not a new installation path |
| `EMENDA_API_KEY` | yes | Per-caller credential value (`Emenda__Security__Callers__0__ApiKey`) |
| `EMENDA_CALLER_NAME` | no | Stable caller name for audit and the intake idempotency namespace (default `platform`) |
| `OSS_INTERNAL_ENDPOINT` / `OSS_ACCESS_KEY` / `OSS_SECRET_KEY` / `OSS_BUCKET_NAME` / `OSS_PUBLIC_BASE_URL` | yes | S3-compatible object storage (`Oss__*`) |
| `VL_API_KEY` / `VL_API_BASE_URL` / `VL_MODEL` | no | OpenAI-compatible vision-language provider (`VlAnalyzer__*`) |
| `EMENDA_ADMIN_AUTHORITY` / `EMENDA_ADMIN_CLIENT_ID` / `EMENDA_ADMIN_CLIENT_SECRET` / `EMENDA_ADMIN_REDIRECT_URI` / `EMENDA_ADMIN_POST_LOGOUT_REDIRECT_URI` | no | SignaCore hosted login for the admin console (`Emenda__Admin__Oidc__*`) |

The optional **admin console** at `GET /admin` uses SignaCore administrator login and
manages dynamic credentials. Before a **new installation**, explicitly initialize the
persistent caller file with `./start.sh --init-callers` using the same image/data mount as
the service, then run `./start.sh`. Initialization never overwrites an existing file.
Missing or invalid configured credential state fails closed with HTTP 503; restore lost
state rather than bootstrapping over historical revocations. Console writes are immediate;
direct file edits require a restart. See [Admin Console](docs/development/AdminConsole.md).

Business requests under `/api/*` must carry `X-Emenda-Api-Key`. Health (`/health/live`,
`/health/ready`), the contract endpoint (`/contracts/*`) and the root endpoint stay
anonymous. Use an explicit connection to a **dedicated Emenda database**, with its own
private S3 bucket. Callers own student authorization; API keys identify trusted backends,
not isolated tenants. Standalone review rejection changes local state and preserves images;
it does not create a Student returned-upload record. See
[Standalone deployment](docs/development/StandaloneDeployment.md).

### Intake API

`POST /api/intake/uploads` (multipart/form-data) is the product-owned mistake intake
entry point. Images are stored and verified directly under the product-owned
`mistakes/intake/` prefix, without copying or deleting legacy source images. The optional
vision-language analyzer groups the images (one group when not configured). Every group,
Created log and completion receipt commits in one PostgreSQL transaction.

| Field | Required | Notes |
|---|---|---|
| `studentId` | yes | Opaque external student reference, at most 36 characters; the caller owns its authorization |
| `sourceKey` | no | Caller-scoped key matching `^[A-Za-z0-9._-]{1,64}$`; bound to the student and ordered image bytes/media types |
| `images` or `images[]` | yes | 1 to 20 files, `image/jpeg` or `image/png`, at most 15 MiB each |

Success returns `{"success":true,"sourceId":"...","items":[{"id":"...","subject":1,"grade":7,"reviewStatus":1,"imagePaths":["mistakes/..."]}]}`.
`sourceId` is opaque and is not `sourceKey`. A completed replay returns the original receipt,
not the current review state. Invalid input returns 400. Reusing a key for different content
returns 409 `source_conflict`; active processing returns 409 `source_in_progress`.
Storage/processing failures return 500, never partial success: retry the **unchanged** keyed
request after the dependency recovers. Product objects remain available for recovery.
Without `sourceKey`, automatic retries are not idempotent. The machine-readable contract is
served anonymously at `GET /contracts/emenda-intake-v1.json`.

## Layout

- `src/` — service solution (`Host`, `Service`, `Domain`, `Database`, `StudentClient`, `Tests`)
- `docs/` — versioned HTTP contract and documentation
- `manifest/` — minimal source package manifest used by the build rehearsal
- `vendor/` — shared components vendored from Ruoyu.Study and maintained independently here
- `start.sh` — container start entry point

## Build

```bash
dotnet build src/Emenda.sln -c Release
dotnet test src/Tests/Emenda.Domain.Tests/Emenda.Domain.Tests.csproj -c Release
```

The database-backed suites need PostgreSQL 15 and a dedicated connection; the image build
needs Docker.

For real PostgreSQL + S3 acceptance, use the repository-owned
[standalone verification entry point](docs/development/StandaloneVerification.md).

## Container images

`release.yml` publishes multi-platform (amd64/arm64) images to GitHub Container Registry
for version tags only; merges to `main` run CI and do not build images.

| Tag | Meaning |
|---|---|
| `ghcr.io/philfanzhou/emenda:latest` | latest stable release |
| `ghcr.io/philfanzhou/emenda:X.Y.Z` (also `X.Y`, `X`) | stable semantic version |
| `ghcr.io/philfanzhou/emenda:X.Y.Z-rc.N` | prerelease |

Every tag also produces a GitHub Release with generated notes. See
[Automation](docs/development/Automation.md) for the workflow contract and the release
process.

## License

MIT — see [LICENSE](LICENSE).