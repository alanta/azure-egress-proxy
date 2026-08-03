## Why

The egress platform has no human surface. Policy is authored by pipelines through the
control-plane API; enforcement decisions land in `EgressProxy_CL`; the deployment's runtime
state lives in ARM and Azure Monitor. Three stores, no view that joins them.

The platform team therefore answers routine questions by hand: *which ruleset governs the
appid in this denial?* is a KQL query, a lookup in `rulesets.json`, and a guess. *What does
this new workload still need before it can leave `report`?* is a second query nobody has
memorised. *Is the proxy healthy, and how much of the egress IP prefix is in use?* is the
Azure portal, in a different tab, for a resource most people cannot name.

This change adds a **read-only management console** for the platform team: one place that
visualises the proxy's configuration and its operational status, and closes the
denial → owning-ruleset loop that no component owns today.

It is deliberately *not* Mode 3. Nothing here writes policy. The management portal of the
roadmap — humans editing rulesets under per-ruleset RBAC — remains deferred, and this change
is scoped so that it does not pre-empt that design.

## What Changes

- **Add the portal as a new optional service** (`src/Portal/`), deployed as a second container
  app gated on `deployPortal`, mirroring the existing `deployControlPlane` pattern. It is a
  backend-for-frontend: it holds the Azure permissions and queries three sources on the
  user's behalf.
- **Built as ASP.NET Core Razor Pages with htmx**, server-rendered, with htmx vendored as a
  single file. No npm and no build step, so the dependency rules that govern this repo stay
  uneventful, and the portal runs as one container in the shape `src/ControlPlane/` already
  uses. Charts are server-rendered SVG.
- **The portal is the only component that understands human identity.** The control-plane API
  stays a machine interface — one RS256/JWKS check over service-principal tokens, unchanged.
  The portal calls it with its own managed identity.
- **Six read-only surfaces**: Overview, Rulesets, Traffic, Lookup, Platform, Runtime.
- **Close the denial → ruleset loop.** The audit table's `Role` column *is* the workload
  `appid`, which is exactly `subjects[].appid`, so denial rows join to the ruleset that governs
  them. Every denial in the console resolves to its owning ruleset and offers a prefilled
  dry-run of the change that would allow it.
- **Read-only, with an escape hatch.** The console composes a candidate change, validates it
  through `POST /rulesets/{name}:check`, and emits a copyable `curl`/pipeline snippet. The
  human applies it through the existing audited machine path. The portal never writes.
- **Extend the control-plane API with two read endpoints**, `GET /grants` and
  `GET /fallback`. Both are already in `State` and reachable through no endpoint today.
  `fallback` is load-bearing for the console's purpose: it is the platform-owned baseline every
  unmatched source lands on, and a view of "the proxy configuration" that omits the deny-all
  floor is a misleading one. Both are auth-only, no verb, consistent with reads-are-open.
- **Surface the state document's `lastModified` and ETag on control-plane reads.** The blob
  already carries a modification date; the portal reaches the state through the API rather
  than the blob, so the API passes it through. This is document-scoped — any ruleset write
  moves it — which is enough to answer *"when did the configuration last change?"* on the
  Overview. Per-ruleset last-modified needs a stamp the model does not have and is deferred
  to [#33](https://github.com/alanta/azure-egress-proxy/issues/33).
- **Runtime status comes from Azure**, not from the proxy: ARM for configuration (VMSS
  capacity and instance view, public-IP-prefix size and consumption), Azure Monitor for
  metrics (network in/out, CPU, VM availability, ILB data-path availability). The portal's
  managed identity holds `Reader` + `Monitoring Reader` on the hub resource group.

## Audience

**The platform team only.** One tier, no per-user scoping.

The proxy stays a black box to everyone else: workload teams keep the pipeline and
`:check` in CI. Serving workload engineers would require a user→ruleset association that
exists in no document today — and designing that association *is* the per-ruleset RBAC model
this change is explicitly deferring. One tier keeps the deferral honest.

## Non-Goals

- Any write to `rulesets.json` — no edit, promote, bind, onboard, or offboard.
- `grants` administration through the API. The API reads grants here; it still never writes them.
- Per-user or per-ruleset authorization. `acl.edit`/`push`/`admin` stay dormant.
- Workload-engineer self-service views.
- Approval or request workflows.
- **Ruleset change history** — deferred to
  [#33](https://github.com/alanta/azure-egress-proxy/issues/33). There is no queryable change
  feed to render, and building one is a control-plane concern, not a portal one.
- **Drift detection between `rulesets.json` and `allowlist.json`.** Keeping the rendered
  allowlist in sync is the control plane's guarantee. The portal is not a debugger for the
  control plane, and a drift panel would quietly relocate that responsibility.
- Any change to the proxy. It exposes no HTTP admin surface and gains none here.

## Impact

- **New:** `src/Portal/` (Razor Pages + htmx), `infra` parameter `deployPortal` and a container
  app, a portal user-assigned identity with `Reader` + `Monitoring Reader` on the hub RG, a CI
  build/publish target for the portal image, and a vendored pinned copy of htmx.
- **Modified:** `src/ControlPlane/` — two read endpoints and `lastModified`/ETag on read
  responses, all read-path; `docs/control-plane.md`; `src/AppHost/` for the local loop.
- **Unchanged:** the proxy, both JSON schemas, the rendered `allowlist.json` contract, the
  control-plane **write path** in its entirety, and every existing write invariant.
