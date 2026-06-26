#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260501"
IMAGE_NAME="ruoyu.study.mistake:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-mistake"
NETWORK_NAME="ruoyu-net"
GRPC_PORT="5006"
HTTP_PORT="5007"

STUDENT_GRPC_ADDR="ruoyu-student:5005"

VL_API_KEY="${VL_API_KEY:-}"
VL_API_BASE_URL="${VL_API_BASE_URL:-https://api.siliconflow.cn/v1}"
VL_MODEL="${VL_MODEL:-Qwen/Qwen3-VL-30B-A3B-Instruct}"
# MaxOutputTokens must leave enough room for the reasoning VL models
# (mimo-v2.5, step-3.7-flash) — see docs/overview/SystemContext.md and HR-12.
VL_MAX_OUTPUT_TOKENS="${VL_MAX_OUTPUT_TOKENS:-4096}"

DB_HOST="ruoyu-postgres"
DB_PORT="5432"
DB_NAME="ruoyu_study_mistake"
DB_USER="postgres"
DB_PASS="postgres"

CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS};"

OSS_ENDPOINT="ruoyu-seaweedfs:8333"
OSS_ACCESS_KEY="seaweedfs_admin"
OSS_SECRET_KEY="seaweedfs_admin"
OSS_BUCKET="ruoyu-study"

docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"
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
  --network "$NETWORK_NAME" \
  -e TZ=Asia/Shanghai \
  -e Endpoints__Grpc="${GRPC_PORT}" \
  -e Endpoints__Http="${HTTP_PORT}" \
  -e ConnectionStrings__Default="${CONNECTION_STRING}" \
  -e Oss__Endpoint="${OSS_ENDPOINT}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET}" \
  -e StudentLearningService__GrpcUrl="http://${STUDENT_GRPC_ADDR}" \
  -e VlAnalyzer__ApiKey="${VL_API_KEY}" \
  -e VlAnalyzer__ApiBaseUrl="${VL_API_BASE_URL}" \
  -e VlAnalyzer__Model="${VL_MODEL}" \
  -e VlAnalyzer__MaxOutputTokens="${VL_MAX_OUTPUT_TOKENS}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
