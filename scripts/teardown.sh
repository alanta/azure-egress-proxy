#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# All three resource groups, regardless of which phase created them. The hub group is created by
# infra/bootstrap.bicep (phase 1) and the other two by infra/main.bicep (phase 2); split ownership
# of resource-group creation is exactly the thing a teardown gets wrong. The management group only
# exists in Mode 2, so its delete is tolerated as a no-op rather than guarded on a flag this script
# does not have — a group nobody deletes is a recurring cost with no symptom.
hub_rg="${HUB_RESOURCE_GROUP:-rg-egress-hub}"
spoke_rg="${SPOKE_RESOURCE_GROUP:-rg-egress-spoke}"
mgmt_rg="${MGMT_RESOURCE_GROUP:-rg-egress-mgmt}"
app_display_name="${APP_DISPLAY_NAME:-egress-proxy}"
delete_app_registration="${DELETE_APP_REGISTRATION:-0}"

for rg in "$hub_rg" "$spoke_rg" "$mgmt_rg"; do
  if az group exists --name "$rg" | grep -q true; then
    az group delete --name "$rg" --yes --no-wait
    echo "Delete requested for $rg."
  else
    echo "Skipping $rg (does not exist)."
  fi
done

if [[ "$delete_app_registration" == "1" ]]; then
  app_id="$(az ad app list --display-name "$app_display_name" --query "[0].id" -o tsv)"
  if [[ -n "$app_id" ]]; then
    az ad app delete --id "$app_id"
  fi
fi

echo "Teardown requested for $hub_rg, $spoke_rg and $mgmt_rg."
