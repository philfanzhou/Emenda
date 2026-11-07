#!/bin/bash
set -e

IMAGE_NAME="ruoyu.study.mistake:20260501"
CONTAINER_NAME="ruoyu-mistake"
Port="5007"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-192.168.100.10:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

VL_API_KEY="${VL_API_KEY:-}"
VL_API_BASE_URL="${VL_API_BASE_URL:-https://api.stepfun.com/v1}"
VL_MODEL="${VL_MODEL:-step-3.7-flash}"

DB_NAME="ruoyu_study_mistake"

# Optional local service identity is supplied only by deployment-owned files.
SERVICE_AUTH_ARGS=()
if [ -n "${MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE:-}" ] || [ -n "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE:-}" ]; then
    : "${MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE:?A protected service authentication env file is required}"
    : "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE:?Service authentication key material is required}"
    test -f "${MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE}"
    test "$(stat -c '%a' "${MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE}")" = 600
    test "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE:0:1}" = /
    case "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE}" in *','*|*$'\n'*|*$'\r'*) exit 2 ;; esac
    test -f "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE}"
    test "$(stat -c '%a' "${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE}")" = 600
    SERVICE_AUTH_ARGS+=(--env-file "${MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE}" --mount "type=bind,source=${MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE},target=/run/ruoyu/mistake-signing-key.pem,readonly")
fi
if [ -n "${MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE:-}" ]; then
    test "${MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE:0:1}" = /
    case "${MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE}" in *','*|*$'\n'*|*$'\r'*) exit 2 ;; esac
    test -f "${MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE}"
    SERVICE_AUTH_ARGS+=(--mount "type=bind,source=${MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE},target=/run/ruoyu/student-ca-bundle.pem,readonly" -e SSL_CERT_FILE=/run/ruoyu/student-ca-bundle.pem)
fi

# Administrator audiences (ADR-0030): the Portal console applications whose administrators
# create or edit mistake items. Space- or comma-separated
# application ids; empty keeps the admin-audience path closed.
ADMIN_AUDIENCE_ARGS=()
if [ -n "${MISTAKE_ADMIN_AUDIENCES:-}" ]; then
    ADMIN_AUDIENCE_ARGS+=(-e "IdentityService__AdminAudience=${MISTAKE_ADMIN_AUDIENCES}")
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
  ${SERVICE_AUTH_ARGS[@]+"${SERVICE_AUTH_ARGS[@]}"} \
  ${ADMIN_AUDIENCE_ARGS[@]+"${ADMIN_AUDIENCE_ARGS[@]}"} \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  -p "${Port}:5007" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Database__Name="${DB_NAME}" \
  -e VlAnalyzer__ApiKey="${VL_API_KEY}" \
  -e VlAnalyzer__BaseUrl="${VL_API_BASE_URL}" \
  -e VlAnalyzer__Model="${VL_MODEL}" \
  "$IMAGE_NAME"
