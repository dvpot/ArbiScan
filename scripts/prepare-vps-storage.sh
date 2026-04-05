#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 2 ]]; then
  echo "Usage: $0 [old_root] [new_root]" >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
old_root="${1:-/srv/ArbiScan}"
new_root="${2:-/srv/ArbiScan}"

mkdir -p "${new_root}/config" "${new_root}/logs" "${new_root}/data" "${new_root}/reports"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"

backup_if_exists() {
  local source_path="$1"
  local target_path="$2"

  if [[ -f "${source_path}" ]]; then
    cp "${source_path}" "${target_path}"
  fi
}

backup_if_exists "${old_root}/config/appsettings.json" "${new_root}/config/appsettings.v1-backup-${timestamp}.json"
backup_if_exists "${old_root}/config/telegramsettings.json" "${new_root}/config/telegramsettings.v1-backup-${timestamp}.json"

if [[ -f "${old_root}/config/telegramsettings.json" && ! -f "${new_root}/config/telegramsettings.json" ]]; then
  cp "${old_root}/config/telegramsettings.json" "${new_root}/config/telegramsettings.json"
fi

if [[ ! -f "${new_root}/config/appsettings.json" ]]; then
  cp "${repo_root}/config/appsettings.example.json" "${new_root}/config/appsettings.json"
fi

if [[ ! -f "${new_root}/config/telegramsettings.json" ]]; then
  cp "${repo_root}/config/telegramsettings.example.json" "${new_root}/config/telegramsettings.json"
fi

rm -rf "${new_root}/logs"/* "${new_root}/data"/* "${new_root}/reports"/*

printf 'Prepared storage at %s for ArbiScan v2\n' "${new_root}"
printf 'Backups (if source files existed) were written to %s/config\n' "${new_root}"
printf 'Runtime directories logs/data/reports were cleared.\n'
