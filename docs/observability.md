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

`EventType` discriminates four events; columns that don't apply land null.

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

**`CANONICAL-PROXY-CONFIG-REJECTED`** — a pushed allowlist document that did not parse and
was not applied (managed mode). Not a request event: `Role`, `Host`, `ReqId` and `Allow` are
empty. See [A rejected allowlist push](#a-rejected-allowlist-push).

| Column | Meaning |
|---|---|
| `DecisionReason` | the rejected ETag and what stays in force: `allowlist etag="0x8DE…" rejected, keeping last-known-good etag="0x8DD…" until the blob changes`, or `… staying FAIL-CLOSED (proxy port closed) …` when no valid document has loaded yet (no listener; see [health.md](health.md)) |
| `Error` | the decode error, e.g. `invalid allowlist document: bad JSON: unexpected end of JSON input` |
| `Computer` | the instance that rejected it |

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

Those retries are deliberately quiet on the *client* side. Polly logs a handled retry at
Warning and the exhausted attempt at Error, each carrying the full exception, so a single
transient failure printed several stack traces into a console. `ServiceDefaults` downgrades
the `ExecutionAttempt` and `OnRetry` events to Debug and exports the `Polly` meter instead —
`resilience.polly.strategy.events` counts retries, `resilience.polly.pipeline.duration` shows
what they cost. Every other resilience event keeps its own severity, so a circuit opening is
still logged. If you are chasing a retry storm, query the metric; the proxy-side row count in
`EgressProxy_CL` is the other half of the same picture.

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

### A rejected allowlist push

When a pushed `allowlist.json` does not decode, the proxy keeps serving last-known-good and
does not download that version again until the blob changes
([allowlist.md § Fail closed](allowlist.md)). Without an audit row, the only trace of that
would be a free-text line in `Syslog`, and nobody watching `EgressProxy_CL` would see that
the push never took effect.

So each instance writes **one** `CANONICAL-PROXY-CONFIG-REJECTED` row per rejected ETag, not
one per poll: a document that stays rejected adds nothing more. A later valid push clears
it, and a later bad push produces a new row. Every instance runs the same code, so they all
reject the same document: group by the ETag in `DecisionReason` to answer "was this push
rejected?" rather than tracking which version each instance runs.

On the instance itself, `/readyz` stays `200` but reports `degraded` (`rejected`) until a
valid document is applied ([health.md](health.md)); the row is the fleet-wide view of the same
state.

The row uses only columns the transform already maps (`DecisionReason`, `Error`), so it
needed no DCR or schema change. Both values come from Storage and the JSON decoder, never
from a proxy client.

Two things it does not cover:

- **A failed download** is transient and retried every poll, so it is not a rejection and
  has no row. It stays a warning in `Syslog`.
- **An unreachable blob** is not a rejection either. While last-known-good is held it is
  not logged at all; before the first valid document it is a warning in `Syslog`.

### `SrcIp` is not a workload identity

On VNet-integrated Container Apps, egress is carried by the environment's infrastructure
nodes — a single replica's connections arrive from **multiple, rotating subnet IPs**
(observed live: one replica, two interleaved node IPs). This is why the allowlist keys on
the JWT `appid` (`Role`), never on the source address.

## Health transitions

Health probes (`/readyz`, `/livez`, see [health.md](health.md)) never reach Smokescreen, so they
add nothing to `EgressProxy_CL`, and individual probes are not logged. What is logged is each
**change** of the readiness verdict, once, with the reason as fields:

```text
level=info msg="readiness changed: not-ready (keys)" ready=false reason=keys status=not-ready
level=info msg="readiness changed: not-ready (allowlist)" ready=false reason=allowlist status=not-ready
level=info msg="readiness changed: ok (ok)" ready=true reason=ok status=ok
{"level":"info","msg":"readiness changed: degraded (rejected)","ready":true,"reason":"rejected","status":"degraded",...}
{"level":"info","msg":"readiness changed: not-ready (shutdown)","ready":false,"reason":"shutdown","status":"not-ready",...}
```

In managed mode the lines before the first allowlist are logrus text and the ones after it JSON,
because Smokescreen switches the formatter when it first starts. A stuck reload loop logs `liveness: the allowlist reload loop has not
finished a poll ...` at `error` when `/livez` first sees it. All of these come from process
`egress-proxy`, so the diagnostic transform ships them to `Syslog`:

```kql
// Readiness history per instance
Syslog
| where ProcessName == "egress-proxy" and SyslogMessage has "readiness changed"
| project TimeGenerated, Computer, SyslogMessage
| order by TimeGenerated desc
```

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

// Rejected allowlist pushes: which versions were refused, why, and by how many instances
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-CONFIG-REJECTED"
| extend RejectedETag = extract(@"etag=(\S+) rejected", 1, DecisionReason)
| summarize FirstSeen=min(TimeGenerated), Instances=dcount(Computer),
            Reason=any(DecisionReason), Error=any(Error) by RejectedETag
| order by FirstSeen desc

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
