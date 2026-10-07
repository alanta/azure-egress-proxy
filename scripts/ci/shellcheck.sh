#!/usr/bin/env bash
# Run shellcheck over every shell script under scripts/, including scripts/ci/.
# Run from anywhere: scripts/ci/shellcheck.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

mapfile -t script_files < <(find scripts -type f -name '*.sh' | sort)
if [ "${#script_files[@]}" -eq 0 ]; then
  echo "No shell scripts found under scripts/."
  exit 0
fi
shellcheck "${script_files[@]}"
echo "shellcheck passed on ${#script_files[@]} scripts."
