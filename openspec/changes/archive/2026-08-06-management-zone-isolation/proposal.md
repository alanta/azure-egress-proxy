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

Checked against the running deployment (`egressbin54c45055`, created 2026-07-05):

| | config account (Bicep) | bootstrap account (CLI) |
|---|---|---|
| `allowSharedKeyAccess` | `false` | **`null`** — unset, so shared keys work |
| `minimumTlsVersion` | `TLS1_2` | `TLS1_2` — the CLI default, same as the declared account |

An earlier draft of this proposal claimed the bootstrap account was also on **TLS 1.0**. That is
wrong: `az storage account show` reports `TLS1_2`, because the CLI's default for new accounts is
TLS 1.2. Corrected here rather than left standing — the account is not as bad as first written, and
a proposal that overstates its own finding is worse than one that understates it.

What remains is the real finding, and it is enough on its own: **a storage account whose blob is
the binary that runs as the security control accepts a bearer key that works from anywhere on the
internet.** The account next to it disabled that explicitly; this one never did, because nothing
declared it. Declaring it in Bicep is what fixes it, and it is why this belongs in the same change
rather than later.

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

- **Close the audit trail's write path, and give the zones somewhere to log.** Three findings that
  surfaced while verifying the zone, all in its remit:
  - The console held **`Monitoring Reader`**, whose bare `*/read` matches
    `Microsoft.OperationalInsights/workspaces/sharedKeys/read`. That key authenticates the legacy
    Data Collector API, which *appends rows to custom tables* — so a "read-only" console could forge
    entries into `EgressProxy_CL`. Swapped for **`Log Analytics Reader`**, which excludes exactly
    that operation in its `notActions` and is otherwise a superset. Paired with
    **`disableLocalAuth: true`** on the workspace, so the key cannot ingest even if read.
  - Neither Container Apps environment had **`appLogsConfiguration`** at all, so console and
    platform logs were never persisted and the Logs blade had nothing to query. Both now use
    `destination: 'azure-monitor'` plus a diagnostic setting — *not* `'log-analytics'`, which
    authenticates with the very shared key just disabled.
  - The two management services logged the Azure SDK at `Information`, where `Azure.Identity` and
    `Azure.Core` between them dominated the stream. Set to `Warning`, which keeps failures.

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
