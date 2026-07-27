## Context

The egress proxy enforces a per-workload FQDN allowlist. Today that allowlist is a single
JSON blob (`egress-config/allowlist.json`) in a locked-down storage account. The proxy
reads it with its own managed identity and hot-reloads on ETag change (~5 s). Writes are
data-plane blob uploads; the CI "allowlist" workflow publishes the git-tracked file on
merge. See `docs/allowlist.md`.

This design adds Mode 2: deployment pipelines pushing *their own* ruleset content through a
validating API. In a control-plane deployment the **blob becomes the control plane's
private store** — the API's identity is its sole writer, the proxy reads it read-only, and
no pipeline ever touches it. Direct-blob-write GitOps (Mode 1) survives only as the
*no-control-plane* topology; where the control plane runs, CI publishes by calling the API.

Constraints:
- The proxy must keep reading one rendered ACL, fail-closed, on its ETag poll (read-only).
  No proxy changes.
- Reuse existing machinery: JWKS/JWT validation (already validating workload tokens on
  CONNECT) and managed-identity blob access.
- The control plane is a **policy enforcement point, not an identity-provenance
  investigator** — it enforces platform-granted RBAC and never reaches into Azure (ARM/Graph)
  to verify which identity owns which subject.
- Reference-implementation ergonomics: the whole loop must be exercisable with `curl` +
  a service principal / managed identity, through to observable proxy enforcement.

## Goals / Non-Goals

**Goals:**
- A ruleset model that generalizes today's "module" and carries subjects + an acl.
- A control-plane API (Mode 2) that validates and applies pipeline pushes to the private
  blob store, authorized by platform-managed RBAC verbs, with forced `report` on new hosts.
- Optimistic concurrency that tolerates concurrent pushes to *different* rulesets without
  spurious failures.
- Preserve every security property of the current model, and add one: a compromised
  workload still cannot widen its own allowlist (writer ≠ subject).
- Ship the API as a standalone process with a working local Aspire setup, so the full Mode 2
  loop (push → blob → proxy enforcement) runs on a developer machine.

**Non-Goals:**
- Management portal / human `edit` verb (Mode 3).
- RBAC administration through the control-plane API — grants are platform-team managed out
  of band (manual) for now.
- Reassigning `subjects` after onboard via the API (write-once at onboard; later change is
  a platform op or offboard-and-re-onboard).
- The control plane verifying subject provenance against Azure (ARM/Graph).
- Ruleset composition, many-to-many, shared trusted-baseline ruleset (use `fallback`).
- Per-ruleset blobs and any compositor step.
- A database or any new stateful store beyond the private blob store.

## Decisions

### 1. Ruleset replaces "module"; a subject belongs to exactly one ruleset

A ruleset is `{ name, subjects[], content{ allowed_hosts[], action }, acl{ edit, push, admin } }`.
`subjects` is a list of workload identities (`appid`) and/or `netid`s.

*Why one-to-one (no composition):* union/precedence semantics are real complexity
(host-level action conflicts, priority ordering, a compositor in the renderer). One-to-one
replaces all of that with a single **uniqueness invariant** — a subject appears in at most
one ruleset — which is cheap to enforce and keeps the renderer a validator, not a
compositor. *Alternative considered:* many-to-many with a shared platform baseline ruleset
(elegant, delivers the "trusted services baseline" roadmap item for free) — deferred; the
existing `fallback` block covers the baseline need for now.

### 2. Platform-managed RBAC (three verbs), enforced not investigated

Two identities are distinct: the **subject** (whose traffic the ruleset governs, the
workload's runtime MI) and the **writer** (the pipeline's service-connection identity). If
they were the same, a compromised workload could widen its own allowlist — the exact attack
the proxy exists to stop. `writer ≠ subject` is enforced at write time.

The **platform team owns the trust mapping.** They grant service-connection identities three
verbs, in a platform-owned RBAC config loaded read-only by the API:

```
  onboard  — create a new ruleset (registry-scoped; the ruleset doesn't exist yet)
  update   — push content to an existing ruleset
  offboard — remove a ruleset, freeing its subjects
```

The control plane **enforces** these grants and nothing more. It does **not** ask Azure
"who really owns subject X?" — reverse-engineering MI→service-connection provenance from
ARM/Graph both overreaches the control plane's remit and couples it to the resource graph.
Whether a given service connection *should* onboard a given subject is the platform team's
judgment, encoded when they provision the service connection — which is exactly where that
knowledge lives.

*Onboarding (trust-on-first-use):* a holder of `onboard` creates a ruleset on first push,
declaring its `subjects`; the creator is recorded as the ruleset's owner (gains
`update`/`offboard`). So onboarding costs one platform grant, not a ticket per module. The
platform team can override any ruleset's grants afterward (manually now; via the API later,
out of scope).

*Subjects are write-once at onboard, frozen for `update`.* `onboard` sets `subjects`;
`update` writes `allowed_hosts` + `action` only. Consequences:
- Steady-state identity-hijack is impossible — `update` can't touch subjects.
- Uniqueness (first-come) protects already-onboarded subjects; the platform's scoping of who
  holds `onboard` bounds squatting on un-onboarded ones. No ARM verification needed.

*Alternative considered:* ARM/Graph-verified subject-claim scopes (control plane confirms
the subject's MI lives in a scope the creator controls). Rejected — overreaches the control
plane's remit and adds a directory dependency; the platform team's RBAC grant is the
authority instead.

*RBAC source:* a separate platform-owned file (a second blob in the same storage account),
written by the platform team, never by the API's write path. Keeps the trust boundary
outside the API's writable surface. TOFU-assigned ownership is recorded alongside the
ruleset; the platform's base grants remain platform-only.

### 3. Blob-as-private-store: the API is the sole writer

The blob stops being a shared bus with many writers and becomes the control plane's
**private persistence**. The API reads the current allowlist blob, splices in the one
ruleset being written, validates, and writes back. No database.

```
  writers:  { control-plane API identity }          ← sole Storage Blob Data Contributor
  readers:  { control-plane API, proxy (read-only) } ← proxy gets Storage Blob Data Reader
  everyone else (pipelines, humans): API only — no blob role at all
```

*Why:* the powerful blob-write role is held by exactly one identity — the control plane —
instead of being sprayed to every team's pipeline. Pipelines interact only through the API,
so all policy (RBAC, forced-report, writer≠subject) is unavoidable. Zero new stateful infra;
the proxy keeps its simple, dependency-free, fail-closed read.
*Alternative considered:* proxy fetches config *via* the API (truly one accessor). Rejected —
it puts the control plane on the proxy's runtime path, making egress enforcement depend on
control-plane availability; the read-only direct poll preserves last-known-good.
*Alternative considered:* API → database → renderer projects DB → blob. Rejected for v1 — a
stateful service + projection loop for no capability the private blob store cannot show.

### 4. Optimistic concurrency with bounded resilience retry

All rulesets share one blob, so a naive `If-Match` write 412s even when two pushes touch
*different* rulesets (false contention on a global optimistic lock). Fix: wrap the **entire
read-modify-write** — GET fresh ETag → re-splice the ruleset → PUT `If-Match` — in a
`Microsoft.Extensions.Resilience` retry pipeline (Polly v8), retrying on 412 with a bounded
attempt count and exponential backoff + jitter.

Because `PUT /rulesets/{name}` is a full replace of that ruleset's content, the delegate is
**idempotent**, so re-running it per attempt is safe. A different-ruleset collision is
absorbed transparently (the re-splice lands on fresh state and succeeds); only sustained
contention on the *same* ruleset exhausts the budget, which surfaces as `409` to the caller
(which may retry at the pipeline level).

*Why not hand-roll:* the resilience stack is idiomatic for the ASP.NET Core API and keeps
the retry policy declarative (a few lines). *Why not per-ruleset blobs now:* that is the
structural fix for contention, but it reintroduces a compositor — deferred as the named
escape hatch if contention ever bites.

### 5. AuthN reuses the proxy's JWT/JWKS validation; AuthZ is the RBAC verbs

The proxy already validates RS256/JWKS tokens (`iss`/`aud`/`exp`) to identify workloads on
CONNECT. The API validates the *caller's* service-connection token the same way, then checks
the caller against the platform RBAC: `onboard` (registry-scoped) for creating a ruleset,
`update`/`offboard` (ruleset-scoped, including TOFU-assigned ownership) for existing ones.
Reads require only a valid token — the egress posture is transparent. Reuse over
reinvention, and one identity model across data plane and control plane.

### 6. Standalone process, wired into the Aspire AppHost

The control plane SHALL be a **new, standalone process** (its own ASP.NET Core project),
not folded into the sample app or any existing service. It gets its own managed identity in
Azure and its own container.

For local development it SHALL be wired into the existing Aspire AppHost
(`src/AppHost/AppHost.cs`) alongside the proxy, Azurite, mock-idp, sample-app, and the
allowlist-seeder, so the full Mode 2 loop is exercisable locally:

```
  mock-idp (JWKS) ──┐                     ┌──▶ proxy ──▶ (enforcement)
                    │                     │      ▲
  service caller ──▶ control-plane API ───┼──────┘ reads allowlist blob (poll)
   (JWT from        │  read-modify-write  │
    mock-idp)       └──▶ Azurite blob ────┘
                         (allowlist + registry files)
```

The API validates caller JWTs against the same mock-idp `JWKS_URL` the proxy already uses,
and is the sole writer of the Azurite `egress-config/allowlist.json` blob the proxy polls
read-only — so a local `curl` push visibly changes proxy behavior within one poll interval.
The platform RBAC grants live in a second file seeded into Azurite alongside the allowlist.

*Why standalone:* clean separation of the control plane from any workload; independent
identity, scaling, and blast radius; mirrors how it deploys in Azure (its own Container App).

### 7. API surface

```
GET    /rulesets              list all rulesets (open to any authenticated caller)
GET    /rulesets/{name}       read one ruleset (content + subjects, acl summary)
PUT    /rulesets/{name}       onboard-if-absent / replace content if present; the core verb
POST   /rulesets/{name}:check dry-run validate a proposed content, no write
DELETE /rulesets/{name}       offboard: remove the ruleset, free its subjects (fail-closed)
```

- **`GET`** — any valid token; returns everything (transparency).
- **`PUT` on an absent name** — *onboard*: requires the `onboard` verb; the body includes
  `subjects`; on success the ruleset is created and the caller is recorded as owner
  (trust-on-first-use). All hosts are new → forced to `report`.
- **`PUT` on an existing name** — *update*: requires `update` (or ownership); a full replace
  of `content` (`allowed_hosts` + `action`); `subjects`/`acl` in the body are rejected.
- **`DELETE`** — *offboard*: requires `offboard` (or ownership); removes the ruleset entry
  and frees its subjects, which fall to the `fallback`/deny block on the next proxy reload —
  decommission is fail-closed by construction.
- **`:check`** — runs the same validation + forced-report coercion as `PUT`, returns the
  effective result and an `{ added, removed }` diff, and writes nothing.

## Risks / Trade-offs

- **Single-blob write contention** → bounded resilience retry absorbs different-ruleset
  collisions; per-ruleset blobs named as the escape hatch if the retry budget is regularly
  exhausted under real load.
- **Forced-report coercion could surprise pipelines** that expect `enforce` immediately →
  document it; `:check` shows the coerced result before a real push. New hosts must be
  observed in `report` and promoted (a later, likely portal-driven, step).
- **The API identity is the sole `Storage Blob Data Contributor`** on the private blob — by
  design it is the chokepoint, but that also makes it the single most valuable identity: a
  compromise of it is a compromise of the whole allowlist. → lock the API's managed identity,
  scope the role to the single blob, keep blob versioning + soft delete for rollback (on
  today). Pipelines hold *no* blob role, which is the security win over the old shared-writer
  model.
- **Over-broad `onboard` grants enable subject squatting** on un-onboarded identities (the
  control plane no longer verifies provenance). → the platform team scopes `onboard` grants
  narrowly; uniqueness (first-come) protects onboarded subjects; removals and onboards are
  audited so a bad grant is visible.
- **Unpushed / unmaintained rulesets** (onboarded but never updated) → empty `allowed_hosts`;
  subjects fall to fallback. Fail-closed, acceptable. Surface them in `GET /rulesets`.
- **Schema evolution module→ruleset** must keep the proxy's read/render path working. →
  design the rendered ACL output to be unchanged; only the authored schema gains `subjects`
  (array) and `acl`. Treat as backward-compatible for the proxy.

## Migration Plan

1. Evolve `allowlist.schema.json` + `docs/allowlist.md`: `module` → `ruleset`, `appid` →
   `subjects[]`, add `acl`. Keep the rendered ACL identical so the proxy is untouched.
2. Add the platform-owned RBAC config source (second blob) with the `onboard`/`update`/
   `offboard` grants, plus a sample.
3. Build the API; grant its managed identity `Storage Blob Data Contributor` (sole writer)
   and switch the proxy's identity to `Storage Blob Data Reader`. Grant pipelines no blob
   role.
4. Deploy the API (Container App); exercise Mode 2 end-to-end via `curl` + a service
   principal — onboard, update, offboard — verifying proxy enforcement changes after a push.
5. Rollback: blob versioning + soft delete revert any bad write. Where the control plane is
   not deployed, the system runs Mode 1 (direct-write GitOps) with no API in the path.

## Resolved Decisions

- **Registry source shape** — a **separate file** (a second blob in the same storage
  account, seeded alongside the allowlist), loaded read-only by the API.
- **`GET` visibility** — **reads are open to any authenticated caller**; `GET /rulesets`
  and `GET /rulesets/{name}` return every ruleset regardless of `acl`. Transparency is a
  feature: any team can see the full egress posture. Only *writes* (`PUT`/`DELETE`) are
  gated by `acl.push`.
- **Host-removal on `PUT`** — `PUT` is a **full replace** (desired-state). The mental model:
  a team keeps a rules file per environment in its repo; the pipeline pushes that file, so
  the ruleset always matches the repo and a host absent from the push is removed. To close
  the one gap this opens — forced-`report` guards *additions* but nothing guards a *removal*
  — the API SHALL emit an audit event for every host removed by a push, and `:check` SHALL
  return an `{ added, removed }` diff so pipelines can gate on unexpected removals.

## Open Questions

<!-- None outstanding. -->
