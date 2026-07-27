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
- A ruleset model that replaces "module" as the authored unit and carries subjects + an acl.
- A control-plane API (Mode 2) that validates and applies pipeline pushes to the private
  blob store, authorized by platform-managed RBAC verbs, with `report` as the onboard default.
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

### 1. Ruleset is the authored unit; a subject belongs to exactly one ruleset

A ruleset is the set of rules applied to one or more clients of the proxy:
`{ name, subjects[], content{ allowed_hosts[], action }, acl{ edit, push, admin } }`.
`subjects` is a list of workload identities (`appid`) and/or `netid`s.

*Terminology.* "Ruleset" replaces "module", which was confusing — it suggested a code artifact
rather than a policy unit. The rename is complete at the authoring layer: teams write rulesets,
the API speaks rulesets, and `docs/` says ruleset. The word `modules` survives in exactly one
place — the `allowlist.json` key the Go proxy parses — because that document is the proxy's
frozen wire format. Under Mode 2 nobody authors it by hand, so the old term is now an
implementation detail of one file rather than a concept anyone has to learn. Under Mode 1
(GitOps, no control plane) teams still author `allowlist.json` directly and so still meet
`modules`; renaming that key is a proxy change and is out of scope here.

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
`update`/`offboard`). So onboarding costs one platform grant, not a ticket per ruleset. The
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

*RBAC source:* a `grants` section inside the single control-plane state blob, edited by the
platform team out of band. The API loads it read-only and its read-modify-write splices only
the one ruleset being written, never `grants` — so "the API cannot widen its own authority" is
a **code-level** invariant here, not a storage-level one (see *Resolved Decisions*).
TOFU-assigned ownership is recorded alongside the ruleset; the platform's base grants remain
platform-only.

### 3. Blob-as-private-store: the API is the sole writer

The blob stops being a shared bus with many writers and becomes the control plane's
**private persistence**. The API reads its current ruleset store, splices in the one
ruleset being written, validates, and writes back. No database.

The control plane's entire internal state is **one blob**, plus the rendered projection:

```
  egress-config/rulesets.json   ALL control-plane state: rulesets (subjects, content, acl, owner)
                                + the platform-owned `grants` + `fallback`      ← the private truth
  egress-config/allowlist.json  the rendered projection the proxy reads — TODAY'S SCHEMA, UNCHANGED
```

`allowlist.json` and `allowlist/allowlist.schema.json` do **not** change: the proxy's read
contract is frozen, and the Go proxy needs no code change at all (see *Resolved Decisions*).
The rulesets store is the authored shape; rendering it is a pure function, and the
`rulesets.json` write is the linearization point (render+publish follows a successful
`If-Match` write).

```
  writers:  { control-plane API identity }          ← sole Storage Blob Data Contributor
  readers:  { control-plane API, proxy (read-only) } ← proxy gets Storage Blob Data Reader
  everyone else (pipelines, humans): API only — no blob role at all
```

*Why:* the powerful blob-write role is held by exactly one identity — the control plane —
instead of being sprayed to every team's pipeline. Pipelines interact only through the API,
so all policy (RBAC, the onboard default, writer≠subject) is unavoidable. Zero new stateful infra;
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
  (trust-on-first-use). The ruleset is stored in `report` whatever action it requested.
- **`PUT` on an existing name** — *update*: requires `update` (or ownership); a full replace
  of `content` (`allowed_hosts` + `action`); `subjects`/`acl` in the body are rejected.
- **`DELETE`** — *offboard*: requires `offboard` (or ownership); removes the ruleset entry
  and frees its subjects, which fall to the `fallback`/deny block on the next proxy reload —
  decommission is fail-closed by construction.
- **`:check`** — runs the same validation + action policy as `PUT`, returns the
  effective result and an `{ added, removed }` diff, and writes nothing.

## Risks / Trade-offs

- **Single-blob write contention** → bounded resilience retry absorbs different-ruleset
  collisions; per-ruleset blobs named as the escape hatch if the retry budget is regularly
  exhausted under real load.
- **Forced `report` at onboard could surprise pipelines** that expect `enforce` immediately →
  document it; `:check` shows the effective action before a real push. A new ruleset must be
  observed in `report` and promoted by an explicit `enforce` push.
- **Nothing coerces a host added to an already-enforcing ruleset** — the audit event and the
  `:check` diff are the only controls, so a compromised *pipeline* identity can widen a
  ruleset it legitimately owns. Accepted: the alternative weakens rules already in force.
  Mitigated by narrow `onboard`/`update` grants, the team's own repo review of its rules file,
  and alerting on the addition audit events.
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
- **Introducing the ruleset model** must keep the proxy's read/render path working. → the
  ruleset model is a *new, separate* store; `allowlist.json` keeps today's schema exactly and
  is produced by rendering that store, so the proxy is untouched. Cost: the rendered blob is
  derived state that can lag its source if the second write fails — bounded by re-rendering
  on the next successful write, and detectable by comparing the two blobs.

## Migration Plan

1. Add `allowlist/rulesets.schema.json` (the authored ruleset store: `name`, `subjects[]`,
   `content`, `acl`, `owner`) and the renderer that projects it onto the **unchanged**
   `allowlist.json`. `allowlist.schema.json` is not touched; `docs/allowlist.md` gains the
   ruleset framing and the two topologies.
2. Add the platform-owned `grants` section to the ruleset store, plus a sample.
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
  the gaps this opens — nothing coerces an *addition* to an enforcing ruleset, and nothing
  guards a *removal* — the API SHALL emit an audit event for every host added or removed by a
  push, and `:check` SHALL return an `{ added, removed }` diff so pipelines can gate on
  unexpected changes before pushing.

- **The allowlist schema is frozen; the ruleset store is a new artifact.** The Go proxy parses
  the *authored* blob (`modules[]` with `appid`/`subnet`), so renaming keys there would have
  been a proxy change, not just a rendering change. Instead `allowlist.json` and its schema
  stay exactly as they are, and the control plane renders them from its own
  `rulesets.json` store. Rendering: one module entry **per subject** — `id` is the ruleset
  name for a single-subject ruleset (so today's file renders byte-identically) and
  `{name}-{n}` for each subject of a multi-subject one; an `appid` subject sets `appid`, a
  `netid` subject sets `subnet`; `action` is the ruleset's already-coerced action; `fallback`
  passes through. *Alternatives considered:* teaching the proxy the ruleset schema (rejected —
  the proxy's read contract is the one thing this change promised not to touch), and a
  superset blob carrying both shapes (rejected — duplicated `allowed_hosts` in the file teams
  read).

- **The control plane's internal state is a single blob**, `egress-config/rulesets.json`,
  holding rulesets *and* the platform-owned `grants` *and* `fallback`. One document = one
  linearizable state and one `If-Match` write; no cross-blob consistency to reason about.
  *Trade-off accepted:* the grants now live inside the blob the API can write, so
  "the API cannot widen its own authority" is enforced in **code** (the RMW splices only the
  target ruleset and copies `grants` through untouched) rather than by storage permissions.
  Blob versioning + soft delete remain the backstop, and a grants change is visible in the
  blob's version history.

- **`report` is the onboarding *default*, not a gate that overrides an explicit request.** Two
  constraints kill the per-host reading: evaluation inside a ruleset must be uniform (a rendered
  module has exactly one `action`), and adding a host must never *downgrade* an enforcing
  ruleset to `report` — that would reduce the security of rules already in force. A new host
  therefore has nowhere to sit as `report` on its own. What remains is a default: **`onboard`
  without an explicit action stores `action: report`**, the on-ramp for a workload whose egress
  is not yet known, and promotion is an explicit later push.

  An explicit `action` is always honoured, including at onboard. This is the direction the
  security argument actually points: `report` **permits all traffic** and merely logs off-list
  hosts, so coercing it over a team's explicit `enforce` would hand a brand-new workload *more*
  egress than it asked for — backwards for a system whose purpose is restricting egress. (This
  corrected an earlier reading of the requirement, caught in exploratory testing.) The system
  still never lowers a ruleset's action on its own. What guards widening an *enforcing* ruleset is the audit event
  per added host plus the `{ added, removed }` diff from `:check`, which a pipeline can gate
  on — the same control that guards removals. No per-host state exists in the model.
  *Alternatives considered:* holding new hosts `pending` and unrendered until a second push
  (adds per-host state, and the gate degrades to a one-deploy delay since CI re-pushes the
  same file); rejecting any widening push against an enforcing ruleset (forces a
  report→enforce cycle for a routine change, and that cycle is itself a real weakening).

## Open Questions

<!-- None outstanding. -->
