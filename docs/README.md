# Emenda Documentation

Emenda is a self-hosted, AI-driven mistake-management service for learning platforms,
extracted from the Ruoyu.Study platform and now an independent open-source product.

## Authoritative product documentation

| Document | Purpose |
|---|---|
| [README.md](../README.md) | Product overview, configuration, intake paths, contracts and schema |
| [SECURITY.md](../SECURITY.md) | Reporting a vulnerability and deployment security expectations |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Local build/test workflow and pull-request expectations |
| [Deployment](./development/Deployment.md) | Container deployment, required configuration, health checks and API examples |
| [Automation and releases](./development/Automation.md) | CI/CodeQL/Dependabot workflows, GHCR image tags and the release process |
| [Lifecycle contract](./Integration/mistake-http-v8.json) | Current `/api/mistakes` lifecycle wire (review, practice, process logs, presigned and sized image reads, conversation collection) |
| [Display sizes](./Integration/ImageVariants.md) | Image kinds, thumbnail sizes, generation, automatic backfill and cleanup with task deletion |
| [Standalone verification](./development/StandaloneVerification.md) | Task-owned PostgreSQL/S3 acceptance entry point (still targets the removed `/api/intake/uploads`; needs porting to extraction) |
| [Local acceptance record](./development/StandaloneVerificationResult.md) | Observed checks, runtime versions, tested binary hashes and unverified boundaries |
| [Automatic extraction](./modules/AutomaticExtraction.md) | Multi-image grouping, AI judgment, asynchronous intake, admin correction and retained history |
| [Extraction HTTP contract](./Integration/emenda-extraction-v1.json) | Asynchronous intake, caller-scoped revisions, and administrative review |
| [Paired AI Tutor source](../integrations/ai-tutor/README.md) | Exact upstream baseline and complete shared-provider implementation patch |

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
