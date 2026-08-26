#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd -- "$script_dir/.." && pwd)"

exec python3 "$script_dir/public_check.py" \
  --repo "$repo_dir" \
  --fixtures "$script_dir/fixtures" \
  --output "$repo_dir/week-1-public-report.json" \
  "$@"
