# The management console (Mode 3, read-only)

A read-only web console for the platform team. It joins the three stores that hold what an
operator needs — authored policy, proxy decisions, and the deployment's runtime state — and
closes the *denial → owning ruleset → candidate change* loop that no component owned before.

It is **not** Mode 3 proper. Nothing here writes policy. See
[`docs/control-plane.md`](../../docs/control-plane.md) for the API it reads and
[`openspec/changes/management-portal-console/design.md`](../../openspec/changes/management-portal-console/design.md)
for why each of the decisions below is the way it is.

## Shape

ASP.NET Core Razor Pages, server-rendered, with **htmx vendored as a single pinned file**
(`wwwroot/lib/htmx/`). No npm, no build step, no client framework, no charting library — charts
are hand-rolled SVG. One container, in the shape `src/ControlPlane/` already uses.

```
Pages/          one Razor page per surface, partials as swap targets
Auth/           who the operator is, and nothing else knows
Clients/        the three read-only data sources, and the cache in front of them
wwwroot/        stylesheet, embedded fonts, vendored htmx
```

## The four rules that are not negotiable

1. **No `unsafe-eval` in the CSP.** So no `hx-vals='js:…'`, no `hx-on:*`, no eval-based htmx
   extensions. Enforced by `SecurityHeaders.ContentSecurityPolicy` and asserted in
   `Portal.Tests/ScaffoldTests`, which strips Razor and HTML comments before it scans — a
   template is free to explain in place why it avoided the attribute it is naming. The CDN scan
   next to it deliberately reads the file raw, because a CDN reference lives inside a `src="https://…"`
   that any comment stripping would cut at the scheme.
2. **Session expiry must never render a login page into a `div`.** `SessionMiddleware` answers an
   `HX-Request` with `401` + `HX-Redirect`, never a `302`.
3. **Polling reads the cache, never Azure.** Every surface takes `ConsoleData`, never the raw
   clients underneath.
4. **htmx is vendored and pinned.** No CDN in the trust path.

## Read-only, and how that is held

The portal holds `Reader` + `Monitoring Reader` on the hub resource group and **no write role
anywhere** — in particular no `Storage Blob Data Contributor` on the allowlist container.

`ControlPlaneClient` has no `PutAsync` and no `DeleteAsync`; the only non-`GET` it makes is
`POST /rulesets/{name}:check`, the API's dry run, which validates and returns without touching
the blob. The console renders the resulting diff and emits a copyable `curl`/pipeline snippet;
the operator applies it through the existing audited machine path. Three tests defend this — a
source scan for write verbs, a reflection check on the client's public surface, and a check that
the only `POST` in the codebase is the dry run.

## Contract for the surfaces

Everything below is what a surface consumes without re-deriving it.

### Data — take `ConsoleData`, never the clients underneath

| Method | Returns | Cached for |
|---|---|---|
| `PolicyAsync` | `PolicySnapshot` — rulesets, grants, fallback, recency | 30s |
| `CheckAsync` | `CheckResult` | **never** — a dry run evaluates what was just typed |
| `TrafficSummaryAsync` | `TrafficSummary` | 2 min |
| `DenialsAsync`, `DecisionsForRoleAsync` | `DecisionRow[]` | 2 min |
| `AuthFailuresAsync` | `AuthFailureGroup[]` | 2 min |
| `ChallengeConversionAsync` | `ChallengeConversion[]` | 2 min |
| `ReportFindingsAsync`, `ReportFindingsForRoleAsync` | `ReportFinding[]` | 2 min |
| `TopTalkersAsync` | `TalkerRow[]` | 2 min |
| `ScaleSetAsync` | `ScaleSetStatus?` | 1 min |
| `EgressPoolAsync` | `EgressPool?` | 1 min |
| `MetricAsync` | `MetricSeries` | 55s |

The lifetimes are sized against the panels' 60-second poll, not picked for tidiness. Metrics sit
just *under* 60s so a poll lands on a new value and just *over* Azure Monitor's 1-minute grain so
it never fetches faster than the data changes. Traffic sits well above, because that is the query
that costs money. `Portal.Tests/ClientContractTests` asserts the relationship.

The cache is shared across operators, which is correct because the portal serves **one audience
tier with no per-user scoping**. If per-ruleset RBAC ever arrives, this key space becomes wrong
and must gain the identity.

### DTOs

| Type | Notes for a surface |
|---|---|
| `Freshness` | On every DTO that came from Azure. **Render it.** A cached value keeps the timestamp of the fetch that produced it — do not restamp it as "now". |
| `Recency` | The state document's `LastModified`/`ETag`. **Document-scoped** — never label it per-ruleset. |
| `RulesetAction` | Enum. Use `RulesetActions.Normalize`; absent/empty/unrecognised is `Enforce`. Never compare action strings by hand. |
| `SubjectView` | `IsNetwork` distinguishes `netid` from `appid`. |
| `RulesetView` | `IsNetworkAttributed` — the traffic view **must** say when a correlation is by source address rather than validated identity. |
| `PolicySnapshot.Governing(appid)` | The denial → ruleset join. `null` means the fallback governs it — say that, do not attribute it to a ruleset. |
| `GrantView` | `IsUnscoped` (null `Rulesets`) means *every* ruleset, not *none*. |
| `FallbackView` | `DenyAll` comes from the API. Render the floor; a configuration view that omits it misleads. |
| `CheckResult` | Four lists. Render `Removed`/`Unbound` too — a push is a full replace. |
| `DecisionRow` | `Role` is the validated `appid`. `SourceIp` is **not** an identity. |
| `AuthFailureGroup` | A DECISION row with an empty `Role`: credentials **were** presented and rejected. Not the 407 handshake. |
| `ChallengeConversion` | Challenged vs. authenticated. `NeverConverted` is the probing signal — an observation, not an alert. |
| `ReportFinding` | From `EnforceWouldDeny`. Lead a promotion prompt with **this**, never with how long a ruleset has been in `report`. |
| `TrafficWindow` | An enum, so no page can issue a free-text KQL time range. Default is 24 hours; `TrafficWindows.Parse` narrows on anything unrecognised. |
| `MetricSeries` | `Interval` is one minute and there is nothing finer. Do not present it as live. |
| `EgressPool` | `Capacity` from the prefix length, `InUse` from assigned addresses. Exhaustion means the next node egresses from an address no partner has allowlisted. |

### Design system — the shared partials

The visual specification is two mockups — **open them in a browser**. Where one of them and this
document disagree about layout, the mockup wins.

- [`portal.html`](../../openspec/changes/archive/2026-08-03-management-portal-console/mockups/portal.html)
  — the design language and every surface's layout.
- [`runtime.html`](../../openspec/changes/runtime-path-schematic/mockups/runtime.html) — the
  Runtime surface as one schematic. Scroll past the first card: the two state studies below it are
  what the implementation has to survive.

The stylesheet in `wwwroot/css/portal.css` is a near-verbatim port of both; its header comment
records the three mockup-only rules that were dropped, the one addendum that was added, and the
single deliberate difference in the `.sx-*` block (icons served as files rather than inlined).

The design language is inherited from ZEP (`~/Projects/Zure/zep`, **outside this repository**).
The semantic `allow` / `report` / `open` ramp is the one thing added here — held separate from the
accent hue so policy state reads at a glance rather than requiring the label to be read.

| Component | Usage | Model |
|---|---|---|
| Card | `<console-card eyebrow="Policy" title="Posture" icon="📋">…</console-card>` | tag helper — a partial cannot take child content |
| Page head | `<partial name="_PageHead" model="…" />` | `PageHeadModel(Title, Lede?, Freshness?)` |
| Stat | `_Stat` | `StatModel(Value, Label, Tone, Suffix?)` — `StatTone.Good/Warn/Bad` |
| Pill | `_Pill` | `PillModel(Text, Variant)`; use `PillModel.For(action)` and `PillModel.For(fallback)` |
| Data table | `_DataTable` | `TableModel(Columns, Rows, Scroll, EmptyMessage?)`, `ColumnModel`, `CellModel` |
| Banner | `_Banner` | `BannerModel(Icon, Text)` — text only, encoded |
| Host list | `_HostList` | `HostListModel(Hosts, Added?, Removed?)` |
| Freshness | `_Freshness` | `FreshnessModel.From(freshness)` or `From((label, freshness), …)` |
| Sparkline | `_Sparkline` | `ChartModel` — 46px, for a card footer |
| Metric graph | `_RuntimeGraph` | `ChartModel` — line, fill and a baseline, at whatever `Width`/`Height` the caller sets |
| Metric track | `_RuntimeTrack` | `ChartModel` — line only. For a series that lives at its own maximum, where a fill would read as a progress bar |
| Unavailable | `_Unavailable` | `string?` — renders `Error` as a banner, so a panel that could not be filled never looks like a panel with nothing to report |

Rules for a surface:

- **Load in parallel, and collect failures rather than assigning them.** A surface's reads are
  independent, so `Task.WhenAll` them: serially, first paint costs the *sum* of every upstream
  call instead of the slowest one, and this console reads three of them. `ResponseCache` is
  concurrency-safe and keyed, so overlapping reads share a fetch rather than duplicating it. Use
  `LoadErrors` for the `Error` property — a plain field written from several tasks reports
  whichever failure finished last, and an operator told "denials could not be read" has no way to
  know policy failed too.
- **Fan-out over subjects is bounded.** `Parallel.ForEachAsync` with a small degree, not one task
  per subject: the other end is a metered query service and a ruleset's subject list has no
  ceiling.

- **Never build markup a partial already covers.** Five surfaces inventing their own table is the
  drift this contract exists to prevent.
- **Never compare an action string by hand.** `PillModel.For(RulesetAction)`.
- **Stamp freshness on every panel fed by Azure.** A cached value keeps the timestamp of the
  fetch that produced it — never restamp it as "now".
- **`Scroll = true` for lists that grow without bound** (the ruleset list, denials). Fixed height,
  sticky header, scrolls in place, so what sits below it stays reachable.
- A surface needing a richer cell — a pill in a column, a row with an `hx-get` — writes that table
  directly with the same CSS classes. Do not grow `TableModel` until it can express arbitrary
  markup.

`_Layout.cshtml` names `hx-indicator="#page-progress"` on `<body>`, and htmx inherits it — so
every request the console makes, boosted navigation and panel swaps alike, drives the progress bar
without a surface doing anything. Do not add per-panel spinners on top of it.

Layout comes from `_Layout.cshtml`: sticky 62px header, the pill tab bar as **real routes** under
`hx-boost`, and `body[data-surface]` selecting the per-surface background tint. Set it from a page
with `ViewData["Surface"] = Surface.Traffic.Key`.

### The Runtime surface — one schematic, four swap targets

Runtime is the one surface that is not a grid of cards. It is a single card drawing the path
traffic actually takes — workloads → load balancer → scale set → egress prefix → partner endpoints
— over an instrument deck in which every reading sits in a lane directly beneath the stage it
describes. The column *is* the association; there is no legend and no cross-referencing.

It replaced five panels (fleet, egress addresses, network out, CPU, availability) that each said a
true thing about one resource and none of which said the thing that matters most: **these are
stages of one path, and a number in one of them constrains the others.** Every number the panels
carried is still here.

| Partial | What it is |
|---|---|
| `_RuntimePath` | The card, the grid, the two static caps, the rule and the risers. Owns nothing that reads |
| `_RuntimeStation` | One station of the process line — `StageView` |
| `_RuntimeDuct` | One duct between two stations — `DuctView` |
| `_RuntimeLoadBalancer` | **Swap target.** Inlet duct, LB station, LB lane |
| `_RuntimeFleet` | **Swap target.** LB→VMSS duct, VMSS station, node lane, both gauges, the trend recorder |
| `_RuntimePrefix` | **Swap target.** VMSS→prefix duct, prefix station, prefix lane, outlet duct |
| `_RuntimeConsequence` | **Swap target.** The consequence bar, composed across every stage |
| `_RuntimeRecorder` | The wide throughput strip — `RuntimeChart` |

Four things about it are load-bearing:

- **Each swap target is a `display: contents` wrapper.** It disappears from layout while its
  children are still placed by the schematic's grid, so one htmx target owns a station in the
  process line *and* a lane in the deck even though the grid puts them in different rows. That is
  what preserves the property the five separate panels had: a slow ARM read degrades its own stage
  while the rest of the card keeps rendering.
- **Unread is a state, not a colour choice.** `LampState.Unread` is unlit and hatched — never the
  appearance of a healthy stage, and never reported as zero. This is `Health()`'s refusal to paint
  a node green without a verdict, one level up. `RuntimeTests` pins it.
- **A duct is about entering the stage to its right**, so that stage owns it. A full prefix makes
  the duct into it *constrained*, never *stopped*: it caps growth, it does not stop today's
  requests.
- **The consequence bar renders on a quiet day.** A bar that disappears when nothing is wrong
  trains an operator not to look at it — and it is the accessible carrier of the whole schematic,
  because the ducts are CSS and invisible to assistive technology.

Document order is stage-major (station, then its own lane); the grid places them row-major. Nothing
on the surface is focusable, so the divergence costs no keyboard user a tab sequence today; it
would need revisiting if a stage ever became a link.

The four Azure icons in `wwwroot/img/azure/` are Microsoft's architecture icons, served as files
rather than inlined because each SVG document scopes its own gradient ids — four inline copies on
one page would collide. Provenance and the one deliberate patch are in [`NOTICE`](../../NOTICE).

### Configuration

| Setting | What |
|---|---|
| `CONTROL_PLANE_URL` | Base address of the control-plane API |
| `CONTROL_PLANE_SCOPE` | Token scope; must match the deployment's `EXPECT_AUD` |
| `LOG_ANALYTICS_WORKSPACE_ID` | Workspace the DCR sends `EgressProxy_CL` to |
| `HUB_SUBSCRIPTION_ID`, `HUB_RESOURCE_GROUP` | The only scope the portal's identity has a role on |
| `PROXY_SCALE_SET_NAME`, `EGRESS_IP_PREFIX_NAME`, `PROXY_LOAD_BALANCER_NAME` | Runtime surface targets |
| `AZURE_CLIENT_ID` | Selects the user-assigned managed identity |

Missing configuration degrades the affected panels to empty with a warning in the log. It never
takes down the surfaces that do not need it.
