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
   `Portal.Tests/ScaffoldTests`.
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

The visual specification is
[`mockups/portal.html`](../../openspec/changes/management-portal-console/mockups/portal.html) —
**open it in a browser**. Where it and this document disagree about layout, the mockup wins. The
stylesheet in `wwwroot/css/portal.css` is a near-verbatim port of it; its header comment records
the three mockup-only rules that were dropped and the one addendum that was added.

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
| Metric chart | `_MetricChart` | `ChartModel` — taller, with a baseline, for Runtime |

Rules for a surface:

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

Layout comes from `_Layout.cshtml`: sticky 62px header, the pill tab bar as **real routes** under
`hx-boost`, and `body[data-surface]` selecting the per-surface background tint. Set it from a page
with `ViewData["Surface"] = Surface.Traffic.Key`.

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
