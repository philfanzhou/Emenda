#!/bin/bash
# Mistake minimal source package rehearsal:
# export the closure recorded in the manifest from a specified commit into an
# empty directory OUTSIDE this repository, then run restore / Release build /
# Domain tests / Service tests (PG-gated) / docker image build step by step.
#
# Usage:
#   06-mistake.minimal-source-rehearsal.sh --target <dir> [--commit <sha>]
#        [--allow-commit-drift] [--baseline-image <tag>] [--image-tag <tag>]
#        [--stop-before-docker]
#   06-mistake.minimal-source-rehearsal.sh --docker-only --target <dir>
#        [--baseline-image <tag>] [--image-tag <tag>]
#
# Any failing step exits non-zero and keeps the target directory as-is for
# inspection. The script never deletes the target directory and does not use
# temporary directories outside it. MISTAKE_MIGRATION_TEST_CONNECTION, when
# present in the environment, is passed to the test process only and is never
# printed or written to disk by this script.
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MANIFEST="$REPO_ROOT/manifest/mistake-minimal-source-package.json"
SOLUTION_REL="src/Emenda.sln"
DOMAIN_TESTS_REL="src/Tests/Emenda.Domain.Tests/Emenda.Domain.Tests.csproj"
SERVICE_TESTS_REL="src/Tests/Emenda.Service.Tests/Emenda.Service.Tests.csproj"
DOCKERFILE_REL="src/Host/Dockerfile"

TARGET=""
COMMIT=""
ALLOW_DRIFT=0
BASELINE_IMAGE=""
IMAGE_TAG=""
DOCKER_ONLY=0
STOP_BEFORE_DOCKER=0

usage() { grep '^#' "$0" | sed 's/^# \?//' | sed -n '2,20p'; exit 2; }

while [ $# -gt 0 ]; do
  case "$1" in
    --target) TARGET="${2:-}"; shift 2 ;;
    --commit) COMMIT="${2:-}"; shift 2 ;;
    --allow-commit-drift) ALLOW_DRIFT=1; shift ;;
    --baseline-image) BASELINE_IMAGE="${2:-}"; shift 2 ;;
    --image-tag) IMAGE_TAG="${2:-}"; shift 2 ;;
    --docker-only) DOCKER_ONLY=1; shift ;;
    --stop-before-docker) STOP_BEFORE_DOCKER=1; shift ;;
    *) echo "Error: unknown argument: $1" >&2; usage ;;
  esac
done

[ -n "$TARGET" ] || { echo "Error: --target <dir> is required" >&2; usage; }
command -v python3 >/dev/null 2>&1 || { echo "Error: python3 is required to read the manifest" >&2; exit 1; }
[ -f "$MANIFEST" ] || { echo "Error: manifest not found: $MANIFEST" >&2; exit 1; }

die() { echo "FAIL: $*" >&2; echo "REHEARSAL RESULT: FAILED (target kept for inspection: $TARGET)" >&2; exit 1; }
step_ok() { echo "PASS: $*"; }

trap 'echo "INTERRUPTED: rehearsal aborted by signal, target kept: $TARGET" >&2; exit 130' INT TERM

manifest_value() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))[sys.argv[2]])' "$MANIFEST" "$1" | tr -d '\r'; }
manifest_paths() {
  python3 - "$MANIFEST" <<'PYEOF' | tr -d '\r'
import json, sys
m = json.load(open(sys.argv[1], encoding="utf-8"))
paths = [m["solution"]]
members = m["members"]
for key in ("mistake_projects", "shared_projects", "context_files"):
    paths.extend(members[key])
print("\n".join(paths))
PYEOF
}

target_abs="$(cd "$(dirname "$TARGET")" 2>/dev/null && pwd)/$(basename "$TARGET")" || target_abs="$TARGET"
repo_real="$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$REPO_ROOT" | tr -d '\r')"
target_real="$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$target_abs" | tr -d '\r')"
case "$target_real/" in
  "$repo_real"/*) die "target directory must be outside the source repository: $target_real" ;;
esac

if [ "$DOCKER_ONLY" -eq 1 ]; then
  # ---------------------------------------------------------------- docker-only
  [ -d "$TARGET/.rehearsal" ] || die "--docker-only requires an existing rehearsal target with .rehearsal state"
  [ -f "$TARGET/.rehearsal/binobj-byproducts.txt" ] || die "--docker-only requires .rehearsal/binobj-byproducts.txt"
  IMAGE_TAG="${IMAGE_TAG:-mistake-rehearsal:docker-only}"
  echo "== [docker-only] residue guard before image build =="
  while IFS= read -r d; do
    [ -n "$d" ] && rm -rf "$TARGET/$d"
  done < "$TARGET/.rehearsal/binobj-byproducts.txt"
  residue="$(cd "$TARGET" && find . -type d \( -name bin -o -name obj \) -print | sed 's|^\./||' | sort)"
  [ -z "$residue" ] || die "bin/obj residue detected in export tree before image build (injected or unexpected): $residue"
  step_ok "export tree free of bin/obj residue"
else
  # ------------------------------------------------------------------- full run
  if [ -e "$TARGET" ]; then
    [ -d "$TARGET" ] || die "target exists and is not a directory: $TARGET"
    if [ -n "$(ls -A "$TARGET")" ]; then die "target directory is not empty: $TARGET"; fi
  else
    mkdir -p "$TARGET" || die "cannot create target directory: $TARGET"
  fi
  mkdir -p "$TARGET/.rehearsal"

  MANIFEST_COMMIT="$(manifest_value source_commit)"
  if [ -z "$COMMIT" ]; then
    COMMIT="$MANIFEST_COMMIT"
    COMMIT_SOURCE="manifest source_commit"
  else
    COMMIT_SOURCE="--commit argument"
  fi
  git -C "$REPO_ROOT" cat-file -e "$COMMIT^{commit}" 2>/dev/null || die "commit not found in repository: $COMMIT"
  ACTUAL_COMMIT="$(git -C "$REPO_ROOT" rev-parse "$COMMIT^{commit}")"
  echo "== [1/9] commit resolution =="
  echo "requested commit ($COMMIT_SOURCE): $ACTUAL_COMMIT"
  echo "manifest source_commit:            $MANIFEST_COMMIT"
  if [ "$ACTUAL_COMMIT" != "$MANIFEST_COMMIT" ] && [ "$ALLOW_DRIFT" -ne 1 ]; then
    die "requested commit differs from manifest source_commit; pass --allow-commit-drift to rehearse a drifted commit (recorded above)"
  fi
  [ "$ACTUAL_COMMIT" = "$MANIFEST_COMMIT" ] && echo "commit match: exact" || echo "commit match: DRIFT ALLOWED (recorded)"
  echo "$ACTUAL_COMMIT" > "$TARGET/.rehearsal/actual-commit.txt"

  echo "== [2/9] closure existence check at commit =="
  MEMBER_PATHS=()
  while IFS= read -r line; do
    [ -n "$line" ] && MEMBER_PATHS+=("$line")
  done < <(manifest_paths)
  [ "${#MEMBER_PATHS[@]}" -ge 10 ] || die "manifest returned too few member paths (${#MEMBER_PATHS[@]})"
  for p in "${MEMBER_PATHS[@]}"; do
    git -C "$REPO_ROOT" cat-file -e "$ACTUAL_COMMIT:$p" 2>/dev/null || die "manifest member missing at commit $ACTUAL_COMMIT: $p"
  done
  step_ok "all ${#MEMBER_PATHS[@]} manifest members exist at commit"

  echo "== [3/9] export via git archive =="
  git -C "$REPO_ROOT" archive "$ACTUAL_COMMIT" -- "${MEMBER_PATHS[@]}" | tar -x -C "$TARGET" || die "git archive / tar extraction failed"
  step_ok "exported ${#MEMBER_PATHS[@]} member paths into $TARGET"

  echo "== [4/9] exported file set equals manifest closure =="
  git -C "$REPO_ROOT" ls-tree -r --name-only "$ACTUAL_COMMIT" -- "${MEMBER_PATHS[@]}" | sort > "$TARGET/.rehearsal/expected-files.txt"
  (cd "$TARGET" && find . -type f -not -path './.rehearsal/*' | sed 's|^\./||' | sort) > "$TARGET/.rehearsal/exported-files.txt"
  if ! diff -u "$TARGET/.rehearsal/expected-files.txt" "$TARGET/.rehearsal/exported-files.txt" > "$TARGET/.rehearsal/fileset.diff"; then
    cat "$TARGET/.rehearsal/fileset.diff" >&2
    die "exported file set differs from manifest closure (see diff above and $TARGET/.rehearsal/fileset.diff)"
  fi
  EXPORTED_COUNT="$(wc -l < "$TARGET/.rehearsal/exported-files.txt" | tr -d ' ')"
  step_ok "exported file set matches manifest exactly ($EXPORTED_COUNT files)"

  echo "== [5/9] source residue and forbidden-content guards =="
  residue="$(cd "$TARGET" && find . -type d \( -name bin -o -name obj \) -print | sed 's|^\./||' | sort)"
  [ -z "$residue" ] || die "bin/obj residue found in freshly exported tree (export integrity broken): $residue"
  step_ok "fresh export contains no bin/obj directories (git archive excludes untracked build output)"
  forbidden_hits="$(cd "$TARGET" && find . \( \
    -path '*MistakeBff*' -o -path '*PortalAuth*' -o -path '*MistakeReviewBff*' -o -path '*MistakeUploadBff*' \
    -o -path '*ruoyu.common/frontend*' -o -path '*ruoyu.common/docs*' -o -path '*ruoyu.common/test*' \
    -o -path '*ruoyu.student*' -o -path '*ruoyu.homework*' \
    -o -path '*teacher_portal*' -o -path '*assistant_portal*' -o -path '*user_portal*' \) -print)"
  [ -z "$forbidden_hits" ] || die "forbidden non-closure content found in export tree: $forbidden_hits"
  step_ok "no Portal/Student/Homework or non-closure shared library content in export tree"

  echo "== [6/9] restore and Release build =="
  (cd "$TARGET" && dotnet restore "$SOLUTION_REL" > "$TARGET/.rehearsal/restore.log" 2>&1) || { tail -20 "$TARGET/.rehearsal/restore.log" >&2; die "dotnet restore failed (log: .rehearsal/restore.log)"; }
  step_ok "dotnet restore exited 0"
  (cd "$TARGET" && dotnet build "$SOLUTION_REL" -c Release --no-restore > "$TARGET/.rehearsal/build.log" 2>&1)
  build_rc=$?
  grep -E "个错误|error CS| error " "$TARGET/.rehearsal/build.log" | tail -3
  [ $build_rc -eq 0 ] || { tail -20 "$TARGET/.rehearsal/build.log" >&2; die "dotnet build failed (log: .rehearsal/build.log)"; }
  step_ok "dotnet build -c Release exited 0"

  echo "== [7/9] Domain tests (must fully pass) =="
  (cd "$TARGET" && dotnet test "$DOMAIN_TESTS_REL" -c Release --no-build > "$TARGET/.rehearsal/domain-tests.log" 2>&1) \
    || { tail -20 "$TARGET/.rehearsal/domain-tests.log" >&2; die "Domain tests failed (log: .rehearsal/domain-tests.log)"; }
  grep -E "已通过!|Passed!" "$TARGET/.rehearsal/domain-tests.log" | tail -1
  step_ok "Domain.Tests all passed"

  echo "== [8/9] Service tests (PG suites stay \$XunitDynamicSkip\$-gated without PG) =="
  if [ -n "${MISTAKE_MIGRATION_TEST_CONNECTION:-}" ]; then
    echo "MISTAKE_MIGRATION_TEST_CONNECTION is set: full run expected"
  else
    echo "MISTAKE_MIGRATION_TEST_CONNECTION not set: PG-dependent cases expected to report \$XunitDynamicSkip\$ gating"
  fi
  (cd "$TARGET" && dotnet test "$SERVICE_TESTS_REL" -c Release --no-build > "$TARGET/.rehearsal/service-tests.log" 2>&1)
  svc_rc=$?
  summary="$(grep -E "失败!|Failed!" "$TARGET/.rehearsal/service-tests.log" | tail -1)"
  failed="$(printf '%s' "$summary" | grep -oE '失败:[[:space:]]*[0-9]+|Failed:[[:space:]]*[0-9]+' | grep -oE '[0-9]+' | head -1)"
  gated="$(grep -c 'XunitDynamicSkip' "$TARGET/.rehearsal/service-tests.log" || true)"
  echo "service test exit code: $svc_rc; failed: ${failed:-0}; XunitDynamicSkip-gated failures: ${gated:-0}"
  [ -n "$summary" ] && echo "$summary"
  if [ $svc_rc -ne 0 ]; then
    failed="${failed:-0}"; gated="${gated:-0}"
    if [ "$failed" -gt 0 ] && [ "$failed" -eq "$gated" ]; then
      step_ok "Service.Tests: $failed failures are all \$XunitDynamicSkip\$ PG-gated (no dedicated PG admin connection); non-PG cases all passed"
    else
      tail -20 "$TARGET/.rehearsal/service-tests.log" >&2
      die "Service.Tests has non-gated failures (failed: $failed, gated: $gated; log: .rehearsal/service-tests.log)"
    fi
  else
    step_ok "Service.Tests all passed (exit 0)"
  fi

  (cd "$TARGET" && find . -type d \( -name bin -o -name obj \) -print | sed 's|^\./||' | sort) > "$TARGET/.rehearsal/binobj-byproducts.txt"
  echo "recorded rehearsal-created bin/obj directories for pre-docker cleanup: $(wc -l < "$TARGET/.rehearsal/binobj-byproducts.txt" | tr -d ' ')"

  if [ "$STOP_BEFORE_DOCKER" -eq 1 ]; then
    echo "REHEARSAL RESULT: STOPPED BEFORE DOCKER (requested; target kept: $TARGET)"
    exit 0
  fi

  echo "== [9/9] residue guard + docker image build =="
  while IFS= read -r d; do
    [ -n "$d" ] && rm -rf "$TARGET/$d"
  done < "$TARGET/.rehearsal/binobj-byproducts.txt"
  residue="$(cd "$TARGET" && find . -type d \( -name bin -o -name obj \) -print | sed 's|^\./||' | sort)"
  [ -z "$residue" ] || die "bin/obj residue detected in export tree before image build (injected or unexpected): $residue"
  step_ok "export tree free of bin/obj residue before docker build"
fi

# Shared tail: .dockerignore context guard + docker build + artifact listing
[ "$IMAGE_TAG" != "" ] || IMAGE_TAG="mistake-rehearsal:$(head -c 12 "$TARGET/.rehearsal/actual-commit.txt" 2>/dev/null || echo adhoc)"
grep -qx '\*\*/bin' "$TARGET/.dockerignore" && grep -qx '\*\*/obj' "$TARGET/.dockerignore" \
  || die "export root .dockerignore must contain '**/bin' and '**/obj' context exclusions"
step_ok ".dockerignore context exclusion patterns present"
docker build --build-arg HTTPS_PROXY --build-arg HTTP_PROXY --build-arg NO_PROXY --build-arg "ASPNET_IMAGE=${ASPNET_IMAGE:-mcr.microsoft.com/dotnet/aspnet:10.0}" --build-arg "SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}" -f "$TARGET/$DOCKERFILE_REL" -t "$IMAGE_TAG" "$TARGET" > "$TARGET/.rehearsal/docker-build.log" 2>&1 \
  || { tail -20 "$TARGET/.rehearsal/docker-build.log" >&2; die "docker build failed in export tree (log: .rehearsal/docker-build.log)"; }
step_ok "docker image built from export tree: $IMAGE_TAG"
docker run --rm --entrypoint /bin/sh "$IMAGE_TAG" -c 'cd /app && find . -type f | sort' > "$TARGET/.rehearsal/image-files.txt" 2>/dev/null \
  || die "failed to list artifact files from image $IMAGE_TAG"
echo "image artifact files: $(wc -l < "$TARGET/.rehearsal/image-files.txt" | tr -d ' ')"
if [ -n "$BASELINE_IMAGE" ]; then
  docker image inspect "$BASELINE_IMAGE" >/dev/null 2>&1 || die "baseline image not found locally: $BASELINE_IMAGE"
  docker run --rm --entrypoint /bin/sh "$BASELINE_IMAGE" -c 'cd /app && find . -type f | sort' > "$TARGET/.rehearsal/baseline-image-files.txt" 2>/dev/null \
    || die "failed to list artifact files from baseline image $BASELINE_IMAGE"
  if ! diff -u "$TARGET/.rehearsal/baseline-image-files.txt" "$TARGET/.rehearsal/image-files.txt" > "$TARGET/.rehearsal/image-fileset.diff"; then
    cat "$TARGET/.rehearsal/image-fileset.diff" >&2
    die "image artifact file name set differs from baseline image $BASELINE_IMAGE (see .rehearsal/image-fileset.diff)"
  fi
  step_ok "image artifact file name set identical to baseline image $BASELINE_IMAGE"
fi
echo "REHEARSAL RESULT: SUCCESS (target: $TARGET, image: $IMAGE_TAG)"
echo "Done."
