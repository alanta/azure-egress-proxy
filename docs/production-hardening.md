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
prompt bidirectional RST: the next write fails immediately with `ECONNRESET` instead of
black-holing. Failing fast is the goal — an idle tunnel is *expected* to die, and clients must
treat that as retryable. TCP reset is not configurable on the instance public IP, so the
outbound leg is covered by keepalives rather than RST.

**The rule every client must follow:** close idle pooled tunnels *before* the platform does.

- The shipped .NET client pins `PooledConnectionIdleTimeout` to **1 min** and rejects any
  value at or above 4 min at startup
  ([EgressProxyOptions](../src/EgressProxy.Client/EgressProxyOptions.cs)) — previously this
  ordering held only by accident, via the `SocketsHttpHandler` default.
- **Non-.NET clients get no such guarantee** and must be configured explicitly: Go
  `http.Transport.IdleConnTimeout`, Python `urllib3` pool recycling, curl's connection reuse,
  Java `keepAliveDuration`. Anything above the LB idle timeout will hand out dead tunnels.

**Streaming and long-poll workloads need keepalives.** `PooledConnectionIdleTimeout` does not
apply to a connection with a request in flight, so an SSE stream, long-poll, gRPC stream, or
slow query-over-HTTPS that goes quiet for 4 minutes is reaped mid-request — no idle-pool
setting protects it, in any language. The .NET client therefore enables **TCP keepalives**
(30 s idle, 3 probes 5 s apart, `EgressProxyOptions.TcpKeepAliveTime`): keepalive traffic
resets the idle timers, and a tunnel that is already gone surfaces within ~15 s. Configure the
equivalent on other stacks (`SO_KEEPALIVE` + `TCP_KEEPIDLE`), or raise
`proxyIdleTimeoutInMinutes` toward 30, or both. Application-level pings (HTTP/2 `PING`,
websocket ping) work equally well — the timers only care that bytes move.
