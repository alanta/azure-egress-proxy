# Observability — the egress audit trail

The proxy emits **one structured JSON line per event** to stdout → journald → syslog. On
Azure, the **Azure Monitor Agent** ships it via a **Data Collection Rule** whose
ingestion-time transform splits the stream on the `CANONICAL-PROXY` marker:

- `CANONICAL-PROXY-*` lines → typed rows in the custom table **`EgressProxy_CL`**
  (the audit trail). The marker is a prefix match, so an added event type flows through
  without an ingestion or schema change — `EventType` is simply the line's `msg`;
- everything else → a narrowed diagnostic breadcrumb in the standard `Syslog` table
  (proxy lifecycle, systemd unit messages) — what you read when the proxy *isn't*
  working and the audit table goes silent. Routine info noise is dropped at ingestion.

Full logs always remain on the instances (journald); the DCR governs what is shipped,
not what is recorded.

## `EgressProxy_CL` rows

`EventType` discriminates three events; columns that don't apply land null.

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

**`CANONICAL-PROXY-AUTH-REQUIRED`** — the 407 pre-auth challenge: one per new tunnel, in
the `basic-*` identity modes. Carries `Host`, `SrcIp`, `ReqId`, `ProxyType`; `Role` is empty
by definition, `Allow` is `false` without meaning a policy denial, and `DecisionReason` reads
`"No proxy credentials presented; answered with a 407 Basic challenge"`. See below.

### The 407 handshake is its own event type

Clients don't send proxy credentials preemptively: every **new tunnel connection** first
issues a bare CONNECT, which the proxy answers with `407 Proxy-Authenticate: Basic`. The
client then repeats the CONNECT with credentials, producing the row that carries the real
`Role` and verdict. So there is one credential-less CONNECT per authenticated connection —
protocol-inherent noise that used to double the decision stream.

The proxy logs it as **`CANONICAL-PROXY-AUTH-REQUIRED`** instead of a decision, so
`EventType == "CANONICAL-PROXY-DECISION"` now means *a verdict was reached*. The split is
made on whether the client presented a `Proxy-Authorization` header at all — nothing else —
which leaves a sharper signal behind it:

> A **`CANONICAL-PROXY-DECISION`** row with an empty `Role` means credentials **were**
> presented and **rejected**. That is an authentication failure worth alerting on, not
> handshake noise.

### `DecisionReason` says what happened

Smokescreen flattens every identity failure to `"Client role cannot be determined"`, which
tells you nothing you couldn't already see from the empty `Role` column, and buries the
actual cause in a separate syslog line. The proxy replaces it with the role func's own error:

| `DecisionReason` | What it means |
|---|---|
| `No proxy credentials presented; answered with a 407 Basic challenge` | The handshake. Always on an `AUTH-REQUIRED` row |
| `Client identity rejected: invalid token: <detail>` | JWT validation failed — signature, `iss`, `aud`, or expiry (`basic-jwt`, `jwt`) |
| `Client identity rejected: token has no appid/azp claim` | Token was valid but carried no workload identity |
| `Client identity rejected: empty token in Basic Proxy-Authorization` | Credentials presented but blank — usually a client that failed to acquire a token |
| `Client identity rejected: source <ip> is not in any configured module subnet` | `netid` mode, unmapped source |

Policy reasons are Smokescreen's own and unchanged (`rule has enforce policy`,
`host matched allowed domain in rule`). Alert on `EventType` and `Role`, not on reason text.

Don't drop the challenge rows at the DCR either: a *stream* of credential-less CONNECTs that
never converts to an authenticated row is exactly what probing looks like. They are
reclassified, not discarded.

Two amplifiers to be aware of: HTTP-client resilience handlers retry denied requests
(each retry is a fresh tunnel), and any sidecar/SDK that honours `HTTPS_PROXY` without
knowing the proxy credentials (e.g. a telemetry exporter missing from `NO_PROXY`) will
generate a persistent stream of challenge rows that never converts.

Two operational notes:

- **Across a rollout**, instances still running an older binary keep folding these into
  `CANONICAL-PROXY-DECISION` with the old `"Client role cannot be determined"` text. A query
  spanning the upgrade should tolerate both shapes — keying on `Role` rather than on reason
  text works across the boundary.
- **`netid` and `jwt` modes have no handshake** — the reclassification is not applied there,
  because a request that arrives without a usable identity is a genuine denial.

### The matching diagnostic line

Smokescreen also logs a non-canonical `"Unable to get role for request"` error per failed
role lookup, which lands in the `Syslog` diagnostic stream. For the credential-less case it
carries nothing the `AUTH-REQUIRED` row doesn't, so the proxy suppresses **that case only**;
a rejected token still produces it, with the validation error intact. Set
`LOG_PREAUTH_DETAIL=1` on the proxy to keep every one of them (debugging the handshake).

### `SrcIp` is not a workload identity

On VNet-integrated Container Apps, egress is carried by the environment's infrastructure
nodes — a single replica's connections arrive from **multiple, rotating subnet IPs**
(observed live: one replica, two interleaved node IPs). This is why the allowlist keys on
the JWT `appid` (`Role`), never on the source address.

## Useful queries

```kql
// Recent decisions — verdicts only; the handshake is no longer in this stream
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-DECISION"
| project TimeGenerated, ReqId, Role, SrcIp, Host, Allow, DecisionReason
| order by TimeGenerated desc

// Authentication failures: credentials WERE presented and rejected. DecisionReason now
// carries the cause (expired token, wrong audience, unmapped subnet), so read it directly.
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-DECISION" and isempty(Role)
| summarize attempts=count(), reasons=make_set(DecisionReason, 5)
            by SrcIp, Host, bin(TimeGenerated, 15m)

// Challenge volume by destination — the baseline for the probing check below
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-AUTH-REQUIRED"
| summarize attempts=count() by SrcIp, Host, bin(TimeGenerated, 15m)

// Possible probing: sources that got challenged and never came back authenticated
let win = 1h;
let challenged = EgressProxy_CL
    | where TimeGenerated > ago(win) and EventType == "CANONICAL-PROXY-AUTH-REQUIRED"
    | summarize challenges=count() by SrcIp;
let authenticated = EgressProxy_CL
    | where TimeGenerated > ago(win) and EventType == "CANONICAL-PROXY-DECISION" and isnotempty(Role)
    | summarize authed=count() by SrcIp;
challenged
| join kind=leftouter authenticated on SrcIp
| extend authed = coalesce(authed, 0)
| where authed == 0
| project SrcIp, challenges
| order by challenges desc

// Handshake overhead: tunnels opened vs requests actually carried
EgressProxy_CL
| summarize challenges=countif(EventType == "CANONICAL-PROXY-AUTH-REQUIRED"),
            decisions=countif(EventType == "CANONICAL-PROXY-DECISION" and isnotempty(Role))
            by bin(TimeGenerated, 1h)

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
| where EventType in ("CANONICAL-PROXY-DECISION", "CANONICAL-PROXY-CN-CLOSE")
| summarize Allow=anyif(Allow, EventType == "CANONICAL-PROXY-DECISION"),
            Role=any(Role), Host=any(Host),
            BytesOut=anyif(BytesOut, EventType == "CANONICAL-PROXY-CN-CLOSE"),
            DurationMs=anyif(DurationMs, EventType == "CANONICAL-PROXY-CN-CLOSE")
            by ReqId
```
