# Health checks — readiness and liveness

The proxy runs a small HTTP health listener next to the proxy port. It answers two questions, and
keeps them apart:

- **`/readyz`: should this instance get traffic?** Yes once it has everything it needs to make a real
  decision on a request: valid configuration, signing keys, and an allowlist from the blob.
- **`/livez`: is the process working?** Yes while the process is running and not stuck. It never
  depends on the blob or the identity provider.

Every platform that runs the proxy reads these endpoints: the scale set's Application Health
extension, the load balancer (indirectly, see [The proxy port follows readiness](#the-proxy-port-follows-readiness)),
and Kubernetes or Container Apps probes.

## Endpoints

| Path | `200` when | `503` when |
|---|---|---|
| `/readyz` | configuration valid, **and** signing keys loaded, **and** an allowlist fetched from the blob at least once since start, **and** not shutting down | any of those is missing, or shutdown has started |
| `/livez` | the health listener answers and the reload loop is not stuck | the reload loop has stopped making progress |

Both answer `GET` (and `HEAD`) only. Every other path is a `404`.

A `200` from `/readyz` carries the body the Application Health extension's rich health states
expect:

```json
{ "ApplicationHealthState": "Healthy", "status": "ok", "reason": "ok" }
```

A `503` carries `"status": "not-ready"` and a reason too, and no `ApplicationHealthState`. The
status code alone is the verdict: Kubernetes ignores the body, and the extension treats any non-2xx
as *Unknown* whatever the body says. The extension ignores the extra fields (verified).

| `status` | HTTP | `reason` |
|---|---|---|
| `ok` | `200` | `ok` |
| `degraded` | `200` | `rejected`: the latest pushed allowlist was invalid, and last-known-good is in force |
| `not-ready` | `503` | `config`, `keys`, `allowlist` or `shutdown` |

*Degraded* is still ready, on purpose. Every instance runs the same code, so they all reject the
same document and all lose the same blob. Taking them out of rotation would turn a bad push or a
storage outage into a total outage. Neither the extension nor Kubernetes has a degraded state, so
degraded exists only in the body. The fleet-wide signal for a rejected push is an audit event
instead (see [Logging and the audit trail](#logging-and-the-audit-trail)).

The body never contains URLs, tenant or app IDs, ETags or allowlist contents. On Kubernetes the
endpoint can be reached from inside the cluster, and it must not become a way to read the proxy's
configuration.

The proxy never returns `Unhealthy`. That state tells Azure to repair the instance, and no
condition is known yet that repairing one instance would fix (see [Why it works this way](#why-it-works-this-way)).

`/livez` answers `{"status":"ok"}`, or `503` with `{"status":"stalled"}`. *Stuck* means the
allowlist reload loop has not finished a poll for three poll intervals plus the 15 s poll timeout,
and never less than a minute (with the default `POLL_SECONDS=10`: one minute). A poll that fails
still finishes, so an unreachable blob is not stuck. Before the loop starts (while the keys load)
and during shutdown, `/livez` is `200`.

## Lifecycle

| Phase | `/readyz` | `/livez` | Proxy port 4750 |
|---|---|---|---|
| Starting: keys and allowlist being fetched | `503` (`keys`, then `allowlist`) | `200` | closed |
| Configuration invalid | the process exits with the missing or malformed settings named in the log ([identity.md § Startup check](identity.md#startup-check)). No listener at all | — | closed |
| Ready | `200` (`ok`) | `200` | open |
| Valid new allowlist document applied | `200` (`ok`) | `200` | briefly closed while Smokescreen restarts |
| Blob unreachable after ready: serving **last-known-good** | `200` (`ok`); staleness is not tracked yet (see [Not yet](#not-yet)) | `200` | open |
| Invalid allowlist document pushed after ready: serving **last-known-good**, no restart | `200` (`degraded`, `rejected`) until a valid document is applied | `200` | open |
| JWKS refresh failing after keys loaded: cached keys still used | `200` (`ok`); the failed refresh is logged | `200` | open |
| Shutting down (SIGTERM): draining tunnels | `503` (`shutdown`) | `200` | stops accepting at once; open tunnels drain |

The configuration check runs before the health listener starts, so a process with an invalid
configuration exits without ever answering. The `config` reason exists for the moment in between,
and is not normally seen.

Last-known-good stays *ready* on purpose. A blob or identity-provider outage hits every instance at
once. Taking them all out of rotation, or having the platform replace them, turns a policy freeze
into a total outage. A replacement would also start without last-known-good and stay not ready. The
body says *degraded*, and the log says since when and why. The probe verdict stays ready.

On SIGTERM `/readyz` turns `503` straight away, before anything else happens. Smokescreen then stops
accepting new connections and waits for open tunnels to close, and the process exits once they have.
A SIGTERM before the proxy port is open (keys loading, no allowlist yet) exits at once: there is
nothing to drain.

### Standalone mode

Without `ALLOWLIST_BLOB_*` the proxy runs against a static ACL file (`--egress-acl-file`). There is
no blob, so readiness is configuration and keys only: the ACL file is loaded with the configuration,
and a file that does not load stops the process. There is no reload loop, so `/livez` is always
`200` while the process answers, and there is no `degraded` state.

## The proxy port follows readiness

Port 4750 opens only once `/readyz` would return `200`. Before that there is no listener. That is
still fail-closed (no listener, no egress), and it means anything that probes TCP 4750 gets the same
answer as `/readyz`. Today that is the scale set's internal load balancer and its Application Health
extension. A new instance that can't reach the blob therefore gets no traffic, instead of denying
requests that other instances would have served.

The cost: during an outage at boot, a client that reaches a not-ready instance directly sees
"connection refused" instead of a denial row in `EgressProxy_CL`. Through the load balancer, clients
don't reach it at all. See [allowlist.md § Fail closed](allowlist.md).

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `HEALTH_ADDR` | binary: `127.0.0.1:4751`; container image: `:4751` | Where the health listener binds. The bare binary (the VM) binds loopback, so nothing outside the host can reach it. The container image sets `HEALTH_ADDR=:4751`, so a kubelet, Container Apps or Docker health probe can reach the pod or container IP without extra configuration. An address that cannot be bound stops the proxy at startup |

The listener is always on, and it never shares a port with the proxy. The image declares
`EXPOSE 4750 4751` (proxy and health).

## Platforms

### Virtual machine scale set (demo IaC and Marketplace image)

The demo IaC ([infra/README.md](../infra/README.md)) still probes **TCP 4750** from both the
Application Health extension and the load balancer. Since the port follows readiness, that already
reads the readiness verdict, without the degraded detail. Moving the extension to `/readyz` and
turning automatic repairs off is a follow-up, verified by a deployment. The target configuration:

The Application Health extension probes `/readyz` from inside the instance. It connects to
`localhost`, so the loopback default works. This was verified on Azure Linux 3 ARM64: `localhost`
resolves to `::1` first, and the probe falls back to `127.0.0.1`.

```bicep
extensionHealthConfig: {
  enabled: true
  protocol: 'http'
  port: 4751
  requestPath: '/readyz'
  intervalInSeconds: 5
  numberOfProbes: 2
  gracePeriod: 120
  autoUpgradeMinorVersion: true
}
automaticRepairsPolicyEnabled: false
```

How the extension (v2.0, rich health states) reads the endpoint, verified on a throwaway scale set
(2026-09-26):

| Endpoint response | Instance health |
|---|---|
| no stable answer yet, within `gracePeriod` after the extension starts, **including after every reboot** | *Initializing* |
| `200` + `"ApplicationHealthState": "Healthy"` (extra fields ignored) | *Healthy* |
| `503`, port closed, empty body, or timeout | *Unknown*, which the platform treats like *Unhealthy* |

What that drives:

- **Automatic OS image upgrades** roll one batch at a time. They wait up to 5 minutes for each
  upgraded instance to report *Healthy*, and roll back its OS disk otherwise. An image that can't
  load its configuration, keys or allowlist is therefore rolled back instead of rolled out. The
  `gracePeriod` stays well inside those 5 minutes.
- **Automatic instance repairs are off**, explicitly. The AVM scale-set module turns them on by
  default. With them on, a blob outage would make Azure replace healthy instances one at a time,
  each replacement starting without last-known-good.
- **The load balancer** keeps its own TCP probe on 4750, which follows readiness (above). It is a
  rule-level probe, not the scale set's `networkProfile.healthProbe`, so it doesn't conflict with
  Azure's rule that a scale set has only one health source.
- **No NSG or host-firewall rule** is needed. Loopback traffic is always accepted.

Reading health without SSH:

```bash
az vmss get-instance-view -g <rg> -n <vmss> --instance-id <id> --query vmHealth.status.code
az vmss run-command invoke -g <rg> -n <vmss> --instance-id <id> --command-id RunShellScript \
  --scripts 'curl -s -w " %{http_code}\n" http://127.0.0.1:4751/readyz'
```

### Kubernetes and Azure Container Apps

The container image already binds the health listener on `:4751`, so there is nothing to set.
Map the probes one to one:

```yaml
startupProbe:
  httpGet: { path: /readyz, port: 4751 }
  periodSeconds: 5
  failureThreshold: 24     # up to 2 minutes for configuration, keys and the first allowlist
readinessProbe:
  httpGet: { path: /readyz, port: 4751 }
  periodSeconds: 5
livenessProbe:
  httpGet: { path: /livez, port: 4751 }
  periodSeconds: 10
```

- **Liveness uses `/livez`, never `/readyz` and never TCP 4750.** Port 4750 stays closed until the
  pod is ready. A liveness probe on readiness would turn a blob outage at pod start into a restart
  loop.
- A rolling deployment waits for readiness the way an OS upgrade waits for *Healthy*.
- On SIGTERM `/readyz` goes to `503`, so the pod leaves the Service's endpoints while its tunnels
  drain.
- The health port is reachable from inside the cluster. The body gives nothing away (above). The
  chart can still add a NetworkPolicy that admits only the kubelet.

### Local (Aspire)

The AppHost publishes the proxy container's health port on `localhost:14751` and registers
`/readyz` as its HTTP health check. It also sets `HEALTH_ADDR=:4751`, which is the image default
anyway, to make the dependency explicit. Dependent resources (the sample app) wait for a proxy
that has actually loaded its allowlist.

## Logging and the audit trail

- Health requests never reach Smokescreen. They produce no `CANONICAL-*` rows and add nothing to
  `EgressProxy_CL`.
- Individual probe requests are not logged: one every 5 s per instance is noise.
- **Readiness transitions are logged**, once per change, with the reason in fields:

  ```text
  level=info msg="readiness changed: not-ready (allowlist)" ready=false reason=allowlist status=not-ready
  {"level":"info","msg":"readiness changed: degraded (rejected)","ready":true,"reason":"rejected","status":"degraded",...}
  ```

  A stuck reload loop is logged at `error` when `/livez` first sees it, and again when it recovers.
  The lines come from process `egress-proxy`, so the DCR's diagnostic transform ships them to the
  `Syslog` table ([observability.md](observability.md#health-transitions)).
- **A rejected push is an audit event**, `CANONICAL-PROXY-CONFIG-REJECTED`
  ([observability.md § A rejected allowlist push](observability.md#a-rejected-allowlist-push)).
  That is the fleet-wide signal. The degraded body only helps someone looking at one instance.
  - The DCR routes on the `CANONICAL-PROXY` prefix, so it lands in `EgressProxy_CL` with no
    ingestion or schema change.
  - It uses existing columns: `DecisionReason` names the rejected ETag and the one kept in force
    (or `staying FAIL-CLOSED (proxy port closed)` before the first allowlist), and `Error`
    carries the parse error.
  - One row per instance per rejected document. Grouping by the ETag (extracted from
    `DecisionReason`) answers "was the pushed config rejected?". There is deliberately no
    per-instance tracking of which version runs: every instance runs the same code and makes the
    same call.

## Not yet

- **Staleness.** An unreachable blob while last-known-good is in force isn't reported. There is no
  `stale` reason, and the watcher doesn't log the outage. Adding it means a last-successful-check
  time on the watcher, a degraded `stale` reason, and log lines when the outage starts and ends.
  Deferred as a future enhancement.

## Why it works this way

- **Not Smokescreen's built-in `/healthcheck`.** Smokescreen can serve a health handler, but only on
  the proxy port, and it matches the path alone. A plain-HTTP proxy request such as
  `GET http://any-host/healthcheck` would be answered by the health handler, skipping role, ACL and
  the decision log. It also goes down on every allowlist reload. A separate listener has neither
  problem.
- **Readiness and liveness are separate** because Kubernetes treats them differently. Liveness
  restarts, readiness routes. The scale set only reads readiness.
- **Last-known-good is ready**, and **repairs are off**, because the dependencies that fail (the blob,
  the identity provider) are shared by every instance. Replacing an instance doesn't fix a shared
  outage, and it throws away the one thing that keeps traffic flowing: last-known-good.
- **Loopback on the VM** because the only consumer is the extension inside the instance. The address
  is a setting because the kubelet probes the pod IP.
