# Architecture

**Three zones**, not two. Hub-and-spoke is the axis the deployment started on, and it is the
wrong one: what it actually has is three populations with different trust postures.

```
  workload zone            data-plane zone           management zone
  ─────────────            ───────────────           ───────────────
  runs untrusted code      parses attacker-          writes the allowlist,
  the proxy exists         controlled CONNECTs       reads every audit row
  to constrain             for a living              and all ARM state

  spoke / sample app       hub / proxy VMSS          mgmt / control plane + console
```

The proxy lives in the hub; workloads live in spokes and reach it over VNet peering. The only
sanctioned path to arbitrary third-party HTTPS is the proxy; an NSG on the workload subnet denies
direct Internet egress, so bypassing the proxy fails closed.

The management plane — the [control plane](control-plane.md) (Mode 2) and the
[console](../src/Portal/README.md) (Mode 3) — is separated from **both** of the others, in its own
resource group, virtual network and Container Apps environment. "Move it to the hub" would have
replaced one bad adjacency with another: the VMSS nodes take hostile input by design, so the hub is
not trusted infrastructure either.

```mermaid
flowchart LR
    subgraph spoke [Spoke VNet — workload zone]
        APP[Sample app<br/>Azure Container Apps<br/>HTTPS_PROXY + MI token]
        NSG[NSG egress floor<br/>deny Internet<br/>allow proxy :4750]
    end
    subgraph hub [Hub VNet — data-plane zone]
        LB[Internal LB<br/>proxy.egress.internal:4750]
        VMSS[VMSS: egress-proxy<br/>Public IP Prefix egress]
        ST[(Allowlist blob<br/>egress-config/allowlist.json)]
        BIN[(Bootstrap blob<br/>proxy binary)]
        ACR[(Container registry)]
        LAW[(Log Analytics<br/>EgressProxy_CL)]
    end
    subgraph mgmt [Mgmt VNet — management zone]
        CP[Control plane<br/>sole writer of the allowlist]
        PORTAL[Console<br/>read-only]
    end
    USER((Client)) -->|HTTPS| APP
    APP -->|CONNECT + Basic MI-JWT| LB --> VMSS
    VMSS -->|allowed FQDNs only| NET((Internet))
    VMSS -->|managed identity, ETag poll| ST
    VMSS -->|boot fetch| BIN
    VMSS -->|AMA/DCR| LAW
    APP -.blocked by NSG.-> NET
    CP -->|blob write, public endpoint| ST
    PORTAL -->|internal DNS, same environment| CP
    PORTAL -->|ARM + Log Analytics| LAW
    APP -.no route.- mgmt
    VMSS -.no route.- mgmt
```

**The management network peers with nothing, and that is the design rather than an omission.**
Everything the management plane reaches is a PaaS endpoint — blob storage, Log Analytics, ARM,
Entra, the registry — so no route to the hub or the spoke is required, and none is created. The
console reaches policy through the control-plane API, and both are applications of the same
Container Apps environment, so that call never leaves it.

That is stronger than a deny rule. Both other NSGs carry an `allow-vnet` any/any, so a same-VNet
placement would have depended on a deny rule sitting above it and never being reordered. No route
is not a rule that can be got wrong — and it makes one property true by construction: **the control
plane cannot depend on the data plane it configures.**

**One consequence worth naming.** The console reads scale-set state, prefix consumption and Monitor
metrics from ARM, so its subnet carries an `AzureResourceManager` egress allow. An NSG sees a
subnet, not a container app, so while the console lived in the spoke the sample workload inherited
that allowance. Moving it moved the rule with it; the workload egress floor is now one destination
narrower.

**The zone exists only in Mode 2.** `deployControlPlane` defaults to `false`, so the default
deployment — proxy, allowlist blob, sample app — creates no management resource group, network,
environment or identity. The condition is `deployControlPlane` rather than either flag, because the
console reads policy through the API and holds no role on the blob: it is not a valid deployment on
its own.

**Two deployment phases.** Compute cannot boot until the artifacts it fetches at start-up exist,
and neither a blob upload nor an image push is an ARM resource a template could sequence. So
`infra/bootstrap.bicep` creates the hub resource group, the bootstrap storage account and the
registry; `scripts/deploy.sh` fills both; `infra/main.bicep` consumes them. See
[infra/README.md](../infra/README.md).

## Design decisions

| Decision | Why |
|---|---|
| **Explicit CONNECT proxy, no transparent fallback** | The proxy resolves the *named* destination itself; a compromised client can't SNI-spoof to an attacker IP under an allowed label. Transparent SNI-peeking is defeatable and is rejected as a security boundary. |
| **Enforcement = the NSG, not proxy opt-in** | `HTTPS_PROXY` is honour-system; the deny-Internet NSG makes the proxy the only route out. A workload that ignores the proxy gets no route (fail closed), not a silent leak. |
| **Identity = workload's managed-identity JWT** (not source IP/subnet) | Services can't be guaranteed to a subnet, and shared Container Apps environments collapse subnet granularity. The token is unforgeable and per-app. See [identity.md](identity.md). |
| **VMSS + Public IP Prefix, no NAT Gateway** | Third parties allowlist *your* egress IPs: instances draw public IPs from a fixed prefix — a known egress CIDR. Each instance gets its own 64k SNAT ports; scale out, not up. Standing in for a NAT Gateway also means inheriting its idle-drop behaviour — see [production-hardening.md § Idle timeouts](production-hardening.md#idle-timeouts--the-stale-tunnel-contract). |
| **Internal Standard LB + stable DNS name** | Spokes target `proxy.egress.internal:4750`; instance IPs can change freely. |
| **Allowlist = one JSON blob, ETag reload** | Atomic writes, cheap conditional GETs, versioning/soft-delete as audit trail. See [allowlist.md](allowlist.md). Written directly (GitOps) or, for per-team self-service, through a validating [control plane](control-plane.md) (Mode 2). |
| **Single static Go binary, reload folded in** | No sidecars, no container runtime on the VM, `systemd` restart is the reload. Distroless/nonroot when containerized (local dev). |
| **Anti-SSRF** | Smokescreen resolves the destination and blocks private/link-local ranges — the proxy cannot be used to reach internal IPs. This makes `NO_PROXY` load-bearing for the client (internal traffic must bypass the proxy). |

## What it doesn't do

The tunnel is end-to-end TLS between the workload and the third party. The proxy sees the
`CONNECT` line and the bytes' volume and timing — never the plaintext. It governs **where**
traffic goes, not **what** goes there.

Concretely, out of scope: DLP, malware/AV scanning, request-path or method granularity
(`api.example.com/v1/read` cannot be allowed while `/v1/admin` is denied), request or
response body policy, and anything that depends on the HTTP layer inside the tunnel. The
allowlist's unit is `host[:port]` and that is the finest grain available — see
[allowlist.md](allowlist.md).

The consequence is that **an allowed FQDN is a full-trust bidirectional
channel.** A compromised workload with `github.com` on its list can exfiltrate through
`github.com`, and the audit row will read as a normal allow. The audit trail narrows this but
does not close it — `CANONICAL-PROXY-CN-CLOSE` carries `BytesIn`/`BytesOut`/`DurationMs` per
connection, so unusual volume to an allowed host is detectable after the fact
([observability.md](observability.md)). Policy hygiene is the real mitigation: prefer
vendor-specific hostnames over wildcards and shared CDN domains, since a wildcard on a domain
anyone can host under is an allowlist in name only.

Full inspection requires TLS interception (MITM with a trusted CA) and it is deliberately not
implemented here. This is a narrow focus solution aimed at gating network egress and preventing
malicious introspection. Where content-level control is genuinely required, it belongs at the 
application or at a dedicated CASB/DLP tier, that is a different solution that could be layered 
on top of this proxy.

## Traffic classes

| Traffic | Path |
|---|---|
| Third-party HTTPS (the governed class) | app → proxy (CONNECT) → allowlist decision → internet |
| Azure PaaS via private endpoints, intra-VNet, IMDS, Azure Monitor | direct (on `NO_PROXY`, allowed by NSG) |
| Everything else to Internet | denied by NSG (and by the proxy's default deny) |

## What a deny looks like

The proxy denies the `CONNECT` with **HTTP 407**. Its JWT-auth model is 407-based
(`Proxy-Authenticate: Basic` challenge), so both an unauthenticated and a policy-denied
tunnel surface as `407 Proxy Authentication Required`. Note: `curl` reports this as
`000`/exit 56 (it expects a tunnel, not a response) — that's a client artifact, not a
proxy failure. The decision (and the would-be destination) is in `EgressProxy_CL`.
