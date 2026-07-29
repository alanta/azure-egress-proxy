# The allowlist contract

> **Two ways to write it.** This document describes the **allowlist document itself** — the
> contract between the blob and the proxy, which is the same in both topologies. *Who* writes
> it differs: in the **GitOps** topology (Mode 1) teams author this file directly and CI
> publishes it; with the **control plane** (Mode 2) teams author *rulesets* through an API and
> the control plane renders this file. See [control-plane.md](control-plane.md) for the
> comparison, and § Write path below.

The allowlist is a **single JSON document** in a blob:
`egress-config/allowlist.json` in a locked-down storage account. The proxy reads it with
its **own managed identity** (no secret) and reloads when the blob's **ETag** changes.
One atomic write = one consistent state, so there is no sentinel/marker object.

## Schema

```jsonc
{
  "modules": [
    {
      "id": "sample-app",                                   // unique slug (ACL name in netid/basic-name modes)
      "appid": "00000000-0000-0000-0000-000000000000",       // the workload's managed-identity CLIENT ID
                                                             // (the ACL key in jwt/basic-jwt modes)
      "subnet": "10.1.0.0/23",                               // only used by the netid identity mode
      "allowed_hosts": ["api.github.com"],                   // exact FQDNs the workload may CONNECT to
      "action": "enforce"                                    // enforce (default) | report | open
    }
  ],
  "fallback": {                                              // optional; ABSENT or EMPTY => deny-all
    "allowed_hosts": []
  }
}
```

## Semantics (all secure-by-default)

- **`action`** — `enforce` denies off-list hosts. `report` lets traffic through but logs
  off-list hosts with `enforce_would_deny: true` — the onboarding on-ramp: start a new
  module in `report`, tune `allowed_hosts` from the logs, flip to `enforce`. Omitted,
  empty, or unrecognised values normalise to **`enforce`**; `report`/`open` are never
  implicit.
- **`fallback`** — the rule for requests whose identity matches **no module** (or that
  present no/invalid token). It widens the default block from pure deny-all to a curated,
  platform-owned baseline. The default block is always `enforce`. Keep it minimal and
  watch its usage in the logs as a migration backlog.
- **Fail closed** — if the blob is unreachable at first start, the proxy renders a
  deny-all ACL and keeps retrying. Once it has config, it holds **last-known-good**
  through transient blob outages.
- **Decommission** — delete the module's entry; that identity falls to the fallback/deny
  block on the next reload. Removal is fail-closed by construction.

## Propagation

The proxy polls the ETag (default every 10 s, `POLL_SECONDS`); on change it downloads,
renders the Smokescreen ACL, and restarts in-process (~4 s). End-to-end propagation is
dominated by the poll interval — measured at ~5 s with a 5 s poll.

## Write path

### GitOps topology (Mode 1) — no control plane

The blob is written data-plane (`az storage blob upload --overwrite --auth-mode login`);
writers hold **Storage Blob Data Contributor**. In this repo the allowlist workflow publishes
[`allowlist/allowlist.json`](../allowlist/allowlist.json) on merge — the config-as-code loop,
with PR review as the trust boundary. Blob **versioning + soft delete** give history/rollback.

### Control-plane topology (Mode 2) — per-team self-service

The blob becomes a **rendered projection** that only the control plane writes. Teams no longer
author this document at all; they push a **ruleset** — their workload's `subjects` plus its
`allowed_hosts` and `action` — to a validating API, which enforces what a shared file cannot:

- **Writer ≠ subject.** The identity allowed to write a ruleset is never the workload identity
  it governs, so a compromised workload cannot widen its own allowlist.
- **Subjects are write-once at onboard**, and a subject belongs to **at most one** ruleset
  (one-to-one, first-come) — so a team cannot claim another workload's identity, and effective
  policy always comes from exactly one place.
- **`report` by default at onboard.** A new ruleset created without an explicit action starts
  in `report` — the same on-ramp described above, made the default rather than something each
  team has to remember. An explicit action is honoured, and the control plane never lowers a
  ruleset's action on its own: adding a host to an already-enforcing ruleset does not
  downgrade it.
- **Audited full-replace.** A push is desired-state, so every added and removed host is logged
  with the pushing identity, and `POST /rulesets/{name}:check` returns the diff before a push.

Ruleset schema: [`allowlist/rulesets.schema.json`](../allowlist/rulesets.schema.json). Rendering
emits one `modules[]` entry **per subject**, so this document's schema — and the proxy — do not
change. Setup, RBAC, and `curl` examples: [control-plane.md](control-plane.md).

## Proxy configuration (env)

| Variable | Meaning |
|---|---|
| `ALLOWLIST_BLOB_URL` | Full https URL of the blob; read via `DefaultAzureCredential` (set `AZURE_CLIENT_ID` to pick a user-assigned identity) |
| `ALLOWLIST_BLOB_CONNECTION_STRING` | Local/dev alternative (Azurite); with `ALLOWLIST_CONTAINER` (default `egress-config`) and `ALLOWLIST_BLOB` (default `allowlist.json`) |
| `POLL_SECONDS` | ETag poll interval (default 10) |
| `OUTPUT_FILE` | Rendered ACL path (default `/render/acl.yaml`) |
| `SMOKESCREEN_ID_MODE` | Identity mode: `basic-jwt` (recommended), `basic-name`, `jwt`, `netid` — see [identity.md](identity.md) |
| `LOG_PREAUTH_DETAIL` | `1` keeps the per-handshake `Unable to get role for request` diagnostic line, suppressed by default — see [observability.md](observability.md) |

Setting either `ALLOWLIST_BLOB_*` variable turns on managed mode (the watch/render/reload
loop). Without them the proxy runs standalone against a static ACL file — useful for
tests, not the deployed shape.
