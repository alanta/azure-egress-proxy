#!/usr/bin/env bash
# Compile every Bicep file under infra/. Needs the Azure CLI (az bicep build).
# Run from anywhere: scripts/ci/build-bicep.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

if ! command -v az >/dev/null 2>&1; then
  echo "error: the Azure CLI is required for az bicep build." >&2
  exit 1
fi
mapfile -t bicep_files < <(find infra -type f -name '*.bicep' | sort)
if [ "${#bicep_files[@]}" -eq 0 ]; then
  echo "No .bicep files found under infra/."
  exit 0
fi
for file in "${bicep_files[@]}"; do
  az bicep build --file "$file" --stdout >/dev/null
  echo "ok  $file"
done
