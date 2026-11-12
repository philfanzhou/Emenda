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
dotnet build src/Emenda.sln -c Release
dotnet test src/Tests/Emenda.Domain.Tests/Emenda.Domain.Tests.csproj -c Release
dotnet test src/Tests/Emenda.Service.Tests/Emenda.Service.Tests.csproj -c Release --filter "FullyQualifiedName!~PostgresTests"
```

`ProductIntakePostgresTests` and `StandaloneIntakePostgresTests` require a task-owned
PostgreSQL admin connection in `MISTAKE_MIGRATION_TEST_CONNECTION`. They exercise durable
payload binding, concurrent Hosts, transaction rollback, cancellation and replay; do not
point this variable at a business database. Other inherited PostgreSQL suites have their
own `MISTAKE_*_TEST_CONNECTION` gates. Excluded/gated tests are not a full database pass.

For real Host + PostgreSQL + S3 acceptance, use
[Standalone Verification](docs/development/StandaloneVerification.md). Build once before
running its acceptance phase; the script does not compile or publish the service itself.

## Pull requests

- Keep changes focused and include tests for behaviour changes.
- Do not commit secrets or deployment-specific configuration.
- Update the documentation in the same change when user-observable behaviour, the HTTP
  contract or the deployment surface changes.
- The product is still being extracted from its platform origin; preserve existing
  behaviour unless the change is explicitly about the product boundary.