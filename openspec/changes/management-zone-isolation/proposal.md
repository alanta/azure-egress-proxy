## Why

The control plane is the sole writer of the allowlist. The console is the surface that reads
every audit row and the deployment's whole ARM state. Both currently run in the **spoke** —
in the same subnet, behind the same NSG, and inside the same Container Apps environment as the
sample workload, which is the untrusted code the proxy exists to constrain.

That was a stated shortcut, not a drift. [`spoke.bicep`](../../../infra/modules/spoke.bicep)
says so twice, in the comments above each app: *"shares the spoke's managed environment for
demo economy, but it is platform infrastructure."* Their identities, their role assignments and
the storage they own already live in the hub. Only the compute landed in the wrong place.

It has already cost something concrete. The console reads scale-set state, prefix consumption
and Monitor metrics from ARM, so the apps subnet carries an `AzureResourceManager` egress
allow — and an NSG sees a subnet, not a container app, so the sample workload inherits it. The
rule's own comment names the fix: *"The production counterpart is a separate subnet for the
admin surface."*

A second, smaller thing surfaced while mapping this: the bootstrap storage account holding the
proxy binary is created imperatively in the **spoke** resource group and is written with
`--auth-mode key` ([`deploy.sh:158`](../../../scripts/deploy.sh)). A shared key is a bearer
credential that works from anywhere on the internet, and the blob it opens is the binary that
runs as the security control. The allowlist account next to it already has
`allowSharedKeyAccess: false`.

Checked against the running deployment, it is worse than that. Because the account is created by
`az storage account create` with no hardening flags, it has taken the CLI defaults on both
settings the declared account sets explicitly:

| | config account (Bicep) | bootstrap account (CLI) |
|---|---|---|
| `allowSharedKeyAccess` | `false` | *unset* — shared keys work |
| `minimumTlsVersion` | `TLS1_2` | **`TLS1_0`** |

A storage account serving the proxy binary over TLS 1.0, reachable with a bearer key from
anywhere, is the weakest thing in the deployment. Declaring it in Bicep is what fixes both, and
it is the reason this is worth doing in the same change rather than later.

## What Changes

- **Add a third zone.** A new `mgmt` resource group and virtual network hosting the control
  plane and the console in their own Container Apps environment. It peers with nothing: its
  dependencies — blob storage, Log Analytics, ARM, Entra, ACR — are all PaaS endpoints, so no
  route to the hub or the spoke is required and none is created. The zone is conditional on
  `deployControlPlane`, so Mode 1 — which is the default — deploys none of it.
- **Move both platform user-assigned identities to the `mgmt` group,** alongside the compute
  they belong to. Their role assignments stay scoped where the resources are: hub resource
  group for the console's `Reader` and `Monitoring Reader`, the config storage account for the
  control plane's `Storage Blob Data Contributor`.
- **Narrow the spoke's egress floor.** `allow-azure-resource-manager` moves to the management
  NSG. The sample workload stops inheriting it.
- **Move both storage accounts to the hub resource group.** They are data-plane dependencies:
  Mode 1 has no control plane at all and still needs the config blob, and the proxy reads it on
  every reload. The bootstrap account is in the spoke only because `deploy.sh` creates it there.
- **Move the container registry to the hub resource group.** It serves all three zones and is
  platform infrastructure; it is in the spoke only because `deploy.sh` creates it there.
- **Give each Container Apps environment one pull identity.** Image pull stops being a grant on
  each application's functional identity and becomes a dedicated identity per environment. Three
  `AcrPull` grants become two, and the sample app's identity — the one whose `appid` claim the
  allowlist keys on — stops carrying a registry grant that has nothing to do with what the proxy
  authenticates it as.
- **Declare the bootstrap account in Bicep with `allowSharedKeyAccess: false`,** and upload the
  proxy binary with `--auth-mode login`.
- **Split an artifact phase ahead of the main deployment.** The proxy binary must exist in the
  blob before the scale set first boots, and the three images must exist in the registry before
  the container apps first pull — and neither a blob upload nor an image push is an ARM resource
  that `main.bicep` could order. A subscription-scoped `bootstrap.bicep` creates the hub resource
  group, the bootstrap account and the registry; `deploy.sh` then fills both; `main.bicep`
  consumes them.

## What This Deliberately Does Not Change

Recorded here so they read as decisions rather than oversights, and mirrored into
[`docs/production-hardening.md`](../../../docs/production-hardening.md):

- **Both management apps keep public ingress.** Mode 2 has to be demonstrable — a workload
  team's pipeline calls the control-plane API, and an operator opens the console from a laptop.
  Internal-only ingress is the production counterpart and needs connectivity (ExpressRoute, VPN,
  Entra Private Access, or a self-hosted runner in the management VNet) that a reference
  implementation cannot assume. The console keeps its platform authentication and optional
  source-IP restriction; the control plane keeps its RS256/JWKS check.
- **Storage keeps `defaultAction: Allow`.** Subnet rules are the cheap real hardening here and
  they are free, but they break all three upload paths — the binary, the allowlist seed, and
  `demo.sh`'s swaps — which run from a laptop or a GitHub-hosted runner in no subnet at all.
  Making them work needs a temporary IP-rule dance around every deployment. Recorded as the
  production step-up, not taken here.
- **ACR stays Basic and public.** It moves to the hub resource group, but the SKU and the public
  endpoint do not change: every zone pulls over the public endpoint, which is why all three
  subnets carry the same `AzureContainerRegistry` + `Storage.<region>` allow the spoke has
  today. Premium with a private endpoint is the production counterpart and would delete the
  `Storage.<region>` rule — recorded, not taken.
- **No behavioural change to the proxy, the control-plane API, or the console.** This change
  moves resources and narrows one NSG. Nothing about policy evaluation, the rendered
  `allowlist.json` contract, the audit stream, or any HTTP surface moves with it.

## Impact

- **Affected code**: `infra/main.bicep`, `infra/modules/{hub,spoke}.bicep`, a new
  `infra/bootstrap.bicep`, a new `infra/modules/mgmt.bicep`, three new one-resource identity
  modules `infra/modules/{hub,spoke,mgmt}-identity.bicep`, `scripts/deploy.sh`,
  `scripts/teardown.sh`, `scripts/demo.sh`
- **Affected specs**: adds `deployment-topology`
- **Affected docs**: [`docs/architecture.md`](../../../docs/architecture.md),
  [`infra/README.md`](../../../infra/README.md),
  [`docs/production-hardening.md`](../../../docs/production-hardening.md),
  [`docs/control-plane.md`](../../../docs/control-plane.md)
- **Breaking for existing deployments**: yes — resources move between resource groups. The
  agreed path is a full `teardown.sh` and a clean deploy; no migration is built or supported.
