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
| Sample image in a **Basic ACR** with NSG allows for `AzureContainerRegistry` **and** `Storage.<region>` (below Premium, ACR serves layer data from shared Azure Storage — the Storage allow makes any in-region storage account reachable, softening the egress floor) | **ACR Premium + private endpoint**: pulls stay on the VNet and both NSG allows disappear |
| Single region, small VMSS | ≥2 instances across availability zones (already the default here), CPU/connection autoscale, prefix sized for SNAT (64k ports per instance IP) |
| `encryptionAtHost` defaults **off** (deploys on any subscription without feature registration) | Register `Microsoft.Compute/EncryptionAtHost` and deploy with `encryptionAtHost=true` |

Unchanged from production intent: explicit CONNECT only (no transparent fallback), the
NSG deny-Internet floor with `defaultOutboundAccess: false`, fail-closed allowlist
handling, managed-identity-only data plane, structured audit logging.

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
