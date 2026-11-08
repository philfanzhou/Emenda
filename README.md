# Emenda

Self-hosted, AI-driven mistake management service for learning platforms.

Emenda owns the mistake lifecycle: intake, AI-assisted image analysis, review, practice
tracking and process history. It is extracted from the [Ruoyu.Study](https://gitee.com/philfanzhou/Ruoyu.Study)
platform, where it ran as the Mistake service; the git history of that service is preserved
in this repository.

## Status

Extraction baseline, work in progress. The repository currently keeps the source layout of
the platform repository:

- `src/services/ruoyu.mistake/` — service source, versioned HTTP contract and documentation

Follow-up changes will move the code to the product layout, vendor the shared components the
service needs to build on its own, and replace the platform-specific integration points
(caller authentication, mistake intake, configuration bootstrap) with product boundaries.

Do not treat this repository as a deployable product until the README says otherwise.

## Requirements

- .NET 10 SDK
- PostgreSQL 15 (database-backed tests)
- Docker (image build; not required for the .NET build and unit tests)

## License

MIT — see [LICENSE](LICENSE).