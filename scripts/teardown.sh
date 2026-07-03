#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

hub_rg="${HUB_RESOURCE_GROUP:-rg-egress-hub}"
spoke_rg="${SPOKE_RESOURCE_GROUP:-rg-egress-spoke}"
app_display_name="${APP_DISPLAY_NAME:-egress-proxy}"
delete_app_registration="${DELETE_APP_REGISTRATION:-0}"

az group delete --name "$hub_rg" --yes --no-wait
az group delete --name "$spoke_rg" --yes --no-wait

if [[ "$delete_app_registration" == "1" ]]; then
  app_id="$(az ad app list --display-name "$app_display_name" --query "[0].id" -o tsv)"
  if [[ -n "$app_id" ]]; then
    az ad app delete --id "$app_id"
  fi
fi

echo "Teardown requested for $hub_rg and $spoke_rg."
