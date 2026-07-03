#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Ampere (D*pls) SKUs are not available in every region — swedencentral is verified.
location="${LOCATION:-swedencentral}"
name_prefix="${NAME_PREFIX:-egress}"
hub_rg="${HUB_RESOURCE_GROUP:-rg-egress-hub}"
spoke_rg="${SPOKE_RESOURCE_GROUP:-rg-egress-spoke}"
identity_file="${IDENTITY_FILE:-$repo_root/infra/identity.generated.json}"
allowlist_file="${ALLOWLIST_FILE:-$repo_root/allowlist/allowlist.json}"
deployment_name="${DEPLOYMENT_NAME:-egress-proxy-demo}"
# The sample app image is pulled through the spoke egress floor, which only opens
# MCR and the demo ACR — GHCR is not reachable from the CAE subnet. deploy.sh
# therefore imports the GHCR release image into a small Basic ACR up front.
# Set SAMPLE_APP_IMAGE to any MCR-pullable image to skip the ACR entirely.
sample_app_image="${SAMPLE_APP_IMAGE:-}"
sample_image_source="${SAMPLE_IMAGE_SOURCE:-ghcr.io/alanta/azure-egress-proxy/sample-app:latest}"
acr_name="${ACR_NAME:-${name_prefix}acr$(az account show --query id -o tsv | tr -d '-' | cut -c1-10)}"
proxy_binary_url="${PROXY_BINARY_URL:-https://github.com/alanta/azure-egress-proxy/releases/latest/download/egress-proxy_linux_arm64}"
proxy_binary_sha256="${PROXY_BINARY_SHA256:-}"
# Space-separated key=value pairs for subscriptions whose policy mandates RG tags,
# e.g. RESOURCE_GROUP_TAGS="Owner=me@example.com Purpose=egress-demo".
resource_group_tags="${RESOURCE_GROUP_TAGS:-}"
deployer_principal_id="${DEPLOYER_PRINCIPAL_ID:-$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)}"
vm_admin_public_key="${VM_ADMIN_PUBLIC_KEY:-${SSH_PUBLIC_KEY:-$(cat "${HOME}/.ssh/id_rsa.pub" 2>/dev/null || true)}}"

if [[ -z "$deployer_principal_id" ]]; then
  echo "Set DEPLOYER_PRINCIPAL_ID or sign in with a user identity." >&2
  exit 1
fi

if [[ -z "$vm_admin_public_key" ]]; then
  echo "Set VM_ADMIN_PUBLIC_KEY (or SSH_PUBLIC_KEY) to a valid SSH public key." >&2
  exit 1
fi

"$repo_root/scripts/setup-identity.sh"

if [[ -z "$proxy_binary_sha256" ]]; then
  proxy_binary_sha256="$(curl -fsSL "${proxy_binary_url}.sha256" | awk '{print $1}')"
fi

container_registry_name=""
if [[ -z "$sample_app_image" ]]; then
  container_registry_name="$acr_name"
  image_tag="${sample_image_source##*:}"
  sample_app_image="${acr_name}.azurecr.io/sample-app:${image_tag}"

  rg_create_args=()
  if [[ -n "$resource_group_tags" ]]; then
    read -r -a tag_pairs <<<"$resource_group_tags"
    rg_create_args+=(--tags "${tag_pairs[@]}")
  fi
  az group create --name "$spoke_rg" --location "$location" --only-show-errors "${rg_create_args[@]}" >/dev/null
  az acr create \
    --name "$acr_name" \
    --resource-group "$spoke_rg" \
    --location "$location" \
    --sku Basic \
    --admin-enabled false \
    --only-show-errors >/dev/null

  # GHCR_USERNAME/GHCR_TOKEN are only needed while the source image is private.
  import_args=()
  if [[ -n "${GHCR_TOKEN:-}" ]]; then
    import_args+=(--username "${GHCR_USERNAME:-$USER}" --password "$GHCR_TOKEN")
  fi
  if ! az acr import \
    --name "$acr_name" \
    --source "$sample_image_source" \
    --image "sample-app:${image_tag}" \
    --force \
    --only-show-errors \
    "${import_args[@]}"; then
    # Private forks (or a private GHCR package) can't be imported anonymously.
    # If the image is already in the ACR — e.g. pushed with
    # `az acr build -r <acr> -t sample-app:<tag> -f src/SampleApp/Dockerfile .` —
    # that is just as good.
    if az acr repository show --name "$acr_name" --image "sample-app:${image_tag}" --only-show-errors >/dev/null 2>&1; then
      echo "WARN: import from $sample_image_source failed, but sample-app:${image_tag} already exists in $acr_name; continuing." >&2
    else
      echo "ERROR: cannot import $sample_image_source and $acr_name has no sample-app:${image_tag}." >&2
      echo "Either set GHCR_USERNAME/GHCR_TOKEN, or build it in place:" >&2
      echo "  az acr build -r $acr_name -t sample-app:${image_tag} -f src/SampleApp/Dockerfile ." >&2
      exit 1
    fi
  fi
fi

read_json() {
  local key="$1"
  python3 - "$identity_file" "$key" <<'PY'
import json,sys
doc=json.load(open(sys.argv[1],encoding="utf-8"))
print(doc[sys.argv[2]])
PY
}

rg_tags_json="$(python3 - "$resource_group_tags" <<'PY'
import json,sys
pairs=sys.argv[1].split()
print(json.dumps(dict(p.split("=",1) for p in pairs)))
PY
)"

expect_aud="$(read_json EXPECT_AUD)"
expect_iss="$(read_json EXPECT_ISS)"
jwks_url="$(read_json JWKS_URL)"
tenant_id="$(read_json tenantId)"

az deployment sub create \
  --name "$deployment_name" \
  --location "$location" \
  --template-file "$repo_root/infra/main.bicep" \
  --parameters \
    location="$location" \
    hubResourceGroupName="$hub_rg" \
    spokeResourceGroupName="$spoke_rg" \
    namePrefix="$name_prefix" \
    deployerPrincipalId="$deployer_principal_id" \
    tenantId="$tenant_id" \
    jwksUrl="$jwks_url" \
    expectIss="$expect_iss" \
    expectAud="$expect_aud" \
    sampleAppImage="$sample_app_image" \
    containerRegistryName="$container_registry_name" \
    resourceGroupTags="$rg_tags_json" \
    proxyBinaryUrl="$proxy_binary_url" \
    proxyBinarySha256="$proxy_binary_sha256" \
    vmAdminPublicKey="$vm_admin_public_key"

deployment_output_json="$(az deployment sub show --name "$deployment_name" --query properties.outputs -o json)"
sample_client_id="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["sampleAppManagedIdentityClientId"]["value"])
PY
)"
allowlist_account="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["allowlistStorageAccountName"]["value"])
PY
)"
allowlist_container="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["allowlistContainerName"]["value"])
PY
)"
allowlist_blob="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["allowlistBlobName"]["value"])
PY
)"
frontdoor_url="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["frontDoorUrl"]["value"])
PY
)"

python3 - "$allowlist_file" "$sample_client_id" <<'PY'
import json,sys
path,appid=sys.argv[1],sys.argv[2]
doc=json.load(open(path,encoding="utf-8"))
for module in doc.get("modules",[]):
    if module.get("id")=="sample-app":
        module["appid"]=appid
json.dump(doc,open(path,"w",encoding="utf-8"),indent=2)
open(path,"a",encoding="utf-8").write("\n")
PY

az storage blob upload \
  --account-name "$allowlist_account" \
  --container-name "$allowlist_container" \
  --name "$allowlist_blob" \
  --file "$allowlist_file" \
  --auth-mode login \
  --overwrite \
  --only-show-errors >/dev/null

echo "Front Door URL: $frontdoor_url"
echo "Demo command: scripts/demo.sh \"$frontdoor_url\""
