#!/usr/bin/env bash
# 打包 FlyScreen.Relay Docker 镜像（构建 / 导出 / 推送）
#
# 用法:
#   ./tools/pack-docker.sh
#   ./tools/pack-docker.sh --tag 1.0.0 --save
#   ./tools/pack-docker.sh --tag 1.0.0 --registry registry.example.com/flyscreen --push
#   ./tools/pack-docker.sh --load ./out/flyscreen-relay-1.0.0.tar
set -euo pipefail

IMAGE_NAME="flyscreen-relay"
TAG=""
REGISTRY=""
SAVE=0
OUT_DIR=""
PUSH=0
NO_CACHE=0
MIRROR=0
SDK_IMAGE=""
RUNTIME_IMAGE=""
LOAD_PATH=""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELAY_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$RELAY_ROOT"

usage() {
  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --image-name) IMAGE_NAME="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    --save) SAVE=1; shift ;;
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    --push) PUSH=1; shift ;;
    --no-cache) NO_CACHE=1; shift ;;
    --mirror) MIRROR=1; shift ;;
    --sdk-image) SDK_IMAGE="$2"; shift 2 ;;
    --runtime-image) RUNTIME_IMAGE="$2"; shift 2 ;;
    --load) LOAD_PATH="$2"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "未知参数: $1" >&2; usage 1 ;;
  esac
done

assert_docker() {
  command -v docker >/dev/null 2>&1 || { echo "未找到 docker 命令" >&2; exit 1; }
  docker info >/dev/null 2>&1 || { echo "Docker 引擎未就绪" >&2; exit 1; }
}

resolve_tag() {
  if [[ -n "$TAG" ]]; then
    echo "$TAG"
    return
  fi
  if command -v git >/dev/null 2>&1; then
    local describe
    describe="$(git -C "$RELAY_ROOT" describe --tags --always --dirty 2>/dev/null || true)"
    if [[ -n "$describe" ]]; then
      echo "${describe//[\/]/}"
      return
    fi
  fi
  date +%Y%m%d.%H%M%S
}

assert_docker

if [[ -n "$LOAD_PATH" ]]; then
  [[ -f "$LOAD_PATH" ]] || { echo "LoadPath 无效: $LOAD_PATH" >&2; exit 1; }
  echo "==> docker load -i $LOAD_PATH"
  docker load -i "$LOAD_PATH"
  echo "完成：已从 $LOAD_PATH 加载镜像"
  exit 0
}

RESOLVED_TAG="$(resolve_tag)"
REFS=("${IMAGE_NAME}:latest" "${IMAGE_NAME}:${RESOLVED_TAG}")
if [[ -n "$REGISTRY" ]]; then
  PREFIX="${REGISTRY%/}"
  REFS+=("${PREFIX}/${IMAGE_NAME}:latest" "${PREFIX}/${IMAGE_NAME}:${RESOLVED_TAG}")
fi

echo "==> 工程目录: $RELAY_ROOT"
echo "==> 镜像标签: ${REFS[*]}"

[[ -f "$RELAY_ROOT/Dockerfile" ]] || { echo "未找到 Dockerfile" >&2; exit 1; }

if [[ "$MIRROR" -eq 1 ]]; then
  [[ -n "$SDK_IMAGE" ]] || SDK_IMAGE="mcr.m.daocloud.io/dotnet/sdk:10.0"
  [[ -n "$RUNTIME_IMAGE" ]] || RUNTIME_IMAGE="mcr.m.daocloud.io/dotnet/aspnet:10.0"
fi

BUILD_ARGS=(build)
[[ "$NO_CACHE" -eq 1 ]] && BUILD_ARGS+=(--no-cache)
if [[ -n "$SDK_IMAGE" ]]; then
  BUILD_ARGS+=(--build-arg "SDK_IMAGE=$SDK_IMAGE")
  echo "==> SDK_IMAGE=$SDK_IMAGE"
fi
if [[ -n "$RUNTIME_IMAGE" ]]; then
  BUILD_ARGS+=(--build-arg "RUNTIME_IMAGE=$RUNTIME_IMAGE")
  echo "==> RUNTIME_IMAGE=$RUNTIME_IMAGE"
fi
for ref in "${REFS[@]}"; do
  BUILD_ARGS+=(-t "$ref")
done
BUILD_ARGS+=(.)

echo "==> docker ${BUILD_ARGS[*]}"
docker "${BUILD_ARGS[@]}"

EXPORT_PATH=""
if [[ "$SAVE" -eq 1 ]]; then
  [[ -n "$OUT_DIR" ]] || OUT_DIR="$RELAY_ROOT/out"
  mkdir -p "$OUT_DIR"
  EXPORT_PATH="$OUT_DIR/${IMAGE_NAME}-${RESOLVED_TAG}.tar"
  echo "==> docker save -o $EXPORT_PATH ${REFS[*]}"
  docker save -o "$EXPORT_PATH" "${REFS[@]}"
  SIZE_MB="$(du -m "$EXPORT_PATH" | awk '{print $1}')"
  echo "已导出: $EXPORT_PATH (${SIZE_MB} MB)"
fi

if [[ "$PUSH" -eq 1 ]]; then
  [[ -n "$REGISTRY" ]] || { echo "使用 --push 时必须指定 --registry" >&2; exit 1; }
  PREFIX="${REGISTRY%/}"
  for ref in "${REFS[@]}"; do
    case "$ref" in
      "${PREFIX}/"*)
        echo "==> docker push $ref"
        docker push "$ref"
        ;;
    esac
  done
fi

PRIMARY="${REFS[0]}"
echo ""
echo "打包完成"
echo "  主镜像: $PRIMARY"
echo "  版本标签: ${IMAGE_NAME}:${RESOLVED_TAG}"
[[ -n "$EXPORT_PATH" ]] && echo "  导出文件: $EXPORT_PATH"
echo ""
echo "本地运行示例:"
echo "  docker run -d --name flyscreen-relay \\"
echo "    -p 8443:8443 -p 9080:9080 -p 15155:15155 -p 8443:8443/udp -p 15156:15156/udp \\"
echo "    -v flyscreen-relay-data:/app/data \\"
echo "    $PRIMARY"
echo "  或: docker compose up -d"
