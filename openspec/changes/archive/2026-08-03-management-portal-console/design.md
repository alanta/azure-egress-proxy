## Context

Three stores hold what an operator needs, and nothing joins them:

| Store | Holds | Reached by |
|---|---|---|
| `rulesets.json` (via the control-plane API) | authored policy: rulesets, subjects, grants, fallback | machine identities, JWT/JWKS |
| `EgressProxy_CL` (Log Analytics) | every proxy decision: `Role`, `Host`, `Allow`, `DecisionReason`, `EnforceWouldDeny` | KQL, workspace RBAC |
| ARM + Azure Monitor | the deployment: VMSS capacity, egress IP prefix, throughput | Azure RBAC |

The join key exists and is exact: the audit table's `Role` column is the workload's `appid`
from the validated JWT, which is precisely `subjects[].appid` in a ruleset. A denial row
therefore resolves to its governing ruleset without heuristics. (`netid`-mode subjects join on
`SrcIp` instead, which is weaker by construction — the repo is emphatic that a source address
is not an identity, but for a `netid` ruleset it is the only key there is.)

Constraints carried in from the existing design:

- The control-plane API is a **policy enforcement point, not an identity-provenance
  investigator**; it never reaches into ARM or Graph. That constraint is about the *write*
  path's trust model.
- Reads on the control-plane API are open to any authenticated caller and consult no verb.
- The proxy exposes **no HTTP admin surface** — `net/http` in `proxy/` is the CONNECT listener
  and nothing else. There is no endpoint to scrape.

## Goals / Non-Goals

**Goals**
- Visualise the proxy's configuration and operational status in one place, for the platform team.
- Close the denial → owning-ruleset → candidate-change loop.
- Keep the deferral of per-ruleset RBAC honest: ship nothing that presupposes its design.

**Non-Goals**
- Writing policy (Mode 3). Change history (#33). Drift detection. Workload-team access.
  Any proxy change.

## Decisions

### D1 — The portal is a separate BFF service, not an extension of the control-plane API

The alternative was a static SPA served by the control plane, with the Log Analytics query
living in a new control-plane endpoint. Rejected.

Filtering what a user may see requires knowing who the user is. Putting the traffic query in
the control-plane API therefore puts *human identity* in the control-plane API — and with it an
opinion about the identity provider. Keeping human identity in the portal preserves the
option of authenticating portal users against something other than Entra without touching the
service that guards policy writes.

Secondary effects, all favourable: the identity holding `Storage Blob Data Contributor` does
not also acquire Log Analytics access and user trust; Log Analytics latency and quota land in
a process that is not on the policy write path; and BFF-shaped concerns (caching, aggregation)
stay out of a service designed around one blob and one ETag.

> **Invariant introduced.** *The control-plane API is a machine interface.* Humans reach
> policy only through the portal, which is the sole component that understands user identity.
> The control-plane API's identity model stays one RS256/JWKS check over service-principal
> tokens.

Cost: one more container app, image, and CI target. The repo already has the pattern —
`deployControlPlane` is a single bool threaded through hub and spoke — so `deployPortal`
follows it rather than inventing anything.

### D2 — The portal calls the control-plane API as itself, not as the user

A pass-through of the user's bearer token would keep the caller's identity visible to the API
and would line the system up for a future where `grants` understands users. It is rejected
because a non-Entra user token cannot satisfy the API's `iss`/`aud` validation — pass-through
silently pins the portal to Entra forever, which is the opposite of D1's purpose.

The portal therefore holds its own managed identity and calls the API with it. For this change
that costs nothing: every endpoint the portal uses (`GET /rulesets`, `GET /rulesets/{name}`,
`:check`, and the two new reads) consults no verb, so the portal exercises no authority it
could misuse.

It does pre-shape Mode 3, and this is recorded deliberately rather than discovered later: when
writes arrive, the portal will be a **delegating principal** — a trusted component holding its
own grant, with the acting user travelling as an audited assertion rather than as an identity
the API validates:

```
portal ──▶ PUT /rulesets/payments
           Authorization: Bearer <portal MI token>      ← authority
           X-Acting-User: <subject from portal's IdP>   ← audit only
```

The consequence is that `acl.edit`/`push`/`admin` likely become portal-side data rather than
something the control-plane API enforces. Mode 3 should confirm or overturn that; nothing in
this change depends on it.

### D3 — Read-only, with a copyable apply snippet

The console composes a candidate change, validates it through `POST /rulesets/{name}:check`,
and renders the resulting `added`/`removed`/`bound`/`unbound` diff — then emits the `curl` or
pipeline snippet that would apply it. The human applies it through the existing audited
machine path.

This is what keeps a read-only console from feeling crippled while leaving the write-path
trust model completely untouched. It also keeps the pipeline as the source of truth: the
snippet is something to commit, not a side-channel edit that the next unrelated deploy would
silently revert (`PUT` is a full replace).

### D4 — Runtime status comes from Azure, not from the proxy

ARM for configuration, Azure Monitor for metrics, one managed identity holding `Reader` +
`Monitoring Reader` on the hub resource group. No per-user Azure RBAC — pushing workspace and
resource permissions down to individuals would multiply administration and re-couple the portal
to Entra, against D1.

| Panel | Source | Granularity |
|---|---|---|
| Nodes online | ARM — VMSS `sku.capacity` + instance view | on request |
| Egress IP pool | ARM — public IP prefix `prefixLength` vs. instance PIPs in use | on request |
| Throughput | Azure Monitor — `Network In/Out Total` | 1 min |
| CPU, VM availability | Azure Monitor — `Percentage CPU`, `VM Availability` | 1 min |
| ILB health | Azure Monitor — data-path availability, health-probe status | 1 min |

The egress path is instance-level public IPs drawn from a prefix (`infra/modules/hub.bicep`),
not a NAT Gateway, which makes the IP-pool panel operationally meaningful rather than
decorative: the prefix is the stable set of addresses partners allowlist on their side.

**No live metrics.** Azure Monitor is 1-minute; Log Analytics ingestion is minutes. Genuinely
live data would require adding an HTTP metrics listener to the proxy — a new listening port on
a security appliance, with its own authentication and NSG question. That is out of scope here
and would be its own proposal. The UI states data freshness rather than implying immediacy.

**Relation to the existing ARM constraint.** "Never reach into ARM/Graph" governs the
control-plane API, where investigating identity provenance would make policy decisions depend
on Azure's view of the world. The portal reads ARM as an observer and decides nothing. The
distinction is deliberate and is stated so that a later reader does not mistake it for erosion.

### D5 — Two control-plane read endpoints, not one

`GET /grants` and `GET /fallback`, separately, rather than a combined `GET /platform`. They are
independent concepts with different audiences — grants answer *who may change policy*, fallback
answers *what unmatched traffic is allowed* — and separate resources leave room for each to grow
(filtering, pagination) without versioning the other. Both are auth-only and consult no verb,
matching the existing reads-are-open rule. Neither is writable; the API still never writes
`grants`.

### D6 — Configuration recency comes from the blob, not from a new field

The state blob already carries a modification date. Control-plane read responses surface it
(with the ETag) so the Overview can answer *"when did the configuration last change?"*

This is document-scoped: any ruleset write moves it, so it cannot answer *"when did **this**
ruleset last change?"* That would need a per-ruleset stamp the model does not have, which is a
write-path change and the first brick of the audit trail — deferred to #33. Taking the blob's
date keeps this change entirely read-path.

### D7 — One audience tier, no filtering

The platform team sees everything; nobody else has access. Any narrower rule needs a
user→ruleset association that exists in no document, and designing that association is most of
the per-ruleset RBAC model this change defers. One tier is the only version of the console that
does not quietly design Mode 3.

### D8 — ASP.NET Core + htmx, server-rendered

Razor Pages — one page per surface, partials as swap targets — with htmx vendored as a single
file. No npm, no build step, no client framework.

Considered and rejected: **Next.js + a .NET BFF** (ZEP's own shape, which would allow direct
component reuse) because it adds a Node runtime and an npm dependency tree to a security
reference implementation whose dependency rules are binding; and **Blazor**, on the team's
preference.

The console is a read-mostly view of server-held data, which is what htmx is for. Nothing in
the mockups needs client state: tabs become real routes under `hx-boost` (which also gets
deep links and the back button, which the mockup's JS tab switching does not); filters,
lookup, and the ruleset detail panel are partial swaps; metric panels refresh with
`hx-trigger="every 60s"`, matching Azure Monitor's 1-minute cadence. Charts are
server-rendered SVG — the mockups already are, and ZEP's `MetricChart` is hand-rolled SVG too,
so no charting library is implied.

**This holds for Mode 3 writes.** The ruleset model is deliberately flat — hosts, one uniform
action, subjects, one-to-one, no composition or precedence — and `PUT` is desired-state full
replace. A ruleset edit is therefore "submit the complete new content", which is a form, not an
application-state problem. A textarea of hosts mirrors both the API's semantics and the file the
team keeps in its repo, with `:check` driving a live diff on blur. Server-side validation
re-rendering the form partial keeps one source of validation truth, which for a security control
is a feature rather than a limitation.

> **Tripwire.** If the ruleset model ever gains composition, precedence, or many-to-many
> subjects, revisit this decision — policy authoring would become genuine client state. All
> three are current non-goals.

Four rules follow from the stack and are settled here rather than discovered later:

1. **No `unsafe-eval` in the CSP.** Avoid `hx-vals='js:…'`, `hx-on:*`, and the eval-based
   extensions. The admin UI for an egress control should not need `unsafe-eval` to render tables.
2. **Session expiry must not render a login page into a `div`.** Detect `HX-Request` and respond
   `401` with `HX-Redirect` rather than a redirect.
3. **Polling reads a server-side cache, never Azure directly.** `every 60s` per operator across
   three sources is otherwise a real Log Analytics bill.
4. **htmx is vendored**, pinned, and served from the app — not from a CDN.

### D9 — Visual design is inherited from ZEP, not invented

**Mockups:** [`mockups/portal.html`](mockups/portal.html) — all six surfaces, self-contained,
open it in a browser. It is the visual specification; where it and this document disagree about
layout, the mockup wins.

**Styling source:** the ZEP project at `~/Projects/Zure/zep` — **outside this repository**, so
it cannot be found by searching here. The design language comes from `zep/web/app/globals.css`
(pastel radial shell backgrounds, per-surface tints) and `zep/web/components/`
(`GlassCard.tsx`, `CardHeader.tsx`, `MetricTile.tsx`, `NavShell.tsx`). ZEP's own standalone
mockups live in `zep/ui-design/`.

Inherited: the glass card treatment, the 62px sticky header with logo chip and pill tab bar,
per-surface background tints, the 9px `0.14em` uppercase mono micro-label, and the
`#1c2333` / `#6a7287` / `#9aa0b4` / `#5b8def` ramp. Typography is Geist and Geist Mono, embedded
as data URIs (SIL OFL 1.1, © Vercel) so the page never silently falls back.

Added, because ZEP has no equivalent and a security console needs it: semantic
allow `#2f9e6b` / report `#d98324` / open `#d1495b`, held separate from the accent hue so policy
state reads at a glance rather than requiring the label to be read.

Two content decisions the mockups embody and that should survive implementation:

- **Copy is written from the operator's side**, not the system's — "Traffic matching no
  ruleset" rather than "fallback block", "Who may change policy" rather than "grants". The
  underlying identifiers still appear where they *are* the identifier.
- **Time in `report` is not a signal.** A ruleset can sit in `report` indefinitely and that is a
  legitimate steady state, not rot. The Overview's promotion prompt leads with *hosts observed
  off-list*; last-modified is context, never a nudge.

## Risks / Trade-offs

- **The portal accumulates read power** — policy, traffic, and infrastructure in one identity.
  Mitigated by it being read-only in all three directions and holding no write role anywhere.
  It is nonetheless the single most informative component to compromise, and should be treated
  as such in the hardening notes.
- **D2 pre-shapes Mode 3 toward a delegating-principal model.** Recorded, not hidden. The cost
  of reversing it later is re-authenticating portal users against Entra specifically.
- **Log Analytics cost and latency** are now user-facing. Queries must be bounded by default
  time windows and cached; an unbounded console query against a busy workspace is a real
  expense.
- **`netid` rulesets get a weaker traffic view** than `appid` ones, because the join key is a
  source address. This should be visible in the UI, not silently degraded.
- **Two services to operate** where there was one optional service.

## Open Questions

- Does the portal need its own inbound network restriction, or does it sit behind the same
  ingress story as the sample app? It is an admin surface for a security control, so the
  answer is probably not "the same". **Settle this before building, not after** — it is
  task 6.4.
- Which identity provider does portal sign-in use first? Entra is the obvious start, and D1
  exists precisely so this is not a one-way door — but it should be chosen deliberately rather
  than by default (task 2.3).
- Default time window for traffic views, and what caching is acceptable before the numbers
  mislead.
- Whether the portal should render the fallback block's practical meaning ("unmatched sources
  may reach these hosts") or its literal contents. The former is more useful and more
  interpretive.
