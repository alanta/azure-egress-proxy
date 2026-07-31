Work runs in three waves. **Wave 1 is one worker, start to finish** — everything after it
inherits its decisions. **Wave 2 is one worker per surface, in parallel** — the surfaces share
no files once wave 1's contract exists. **Wave 3 is one worker** closing the change out.

The wave-1 → wave-2 handoff is a *contract*, not just working code: the DTOs and the Razor
partial set. If those are left vague, five parallel workers will each invent their own, which
is the drift this structure exists to prevent.

Before starting, read `proposal.md` and `design.md` in full, and open `mockups/portal.html` in a
browser — it is the visual specification for every surface.

---

## Wave 1 — foundation (one worker, sequential)

### 1. Control-plane read extensions

- [x] 1.1 Add `GET /grants` returning the platform-managed grants; auth-only, no verb, read-only
- [x] 1.2 Add `GET /fallback` returning the fallback block; auth-only, no verb; report an absent or empty fallback as deny-all
- [x] 1.3 Surface the state document's last-modified time and ETag on control-plane read responses
- [x] 1.4 Tests: both endpoints reachable with any valid token and no grants; neither writable; recency advances after a ruleset write
- [x] 1.5 Update `docs/control-plane.md` § API surface with both endpoints and the recency semantics

### 2. Portal service scaffold

- [x] 2.1 Create `src/Portal/` as an ASP.NET Core Razor Pages app — its own process, its own image, the Dockerfile shape `src/ControlPlane/` uses
- [x] 2.2 Vendor htmx as a single pinned file served by the app; no npm, no build step, no CDN
- [x] 2.3 Add user authentication behind an abstraction, so the identity provider can change without touching the control-plane client
- [x] 2.4 Return `401` + `HX-Redirect` for expired sessions on `HX-Request` calls, so a login page can never render into a swap target
- [x] 2.5 Set a CSP with no `unsafe-eval`, and forbid `hx-vals='js:…'`, `hx-on:*`, and eval-based extensions in review
- [x] 2.6 Assert in tests that the portal issues no `PUT` or `DELETE` against the control-plane API

### 3. Data clients — **contract for wave 2**

- [x] 3.1 Control-plane client calling the API with the portal's own managed identity; typed DTOs for rulesets, grants, fallback, recency, and the `:check` diff
- [x] 3.2 Log Analytics client with bounded default time windows; typed DTOs for decisions, denials, auth failures, challenge conversion, and report-mode findings
- [x] 3.3 ARM + Azure Monitor client; typed DTOs for scale-set capacity and instance view, public-IP-prefix capacity and consumption, and metric series
- [x] 3.4 Server-side response cache in front of all three, sized so a 60-second poll per operator does not reach Azure on every tick
- [x] 3.5 Write down the DTO set and the caching rules as the wave-2 contract

### 4. Design system — **contract for wave 2**

Read `design.md` § D9 first. The mockups at `mockups/portal.html` are the visual specification —
open them in a browser. The styling they inherit comes from the ZEP project at
`~/Projects/Zure/zep`, **outside this repository**.

- [x] 4.1 Port the CSS from `mockups/portal.html` into the portal as the stylesheet: tokens, glass card, per-surface shell tints, semantic allow/report/open
- [x] 4.2 Embed Geist and Geist Mono as the mockups do, with the SIL OFL notice retained
- [x] 4.3 Build the shared Razor partials the mockups imply: card, card header, stat, pill, data table, banner, host list, freshness stamp, sparkline, metric chart
- [x] 4.4 Establish the surface layout: sticky header, pill tab bar as real routes under `hx-boost`, per-surface background tint
- [x] 4.5 Write down the partial set and its parameters as the wave-2 contract

### 5. Vertical slice — Overview

Built in wave 1 deliberately: it exercises all three data sources, so if it works the remaining
surfaces are repetition rather than risk.

- [x] 5.1 Posture summary: ruleset count, enforce/report/open split, fallback state
- [x] 5.2 Configuration last-modified, labelled as document-scoped
- [x] 5.3 Traffic summary: denials, authentication failures, unconverted challenges
- [x] 5.4 Runtime summary: nodes, egress IP pool, throughput
- [x] 5.5 "Worth a look" panel — observations, explicitly not alerts
- [x] 5.6 Metric panels refreshing on `hx-trigger="every 60s"` against the cache, with freshness stamped

### 6. Infrastructure

- [x] 6.1 Add the `deployPortal` parameter, threaded through hub and spoke as `deployControlPlane` is
- [x] 6.2 Create the portal user-assigned identity; assign `Reader` + `Monitoring Reader` on the hub resource group — and no write role anywhere
- [x] 6.3 Deploy the portal container app; grant `AcrPull`
- [x] 6.4 Decide and implement the portal's inbound exposure — it is an admin surface for a security control, not a sample workload
- [x] 6.5 Add the portal image to the release build and to `scripts/deploy.sh`
- [x] 6.6 Wire the portal into `src/AppHost/` so the console runs against Azurite, the mock IdP, and the local control plane

---

## Wave 2 — surfaces (one worker each, in parallel)

Each surface is a Razor page plus partials, built against wave 1's DTOs and shared partials.
No surface edits another's files.

### 7. Rulesets

- [ ] 7.1 Ruleset list: subjects, host count, action, owner, denials — fixed height, sticky header, scrolls in place
- [ ] 7.2 Detail panel swapped by `hx-get` on row selection: subjects, allowed hosts, action, owner
- [ ] 7.3 Observed-but-denied hosts for the selected ruleset
- [ ] 7.4 `:check` sandbox — `hx-post` returning the rendered `added`/`removed`/`bound`/`unbound` diff
- [ ] 7.5 Copyable `curl`/pipeline snippet, with the pipeline named as the source of truth
- [ ] 7.6 Explain in place that `netid` rulesets are attributed by source address rather than validated identity

### 8. Traffic

- [ ] 8.1 Denials table: time, workload, source IP, governing ruleset, destination, reason
- [ ] 8.2 Join denials to rulesets on `Role` = `subjects[].appid`; fall back to `SrcIp` for `netid` rulesets; show source IP for every row
- [ ] 8.3 Attribute an unmatched subject to the fallback rather than to a ruleset
- [ ] 8.4 Filters — window, subject, host — as `hx-get` with `hx-push-url`
- [ ] 8.5 Rejected credentials: `CANONICAL-PROXY-DECISION` with an empty `Role`, grouped by reason
- [ ] 8.6 Challenge conversion: sources challenged versus authenticated, as the probing signal
- [ ] 8.7 Volume and top talkers from `CN-CLOSE` byte counts

### 9. Lookup

- [ ] 9.1 Resolve an `appid` or `netid` to its governing ruleset, or report that it falls to the fallback
- [ ] 9.2 Reverse index: given a host, which rulesets permit it — including `open` rulesets, marked as reaching everything regardless of host
- [ ] 9.3 `hx-trigger="input changed delay:300ms"` against the resolution partial

### 10. Platform

- [ ] 10.1 Grants table: identity, verbs, scope
- [ ] 10.2 State plainly that authority is granted outside the portal and the API never writes it
- [ ] 10.3 Fallback block, rendered so the deny-all floor is legible

### 11. Runtime

- [ ] 11.1 Scale-set instances: state, image, address, health
- [ ] 11.2 Egress IP pool: prefix capacity versus addresses in use, with the partner-allowlist framing
- [ ] 11.3 Throughput, CPU, and availability charts as server-rendered SVG
- [ ] 11.4 Load-balancer data-path availability and probe status
- [ ] 11.5 Freshness stamped on every panel

---

## Wave 3 — close-out (one worker, sequential)

### 12. Documentation

- [ ] 12.1 Document the portal in `docs/control-plane.md`: Mode 3 status, the read-only scope, and what is deferred
- [ ] 12.2 Record the invariant that the control-plane API is a machine interface, in `AGENTS.md` § Invariants
- [ ] 12.3 Note the portal's read-only Azure permissions and its concentration of read power in `docs/production-hardening.md`
- [ ] 12.4 Update `ROADMAP.md`: the console ships, human editing (Mode 3 proper) remains deferred
- [ ] 12.5 Add the portal to `AGENTS.md` § Where the code lives and the read-before-you-write table

### 13. Verification

- [ ] 13.1 `dotnet restore --locked-mode`, build, and test the full solution; commit regenerated lock files alongside any package change
- [ ] 13.2 Run the `SECURITY_GUIDELINES.md` review checklist over the new workflow and infrastructure changes
- [ ] 13.3 Confirm the deployed CSP carries no `unsafe-eval` and that no `hx-on:*` or `js:` attribute reached the templates
- [ ] 13.4 Confirm a portal outage leaves proxy enforcement and pipeline writes unaffected
- [ ] 13.5 Exercise the local loop end to end: push a ruleset, provoke a denial, and confirm the console traces it back to the ruleset that caused it
