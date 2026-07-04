#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Lightweight step logging so it's clear what the script is doing and where it
# stops if something fails. Steps go to stderr to keep stdout clean for the
# final output values.
step_no=0
log() { printf '\n\033[1;34m==>\033[0m %s\n' "$*" >&2; }
info() { printf '    %s\n' "$*" >&2; }
step() { step_no=$((step_no + 1)); log "[$step_no] $*"; }

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
# Binary delivery. The VM fetches the proxy binary from PROXY_BINARY_URL at boot via
# a plain (unauthenticated) curl, so it MUST be an http(s):// URL reachable from the
# proxy subnet — never a local path. Leave PROXY_BINARY_URL unset (the default) to have
# deploy.sh seed the binary into a public-read bootstrap storage blob and hand the VM
# that URL; this keeps the artifact in-tenant and pins the checksum to the exact bytes
# uploaded (no latest-tag TOCTOU). Set PROXY_BINARY_URL only to point at a URL you host
# yourself (e.g. a public GitHub release once the repo is public).
proxy_binary_url="${PROXY_BINARY_URL:-}"
proxy_binary_sha256="${PROXY_BINARY_SHA256:-}"
# Source deploy.sh pulls the binary FROM when seeding to storage: a local file if set,
# otherwise downloaded from this URL (the canonical release asset).
proxy_binary_file="${PROXY_BINARY_FILE:-}"
proxy_binary_source_url="${PROXY_BINARY_SOURCE_URL:-https://github.com/alanta/azure-egress-proxy/releases/latest/download/egress-proxy_linux_arm64}"
bootstrap_storage_account="${BOOTSTRAP_STORAGE_ACCOUNT:-${name_prefix}bin$(az account show --query id -o tsv | tr -d '-' | cut -c1-8)}"
bootstrap_container="${BOOTSTRAP_CONTAINER:-proxy-bin}"
bootstrap_blob_name="egress-proxy_linux_arm64"

# --refresh-binary: fast dev loop — re-seed the binary and hot-swap it on the running
# VMSS instances (no full redeploy, since cloud-init only runs at first provision).
refresh_binary_only=false
for arg in "$@"; do
  case "$arg" in
    --refresh-binary) refresh_binary_only=true ;;
    *) echo "Unknown argument: $arg" >&2; exit 1 ;;
  esac
done
# Space-separated key=value pairs for subscriptions whose policy mandates RG tags,
# e.g. RESOURCE_GROUP_TAGS="Owner=me@example.com Purpose=egress-demo".
resource_group_tags="${RESOURCE_GROUP_TAGS:-}"
deployer_principal_id="${DEPLOYER_PRINCIPAL_ID:-$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)}"
vm_admin_public_key="${VM_ADMIN_PUBLIC_KEY:-${SSH_PUBLIC_KEY:-$(cat "${HOME}/.ssh/id_rsa.pub" 2>/dev/null || true)}}"

# Ensure a throwaway storage account + public-read container exists to host the binary.
# It uses shared-key auth (kept enabled here) so it works without waiting on data-plane
# RBAC propagation — this account holds only the non-secret proxy binary.
ensure_bootstrap_storage() {
  info "Ensuring bootstrap storage '$bootstrap_storage_account' in '$spoke_rg'"
  az group create --name "$spoke_rg" --location "$location" --only-show-errors >/dev/null
  if ! az storage account show --name "$bootstrap_storage_account" --resource-group "$spoke_rg" --only-show-errors >/dev/null 2>&1; then
    az storage account create \
      --name "$bootstrap_storage_account" \
      --resource-group "$spoke_rg" \
      --location "$location" \
      --sku Standard_LRS \
      --kind StorageV2 \
      --allow-blob-public-access true \
      --only-show-errors >/dev/null
  fi
  az storage container create \
    --name "$bootstrap_container" \
    --account-name "$bootstrap_storage_account" \
    --public-access blob \
    --auth-mode key \
    --only-show-errors >/dev/null
}

# Resolve the delivery URL + checksum the VM will use. Either honour an explicitly
# provided (http(s)) PROXY_BINARY_URL, or seed the binary into bootstrap storage and
# point at that blob. Sets proxy_binary_url and proxy_binary_sha256.
resolve_proxy_binary() {
  if [[ -n "$proxy_binary_url" ]]; then
    if [[ "$proxy_binary_url" != http://* && "$proxy_binary_url" != https://* ]]; then
      echo "ERROR: PROXY_BINARY_URL must be an http(s):// URL reachable from the VM (the VM curls it at boot); got: '$proxy_binary_url'" >&2
      exit 1
    fi
    if [[ -z "$proxy_binary_sha256" ]]; then
      info "Fetching ${proxy_binary_url}.sha256"
      proxy_binary_sha256="$(curl -fsSL "${proxy_binary_url}.sha256" | awk '{print $1}')"
    fi
    info "Proxy binary (external): $proxy_binary_url"
    info "SHA256: $proxy_binary_sha256"
    return
  fi

  local workdir binfile published
  workdir="$(mktemp -d)"
  trap 'rm -rf "$workdir"' RETURN
  binfile="$workdir/egress-proxy"
  if [[ -n "$proxy_binary_file" ]]; then
    info "Binary source (local): $proxy_binary_file"
    cp "$proxy_binary_file" "$binfile"
  else
    info "Binary source (download): $proxy_binary_source_url"
    curl -fsSL "$proxy_binary_source_url" -o "$binfile"
    if published="$(curl -fsSL "${proxy_binary_source_url}.sha256" 2>/dev/null | awk '{print $1}')" && [[ -n "$published" ]]; then
      echo "${published}  ${binfile}" | sha256sum -c - >/dev/null
      info "Verified download against published .sha256"
    fi
  fi
  # Pin the checksum to the exact bytes we host — no dependence on a mutable upstream.
  proxy_binary_sha256="$(sha256sum "$binfile" | awk '{print $1}')"

  ensure_bootstrap_storage
  info "Uploading binary to ${bootstrap_storage_account}/${bootstrap_container}/${bootstrap_blob_name}"
  az storage blob upload \
    --account-name "$bootstrap_storage_account" \
    --container-name "$bootstrap_container" \
    --name "$bootstrap_blob_name" \
    --file "$binfile" \
    --auth-mode key \
    --overwrite \
    --only-show-errors >/dev/null
  proxy_binary_url="https://${bootstrap_storage_account}.blob.core.windows.net/${bootstrap_container}/${bootstrap_blob_name}"
  info "Delivery URL (in-tenant): $proxy_binary_url"
  info "SHA256: $proxy_binary_sha256"
}

# Hot-swap the binary on the already-provisioned VMSS instances. Everything else
# (systemd unit, env, config) is written by cloud-init at first boot, so this only
# needs to drop the binary and restart the service.
hotswap_proxy_binary() {
  local vmss ids
  vmss="$(az vmss list -g "$hub_rg" --query "[0].name" -o tsv 2>/dev/null || true)"
  if [[ -z "$vmss" ]]; then
    echo "ERROR: no VMSS found in '$hub_rg' — run a full deploy before --refresh-binary." >&2
    exit 1
  fi
  ids="$(az vmss list-instances -g "$hub_rg" -n "$vmss" --query "[].instanceId" -o tsv)"
  local script
  script="set -e
curl -fsSL '${proxy_binary_url}' -o /tmp/egress-proxy.new
echo '${proxy_binary_sha256}  /tmp/egress-proxy.new' | sha256sum -c -
install -m0755 /tmp/egress-proxy.new /usr/local/bin/egress-proxy
rm -f /tmp/egress-proxy.new
systemctl restart egress-proxy
sleep 2
systemctl is-active egress-proxy
ss -ltn | grep -q ':4750' && echo 'LISTENING on 4750' || { echo 'NOT LISTENING'; exit 1; }"
  for id in $ids; do
    info "Refreshing binary on '$vmss' instance $id"
    az vmss run-command invoke -g "$hub_rg" -n "$vmss" --instance-id "$id" \
      --command-id RunShellScript --scripts "$script" \
      --query "value[0].message" -o tsv >&2
  done
}

if [[ "$refresh_binary_only" == true ]]; then
  step "Fast loop: refreshing proxy binary on running VMSS (no redeploy)"
  resolve_proxy_binary
  hotswap_proxy_binary
  log "Binary refresh complete"
  exit 0
fi

if [[ -z "$deployer_principal_id" ]]; then
  echo "Set DEPLOYER_PRINCIPAL_ID or sign in with a user identity." >&2
  exit 1
fi

if [[ -z "$vm_admin_public_key" ]]; then
  echo "Set VM_ADMIN_PUBLIC_KEY (or SSH_PUBLIC_KEY) to a valid SSH public key." >&2
  exit 1
fi

step "Setting up workload identity (setup-identity.sh)"
"$repo_root/scripts/setup-identity.sh"

step "Resolving / seeding proxy binary"
resolve_proxy_binary

container_registry_name=""
if [[ -z "$sample_app_image" ]]; then
  step "Preparing sample-app image in a private ACR"
  container_registry_name="$acr_name"
  image_tag="${sample_image_source##*:}"
  sample_app_image="${acr_name}.azurecr.io/sample-app:${image_tag}"

  rg_create_args=()
  if [[ -n "$resource_group_tags" ]]; then
    read -r -a tag_pairs <<<"$resource_group_tags"
    rg_create_args+=(--tags "${tag_pairs[@]}")
  fi
  info "Ensuring spoke resource group '$spoke_rg' in '$location'"
  az group create --name "$spoke_rg" --location "$location" --only-show-errors "${rg_create_args[@]}" >/dev/null
  info "Ensuring ACR '$acr_name' (Basic)"
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
  info "Importing $sample_image_source -> $sample_app_image"
  if ! az acr import \
    --name "$acr_name" \
    --source "$sample_image_source" \
    --image "sample-app:${image_tag}" \
    --force \
    --only-show-errors \
    "${import_args[@]}"; then
    # Private forks (or a private GHCR package) can't be imported anonymously.
    # If the image is already in the ACR — e.g. built locally and pushed (see the hint
    # below) — that is just as good. NB: `az acr build` does NOT work with this Dockerfile:
    # ACR Tasks' dependency scanner can't parse the BuildKit `FROM --platform=$BUILDPLATFORM`
    # line, so build locally with docker/podman instead.
    if az acr repository show --name "$acr_name" --image "sample-app:${image_tag}" --only-show-errors >/dev/null 2>&1; then
      echo "WARN: import from $sample_image_source failed, but sample-app:${image_tag} already exists in $acr_name; continuing." >&2
    else
      echo "ERROR: cannot import $sample_image_source and $acr_name has no sample-app:${image_tag}." >&2
      echo "Either set GHCR_USERNAME/GHCR_TOKEN, or build and push it locally:" >&2
      echo "  docker build --platform linux/amd64 -t $acr_name.azurecr.io/sample-app:${image_tag} -f src/SampleApp/Dockerfile ." >&2
      echo "  az acr login -n $acr_name && docker push $acr_name.azurecr.io/sample-app:${image_tag}" >&2
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

step "Reading identity config from $identity_file"
expect_aud="$(read_json EXPECT_AUD)"
expect_iss="$(read_json EXPECT_ISS)"
jwks_url="$(read_json JWKS_URL)"
tenant_id="$(read_json tenantId)"

step "Deploying infrastructure (az deployment sub create: $deployment_name)"
info "This is the long one — provisions hub/spoke, proxy VM, ACA + sample app, etc."
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

step "Reading deployment outputs"
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
app_url="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["sampleAppUrl"]["value"])
PY
)"

step "Patching allowlist with sample-app client id ($sample_client_id)"
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

step "Uploading allowlist to $allowlist_account/$allowlist_container/$allowlist_blob"
az storage blob upload \
  --account-name "$allowlist_account" \
  --container-name "$allowlist_container" \
  --name "$allowlist_blob" \
  --file "$allowlist_file" \
  --auth-mode login \
  --overwrite \
  --only-show-errors >/dev/null

log "Deployment complete"
echo "Sample app URL: $app_url"
echo "Demo command: scripts/demo.sh \"$app_url\""
