#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260501"
IMAGE_NAME="ruoyu.study.mistake:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-mistake"
NETWORK_NAME="ruoyu-net"
# HTTP listen port is hardcoded to 5007 inside the container (Program.cs).
# Port is the host port mapped to the container's 5007.
Port="5007"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-host.docker.internal:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

VL_API_KEY="${VL_API_KEY:-}"
VL_API_BASE_URL="${VL_API_BASE_URL:-https://api.siliconflow.cn/v1}"
VL_MODEL="${VL_MODEL:-Qwen/Qwen3-VL-30B-A3B-Instruct}"

DB_NAME="ruoyu_study_mistake"

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
  --add-host=host.docker.internal:host-gateway \
  -p "${Port}:5007" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Database__Name="${DB_NAME}" \
  -e VlAnalyzer__ApiKey="${VL_API_KEY}" \
  -e VlAnalyzer__BaseUrl="${VL_API_BASE_URL}" \
  -e VlAnalyzer__Model="${VL_MODEL}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
