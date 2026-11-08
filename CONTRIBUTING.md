# Contributing to Emenda

Emenda is a self-hosted mistake-management service for learning platforms. This guide covers
the local workflow; the product behaviour is described in [`README.md`](README.md) and
[`docs/`](docs/).

## Prerequisites

- .NET 10 SDK
- PostgreSQL 15 for the database-backed tests
- Docker for the image build

## Build and test

```bash
dotnet build src/Ruoyu.Study.Mistake.sln -c Release
dotnet test src/Tests/Ruoyu.Study.Mistake.Domain.Tests/Ruoyu.Study.Mistake.Domain.Tests.csproj -c Release
```

Database-backed Service tests require a dedicated PostgreSQL admin connection supplied
through `MISTAKE_MIGRATION_TEST_CONNECTION`; without it the PostgreSQL suites are gated and
the remaining tests still run.

## Pull requests

- Keep changes focused and include tests for behaviour changes.
- Do not commit secrets or deployment-specific configuration.
- Update the documentation in the same change when user-observable behaviour, the HTTP
  contract or the deployment surface changes.
- The product is still being extracted from its platform origin; preserve existing
  behaviour unless the change is explicitly about the product boundary.