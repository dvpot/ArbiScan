#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
v2_base_commit="d43d8150de01a8e89ba9b6172047e64b4ab66eff"
patch_count="$(git -C "$repo_root" rev-list --count "${v2_base_commit}..HEAD")"

printf '2.0.%s\n' "$patch_count"
