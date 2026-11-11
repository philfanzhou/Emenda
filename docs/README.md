# Emenda Documentation

Emenda is a self-hosted, AI-driven mistake-management service for learning platforms,
extracted from the Ruoyu.Study platform and now an independent open-source product.

## Authoritative product documentation

| Document | Purpose |
|---|---|
| [README.md](../README.md) | Product overview, standalone deployment, configuration and the intake API |
| [SECURITY.md](../SECURITY.md) | Reporting a vulnerability and deployment security expectations |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Local build/test workflow and pull-request expectations |
| [Standalone deployment](./development/StandaloneDeployment.md) | Container deployment, required configuration, health checks and API examples |
| [Automation and releases](./development/Automation.md) | CI/CodeQL/Dependabot workflows, GHCR image tags and the release process |
| [Product intake contract](./Integration/emenda-intake-v1.json) | Caller authentication, input limits, immutable replay and error handling for standalone intake |
| [Lifecycle contract](./Integration/mistake-http-v3.json) | Inherited lifecycle transport; Standalone authentication and rejection semantics are defined by the deployment guide |
| [Standalone verification](./development/StandaloneVerification.md) | Task-owned PostgreSQL/S3 acceptance entry point, without the platform |
| [Local acceptance record](./development/StandaloneVerificationResult.md) | Observed checks, runtime versions, tested binary hashes and unverified boundaries |

## Inherited service documentation

The directories below were inherited from the Ruoyu.Study Mistake service. They still
describe the platform-era implementation (including stale gRPC-era descriptions) and are
being rewritten for the standalone product. Treat `README.md` as authoritative for the
product surface until a document says otherwise.

| Entry | Purpose |
|---|---|
| [overview/](./overview/README.md) | Service-level overview inherited from the platform |
| [modules/](./modules/README.md) | Internal business capabilities |
| [Integration/](./Integration/README.md) | External system interactions |
| [database/](./database/README.md) | Data structures and ownership |
| [development/](./development/README.md) | Development and verification entry points |

The domain terminology inherited from the platform is in [CONTEXT.md](../CONTEXT.md).