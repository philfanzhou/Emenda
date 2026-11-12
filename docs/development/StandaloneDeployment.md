# Standalone Deployment

Emenda serves new, trusted backend callers with a dedicated PostgreSQL database and a
private S3-compatible bucket. `Emenda:Runtime:Mode=Standalone` uses appsettings/environment
configuration, without Consul, Loki or Student. The optional admin console uses SignaCore;
business API calls do not require it. The image defaults to the Standalone environment.

## Configuration

| Purpose | `start.sh` environment | Host configuration |
|---|---|---|
| Dedicated PostgreSQL connection | `EMENDA_DB_CONNECTION` (required) | `ConnectionStrings:Emenda` |
| Schema ownership | `EMENDA_SCHEMA_OWNERSHIP` (default `Own`) | `Emenda:Database:SchemaOwnership` |
| Static caller credential | `EMENDA_API_KEY` (required) | `Emenda:Security:Callers:0:ApiKey` |
| Stable caller name | `EMENDA_CALLER_NAME` (default `platform`) | `Emenda:Security:Callers:0:Name` |
| Persistent credential mount | `EMENDA_DATA_DIR` (default named volume `emenda-data`) | file `/app/data/emenda-callers.json` |
| Internal S3 endpoint | `OSS_INTERNAL_ENDPOINT` (required) | `Oss:InternalEndpoint` |
| Internal TLS | `OSS_INTERNAL_SECURE` (default `false`) | `Oss:InternalSecure` |
| S3 credentials | `OSS_ACCESS_KEY`, `OSS_SECRET_KEY` (required) | `Oss:AccessKey`, `Oss:SecretKey` |
| Private bucket | `OSS_BUCKET_NAME` (default `emenda`) | `Oss:BucketName` |
| Public pre-signed URL base | `OSS_PUBLIC_BASE_URL` (required) | `Oss:PublicBaseUrl` |
| Optional vision provider | `VL_API_KEY`, `VL_API_BASE_URL`, `VL_MODEL` | `VlAnalyzer:*` |
| Host port | `EMENDA_HTTP_PORT` (default `5007`) | published container port `5007` |
| Image | `EMENDA_IMAGE` (default local `emenda:latest`) | container image |

The connection is explicit: Standalone never falls back to the platform's example connection.
Callers must be trusted application backends. `X-Emenda-Api-Key` identifies the backend, not
a student or a tenant boundary; the backend must authorize every student and resource it
accesses. Anonymous API mode is for local development only.

The bucket stays private. Returned pre-signed URLs are short-lived bearer capabilities;
configure the public endpoint/proxy so the signed host and object path reach the same bucket.

## Initialize a new installation

Configure the image and persistent mount first, then explicitly create an empty caller
registry **once**:

```bash
export EMENDA_IMAGE='ghcr.io/philfanzhou/emenda:latest'
export EMENDA_DATA_DIR='emenda-data'
./start.sh --init-callers
```

This command creates `{"version":1,"callers":[]}` exclusively; it fails instead of
replacing an existing file. Service startup never silently initializes missing credential
state. The static credential supplied below is merged with this registry; rotations and
deletions retain durable suppression of any predecessor. Preserve the credential volume.
If it is lost or damaged, restore it and restart. Creating a fresh empty registry or
restoring an old snapshot can undo historical revocations: also rotate/remove deployment
static keys before such an explicit recovery. See [Admin Console](AdminConsole.md).

Configure secrets in your deployment environment, not in source control:

```bash
export EMENDA_DB_CONNECTION='Host=postgres;Port=5432;Database=emenda;Username=emenda;Password=<secret>'
export EMENDA_API_KEY='<random 32+ character secret>'
export EMENDA_CALLER_NAME='learning-backend'
export OSS_INTERNAL_ENDPOINT='seaweedfs:8333'
export OSS_ACCESS_KEY='<secret>'
export OSS_SECRET_KEY='<secret>'
export OSS_BUCKET_NAME='emenda'
export OSS_PUBLIC_BASE_URL='https://example.internal/oss'
./start.sh
```

`start.sh` validates required variables and the presence of the credential file before
replacing its named container. A bad registry causes business authentication to return 503,
not a fallback to old static credentials. Management changes are immediate; external file
edits require a restart. For static-only deployments outside `start.sh`, explicitly set
`Emenda:Security:DynamicCallers:Path` to an empty value and manage keys through deployment
configuration only.

The optional admin console configuration is documented in [Admin Console](AdminConsole.md).
No identity-provider configuration is needed to use the business API.

## Schema ownership and upgrades

New installations use `Own` (the default). Startup transactionally creates or upgrades the
product schema, including migration `20261111000000_AddProductIntakeRequests` (version 15).
An existing Emenda version-14 database is a supported input. Existing mistake IDs, logs,
practice data and object paths are preserved; new product requests get durable receipts.
Back up the complete database and credential volume before upgrading a deployed instance.

A nonempty `product_intake_request` table blocks downgrading past its creation: dropping
these records would lose payload binding and replay guarantees. Recover by restoring a
consistent database/object/credential backup rather than deleting receipts or stamping
migration history manually. Intake receipts are creation facts; deleting or reviewing an
item does not rewrite the original receipt.

Previous product uploads have no authenticated caller/payload receipt and cannot be safely
adopted into the new idempotency namespace. Existing items remain accessible through their
IDs/source references. Do not blindly resubmit a historical upload as new intake; reconcile
its existing outcome before choosing a new request key.

`Shared` remains a compatibility option, **not** the recommended path for new callers.
It issues no DDL and checks only migration-history coverage, not full schema compatibility
or absence of business writes. The current Ruoyu.Study migration chain has diverged; matching
migration counts do not establish compatibility. Background reanalysis/cleanup workers are
not disabled by `Shared`. This guide does not authorize or describe a platform cutover.

## Intake and lifecycle

Use `POST /api/intake/uploads` with `X-Emenda-Api-Key` and multipart fields:

| Field | Constraint |
|---|---|
| `studentId` | Required, nonblank, maximum 36 characters |
| `sourceKey` | Optional, `^[A-Za-z0-9._-]{1,64}$`; strongly recommended for retries |
| `images` or `images[]` | 1–20 JPEG/PNG files, each 1 byte through 15 MiB |

```bash
curl -sS \
  -H "X-Emenda-Api-Key: $EMENDA_API_KEY" \
  -F studentId=student-123 \
  -F sourceKey=homework-2026-11-11-001 \
  -F images=@page-1.jpg \
  -F images=@page-2.jpg \
  http://127.0.0.1:5007/api/intake/uploads
```

The idempotency namespace is the stable authenticated caller **name**, independent of its
rotating key. The request binds the exact student ID, ordered image bytes and normalized
media types; filenames are not identity. `image/jpg` normalizes to `image/jpeg` and media
parameters are ignored. Another caller can use the same source key independently.

Images are written directly to deterministic `mistakes/intake/` keys and read back for
SHA-256 verification. The service never deletes an intake image while creating the batch.
A short transaction persists the payload binding; the processing transaction holds a
PostgreSQL advisory lock and commits every grouped item, Created log and original response
together. A failed/unknown commit is not reported as success. Repeating the unchanged
request finds the completed receipt or resumes with the same product objects. A transient
failed request may leave unreferenced product objects; they are retained for recovery,
not automatically garbage-collected.

| HTTP result | Caller action |
|---|---|
| 200 | Entire batch committed, or original receipt replayed; `sourceId` is opaque, not `sourceKey` |
| 400 `invalid_request` | Correct invalid input; no intake binding was created |
| 409 `source_conflict` | Key is already bound to different input; do not change a retry's payload |
| 409 `source_in_progress` | Retry the unchanged keyed request after the active attempt finishes |
| 500 `vl_failed` / `processing_failed` | Completion not confirmed; recover dependency and retry unchanged |
| 503 | Credential registry unavailable; restore/repair it and restart before retrying |

Without `sourceKey`, each request is new and a lost response cannot be retried idempotently.
Configured AI analysis must cover every image exactly once with valid subject/grade values;
invalid or failed analysis is not silently converted to success. If no provider is configured,
the documented default is one group (subject 1, grade 7).

The inherited lifecycle routes support local review, practice, logs and reanalysis.
Standalone rejection persists `Rejected` and its process log without contacting Student or
moving images. Managed Student source intake/handoff/return routes are compatibility code,
not part of new product intake.

## Health, contracts and verification

| Route | Authentication | Purpose |
|---|---|---|
| `GET /health/live` | Anonymous | Process liveness |
| `GET /health/ready` | Anonymous | DB/S3 plus explicitly enabled capabilities |
| `GET /contracts/emenda-intake-v1.json` | Anonymous | Product intake contract |
| `GET /contracts/mistake-http-v3.json` | Anonymous | Inherited lifecycle contract |
| `/api/*` | `X-Emenda-Api-Key` | Trusted backend business requests |

Readiness does not prove upload permissions, public signed-image routing or completed
business processing. Run [Standalone Verification](StandaloneVerification.md) against
isolated PostgreSQL and S3 for those checks; inherited Local/Platform deployment scripts
are not the standalone acceptance entry point.
