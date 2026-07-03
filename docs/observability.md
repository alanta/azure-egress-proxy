# Observability — the egress audit trail

The proxy emits **one structured JSON line per event** to stdout → journald → syslog. On
Azure, the **Azure Monitor Agent** ships it via a **Data Collection Rule** whose
ingestion-time transform splits the stream on the `CANONICAL-PROXY` marker:

- `CANONICAL-PROXY-*` lines → typed rows in the custom table **`EgressProxy_CL`**
  (the audit trail);
- everything else → a narrowed diagnostic breadcrumb in the standard `Syslog` table
  (proxy lifecycle, systemd unit messages) — what you read when the proxy *isn't*
  working and the audit table goes silent. Routine info noise is dropped at ingestion.

Full logs always remain on the instances (journald); the DCR governs what is shipped,
not what is recorded.

## `EgressProxy_CL` rows

`EventType` discriminates two events; columns that don't apply land null.

**`CANONICAL-PROXY-DECISION`** — the allow/deny record:

| Column | Meaning |
|---|---|
| `Allow`, `DecisionReason` | the verdict and why |
| `Host` | requested destination `host:port` |
| `Role` | **the workload identity** — in `basic-jwt` mode, the caller's managed-identity client ID from the validated JWT |
| `EnforceWouldDeny` | `true` on off-list hosts in `report` mode — the onboarding signal |
| `SrcIp`, `ReqId`, `DnsLookupMs` | source, per-request correlation id, resolution time |

**`CANONICAL-PROXY-CN-CLOSE`** — the connection summary: `BytesIn`, `BytesOut`,
`DurationMs`, `ConnEstablishMs`, `Host`, `Role`, `Error`, same `ReqId` as its decision.

### Expect `"Client role cannot be determined"` rows — they are the 407 handshake

Clients don't send proxy credentials preemptively: every **new tunnel connection** first
issues a bare CONNECT, which the proxy logs as a decision with
`DecisionReason == "Client role cannot be determined"` and answers with `407`. The client
then repeats the CONNECT with credentials, producing the row that carries the real `Role`
and verdict. So one no-role row per authenticated connection is protocol-inherent noise —
filter it in dashboards (see queries below) but don't drop it at the DCR: a *stream* of
credential-less CONNECTs that never converts to an authenticated row is exactly what
probing looks like.

Two amplifiers to be aware of: HTTP-client resilience handlers retry denied requests
(each retry is a fresh tunnel), and any sidecar/SDK that honours `HTTPS_PROXY` without
knowing the proxy credentials (e.g. a telemetry exporter missing from `NO_PROXY`) will
generate a persistent stream of no-role denials.

### `SrcIp` is not a workload identity

On VNet-integrated Container Apps, egress is carried by the environment's infrastructure
nodes — a single replica's connections arrive from **multiple, rotating subnet IPs**
(observed live: one replica, two interleaved node IPs). This is why the allowlist keys on
the JWT `appid` (`Role`), never on the source address.

## Useful queries

```kql
// Recent decisions (the 407-handshake rows filtered out)
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-DECISION"
| where DecisionReason != "Client role cannot be determined"
| project TimeGenerated, ReqId, Role, SrcIp, Host, Allow, DecisionReason
| order by TimeGenerated desc

// Possible probing: credential-less CONNECT volume by destination
EgressProxy_CL
| where DecisionReason == "Client role cannot be determined"
| summarize attempts=count() by Host, bin(TimeGenerated, 15m)

// Denies per workload (who is trying to go where they shouldn't)
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-DECISION" and Allow == false
| summarize count() by Role, Host

// report-mode findings: what a new module actually needs allowed
EgressProxy_CL
| where Role == "<appid>" and EnforceWouldDeny
| summarize count() by Host

// Correlate decision with bytes/duration via ReqId
EgressProxy_CL
| summarize Allow=anyif(Allow, EventType == "CANONICAL-PROXY-DECISION"),
            Role=any(Role), Host=any(Host),
            BytesOut=anyif(BytesOut, EventType == "CANONICAL-PROXY-CN-CLOSE"),
            DurationMs=anyif(DurationMs, EventType == "CANONICAL-PROXY-CN-CLOSE")
            by ReqId
```
