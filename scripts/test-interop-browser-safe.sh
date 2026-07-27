#!/usr/bin/env bash
set -euo pipefail

readonly script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repository_root="$(cd -- "${script_dir}/.." && pwd)"
readonly project_path="${repository_root}/tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj"
readonly cleanup_label="com.callora.voipsdk.interop.browser-safe=true"
readonly suite_lock="/tmp/callora-voipsdk-browser-safe-suite.lock"

cleanup_browser_safe_containers() {
  local -a container_ids=()
  mapfile -t container_ids < <(docker ps --all --quiet --filter "label=${cleanup_label}")
  if (( ${#container_ids[@]} > 0 )); then
    docker rm --force "${container_ids[@]}" >/dev/null
  fi
}

exec 9>"${suite_lock}"
flock 9

cleanup_browser_safe_containers
trap cleanup_browser_safe_containers EXIT

export CALLORA_INTEROP_BROWSER_SAFE=1
export TESTCONTAINERS_RYUK_DISABLED=true

if (( $# == 0 )); then
  set -- \
    --configuration Release \
    --framework net10.0 \
    --filter "Category=Interop" \
    --nologo \
    --verbosity minimal
fi

dotnet test "${project_path}" "$@"
