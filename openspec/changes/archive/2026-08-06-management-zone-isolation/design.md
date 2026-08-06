# Design — three zones, no route between two of them

## The zones

Hub and spoke is the wrong axis for this deployment. What it actually has is three populations
with different trust postures, and the current topology puts two of them in one subnet:

```
  workload zone            data-plane zone           management zone
  ─────────────            ───────────────           ───────────────
  runs untrusted code      parses attacker-          writes the allowlist,
  the proxy exists         controlled CONNECTs       reads every audit row
  to constrain             for a living              and all ARM state

  spoke / sample app       hub / proxy VMSS          mgmt / control plane + console
```

The data plane is not trusted infrastructure — the VMSS nodes take hostile input by design.
So "move the control plane to the hub" would have replaced one bad adjacency with another. The
management zone is separated from **both**.

## Target topology

```
 HUB RG                              SPOKE RG                    MGMT RG
 hub-vnet 10.0.0.0/22                spoke-vnet 10.1.0.0/22      mgmt-vnet 10.2.0.0/22
 └─ snet-proxy ◀════ peering ══════▶ └─ snet-apps                └─ snet-mgmt
     VMSS + LB + prefix                  ACA env (external)          ACA env (external)
     proxy UAMI                          sample app                  ├─ control plane
                                         sample-app UAMI             └─ console
 bootstrap storage    ← moves           sample-app UAMI              control-plane UAMI
 config storage       ← stays                                        console UAMI
 ACR (Basic, public)  ← moves
 Log Analytics
                                                              no peering, to anything
```

**Why no peering is possible.** Everything the management zone reaches is a PaaS endpoint:
blob storage, Log Analytics, ARM, Entra, ACR. The control plane and the proxy share the config
blob, not a route. The console reaches policy through the control-plane API — it holds no role
on the storage account at all — and that call stays inside the new environment. So the
management zone needs no path to the hub or the spoke, and gets none.

That is stronger than an NSG deny rule: both existing NSGs carry `allow-vnet` any/any, so a
same-VNet placement would have depended on a deny rule sitting above it and never being
reordered. No route is not a rule that can be got wrong.

**A property worth naming:** the management zone has no route to the proxy, so the control
plane *cannot* depend on the data plane it configures. The invariant that
[`spoke.bicep:506-507`](../../../infra/modules/spoke.bicep) currently states in a comment and
enforces with a `NO_PROXY` entry becomes a fact about the network.

## The zone only exists in Mode 2

`deployControlPlane` and `deployPortal` both default to `false`, so **Mode 1 is the default
deployment** — proxy, allowlist blob, sample app, and no management plane at all. Today that
costs nothing extra: the two applications are conditional inside an environment the sample app
needs regardless. Giving them a zone of their own changes that, because a zone with no residents
is still a resource group, a virtual network, a network security group and a Container Apps
environment.

```
  deployControlPlane = false   → no management resource group, network or environment,
                                 and no management identities
  deployControlPlane = true    → the zone, with the control plane
    + deployPortal = true      → and the console
```

The condition is `deployControlPlane`, not either flag: the console requires the control plane —
it reads policy through the API and holds no role on the blob — so `deployPortal` alone is not a
valid deployment and does not need to bring a zone into existence on its own.

This propagates further than the zone module. The management identities are conditional, and so
are the grants that depend on them: the registry's `AcrPull` for the management pull identity,
and the console's `Reader` and `Monitoring Reader`. The last two are already guarded that way in
`hub.bicep` today, so the pattern to follow is there.

## Address space

`10.2.0.0/22` for the management VNet, `10.2.0.0/23` for `snet-mgmt`, continuing the hub/spoke
pattern. Nothing peers, so no CIDR conflict is possible — the range is chosen for legibility,
not necessity. The `/23` matches the spoke's apps subnet; a workload-profile environment needs
far less, but matching the sibling subnet is worth more than the addresses saved.

## Dependency ordering — the one non-obvious part

Two rules pull against each other. Identities follow the compute they serve, so they scatter
across all three resource groups. Role assignments belong at the scope of the resource being
granted, and after this change the hub owns almost every such resource — the config storage
account, the registry, and the platform the console reads. Naively that is circular in two
places: `hub` would need principal IDs from `spoke` and `mgmt`, while both need hub outputs.

Broken by making identity creation its own phase. User-assigned identities have no dependencies
of any kind, so they can all be created before anything grants on them, which lets a single rule
hold everywhere:

> **Identities are created in the resource group of their compute, in a first-phase module per
> group. Every role assignment lives in the module that owns the resource it grants on.**

```
  PHASE 1 — artifacts   bootstrap.bicep, subscription scope
    hub resource group
    bootstrap storage    allowSharedKeyAccess: false, deployer → Blob Data Contributor
    ACR                  Basic, public, admin user disabled
      ↓
    deploy.sh fills both: binary upload (--auth-mode login), three image builds and pushes

  PHASE 2 — deployment  main.bicep, subscription scope
    2a  spokeRg, mgmtRg                     (hubRg already exists)
    2b  hub-identity    → hubRg             proxy UAMI
        spoke-identity  → spokeRg           sample-app UAMI, spoke-acr-pull UAMI
        mgmt-identity   → mgmtRg            control-plane + console UAMIs,
                                            mgmt-acr-pull UAMI
    2c  hub.bicep       → hubRg             receives the principalIds it grants on
                                            config storage + Blob Data Reader/Contributor
                                            ACR (existing) + AcrPull ×2 (the pull identities)
                                            Reader + Monitoring Reader (console)
                                            VMSS, LB, prefix, Log Analytics
    2d  spoke.bicep     → spokeRg           vnet, NSG, ACA env, sample app
    2e  mgmt.bicep      → mgmtRg            vnet, NSG, ACA env, control plane + console
    2f  peering, private DNS                hub ↔ spoke only; mgmt is linked to neither
```

The three identity modules are one resource each, which looks like ceremony until you notice it
is what makes the rule statable without an exception. The alternative — a trailing
`acr-roles.bicep` scoped to the hub and deployed last, in the manner of `peering.bicep` — is
less churn but splits grants away from their resources, and would leave the ACR's three grants
somewhere other than where its other properties are declared. For a repo whose navigability is
the point, one rule with no exceptions is worth three small files.

**Every grant on a shared platform resource lands in `hub.bicep`.** That concentrates them,
which is the intent: the hub owns the shared resources, so it is the single place to read to
learn who can touch them.

**The console's `Reader` stays scoped to the hub resource group.** It renders the egress
platform, all of which is in hub. It gets no role on its own resource group, which is correct:
the console must not be able to read the deployment of the thing that authenticates it.

## The artifact phase

Both the bootstrap storage account and the registry are created imperatively today, and for the
same reason: **compute cannot boot until an artifact it fetches already exists, and neither a
blob upload nor an image push is an ARM resource that `main.bicep` could sequence.**

```
   binary  ──must exist before──▶  VMSS cloud-init
   images  ──must exist before──▶  container apps' first pull
```

The registry has a second reason to be early that the storage account does not: the images are
*imported into* it before any deployment references them, so it must exist before the images
exist, which must be before the apps do.

Recognising that as one concern is what makes moving the registry cheap. The phase is named for
what it holds rather than for one of its two occupants:

```
  deploy.sh
    ├─ az deployment sub create --template-file infra/bootstrap.bicep
    │     hub resource group
    │     bootstrap storage — allowSharedKeyAccess: false
    │     deployer → Storage Blob Data Contributor
    │     ACR — Basic, public, admin user disabled
    ├─ az storage blob upload --auth-mode login          ← retry: role propagation
    ├─ az acr import ×N  from GHCR — server-side pull, so it needs no subnet
    │                    (or a local docker build + push under BUILD_IMAGES_LOCALLY=true)
    └─ az deployment sub create --template-file infra/main.bicep
          both referenced as existing; compute boots and can fetch
```

**Import, not build.** The registry is filled by `az acr import` from the published GHCR images.
That is a server-side pull — the registry service fetches from GHCR — which is why it works at
all: GHCR is not on the Container Apps egress floor, which opens MCR and this registry only.
Nothing about that mechanism depends on the registry's resource group, so the GHCR path survives
the move unchanged. `az acr build` is **not** an option here; `deploy.sh` says so at its
`import_image` fallback, and the alternative is the local `BUILD_IMAGES_LOCALLY=true` branch.

**The registry stays optional.** Setting `SAMPLE_APP_IMAGE`, `CONTROL_PLANE_IMAGE` and
`PORTAL_IMAGE` to references that are already pullable skips the registry entirely today —
`container_registry_name` stays empty and the modules are conditional on it. Phase 1 must keep
that: it runs before image preparation, but `deploy.sh` knows by then whether it needs a
registry, so it passes the decision in rather than creating one unconditionally.

**Subscription scope, and it creates the hub resource group.** `main.bicep` creates the resource
groups today, so a hub-scoped phase-1 template would have had nothing to deploy into. Phase 1
takes ownership of the hub group; `main.bicep` references it as existing and keeps creating the
other two.

**The retry matters.** `main.bicep` already assigns the deployer `Storage Blob Data Contributor`
on the config account and `deploy.sh` already uploads to it with `--auth-mode login`, so the
pattern is proven in this repo — but there, minutes of deployment sit between the assignment and
the upload. Here the upload follows immediately, and Entra role propagation is not instant. A
bounded retry loop around the upload is part of the change, not an afterthought.

## One pull identity per environment

Image pull is a property of the hosting environment, not of the application. Today it is neither:
each app's functional identity carries `AcrPull` alongside the roles it actually needs.

```
  before                                    after
  ──────                                    ─────
  sample-app UAMI    → AcrPull              spoke-acr-pull UAMI → AcrPull
                       (+ nothing else)     sample-app UAMI     → (nothing)

  control-plane UAMI → AcrPull              mgmt-acr-pull UAMI  → AcrPull
                       Blob Data Contributor control-plane UAMI  → Blob Data Contributor
  console UAMI       → AcrPull              console UAMI        → Reader, Monitoring Reader
                       Reader, Mon. Reader
```

**The sample app is why this matters here and not only in general.** Its user-assigned identity
is the identity the proxy authenticates — the `appid` claim the allowlist keys on *is* this
principal. An identity that means "which workload is asking" should not also mean "may read the
registry." Separating the two keeps workload identity single-purpose, which is the same reason
the deployment keeps identity out of the network layer.

**Per environment, not one shared.** A single pull identity across both environments would be
one identity and one grant instead of two — but it would place the same credential in the
workload zone and the management zone, re-crossing the boundary this change exists to draw. The
environment is the trust boundary, so it is the right scope for the identity.

**Caveat for the implementation.** Container Apps configures image pull on the application, in
`properties.configuration.registries`, with no environment-level equivalent. So this is a
convention, not a platform setting: one identity created alongside the environment, referenced
by every app in it, and assigned to each app *in addition to* its functional identity — Container
Apps accepts several user-assigned identities per app. A reviewer looking for an environment
property will not find one, which is worth a comment in the module.

## Role assignments as shared modules

Most grants in this repo ride the AVM `roleAssignments` parameter, the way the config storage
account does. That works when the resource and the principal are created in the same deployment.
Two families here are not:

| Grant | Why the AVM parameter cannot carry it |
|---|---|
| `AcrPull` ×2 on the registry | the registry is created in phase 1, the pull identities in phase 2 |
| `Reader`, `Monitoring Reader` for the console | resource-group scope; there is no resource to pass them to |

Both are written today as hand-rolled `Microsoft.Authorization/roleAssignments` resources, and the
change would have added two more of the same. The AVM index was checked first, per the repo's
convention, and **it covers the harder of the two families**:

```
  br/public:avm/ptn/authorization/resource-role-assignment:0.1.2
      resourceId, principalId, roleDefinitionId, principalType
```

**"One generic module is impossible" turns out to be false, but only for AVM.** In Bicep a role
assignment's `scope` must be a typed `existing` resource declared in the same file, which is what
would have forced one local module per target type. The AVM module escapes that by dropping to raw
ARM: it emits a nested deployment whose inner template sets `scope` to the resource-id *string*,
which the Bicep language cannot express. So a single module does work for any resource type — and
the reason it works is worth knowing before anyone tries to write the local equivalent.

Its default assignment name is `guid(resourceId, principalId, roleDefinitionId)`, which is exactly
the convention this change wanted, so it arrives for free.

**The resource-group family needs no module at all.** `hub.bicep` is already resource-group scoped,
so a resource-group-scoped assignment declared inline in it needs no scope indirection — the case
for a module there was never the scope, only the naming, and the names are simply corrected in
place. The console's `Reader` and `Monitoring Reader` therefore stay as inline resources, renamed
from `guid(resourceGroup().id, 'portal', 'Reader')` — string literals standing in for the principal
and the role, so that changing the principal left the name unchanged and ARM updated in place
rather than replacing — to `guid(resourceGroup().id, principalId, roleDefinitionId)`.

Net: no files under `infra/modules/shared/`, one AVM reference for the two `AcrPull` grants, and
two renamed inline resources for the console's.

## Why the registry belongs in the hub

It serves all three zones. Left in the spoke it is a dependency pointing from the platform zone
into the workload zone — the control plane, which no workload may influence, would boot from an
artifact store that lives among the workloads. Nothing about the spoke's lifecycle should be
able to interrupt the management plane starting.

It also removes the last reason the spoke had to know about the management identities: with the
registry in the hub, both `AcrPull` grants collect next to the registry, and `spoke.bicep` stops
taking principal-ID parameters for two apps it no longer hosts.

The SKU and the public endpoint are unchanged — this is a placement fix, not a hardening one.
All three subnets keep the `AzureContainerRegistry` + `Storage.<region>` allow that Basic
requires, and the `Storage.<region>` softening stays on the record in
`docs/production-hardening.md` with Premium plus a private endpoint as its counterpart.

## Why the two storage accounts stay two

Container-scoped RBAC would let one account carry both the config and the binary with distinct
grants, so access rights are not the argument. What one account cannot separate is the
**network ACL**, which is per-account: a merged account would have to admit both the proxy
subnet and the management subnet, giving the control plane a network path to the binary that
only RBAC would stop.

That argument is currently theoretical, because this change keeps `defaultAction: Allow`. It is
recorded because it is the reason not to merge them *later*, when subnet rules do arrive — and
because merging is work with no benefit today.

## NSG deltas

| Rule | spoke | mgmt | note |
|---|---|---|---|
| `allow-azure-resource-manager` | **removed** | added | the point of the change |
| `allow-acr-storage` | kept | added | Basic ACR serves layers from Azure Storage; also covers the control plane's blob writes, so its description changes |
| `allow-aad` | kept | added | token acquisition, JWKS |
| `allow-azure-monitor` | kept | added | console queries, app telemetry |
| `allow-dns` | kept | added | |
| `allow-vnet` | kept | added | |
| `allow-proxy-egress` | kept | **not added** | the management zone has no proxy route by design |
| `deny-internet` floor | kept | added | |
| `allow-afd-backend-443/31443` | kept | added | ACA platform dependency for external ingress |
| `allow-mcr` | kept | added | ACA platform dependency; the table originally omitted it |
| `allow-afd-firstparty` | kept | added | ACA platform dependency; the table originally omitted it |
| `allow-azure-load-balancer-inbound` | kept | added | ACA platform health flows |

The last three were missing from the first draft of this table. They are not optional: the
management zone hosts a Container Apps environment exactly as the spoke does, and an environment
whose subnet cannot reach `MicrosoftContainerRegistry` does not come up at all. `allow-acr-storage`
is also **unconditional** here where the spoke guards it on there being a registry — the tag covers
the control plane's blob writes as well as ACR layer data, so the control plane needs it even with
every image pulled from a public reference.

## The inbound rules — tested, inert, and kept anyway

The documentation could not settle whether the three inbound rules on these subnets are consulted.
[Microsoft's NSG reference](https://learn.microsoft.com/en-us/azure/container-apps/firewall-integration)
says inbound NSG rules do not apply to an **external workload-profile** environment (which both of
these are), and on the same page lists an inbound `443, 31443` rule for workload profiles. So it
was tested instead.

**Method.** Against the sample app — chosen because it has no authentication in front of it, so a
`200` can only have been produced by the application process itself, inside the delegated subnet.
One replica confirmed running first, since a cold start would have muddied the result.

| | `allow-afd-backend-443` / `-31443` / `allow-azure-load-balancer-inbound` | `GET /healthz` |
|---|---|---|
| baseline | present | `200`, `200`, `200` |
| after deletion (+60s) | **all three removed** | `200`, `200`, `200` |
| restored | present | — |

**Result: the rules are not consulted.** Ingress reached the container with all three absent. The
external-environment carve-out is the operative statement; the inbound table describes *internal*
workload-profile environments.

**They are kept regardless, and this is the reasoning rather than inertia.** Three arguments, and
the third is decisive:

1. Removing them changes nothing today — they permit traffic that never arrives.
2. They are the documented rule set for an **internal** environment. Flipping `internal: true` on
   either environment — the production posture for the console, recorded in
   `docs/production-hardening.md` — makes inbound traverse the subnet, at which point their absence
   breaks ingress. Deleting them would plant a failure for whoever takes that step.
3. One experiment, on one platform, on one day. The result is clear but it is a statement about
   current Container Apps behaviour, not a guarantee. Removing a rule set on that basis buys
   tidiness and risks an outage; keeping it costs nothing.

What was wrong was never the rules — it was the **comment** asserting a mechanism nobody had
tested. That is corrected in both modules: they now say the rules are inert on an external
environment, why they are retained, and that the 31443 edge-proxy port is real but the
`AzureFrontDoor.Backend` source is this repo's inference rather than the reference's wording.

`allow-azure-load-balancer-inbound` additionally duplicates Azure's default
`AllowAzureLoadBalancerInBound` (priority 65001), since neither NSG carries an inbound deny. Also
kept, and for the same reason: being explicit in a security control is worth more than one saved
line, and it is the documented rule for the internal case.

## The registry is not VNet-integrated, and that is why the rules exist

Worth stating because it reads backwards. `AzureContainerRegistry` and `Storage.<region>` are
required on all three subnets **because** the registry is Basic with a public endpoint and no
private endpoint — confirmed live: `publicNetworkAccess: Enabled`, `privateEndpointConnections:
[]`, `networkRuleSet: null`. An image pull is therefore ordinary public-bound egress leaving the
subnet, which `deny-internet` would refuse; the service tags are sets of those services' *public*
prefixes, sitting above the floor.

The reference is explicit about the inverse, which is the production posture:

> You don't need to add an NSG rule for Container Registry when it's configured with private
> endpoints.

So the rules disappear under ACR Premium + private endpoint — at which point the traffic really
does stay on the VNet. Today it does not.

## What verification is available

Bicep has no unit-test story here, and nothing in this change is exercised by `dotnet test` or
`go test`. Two gates, neither of which replaces the other:

- **Compilation, in CI.** [`ci.yml`](../../../.github/workflows/ci.yml) already finds every
  `.bicep` under `infra/` and runs `az bicep build` on each, gated on the `infra/**` paths
  filter. New files are picked up by the `find` without touching the workflow.
- **Lint, which is currently silent.** `az bicep build` exits 0 on warnings and the repo has no
  `bicepconfig.json`, so the linter runs at defaults and fails nothing. Adding one with
  `no-unused-params` and `no-unused-vars` at `error` turns the existing job into a real gate —
  and this change is mostly deletion and rewiring, so unused leftovers are its most likely
  defect. That is a repo-wide gate arriving with this change, so the whole `infra/` tree must be
  clean under it before it can be enabled.

Everything else needs a real deployment. That is what group 8 is.

## Risks

- **Phase 1 owns the hub resource group, phase 2 owns the other two.** Split ownership of
  resource-group creation is a thing to get wrong on a second deployment or a partial teardown.
  `main.bicep` must reference the hub group rather than declare it, and `teardown.sh` must
  delete all three regardless of which phase made them.
- **`az acr build` needs the registry reachable from wherever `deploy.sh` runs.** True today and
  unchanged by the move, but the failure now happens in a new phase and should say so clearly.
- **Console → control plane over environment-internal DNS.** Both apps move to the new
  environment together, so the call by app name
  ([`spoke.bicep:714`](../../../infra/modules/spoke.bicep)) keeps working unchanged. It would
  break if only one of them moved — they must move as a pair.
- **Teardown.** `teardown.sh` deletes two resource groups by name. A third group that it does
  not know about is a resource leak that costs money silently.
- **First-deploy ordering.** If `bootstrap.bicep` and the upload are skipped or fail, the scale
  set comes up with no binary and the proxy never starts. The failure should be loud in
  `deploy.sh`, not discovered at the first `curl`.
