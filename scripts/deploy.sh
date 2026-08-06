#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Scratch space for the config documents this script patches before upload. They default to the
# repo's tracked allowlist/*.json, so patching them in place would leave the working tree dirty
# and feed already-patched files to the next run (it also broke the byte-for-byte renderer test).
patch_dir="$(mktemp -d -t egress-deploy.XXXXXX)"
trap 'rm -rf "$patch_dir"' EXIT

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
# The management zone: control plane + console, in their own network and their own Container Apps
# environment, peered with nothing. Created only when the control plane is deployed — Mode 1 is the
# default and pays for none of it.
mgmt_rg="${MGMT_RESOURCE_GROUP:-rg-egress-mgmt}"
identity_file="${IDENTITY_FILE:-$repo_root/infra/identity.generated.json}"
allowlist_file="${ALLOWLIST_FILE:-$repo_root/allowlist/allowlist.json}"
deployment_name="${DEPLOYMENT_NAME:-egress-proxy-demo}"
# The sample app image is pulled through the spoke egress floor, which only opens
# MCR and the demo ACR — GHCR is not reachable from the CAE subnet. deploy.sh
# therefore imports the GHCR release image into a small Basic ACR up front.
# Set SAMPLE_APP_IMAGE to any MCR-pullable image to skip the ACR entirely.
sample_app_image="${SAMPLE_APP_IMAGE:-}"
sample_image_source="${SAMPLE_IMAGE_SOURCE:-ghcr.io/alanta/azure-egress-proxy/sample-app:latest}"
# Mode 2 (control-plane API) is opt-in: DEPLOY_CONTROL_PLANE=true deploys it and seeds its state
# blob. Left false, this is the GitOps topology (Mode 1) and nothing below changes. The image is
# imported into the demo ACR like the sample app's, for the same reason (GHCR is off the floor);
# set CONTROL_PLANE_IMAGE to a ref that is already pullable to skip the import.
deploy_control_plane="${DEPLOY_CONTROL_PLANE:-false}"
control_plane_image="${CONTROL_PLANE_IMAGE:-}"
control_plane_image_source="${CONTROL_PLANE_IMAGE_SOURCE:-ghcr.io/alanta/azure-egress-proxy/control-plane:latest}"
# Mode 3 (read-only management console) is opt-in on top of Mode 2: it reads the control-plane
# API, so DEPLOY_PORTAL=true without DEPLOY_CONTROL_PLANE=true is rejected below. Its image is
# imported into the demo ACR like the others (GHCR is off the egress floor).
#
# PORTAL_ALLOWED_SOURCE_IPS is a comma-separated CIDR list, e.g. "203.0.113.0/24". Empty means no
# network restriction — the console is still behind Entra sign-in, but it is an admin surface for
# a security control, so restricting it is the production posture.
deploy_portal="${DEPLOY_PORTAL:-false}"
portal_image="${PORTAL_IMAGE:-}"
portal_image_source="${PORTAL_IMAGE_SOURCE:-ghcr.io/alanta/azure-egress-proxy/portal:latest}"
portal_allowed_source_ips="${PORTAL_ALLOWED_SOURCE_IPS:-}"
# The console signs operators in through an Entra app registration, which is not an ARM resource
# and so cannot be created by the template. Left unset, deploy.sh creates one (see below).
# The registry serves all three zones, so it is platform infrastructure and lives in the HUB
# resource group. Only the name derivation is unchanged; bootstrap.bicep creates it.
acr_name="${ACR_NAME:-${name_prefix}acr$(az account show --query id -o tsv | tr -d '-' | cut -c1-10)}"
# BUILD_IMAGES_LOCALLY=true builds the three platform images from this working tree and pushes
# them to the demo ACR, instead of importing the published ones from GHCR. That is what you want
# when the change under test has not been pushed yet — there is no release image to import — and
# it is the same ACR either way, because the CAE egress floor opens MCR and this registry only.
# Images are tagged from the git description, so a redeploy of changed code always lands as a new
# container-app revision rather than silently reusing a cached :latest.
build_images_locally="${BUILD_IMAGES_LOCALLY:-false}"
container_cli="${CONTAINER_CLI:-}"
# Binary delivery. The VM fetches the proxy binary from PROXY_BINARY_URL at boot via
# a plain (unauthenticated) curl, so it MUST be an http(s):// URL reachable from the
# proxy subnet — never a local path. Leave PROXY_BINARY_URL unset (the default) to have
# deploy.sh seed the binary into a public-read bootstrap storage blob and hand the VM
# that URL; this keeps the artifact in-tenant and pins the checksum to the exact bytes
# uploaded (no latest-tag TOCTOU). Set PROXY_BINARY_URL only to point at a URL you host
# yourself (e.g. a public GitHub release once the repo is public).
proxy_binary_url="${PROXY_BINARY_URL:-}"
# Kept separate from proxy_binary_url, which resolve_proxy_binary fills in either way. Only an
# explicit override is passed to main.bicep: left empty, hub.bicep composes the URL from the
# bootstrap account it references as existing, which also keeps it right in sovereign clouds.
proxy_binary_url_override="${PROXY_BINARY_URL:-}"
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

# ── Phase 1: the artifact phase ────────────────────────────────────────────────────────────────
# Compute cannot boot until the artifacts it fetches at start-up exist, and neither a blob upload
# nor an image push is an ARM resource main.bicep could sequence. So bootstrap.bicep creates the
# hub resource group, the bootstrap storage account and (optionally) the registry; this script
# fills both; main.bicep consumes them.
#
# It replaces the old ensure_bootstrap_storage / ensure_demo_acr pair. Those created the accounts
# imperatively, which is how the bootstrap account ended up with the CLI's defaults on two settings
# the declared config account has always set: shared-key access left enabled, and TLS 1.0.
# ensure_demo_acr also created the SPOKE resource group as a side effect; main.bicep still creates
# it, and nothing between the two phases needs it earlier than that.
deploy_bootstrap() {
  [[ -n "$bootstrap_done" ]] && return 0

  local acr_param="false"
  [[ "$needs_registry" == "true" ]] && acr_param="true"

  step "Deploying the artifact phase (az deployment sub create: ${deployment_name}-bootstrap)"
  info "Hub resource group '$hub_rg', bootstrap storage '$bootstrap_storage_account', registry: $acr_param"
  az deployment sub create \
    --name "${deployment_name}-bootstrap" \
    --location "$location" \
    --template-file "$repo_root/infra/bootstrap.bicep" \
    --parameters \
      location="$location" \
      hubResourceGroupName="$hub_rg" \
      resourceGroupTags="$rg_tags_json" \
      deployerPrincipalId="$deployer_principal_id" \
      bootstrapStorageAccountName="$bootstrap_storage_account" \
      bootstrapContainerName="$bootstrap_container" \
      deployContainerRegistry="$acr_param" \
      containerRegistryName="$acr_name" \
    --only-show-errors >/dev/null

  bootstrap_done=1
}

# Upload the proxy binary with --auth-mode login. Shared keys are disabled on the account, so the
# Storage Blob Data Contributor assignment bootstrap.bicep just made is the only way in — and Entra
# role propagation is not instant. main.bicep does the same thing on the config account, but there
# minutes of deployment sit between the assignment and the upload; here the upload follows
# immediately, so the retry is part of the design rather than defensive padding.
upload_with_retry() {
  local account="$1" container="$2" blob="$3" file="$4"
  local attempt=1 max=10 delay=15

  while true; do
    if az storage blob upload \
      --account-name "$account" \
      --container-name "$container" \
      --name "$blob" \
      --file "$file" \
      --auth-mode login \
      --overwrite \
      --only-show-errors >/dev/null 2>"$patch_dir/upload.err"; then
      return 0
    fi
    if (( attempt >= max )); then
      echo "ERROR: uploading '$blob' to $account/$container failed after $attempt attempts." >&2
      echo "       The proxy binary must exist in the blob before the scale set first boots; a" >&2
      echo "       missing one surfaces as a VM that starts and never serves. Last error:" >&2
      sed 's/^/       /' "$patch_dir/upload.err" >&2
      exit 1
    fi
    info "Upload attempt $attempt failed (likely Entra role propagation); retrying in ${delay}s"
    attempt=$((attempt + 1))
    sleep "$delay"
  done
}

# Resolve the delivery URL + checksum the VM will use. Either honour an explicitly provided
# (http(s)) PROXY_BINARY_URL, or seed the binary into the bootstrap blob and point at it. Sets
# proxy_binary_url and proxy_binary_sha256. The URL is only needed by this script (the
# --refresh-binary hot-swap); hub.bicep composes the same URL from the account it references.
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

  info "Uploading binary to ${bootstrap_storage_account}/${bootstrap_container}/${bootstrap_blob_name}"
  upload_with_retry "$bootstrap_storage_account" "$bootstrap_container" "$bootstrap_blob_name" "$binfile"
  proxy_binary_url="$(az storage account show --name "$bootstrap_storage_account" --resource-group "$hub_rg" \
    --query "primaryEndpoints.blob" -o tsv)${bootstrap_container}/${bootstrap_blob_name}"
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

# Bicep takes this as a bool, and az passes the string through: a typo like DEPLOY_CONTROL_PLANE=1
# would silently deploy Mode 1 instead of failing, so reject anything that isn't true/false here.
if [[ "$deploy_control_plane" != "true" && "$deploy_control_plane" != "false" ]]; then
  echo "DEPLOY_CONTROL_PLANE must be 'true' or 'false' (got '$deploy_control_plane')." >&2
  exit 1
fi

if [[ "$deploy_portal" != "true" && "$deploy_portal" != "false" ]]; then
  echo "DEPLOY_PORTAL must be 'true' or 'false' (got '$deploy_portal')." >&2
  exit 1
fi

# The console reads policy through the control-plane API and has no other route to it. Deploying
# it without Mode 2 would produce a console whose policy surfaces are permanently empty.
if [[ "$deploy_portal" == "true" && "$deploy_control_plane" != "true" ]]; then
  echo "DEPLOY_PORTAL=true requires DEPLOY_CONTROL_PLANE=true: the console reads policy through the control-plane API." >&2
  exit 1
fi

step "Setting up workload identity (setup-identity.sh)"
"$repo_root/scripts/setup-identity.sh"

# Import one GHCR image into the registry. Every application in this deployment runs in a Container
# Apps subnet whose egress floor opens MCR and this registry only — GHCR is not reachable from
# there — so every image the platform runs has to come through here.
#
# `az acr import` is a SERVER-SIDE pull: the registry service fetches from GHCR itself. That is why
# it works without a subnet, and why moving the registry to the hub resource group changes nothing
# here beyond which group it is looked up in.
# Args: <source ref> <repo name in acr> <dockerfile path, for the build-it-yourself hint>
import_image() {
  local source="$1" repo="$2" dockerfile="$3"
  local tag="${source##*:}"
  local target="${acr_name}.azurecr.io/${repo}:${tag}"

  # GHCR_USERNAME/GHCR_TOKEN are only needed while the source image is private.
  local import_args=()
  if [[ -n "${GHCR_TOKEN:-}" ]]; then
    import_args+=(--username "${GHCR_USERNAME:-$USER}" --password "$GHCR_TOKEN")
  fi
  info "Importing $source -> $target"
  if ! az acr import \
    --name "$acr_name" \
    --source "$source" \
    --image "${repo}:${tag}" \
    --force \
    --only-show-errors \
    "${import_args[@]}"; then
    # Private forks (or a private GHCR package) can't be imported anonymously.
    # If the image is already in the ACR — e.g. built locally and pushed (see the hint
    # below) — that is just as good. NB: `az acr build` does NOT work with these Dockerfiles:
    # ACR Tasks' dependency scanner can't parse the BuildKit `FROM --platform=$BUILDPLATFORM`
    # line, so build locally with docker/podman instead.
    if az acr repository show --name "$acr_name" --image "${repo}:${tag}" --only-show-errors >/dev/null 2>&1; then
      echo "WARN: import from $source failed, but ${repo}:${tag} already exists in $acr_name; continuing." >&2
    else
      echo "ERROR: cannot import $source and $acr_name has no ${repo}:${tag}." >&2
      echo "Either set GHCR_USERNAME/GHCR_TOKEN, or build and push it locally:" >&2
      echo "  docker build --platform linux/amd64 -t $target -f $dockerfile ." >&2
      echo "  az acr login -n $acr_name && docker push $target" >&2
      exit 1
    fi
  fi

  printf '%s' "$target"
}

# Build one image from this working tree and push it to the demo ACR. Same contract as
# import_image: everything chatty goes to stderr, the resolved reference is the only thing on
# stdout, because the call site captures it.
# Args: <repo name in acr> <dockerfile path>
build_image() {
  local repo="$1" dockerfile="$2"
  local target="${acr_name}.azurecr.io/${repo}:${local_image_tag}"

  info "Building $dockerfile -> $target (linux/amd64)"
  # --platform is explicit because the Container Apps environment is amd64 and a developer on
  # arm64 would otherwise push an image that cannot start there, with a runtime error rather
  # than a build one.
  "$container_cli" build --platform linux/amd64 -t "$target" -f "$repo_root/$dockerfile" "$repo_root" >&2

  acr_docker_login
  info "Pushing $target"
  "$container_cli" push "$target" >&2

  printf '%s' "$target"
}

# One registry login per run, with the token on stdin rather than in argv — a push credential is
# a credential (SECURITY_GUIDELINES.md § 1.4), and argv is world-readable through /proc.
acr_docker_login() {
  [[ -n "$acr_logged_in" ]] && return 0

  local token
  info "Signing in to ACR '$acr_name'"
  token="$(az acr login --name "$acr_name" --expose-token --only-show-errors --query accessToken -o tsv)"
  printf '%s' "$token" | "$container_cli" login "${acr_name}.azurecr.io" \
    --username 00000000-0000-0000-0000-000000000000 --password-stdin >&2

  acr_logged_in=1
}

# Import the published image, or build the local one. The call sites do not care which.
prepare_image() {
  local source="$1" repo="$2" dockerfile="$3"

  if [[ "$build_images_locally" == "true" ]]; then
    build_image "$repo" "$dockerfile"
  else
    import_image "$source" "$repo" "$dockerfile"
  fi
}

container_registry_name=""
bootstrap_done=""
acr_logged_in=""
local_image_tag=""

if [[ "$build_images_locally" == "true" ]]; then
  if [[ -z "$container_cli" ]]; then
    if command -v docker >/dev/null 2>&1; then container_cli=docker
    elif command -v podman >/dev/null 2>&1; then container_cli=podman
    else
      echo "ERROR: BUILD_IMAGES_LOCALLY=true needs docker or podman on PATH (or set CONTAINER_CLI)." >&2
      exit 1
    fi
  fi
  # The tag names the commit the image was built from, so a deployed revision is traceable back
  # to source. A dirty tree gets a timestamp as well: those bytes exist in no commit, and reusing
  # the clean commit's tag for them would make the registry lie about what is running.
  local_image_tag="local-$(git -C "$repo_root" rev-parse --short HEAD)"
  if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=no)" ]]; then
    local_image_tag="${local_image_tag}-dirty-$(date +%Y%m%d%H%M%S)"
  fi
  info "Building images locally with $container_cli, tagged :$local_image_tag"
fi

rg_tags_json="$(python3 - "$resource_group_tags" <<'PY'
import json,sys
pairs=sys.argv[1].split()
print(json.dumps(dict(p.split("=",1) for p in pairs)))
PY
)"

# Whether a registry is needed at all, decided BEFORE phase 1 runs. Setting SAMPLE_APP_IMAGE,
# CONTROL_PLANE_IMAGE and PORTAL_IMAGE to references that are already pullable skips the registry
# entirely — nothing to import, nothing to pull from, no AcrPull to grant. Phase 1 runs before
# image preparation, but the decision is knowable here, so it is passed in rather than assumed.
needs_registry=false
[[ -z "$sample_app_image" ]] && needs_registry=true
[[ "$deploy_control_plane" == "true" && -z "$control_plane_image" ]] && needs_registry=true
[[ "$deploy_portal" == "true" && -z "$portal_image" ]] && needs_registry=true

# ── Phase 1 ───────────────────────────────────────────────────────────────────────────────────
# Both artifact stores, then both artifacts, all before main.bicep creates the compute that
# fetches them.
deploy_bootstrap

step "Seeding the proxy binary"
resolve_proxy_binary

if [[ "$needs_registry" == "true" ]]; then
  container_registry_name="$acr_name"
fi

if [[ -z "$sample_app_image" ]]; then
  step "Preparing sample-app image in the platform registry"
  sample_app_image="$(prepare_image "$sample_image_source" sample-app src/SampleApp/Dockerfile)"
fi

# Mode 2. The control plane is the only writer of the allowlist blobs, so deploying it changes
# the topology: DEPLOY_CONTROL_PLANE=true opts in, and everything below (its image, the bicep
# flag, the state seed) follows from that one switch.
if [[ "$deploy_control_plane" == "true" && -z "$control_plane_image" ]]; then
  step "Preparing control-plane image in the platform registry"
  control_plane_image="$(prepare_image "$control_plane_image_source" control-plane src/ControlPlane/Dockerfile)"
fi

# Mode 3.
if [[ "$deploy_portal" == "true" && -z "$portal_image" ]]; then
  step "Preparing management-console image in the platform registry"
  portal_image="$(prepare_image "$portal_image_source" portal src/Portal/Dockerfile)"
fi

# The console's sign-in app registration. Created here rather than in Bicep because an Entra app
# registration is not an ARM resource. Idempotent: an existing registration with this display name
# is reused, and a fresh client secret is minted for this deployment.
portal_auth_client_id="${PORTAL_AUTH_CLIENT_ID:-}"
portal_auth_client_secret="${PORTAL_AUTH_CLIENT_SECRET:-}"

if [[ "$deploy_portal" == "true" && -z "$portal_auth_client_id" ]]; then
  step "Preparing the console's Entra app registration"
  portal_app_name="${name_prefix}-portal-console"
  portal_auth_client_id="$(az ad app list --display-name "$portal_app_name" --query "[0].appId" -o tsv 2>/dev/null || true)"

  if [[ -z "$portal_auth_client_id" ]]; then
    portal_auth_client_id="$(az ad app create --display-name "$portal_app_name" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
    info "Created app registration $portal_app_name ($portal_auth_client_id)"
  else
    info "Reusing app registration $portal_app_name ($portal_auth_client_id)"
  fi

  # An app registration is only half of it. `az ad app create` creates the application object;
  # signing in additionally needs a service principal — the enterprise application — in this
  # tenant, and without one every sign-in fails after the credential prompt. It is also the
  # object the platform team gets assigned to, which the closing message tells you to do.
  if ! az ad sp show --id "$portal_auth_client_id" --only-show-errors >/dev/null 2>&1; then
    az ad sp create --id "$portal_auth_client_id" --only-show-errors >/dev/null
    info "Created the enterprise application for $portal_app_name"
  fi

  # Container Apps' built-in authentication signs in with response_type=code+id_token — the
  # hybrid flow — so the registration has to be willing to issue an ID token. Off by default on
  # a registration created from the CLI, and the resulting failure lands after sign-in, where it
  # reads as the console rejecting a perfectly good account.
  az ad app update --id "$portal_auth_client_id" --enable-id-token-issuance true --only-show-errors

  portal_auth_client_secret="$(az ad app credential reset --id "$portal_auth_client_id" --display-name "deploy-$(date +%Y%m%d%H%M%S)" --query password -o tsv)"
fi

# The client secret travels to `az` in a parameters file, not as a command-line argument:
# argv is world-readable through /proc for as long as the deployment runs, which is tens of
# minutes. The file lives in $patch_dir (mktemp -d, mode 700) and goes with it on exit, and the
# value reaches python through the environment rather than argv for the same reason. The
# parameter is @secure() in Bicep, so ARM does not keep it in the deployment history either.
secret_params=()
if [[ -n "$portal_auth_client_secret" ]]; then
  portal_secret_file="$patch_dir/portal-auth.parameters.json"
  PORTAL_AUTH_CLIENT_SECRET_VALUE="$portal_auth_client_secret" python3 - "$portal_secret_file" <<'PY'
import json, os, sys

with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({
        "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
        "contentVersion": "1.0.0.0",
        "parameters": {
            "portalAuthClientSecret": {"value": os.environ["PORTAL_AUTH_CLIENT_SECRET_VALUE"]},
        },
    }, handle)
PY
  secret_params=(--parameters "@$portal_secret_file")
fi

read_json() {
  local key="$1"
  python3 - "$identity_file" "$key" <<'PY'
import json,sys
doc=json.load(open(sys.argv[1],encoding="utf-8"))
print(doc[sys.argv[2]])
PY
}

# az takes a Bicep array parameter as JSON on the command line. An empty list means no network
# restriction, which is the demo default; a non-empty one restricts the console to those ranges.
portal_allowed_source_ips_json="$(python3 - "$portal_allowed_source_ips" <<'PYEOF'
import json, sys
print(json.dumps([r.strip() for r in sys.argv[1].split(",") if r.strip()]))
PYEOF
)"

step "Reading identity config from $identity_file"
expect_aud="$(read_json EXPECT_AUD)"
expect_iss="$(read_json EXPECT_ISS)"
jwks_url="$(read_json JWKS_URL)"
tenant_id="$(read_json tenantId)"

step "Deploying infrastructure (az deployment sub create: $deployment_name)"
info "This is the long one — provisions hub/spoke (and mgmt, in Mode 2), proxy VM, ACA + apps."
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
    bootstrapStorageAccountName="$bootstrap_storage_account" \
    bootstrapContainerName="$bootstrap_container" \
    bootstrapBlobName="$bootstrap_blob_name" \
    mgmtResourceGroupName="$mgmt_rg" \
    resourceGroupTags="$rg_tags_json" \
    proxyBinaryUrl="$proxy_binary_url_override" \
    proxyBinarySha256="$proxy_binary_sha256" \
    vmAdminPublicKey="$vm_admin_public_key" \
    deployControlPlane="$deploy_control_plane" \
    controlPlaneImage="$control_plane_image" \
    deployPortal="$deploy_portal" \
    portalImage="$portal_image" \
    portalAllowedSourceIps="$portal_allowed_source_ips_json" \
    portalAuthClientId="$portal_auth_client_id" \
  ${secret_params[@]+"${secret_params[@]}"}

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
allowlist_patched="$patch_dir/allowlist.json"
cp "$allowlist_file" "$allowlist_patched"
python3 - "$allowlist_patched" "$sample_client_id" <<'PY'
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
  --file "$allowlist_patched" \
  --auth-mode login \
  --overwrite \
  --only-show-errors >/dev/null

control_plane_url="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc.get("controlPlaneUrl",{}).get("value",""))
PY
)"

# Mode 2 only: seed the control plane's own state blob. The allowlist blob uploaded above is then
# a rendered projection the control plane overwrites on its first push, so seeding both keeps the
# proxy serving correct rules from the moment it starts.
if [ -n "$control_plane_url" ]; then
  rulesets_blob="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1])
print(doc["rulesetsBlobName"]["value"])
PY
)"
  rulesets_file="${RULESETS_FILE:-$repo_root/allowlist/rulesets.json}"

  rulesets_patched="$patch_dir/rulesets.json"
  cp "$rulesets_file" "$rulesets_patched"

  step "Patching rulesets with sample-app client id ($sample_client_id)"
  python3 - "$rulesets_patched" "$sample_client_id" <<'PY'
import json,sys
path,appid=sys.argv[1],sys.argv[2]
doc=json.load(open(path,encoding="utf-8"))
for ruleset in doc.get("rulesets",[]):
    if ruleset.get("name")=="sample-app":
        ruleset["subjects"]=[{"appid":appid}]
json.dump(doc,open(path,"w",encoding="utf-8"),indent=2)
open(path,"a",encoding="utf-8").write("\n")
PY

  step "Uploading control-plane state to $allowlist_account/$allowlist_container/$rulesets_blob"
  az storage blob upload \
    --account-name "$allowlist_account" \
    --container-name "$allowlist_container" \
    --name "$rulesets_blob" \
    --file "$rulesets_patched" \
    --auth-mode login \
    --overwrite \
    --only-show-errors >/dev/null
fi

portal_url=""
if [[ "$deploy_portal" == "true" ]]; then
  portal_url="$(python3 - "$deployment_output_json" <<'PYEOF'
import json, sys
doc = json.loads(sys.argv[1])
print(doc.get("portalUrl", {}).get("value", ""))
PYEOF
)"

  # The sign-in callback can only be registered now: it is derived from the container app's FQDN,
  # which the deployment just assigned. Without it Entra refuses the redirect and sign-in fails
  # with AADSTS50011 rather than with anything that points at the cause.
  if [[ -n "$portal_url" && -n "$portal_auth_client_id" ]]; then
    step "Registering the console's sign-in redirect URI"
    az ad app update \
      --id "$portal_auth_client_id" \
      --web-redirect-uris "${portal_url}/.auth/login/aad/callback" \
      --only-show-errors
  fi
fi

log "Deployment complete"
echo "Sample app URL: $app_url"
[ -n "$control_plane_url" ] && echo "Control plane URL: $control_plane_url"
if [ -n "$portal_url" ]; then
  echo "Management console: $portal_url"
  # Nothing in the deployment grants anyone access: the app registration exists, but who may sign
  # in to it is a decision for the platform team, not for a deploy script.
  echo "  Sign-in is restricted to your tenant. Assign the platform team to the"
  echo "  '${name_prefix}-portal-console' enterprise application to let them in."
fi
echo "Demo command: scripts/demo.sh \"$app_url\""
