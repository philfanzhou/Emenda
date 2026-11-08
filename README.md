# Emenda

Self-hosted, AI-driven mistake management service for learning platforms.

Emenda owns the mistake lifecycle: intake, AI-assisted image analysis, review, practice
tracking and process history. It is extracted from the [Ruoyu.Study](https://gitee.com/philfanzhou/Ruoyu.Study)
platform, where it ran as the Mistake service; the git history of that service is preserved
in this repository.

## Status

Extraction baseline, work in progress. The source has been moved to the product layout and
the components the service needs to build on its own have been vendored into this
repository. The platform-specific integration points (caller authentication, mistake intake,
configuration bootstrap) and the Emenda naming are follow-up changes.

Do not treat this repository as a deployable product until the README says otherwise.

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