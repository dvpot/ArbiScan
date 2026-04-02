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

mkdir -p "${bundle_dir}"

copy_if_exists() {
  local source_path="$1"
  local target_name="$2"

  if [[ -f "${source_path}" ]]; then
    cp "${source_path}" "${bundle_dir}/${target_name}"
  else
    echo "missing: ${source_path}" >> "${bundle_dir}/missing-files.txt"
  fi
}

copy_latest_matching() {
  local pattern="$1"
  local target_name="$2"
  local latest_file

  latest_file="$(find "${storage_root}/reports" -maxdepth 1 -type f -name "${pattern}" | sort | tail -n 1 || true)"
  if [[ -n "${latest_file}" ]]; then
    cp "${latest_file}" "${bundle_dir}/${target_name}"
  else
    echo "missing: ${storage_root}/reports/${pattern}" >> "${bundle_dir}/missing-files.txt"
  fi
}

copy_if_exists "${storage_root}/logs/application-${analysis_date}.log" "application-${analysis_date}.log"
copy_if_exists "${storage_root}/reports/health-events-${analysis_date}.jsonl" "health-events-${analysis_date}.jsonl"
copy_if_exists "${storage_root}/reports/orderbook-snapshots-${analysis_date}.jsonl" "orderbook-snapshots-${analysis_date}.jsonl"
copy_if_exists "${storage_root}/reports/window-events-${analysis_date}.jsonl" "window-events-${analysis_date}.jsonl"

copy_latest_matching "hourly-*.json" "latest-hourly-summary.json"
copy_latest_matching "daily-*.json" "latest-daily-summary.json"
copy_latest_matching "cumulative-*.json" "latest-cumulative-summary.json"
copy_latest_matching "health-hourly-*.json" "latest-health-hourly.json"
copy_latest_matching "health-daily-*.json" "latest-health-daily.json"
copy_latest_matching "health-cumulative-*.json" "latest-health-cumulative.json"

appsettings_source="${storage_root}/config/appsettings.json"
if [[ -f "${appsettings_source}" ]]; then
  sed -E \
    -e 's/"ApiKey"[[:space:]]*:[[:space:]]*"[^"]*"/"ApiKey": "***REDACTED***"/g' \
    -e 's/"ApiSecret"[[:space:]]*:[[:space:]]*"[^"]*"/"ApiSecret": "***REDACTED***"/g' \
    -e 's/"BotToken"[[:space:]]*:[[:space:]]*"[^"]*"/"BotToken": "***REDACTED***"/g' \
    "${appsettings_source}" > "${bundle_dir}/appsettings.redacted.json"
else
  echo "missing: ${appsettings_source}" >> "${bundle_dir}/missing-files.txt"
fi

log_source="${storage_root}/logs/application-${analysis_date}.log"
if [[ -f "${log_source}" ]]; then
  rg -n "BybitStale|Stale transition|data age threshold crossed|Reconnecting|Resync|OverallHealthChanged|StaleQuotes" "${log_source}" \
    > "${bundle_dir}/stale-diagnostics-excerpts.txt" || true
fi

cat > "${bundle_dir}/README.txt" <<EOF
ArbiScan analysis bundle
date=${analysis_date}
storage_root=${storage_root}
generated_utc=${timestamp}

Included when available:
- application log for the requested date
- health-events jsonl
- orderbook-snapshots jsonl
- window-events jsonl
- latest trading summaries
- latest health reports
- redacted appsettings
- stale diagnostics excerpts
EOF

tar -czf "${output_root}/${bundle_name}.tar.gz" -C "${output_root}" "${bundle_name}"

echo "Bundle directory: ${bundle_dir}"
echo "Bundle archive: ${output_root}/${bundle_name}.tar.gz"
