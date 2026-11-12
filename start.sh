#!/bin/bash
# Emenda standalone deployment entry point.
#
# The product reads standard appsettings/environment configuration only: it
# does not read Consul, does not require Loki and does not depend on the
# Ruoyu.Study platform identity or Student services. Callers authenticate with
# a per-caller credential header (X-Emenda-Api-Key).
set -e

IMAGE_NAME="${EMENDA_IMAGE:-emenda:latest}"
CONTAINER_NAME="${EMENDA_CONTAINER_NAME:-emenda}"
PORT="${EMENDA_HTTP_PORT:-5007}"
DATA_MOUNT="${EMENDA_DATA_DIR:-emenda-data}:/app/data"

if [ "${1:-}" = "--init-callers" ]; then
    # Exclusive creation only. Reinitializing lost state is an operator action,
    # never an automatic response to a missing credential volume.
    docker run --rm --entrypoint /bin/sh -v "$DATA_MOUNT" "$IMAGE_NAME" -c \
      'umask 077; set -C; printf '\''{"version":1,"callers":[]}\n'\'' > /app/data/emenda-callers.json'
    echo "Initialized caller store. Preserve this volume; initialization does not restore historical revocations."
    exit 0
fi
if [ "$#" -ne 0 ]; then
    echo "Usage: $0 [--init-callers]" >&2
    exit 2
fi

require_env() {
    local name="$1"
    if [ -z "${!name:-}" ]; then
        echo "Error: $name is required." >&2
        exit 1
    fi
}

require_env EMENDA_DB_CONNECTION
require_env EMENDA_API_KEY
require_env OSS_INTERNAL_ENDPOINT
require_env OSS_ACCESS_KEY
require_env OSS_SECRET_KEY
require_env OSS_PUBLIC_BASE_URL

OSS_INTERNAL_ENDPOINT="${OSS_INTERNAL_ENDPOINT}"
OSS_INTERNAL_SECURE="${OSS_INTERNAL_SECURE:-false}"
OSS_ACCESS_KEY="${OSS_ACCESS_KEY}"
OSS_SECRET_KEY="${OSS_SECRET_KEY}"
OSS_BUCKET_NAME="${OSS_BUCKET_NAME:-emenda}"
OSS_PUBLIC_BASE_URL="${OSS_PUBLIC_BASE_URL}"

VL_API_KEY="${VL_API_KEY:-}"
VL_API_BASE_URL="${VL_API_BASE_URL:-https://api.siliconflow.cn/v1}"
VL_MODEL="${VL_MODEL:-Qwen/Qwen3-VL-30B-A3B-Instruct}"

if ! docker run --rm --entrypoint /bin/sh -v "$DATA_MOUNT" "$IMAGE_NAME" \
    -c 'test -s /app/data/emenda-callers.json'; then
    echo "Caller store is missing or empty. For a new installation, run $0 --init-callers explicitly; restore lost state instead of reinitializing it." >&2
    exit 1
fi

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  -p "${PORT}:5007" \
  -v "$DATA_MOUNT" \
  -e TZ="${TZ:-Asia/Shanghai}" \
  -e Emenda__Runtime__Mode=Standalone \
  -e ConnectionStrings__Emenda="${EMENDA_DB_CONNECTION}" \
  -e Emenda__Database__SchemaOwnership="${EMENDA_SCHEMA_OWNERSHIP:-Own}" \
  -e Emenda__Security__DynamicCallers__Path="/app/data/emenda-callers.json" \
  -e Emenda__Admin__RequiredRole="${EMENDA_ADMIN_REQUIRED_ROLE:-admin}" \
  -e Emenda__Admin__Oidc__Authority="${EMENDA_ADMIN_AUTHORITY:-}" \
  -e Emenda__Admin__Oidc__ClientId="${EMENDA_ADMIN_CLIENT_ID:-}" \
  -e Emenda__Admin__Oidc__ClientSecret="${EMENDA_ADMIN_CLIENT_SECRET:-}" \
  -e Emenda__Admin__Oidc__RedirectUri="${EMENDA_ADMIN_REDIRECT_URI:-}" \
  -e Emenda__Admin__Oidc__PostLogoutRedirectUri="${EMENDA_ADMIN_POST_LOGOUT_REDIRECT_URI:-}" \
  -e Emenda__Security__Callers__0__Name="${EMENDA_CALLER_NAME:-platform}" \
  -e Emenda__Security__Callers__0__ApiKey="${EMENDA_API_KEY}" \
  -e Oss__InternalEndpoint="${OSS_INTERNAL_ENDPOINT}" \
  -e Oss__InternalSecure="${OSS_INTERNAL_SECURE}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET_NAME}" \
  -e Oss__PublicBaseUrl="${OSS_PUBLIC_BASE_URL}" \
  -e VlAnalyzer__ApiKey="${VL_API_KEY}" \
  -e VlAnalyzer__BaseUrl="${VL_API_BASE_URL}" \
  -e VlAnalyzer__Model="${VL_MODEL}" \
  "$IMAGE_NAME"