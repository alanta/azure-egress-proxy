## Why

Today the allowlist is written by direct blob writes driven by CI/GitOps: a single
config file, published on merge. That is a valid operating mode, but it cannot support
per-team self-service — every egress change funnels through one shared file and one
review queue. Teams that own a workload cannot push that workload's egress rules from
their own deployment pipeline without touching a shared artifact.

This change introduces a **control-plane API** as a validating write path in front of
the allowlist blob, so deployment pipelines push their own rules directly. It is the API
half of the roadmap's "Control-plane API + management portal" item; the portal is
deferred.

## What Changes

- **Introduce the `ruleset` model.** A *ruleset* is the set of rules applied to one or more
  clients of the proxy, and it replaces "module" as the unit teams author: a `name`, a set of
  `subjects` (workload identities / netids the ruleset governs), the `content`
  (`allowed_hosts` + `action`), and an `acl` (`edit` for humans, `push` for machines, `admin`
  governing the acl itself). "Module" survives only as a key inside the rendered
  `allowlist.json` — the proxy's wire format, which no one authors by hand under Mode 2. A subject belongs to **at most
  one** ruleset (one-to-one; no composition). The identity that may write a ruleset is
  **never** the workload identity it governs (writer ≠ subject).
- **Frame four operating modes**, of which only Mode 2 is built here:
  1. GitOps (today) — single file, PR-published, no control plane.
  2. **Pipeline push — teams push ruleset content from deployment pipelines via the API. ← built.**
  3. Management portal — humans edit via an app with per-ruleset RBAC. *(deferred)*
  4. Mix & match per ruleset. *(deferred)*
- **Add the control-plane API** (Mode 2): `GET /rulesets`, `GET /rulesets/{name}`,
  `PUT /rulesets/{name}`, `POST /rulesets/{name}:check` (dry-run), `DELETE /rulesets/{name}`.
- **AuthN** reuses the proxy's existing JWT/JWKS validation for the caller's
  managed-identity token. **AuthZ** is **platform-managed RBAC**: the platform team grants
  service-connection (pipeline) identities the verbs `onboard`, `update`, `bind`, and
  `offboard`. Reads are open to any authenticated caller; writes require the matching verb.
  The control plane **enforces** these grants — it does **not** investigate Azure to verify
  subject provenance (no ARM/Graph traceback).
- **Self-provisioning (onboarding)**: a service connection holding `onboard` can create a
  new ruleset on first push, declaring its `subjects`. **Trust-on-first-use** — the creator
  is recorded as the ruleset's owner (gains `update`/`bind`/`offboard`), so onboarding needs
  one platform grant, not a platform ticket per ruleset. The platform team can override any
  ruleset's grants afterward.
- **Membership is separated from content**: `onboard` sets `subjects`; a plain `update` may
  write **`allowed_hosts` and `action` only** and never touches `subjects` (steady-state
  anti-hijack). A module grows, though, so changing membership — adding/removing the
  workloads a ruleset governs — is a first-class operation gated by its own **`bind`** verb,
  validated and uniqueness-checked like an onboard. Uniqueness (first-come) protects
  already-onboarded subjects; the platform's scoping of who gets `onboard`/`bind` bounds
  squatting on un-onboarded ones.
- **`report` is the onboarding default, never an override**: evaluation within a ruleset is
  uniform (one `action`), and the system never lowers a ruleset's action on its own. A ruleset
  onboarded *without* an explicit action starts in `report` — the documented on-ramp for a
  workload whose egress is not yet known — but an explicit `enforce` is honoured, at onboard and
  after. (`report` permits all traffic and merely logs it, so overriding an explicit `enforce`
  would hand a new workload more egress than it asked for.) Widening an enforcing ruleset is
  controlled by the per-host audit events and the `:check` `{ added, removed }` diff.
- **The blob becomes the control plane's private store.** The **only writer** is the
  control plane API's own identity; **no pipeline ever gets a blob role** — pipelines reach
  the config only through the API. The proxy keeps a **read-only** direct blob poll. The
  API is a validating read-modify-write over this store; no new database.
- **Optimistic concurrency** (now purely internal, single writer): ETag `If-Match` on the
  blob write, with the whole read-modify-write wrapped in a bounded
  `Microsoft.Extensions.Resilience` retry pipeline (retry on 412, re-read + re-splice per
  attempt, expo backoff + jitter). Exhausted budget → `409` to the caller.
- The **proxy is unchanged** — it still reads one rendered ACL from the blob, fail-closed,
  on its ETag poll (now read-only). Direct-blob-write GitOps (Mode 1) exists only in the
  **no-control-plane** topology; where the control plane is deployed, CI publishes by
  calling the API.

## Capabilities

### New Capabilities

- `ruleset-model`: The ruleset data model and its invariants — schema (name, subjects,
  content, acl), one-to-one subject↔ruleset uniqueness, writer≠subject, the
  platform-owned registry vs. writable content split, and the `report`-by-default-at-onboard
  policy. Replaces "module" as the authored unit; the proxy's read contract is untouched,
  because rulesets are *rendered* into it rather than replacing it.
- `control-plane-api`: The HTTP API surface for Mode 2 — endpoints, authN via JWT/JWKS,
  platform-managed RBAC authZ (`onboard`/`update`/`bind`/`offboard` verbs) with trust-on-first-use
  ownership, the blob-as-private-store read-modify-write, and ETag optimistic concurrency
  with bounded resilience retry.

### Modified Capabilities

<!-- None. openspec/specs/ is empty; the current allowlist contract lives in docs/allowlist.md, not as an OpenSpec capability. -->

## Impact

- **New code**: an ASP.NET Core control-plane API (alongside the existing `src/SampleApp`
  and `src/EgressProxy.Client`), likely deployed on Azure Container Apps like the sample
  workload. Reuses existing JWKS/JWT validation and managed-identity blob access.
- **Allowlist schema**: unchanged. `allowlist/allowlist.schema.json` and `allowlist.json`
  stay exactly as they are — they are the proxy's read contract. The ruleset model lives in a
  **new** store (`allowlist/rulesets.schema.json`, blob `egress-config/rulesets.json`) that
  the control plane renders into the allowlist document, so the Go proxy needs no code change.
  `docs/allowlist.md` gains the ruleset/topology framing.
- **Infra / access model**: new Container App + its managed identity. That identity is the
  **sole** holder of `Storage Blob Data Contributor` on the (now private) allowlist blob;
  the proxy's identity gets `Storage Blob Data Reader`; **no pipeline gets any blob role**.
  A platform-owned `grants` section of the control-plane state blob holds the
  `onboard`/`update`/`bind`/`offboard` grants and is loaded read-only by the API.
- **Dependencies**: `Microsoft.Extensions.Resilience` (Polly v8) added to the API project.
- **Docs**: a new `docs/control-plane.md` (model, RBAC, API, `curl` examples) plus README /
  `docs/allowlist.md` updates covering the two topologies — **without** the control plane
  (GitOps / Mode 1) and **with** it (Mode 2) — and setup instructions for each.

### Non-goals (deferred, so scope cannot creep)

- Management portal and the human `edit` verb (Mode 3).
- **RBAC administration through the control-plane API** — grants are managed by the
  platform team out of band (manually) for now; an API to manage them is future work.
- The control plane verifying subject provenance against Azure (ARM/Graph) — trust is the
  platform team's RBAC grant, enforced not investigated.
- Ruleset composition / many-to-many / a shared trusted-baseline ruleset — the existing
  `fallback` block covers a platform baseline for now.
- Per-ruleset blobs — named escape hatch for write contention; would reintroduce a
  compositor.
- Any self-healing beyond the bounded resilience retry.
