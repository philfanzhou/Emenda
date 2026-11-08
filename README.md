# Emenda

Self-hosted, AI-driven mistake management service for learning platforms.

Emenda owns the mistake lifecycle: intake, AI-assisted image analysis, review, practice
tracking and process history. It is extracted from the [Ruoyu.Study](https://gitee.com/philfanzhou/Ruoyu.Study)
platform, where it ran as the Mistake service; the git history of that service is preserved
in this repository.

## Status

Extraction baseline, work in progress. The source has been moved to the product layout and
the components the service needs to build on its own have been vendored into this
repository.

**Standalone mode is implemented:** the service runs from standard
appsettings/environment configuration, requires no Consul, Loki, SignaCore or Student
service, and authenticates callers with a per-caller credential. The platform-specific
intake/authorization code from Ruoyu.Study remains available only in the legacy modes and is
being replaced by the product intake API. Do not treat this repository as a deployable
product until the README says otherwise.

## Standalone mode

Configuration comes from `appsettings*.json` and environment variables; the product reads
no Consul KV and writes no Loki sink. Start the container with `./start.sh` (or the same
environment variables):

| Environment variable | Required | Purpose |
|---|---|---|
| `EMENDA_DB_CONNECTION` | yes | PostgreSQL connection string (`ConnectionStrings__Emenda`) |
| `EMENDA_API_KEY` | yes | Per-caller credential value (`Emenda__Security__Callers__0__ApiKey`) |
| `EMENDA_CALLER_NAME` | no | Caller name recorded for audit (default `platform`) |
| `OSS_INTERNAL_ENDPOINT` / `OSS_ACCESS_KEY` / `OSS_SECRET_KEY` / `OSS_BUCKET_NAME` / `OSS_PUBLIC_BASE_URL` | yes | S3-compatible object storage (`Oss__*`) |
| `VL_API_KEY` / `VL_API_BASE_URL` / `VL_MODEL` | no | OpenAI-compatible vision-language provider (`VlAnalyzer__*`) |

Business requests under `/api/*` must carry `X-Emenda-Api-Key`. Health (`/health/live`,
`/health/ready`), the contract endpoint (`/contracts/*`) and the root endpoint stay
anonymous. The service applies database migrations automatically on startup.

## Layout

- `src/` — service solution (`Host`, `Service`, `Domain`, `Database`, `StudentClient`, `Tests`)
- `docs/` — versioned HTTP contract and documentation
- `manifest/` — minimal source package manifest used by the build rehearsal
- `vendor/` — shared components vendored from Ruoyu.Study and maintained independently here
- `start.sh` — container start entry point

## Build

```bash
dotnet build src/Ruoyu.Study.Mistake.sln -c Release
dotnet test src/Tests/Ruoyu.Study.Mistake.Domain.Tests/Ruoyu.Study.Mistake.Domain.Tests.csproj -c Release
```

The database-backed suites need PostgreSQL 15 and a dedicated connection; the image build
needs Docker.

## License

MIT — see [LICENSE](LICENSE).