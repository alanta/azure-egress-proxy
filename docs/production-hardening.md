# Production hardening — deltas from this demo

This repo optimises for **reproducibility by a reader with one subscription and an
afternoon**. Running the pattern for real, change these — each is a deliberate,
documented simplification here:

| Demo choice | Production posture |
|---|---|
| VMSS installs the proxy via **cloud-init + GitHub Release binary** (checksum-pinned) | Bake a **versioned golden image** (Compute Gallery) and roll it with VMSS rolling upgrades — immutable infrastructure, nothing fetched at boot |
| Allowlist storage: **public endpoint, Entra-only RBAC** (`allowSharedKeyAccess: false`) | `publicNetworkAccess: Disabled` + **private endpoint** (`privatelink.blob.core.windows.net`); allowlist writes then need network reach (VNet-integrated runner/agent or deployment script) |
| Sample app **openly exposed on its ACA external ingress** (no ingress gate — this demo is about *egress*, so ingress is intentionally left simple) | Put a WAF in front (**Front Door Premium + Private Link origin** to an internal-only Container Apps environment — no public origin at all), or restrict ingress and reach the app over private connectivity |
| One shared allowlist document, centrally written | Per-module blobs with path-scoped RBAC (write isolation), or a validating control-plane API (see [ROADMAP](../ROADMAP.md)) |
| Platform images in a **Basic, public ACR**, with NSG allows for `AzureContainerRegistry` **and** `Storage.<region>` on all three subnets (below Premium, ACR serves layer data from shared Azure Storage — the Storage allow makes any in-region storage account reachable, softening the egress floor). Its *placement* is no longer a trade-off: it is in the platform resource group, where a resource serving all three zones belongs | **ACR Premium + private endpoint**: pulls stay on the VNet and both NSG allows disappear. On the management subnet the `Storage.<region>` allow would still be needed — the control plane writes the allowlist blobs over the same tag |
| Management console on **external ACA ingress**, with an *optional* source-IP allow list (`PORTAL_ALLOWED_SOURCE_IPS`, empty by default so the demo is runnable from a laptop) | Internal-only ingress reached over private connectivity, or Front Door Premium + Private Link as for the sample app — plus Conditional Access on its app registration. It is an admin surface for a security control, not a sample workload |
| Console sign-in uses an app-registration **client secret**, minted by `deploy.sh` on every run (`az ad app credential reset`) and passed to Bicep as a deployment parameter, where it lands in a container-app secret | A certificate or federated credential instead, held in Key Vault and referenced by the container app, so no secret value ever travels through a deployment parameter or a shell |
| **Control-plane API on external ACA ingress**, with its RS256/JWKS check as the only gate | Internal-only ingress reached over private connectivity. Mode 2 has to be demonstrable — a workload team's pipeline calls this API — and a reference implementation cannot assume ExpressRoute, a VPN, Entra Private Access, or a self-hosted runner in the management VNet |
| Both storage accounts keep **`networkAcls.defaultAction: Allow`** (public endpoint, Entra-only RBAC) | Subnet rules: the proxy subnet for reads, the management subnet for the control plane's writes. They are cheap and free, and they break all three upload paths — the binary, the allowlist seed, and `demo.sh`'s swaps — which run from a laptop or a GitHub-hosted runner in no subnet at all. Making them work needs a temporary IP-rule dance around every deployment |
| The two storage accounts (**config** and **bootstrap**) stay separate even though container-scoped RBAC could merge them | Keep them separate. Access rights are not the argument — the **network ACL is per account**, so a merged account would have to admit both the proxy subnet and the management subnet, giving the control plane a network path to the proxy binary that only RBAC would stop. Theoretical while `defaultAction` is `Allow`; it is the reason not to merge them once subnet rules arrive |
| Single region, small VMSS | ≥2 instances across availability zones (already the default here), CPU/connection autoscale, prefix sized for SNAT (64k ports per instance IP) |
| `encryptionAtHost` defaults **off** (deploys on any subscription without feature registration) | Register `Microsoft.Compute/EncryptionAtHost` and deploy with `encryptionAtHost=true` |

Unchanged from production intent: explicit CONNECT only (no transparent fallback), the
NSG deny-Internet floor with `defaultOutboundAccess: false`, fail-closed allowlist
handling, managed-identity-only data plane, structured audit logging.

## The management console concentrates read power

The console ([`src/Portal/`](../src/Portal/), Mode 3's read half) writes nothing. Its identity
holds `Reader` + `Log Analytics Reader` on the hub resource group and **no write role anywhere** — in
particular no `Storage Blob Data Contributor` on the allowlist container — and the only non-`GET`
it makes against the control-plane API is `:check`, the dry run. That is a real boundary and it is
enforced in the deployment, not just in the code.

Read-only is not the same as low-value, though, and this is the part worth designing around: the
console is the first identity in the deployment that can see **all authored policy, every proxy
decision, and the deployment's runtime state at once**. Before it, that reach was three sets of
permissions held by different principals; a person who wanted the whole picture had to hold all
three. Joining them is precisely what makes the console useful, and precisely what makes it the
single most informative component to compromise. Every workload's egress profile — who talks to
which partner, and when they started — is one page.

So treat it as an admin surface, not as a dashboard:

- **Azure SDK log levels.** The management apps set `Logging:LogLevel:Azure` to `Warning`. At
  `Information` the SDK dominates the console stream — `Azure.Identity` emits MSAL cache and token
  metrics, and `Azure.Core[5]` logs every HTTP response with its full header set. `Warning` keeps
  credential and request *failures*, which are the half worth having on an admin surface.
- **`Log Analytics Reader`, not `Monitoring Reader`.** Both look read-only and both are built on
  `*/read` — but `*/read` matches `Microsoft.OperationalInsights/workspaces/sharedKeys/read`, and
  the workspace shared key authenticates the legacy Data Collector API, which *appends rows to
  custom tables*. A principal that can read that key can forge entries into `EgressProxy_CL`.
  `Log Analytics Reader` excludes the key read in its `notActions`; `Monitoring Reader` does not.
  The workspace additionally sets `disableLocalAuth: true`, so the key cannot ingest even if read.
- **Do not widen its Azure roles.** `Reader` + `Log Analytics Reader`, scoped to the hub resource
  group, is the whole grant. A write role on it would also break the invariant in
  [AGENTS.md](../AGENTS.md) § Invariants, not just the least-privilege story.
- **Gate reaching it**, per the ingress row above. The platform's built-in authentication runs
  in front of the container, so an unauthenticated request never reaches the app; that is
  authentication, not network exposure control, and the two are not substitutes.
- **Everyone who can sign in sees everything.** There is one audience tier and no per-user
  scoping — that is a deliberate deferral, because narrower rules need the per-ruleset RBAC model
  the control plane has not designed yet. Membership of the console's app registration is
  therefore the access control, and should be reviewed like one.
- **Its audit trail is Entra's and Azure's**, not the control plane's. Policy changes are audited
  by the API because they go through the API; *reads* through the console are not audited anywhere
  in this repo. If who-looked-at-what matters to you, that is a diagnostic-settings and sign-in-log
  question to answer at deployment time.

## Idle timeouts — the stale-tunnel contract

Replacing a NAT Gateway means inheriting its best-known failure mode: an idle flow is reaped,
both endpoints stay `ESTABLISHED`, and the application only discovers the loss on its next
write — which buffers, retransmits, and errors out tens of seconds to minutes later. The
initial write *appears* to succeed. This is not a proxy behaviour you can code around; it is
a property of the two Azure idle timers a CONNECT tunnel crosses.

A tunnel is two stitched TCP legs, each with its own timer, both set from
`proxyIdleTimeoutInMinutes` in [hub.bicep](../infra/modules/hub.bicep) (**4 min**, the Azure
default; raisable to 30):

| Leg | Timer |
|---|---|
| client → internal LB → proxy | the `proxy-tcp-rule` load-balancing rule |
| proxy → SNAT → destination | the VMSS instance public IP |

The LB rule sets **`enableTcpReset: true`**, so a reap on the client-facing leg arrives as a
prompt bidirectional RST rather than a silent drop. TCP reset is not configurable on the
instance public IP, so the outbound leg relies on keepalives instead.

### What actually happens (measured)

`enableTcpReset` is **defence in depth, not the load-bearing protection**. Probing the
reference deployment from a VNet-integrated Container Apps replica (swedencentral,
2026-07-28) showed the Azure reaper is never the thing that ends an idle connection, and
**no black hole was reproducible** — before or after enabling reset:

| Probed | Result |
|---|---|
| Idle client → LB → proxy flow, no tunnel | Clean **FIN at 300.7 s**, identical with reset off and on. Reuse failed in 0.0 s. |
| Idle CONNECT tunnel to `api.github.com` | Clean **FIN at 31 s** — the destination closed its own keep-alive first. |

Two mechanisms get there before the 4-minute timer:

- **Go's default TCP keepalives** on both proxy legs (`net/http` on accepted client
  connections, `net.Dialer` on outbound ones) put bytes on the wire well inside 4 minutes, so
  the Azure idle timers keep getting reset and never fire.
- **Smokescreen closes an idle client connection at 300 s** — its `DefaultReadTimeout`, which
  `http.Server` also uses as `IdleTimeout` — with a graceful FIN. That is *after* the LB's
  240 s, and the FIN still arrived, which is what proves the LB flow was alive the whole time.

So a dead tunnel surfaces as an ordinary close (`EOF` / "connection reset by peer"), promptly,
and clients must treat it as retryable. The reset setting matters only if a flow ever does
reach the LB timer — keepalives disabled somewhere in the path, a client that suppresses them,
or a raised proxy `ReadTimeout`. That is exactly when you want it already on.

**The rule every client must follow:** close idle pooled tunnels *before* the platform does.

- The shipped .NET client pins `PooledConnectionIdleTimeout` to **1 min** and rejects any
  value at or above 4 min at startup
  ([EgressProxyOptions](../src/EgressProxy.Client/EgressProxyOptions.cs)) — previously this
  ordering held only by accident, via the `SocketsHttpHandler` default.
- **Non-.NET clients get no such guarantee** and must be configured explicitly: Go
  `http.Transport.IdleConnTimeout`, Python `urllib3` pool recycling, curl's connection reuse,
  Java `keepAliveDuration`. Anything above the proxy's 300 s idle close will hand out
  connections the proxy has already closed — cheap to retry, but only if the client retries.

**Streaming and long-poll workloads rely on the keepalives above.**
`PooledConnectionIdleTimeout` does not apply to a connection with a request in flight, so an
SSE stream, long-poll, gRPC stream, or slow query-over-HTTPS that goes quiet is protected by
nothing on the client side — it depends on the proxy's keepalives continuing to cover the
path. The shipped .NET client deliberately does *not* add a second layer of client-side
keepalives; that would duplicate protection the proxy already provides. If you ever run a
proxy build with keepalives disabled, add them at the client (`SO_KEEPALIVE` + `TCP_KEEPIDLE`,
or .NET's `SocketsHttpHandler.ConnectCallback`), raise `proxyIdleTimeoutInMinutes` toward 30,
or both. Application-level pings (HTTP/2 `PING`, websocket ping) work equally well — the
timers only care that bytes move.
