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
  -p "${Port}:5007" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Database__Name="${DB_NAME}" \
  -e VlAnalyzer__ApiKey="${VL_API_KEY}" \
  -e VlAnalyzer__BaseUrl="${VL_API_BASE_URL}" \
  -e VlAnalyzer__Model="${VL_MODEL}" \
  "$IMAGE_NAME"
