#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo "Usage: $0 YYYYMMDD [storage_root] [output_root]" >&2
  exit 1
fi

analysis_date="$1"
storage_root="${2:-/srv/ArbiScan}"
output_root="${3:-$(pwd)/analysis-bundles}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
bundle_name="arbiscan-analysis-${analysis_date}-${timestamp}"
bundle_dir="${output_root}/${bundle_name}"
reports_root="${storage_root}/reports"
logs_root="${storage_root}/logs"
config_root="${storage_root}/config"

mkdir -p "${bundle_dir}"
: > "${bundle_dir}/missing-files.txt"

copy_if_exists() {
  local source_path="$1"
  local target_name="$2"

  if [[ -f "${source_path}" ]]; then
    cp "${source_path}" "${bundle_dir}/${target_name}"
  else
    echo "missing: ${source_path}" >> "${bundle_dir}/missing-files.txt"
  fi
}

copy_matching_files() {
  local source_dir="$1"
  local pattern="$2"
  local found=0

  while IFS= read -r source_path; do
    found=1
    cp "${source_path}" "${bundle_dir}/$(basename "${source_path}")"
  done < <(find "${source_dir}" -maxdepth 1 -type f -name "${pattern}" | sort || true)

  if [[ ${found} -eq 0 ]]; then
    echo "missing: ${source_dir}/${pattern}" >> "${bundle_dir}/missing-files.txt"
  fi
}

write_missing_marker() {
  local marker_name="$1"
  local message="$2"
  printf '%s\n' "${message}" > "${bundle_dir}/${marker_name}"
}

redact_json() {
  local source_path="$1"
  local target_name="$2"

  if [[ -f "${source_path}" ]]; then
    sed -E \
      -e 's/"ApiKey"[[:space:]]*:[[:space:]]*"[^"]*"/"ApiKey": "***REDACTED***"/g' \
      -e 's/"ApiSecret"[[:space:]]*:[[:space:]]*"[^"]*"/"ApiSecret": "***REDACTED***"/g' \
      -e 's/"BotToken"[[:space:]]*:[[:space:]]*"[^"]*"/"BotToken": "***REDACTED***"/g' \
      "${source_path}" > "${bundle_dir}/${target_name}"
  else
    echo "missing: ${source_path}" >> "${bundle_dir}/missing-files.txt"
  fi
}

copy_matching_files "${logs_root}" "application-*.log"
copy_if_exists "${reports_root}/health-events-${analysis_date}.jsonl" "health-events-${analysis_date}.jsonl"
copy_if_exists "${reports_root}/raw-signal-events-${analysis_date}.jsonl" "raw-signal-events-${analysis_date}.jsonl"

if [[ -f "${reports_root}/window-events-${analysis_date}.jsonl" ]]; then
  cp "${reports_root}/window-events-${analysis_date}.jsonl" "${bundle_dir}/window-events-${analysis_date}.jsonl"
else
  write_missing_marker "window-events-${analysis_date}.missing.txt" "No window-events export was present for ${analysis_date}."
  echo "missing: ${reports_root}/window-events-${analysis_date}.jsonl" >> "${bundle_dir}/missing-files.txt"
fi

copy_matching_files "${reports_root}" "hourly-*.json"
copy_matching_files "${reports_root}" "daily-*.json"
copy_matching_files "${reports_root}" "cumulative-*.json"
copy_matching_files "${reports_root}" "health-hourly-*.json"
copy_matching_files "${reports_root}" "health-daily-*.json"
copy_matching_files "${reports_root}" "health-cumulative-*.json"

redact_json "${config_root}/appsettings.json" "production-appsettings.redacted.json"
redact_json "${config_root}/telegramsettings.json" "telegramsettings.redacted.json"

log_source="${logs_root}/application-${analysis_date}.log"
if [[ -f "${log_source}" ]]; then
  if command -v rg >/dev/null 2>&1; then
    rg -n "started|stale|recovered|critical error|heartbeat|Telegram notification error" "${log_source}" \
      > "${bundle_dir}/health-excerpts.txt" || true
  else
    grep -nE "started|stale|recovered|critical error|heartbeat|Telegram notification error" "${log_source}" \
      > "${bundle_dir}/health-excerpts.txt" || true
  fi
else
  echo "missing: ${log_source}" >> "${bundle_dir}/missing-files.txt"
fi

git_sha="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
app_version="$(./scripts/get-version.sh 2>/dev/null || echo unknown)"
collection_time_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
container_id="$(docker ps --filter "name=^arbiscan$" --format '{{.ID}}' 2>/dev/null | head -n 1 || true)"
container_started_at="unknown"
container_image_tag="unknown"
uptime="unknown"

if [[ -n "${container_id}" ]]; then
  container_started_at="$(docker inspect --format '{{.State.StartedAt}}' "${container_id}" 2>/dev/null || echo unknown)"
  container_image_tag="$(docker inspect --format '{{.Config.Image}}' "${container_id}" 2>/dev/null || echo unknown)"
  uptime="$(
    CONTAINER_STARTED_AT="${container_started_at}" python3 -c '
from datetime import datetime, timezone
import os
value = os.environ.get("CONTAINER_STARTED_AT", "unknown")
try:
    started = datetime.fromisoformat(value.replace("Z", "+00:00"))
    now = datetime.now(timezone.utc)
    print(int((now - started).total_seconds()))
except Exception:
    print("unknown")
' 2>/dev/null || echo unknown
  )"
fi

cat > "${bundle_dir}/runtime-meta.json" <<EOF
{
  "appVersion": "${app_version}",
  "gitSha": "${git_sha}",
  "uptimeSeconds": "${uptime}",
  "vpsRegion": "ap-northeast-1",
  "containerImageTag": "${container_image_tag}",
  "startTimeUtc": "${container_started_at}",
  "collectionTimeUtc": "${collection_time_iso}"
}
EOF

cat > "${bundle_dir}/README.txt" <<EOF
ArbiScan analysis bundle
date=${analysis_date}
storage_root=${storage_root}
generated_utc=${timestamp}

Included when available:
- application-*.log
- health-events jsonl
- raw-signal-events jsonl
- window-events jsonl or explicit missing marker
- hourly/daily/cumulative summaries
- health-hourly/health-daily/health-cumulative reports
- redacted production appsettings
- redacted telegramsettings
- runtime-meta.json
- health excerpts
EOF

tar -czf "${output_root}/${bundle_name}.tar.gz" -C "${output_root}" "${bundle_name}"

echo "Bundle directory: ${bundle_dir}"
echo "Bundle archive: ${output_root}/${bundle_name}.tar.gz"
