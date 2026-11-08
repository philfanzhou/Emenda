# Standalone Deployment

Emenda runs as one container with a PostgreSQL database and an S3-compatible object store.
Standalone mode (`Emenda:Runtime:Mode=Standalone`) uses standard configuration only: it
reads no Consul KV, writes no Loki sink, requires no SignaCore identity provider and no
Student service. The product image defaults to `ASPNETCORE_ENVIRONMENT=Standalone`.

## Required configuration

| Purpose | Environment variable | Configuration key |
|---|---|---|
| PostgreSQL connection | `EMENDA_DB_CONNECTION` / `ConnectionStrings__Emenda` | `ConnectionStrings:Emenda` |
| Caller credential (per caller) | `EMENDA_API_KEY` / `Emenda__Security__Callers__0__ApiKey` | `Emenda:Security:Callers:0:ApiKey` |
| Caller name (audit) | `Emenda__Security__Callers__0__Name` | `Emenda:Security:Callers:0:Name` |
| S3-compatible endpoint | `OSS_INTERNAL_ENDPOINT` | `Oss:InternalEndpoint` |
| S3 credentials | `OSS_ACCESS_KEY` / `OSS_SECRET_KEY` | `Oss:AccessKey` / `Oss:SecretKey` |
| Bucket | `OSS_BUCKET_NAME` | `Oss:BucketName` |
| Public pre-signed base URL | `OSS_PUBLIC_BASE_URL` | `Oss:PublicBaseUrl` |
| Vision-language provider | `VL_API_KEY` / `VL_API_BASE_URL` / `VL_MODEL` | `VlAnalyzer:*` |

The service refuses to start in standalone mode without a database connection or at least
one caller credential. `Emenda:Security:AllowAnonymous=true` exists only for local
development; never expose such a deployment.

The bucket must stay private. Pre-signed image URLs returned by `/api/mistakes/presigned-url`
are short-lived bearer capabilities.

## Run

```bash
export EMENDA_DB_CONNECTION='Host=postgres;Port=5432;Database=emenda;Username=emenda;Password=<secret>'
export EMENDA_API_KEY='<random 32+ character secret>'
export OSS_INTERNAL_ENDPOINT='seaweedfs:8333'
export OSS_ACCESS_KEY='<secret>'
export OSS_SECRET_KEY='<secret>'
export OSS_BUCKET_NAME='emenda'
export OSS_PUBLIC_BASE_URL='https://example.internal/oss'
export VL_API_KEY='<provider key>'
export VL_API_BASE_URL='https://api.siliconflow.cn/v1'
export VL_MODEL='Qwen/Qwen3-VL-30B-A3B-Instruct'
export EMENDA_IMAGE='ghcr.io/philfanzhou/emenda:latest'
./start.sh
```

`start.sh` replaces an existing container, publishes port `5007` and passes the variables
above. The container applies database migrations automatically on startup.

## Health and contracts

| Route | Authentication | Purpose |
|---|---|---|
| `GET /health/live` | anonymous | Process liveness |
| `GET /health/ready` | anonymous | Database / object storage / optional capability readiness |
| `GET /contracts/mistake-http-v3.json` | anonymous | Published HTTP contract bytes |
| `GET /` | anonymous | Host banner |
| `/api/*` | `X-Emenda-Api-Key` | Business surface |

## Intake example

```bash
curl -sS \
  -H "X-Emenda-Api-Key: $EMENDA_API_KEY" \
  -F studentId=student-123 \
  -F sourceKey=homework-2026-11-09-001 \
  -F images=@page-1.jpg \
  -F images=@page-2.jpg \
  http://127.0.0.1:5007/api/intake/uploads
```

`sourceKey` is an idempotency key: repeating the request with the same key returns the
items created by the first request instead of creating duplicates. Reuse the key only when
retrying the same content.

## Rotation

- A caller credential is rotated by adding the new credential to
  `Emenda:Security:Callers`, restarting, updating the caller, then removing the old entry.
- Database and object-storage credentials are read from the environment at startup only;
  rotate them with a restart.