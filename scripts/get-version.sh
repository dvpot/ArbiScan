#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
commit_count="$(git -C "$repo_root" rev-list --count HEAD)"

printf '2.0.%s\n' "$commit_count"
