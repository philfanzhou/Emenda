# Standalone Deployment

Emenda serves trusted backend callers with a dedicated PostgreSQL database and a private
S3-compatible bucket, configured through appsettings/environment configuration only. The
optional admin console uses SignaCore; business API calls do not require it. The image
defaults to the `Standalone` ASP.NET Core environment (`appsettings.Standalone.json`).

The platform-era settings `Mistake:ConfigMode`, `Emenda:Runtime:Mode` (other than
`Standalone`) and `Emenda:Database:SchemaOwnership=Shared` fail startup; `start.sh` also
refuses `EMENDA_SCHEMA_OWNERSHIP` values other than `Own`. Other retired platform sections
(Student, workers, managed sources, `IdentityService`, `VlAnalyzer`, Consul, Loki) are ignored
and named in a startup warning. Remove them when upgrading.

## Configuration

| Purpose | `start.sh` environment | Host configuration |
|---|---|---|
| Dedicated PostgreSQL connection | `EMENDA_DB_CONNECTION` (required) | `ConnectionStrings:Emenda` |
| Static caller credential | `EMENDA_API_KEY` (required) | `Emenda:Security:Callers:0:ApiKey` |
| Stable caller name | `EMENDA_CALLER_NAME` (default `platform`) | `Emenda:Security:Callers:0:Name` |
| Persistent credential mount | `EMENDA_DATA_DIR` (default named volume `emenda-data`) | file `/app/data/emenda-callers.json` |
| Internal S3 endpoint | `OSS_INTERNAL_ENDPOINT` (required) | `Oss:InternalEndpoint` |
| Internal TLS | `OSS_INTERNAL_SECURE` (default `false`) | `Oss:InternalSecure` |
| S3 credentials | `OSS_ACCESS_KEY`, `OSS_SECRET_KEY` (required) | `Oss:AccessKey`, `Oss:SecretKey` |
| Private bucket | `OSS_BUCKET_NAME` (default `emenda`) | `Oss:BucketName` |
| Public pre-signed URL base | `OSS_PUBLIC_BASE_URL` (required) | `Oss:PublicBaseUrl` |
| Host port | `EMENDA_HTTP_PORT` (default `5007`) | published container port `5007` |
| Image | `EMENDA_IMAGE` (default local `emenda:latest`) | container image |

The connection is explicit; there is no fallback connection.
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

## Schema upgrades

Startup transactionally creates or upgrades the product schema it owns. The current target
is schema v19 (`20261122000000_DropManagedSourceTables`), which removes the platform-era
managed source tables and guard triggers. That upgrade locks those tables and refuses to run
— leaving the database unchanged — if any of them holds a row; mistakes, practice data, logs,
extraction and conversation data are untouched. v19 is forward-only: restore a backup
instead of migrating down. Back up the complete database and credential volume before
upgrading a deployed instance. See [migration history](../database/migrations.md).

A nonempty `product_intake_request` table (receipts of the removed `/api/intake/uploads`
API) is retained; it blocks downgrading past its creation. Existing items remain accessible
through their IDs and source references.

### PostgreSQL 18 catalog compatibility

PostgreSQL 18 records `NOT NULL` constraints in `pg_constraint` as type `n`.
Startup validates their effective nullability through `pg_attribute.attnotnull`,
and excludes only validated type-`n` entries from the separate key/check constraint
comparison. Unvalidated entries, unexpected checks and nullability changes still
fail closed. The regression suite `CatalogStartupPostgresTests` exercises current-schema
restart without rewriting constraints, migration history or existing rows, plus
rejection of changed nullability and additional checks. Run it against an isolated
PostgreSQL instance using `MISTAKE_READONLY_TEST_CONNECTION`.

Older images such as `0.1.1-rc.3` predate this catalog normalization and can refuse
to restart after a PostgreSQL 18 upgrade. Deploy the compatibility fix; do not
delete `NOT NULL` constraints or change migration history merely to bypass the
failure. This compatibility check does not establish support for
every historical legacy-schema conversion on PostgreSQL 18.


## Intake and lifecycle

Mistakes enter through the AI Tutor-backed paths only:

- `POST /api/intake/extractions` — asynchronous automatic extraction (StructaDoc layout plus
  AI Tutor recognition and judgement). It defaults off; see
  [Automatic extraction](../modules/AutomaticExtraction.md) for configuration, limits and
  the `emenda-extraction-v1` contract.
- `/api/mistakes/conversation-collections` — conversation collection, default off; see
  [Conversation collection](../Integration/ConversationCollection.md).

The removed entry points `POST /api/intake/uploads`, `POST /api/mistakes/upload` and
`POST /api/mistakes` are no longer mapped.

The lifecycle routes under `/api/mistakes` support review, partial update, deletion,
practice, process logs and presigned reads. Rejection persists `Rejected` and its process
log without moving images. Extraction-generated items are reviewed through
`/api/intake/extractions/{id}/questions/{questionId}/review`.

Image display sizes are generated in the background (`Emenda__ImageVariants__Enabled`, default
`true`; `Emenda__ImageVariants__BatchSize`, default 8). Schema v23 queues every existing image on
upgrade, so the first start after upgrading backfills thumbnails without manual steps. See
[Display sizes](../Integration/ImageVariants.md).

## Health, contracts and verification

| Route | Authentication | Purpose |
|---|---|---|
| `GET /health/live` | Anonymous | Process liveness |
| `GET /health/ready` | Anonymous | DB/S3, plus StructaDoc when layout detection is enabled |
| `GET /contracts/mistake-http-v6.json` | Anonymous | Lifecycle contract |
| `GET /contracts/emenda-extraction-v1.json` | Anonymous | Extraction intake contract |
| `/api/*` | `X-Emenda-Api-Key` | Trusted backend business requests |

Earlier contracts (`mistake-http-v1`…`v5`, `emenda-intake-v1`) remain available with an
`X-Emenda-Contract-Status: historical` header and a `Link` to their successor.

Readiness does not prove upload permissions, public signed-image routing or completed
business processing. [Standalone Verification](StandaloneVerification.md) still exercises
the removed `/api/intake/uploads` path and must be ported to extraction before it can be
used again.
