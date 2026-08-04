## Why

The Runtime surface answers its questions correctly and separately. Five panels — the scale
set, the egress addresses, network out, CPU, availability — each say a true thing about one
resource, and none of them says the thing that matters most: **these are stages of one path,
and a number in one of them constrains the others.**

The consequence is concrete. A fleet of two nodes on a `/31` prefix is at its ceiling: a third
node has no address left to take, would egress from outside the block, and its traffic would be
refused at a partner's edge where this console cannot see it. Today that reading requires an
operator to hold the fleet panel and the address panel in their head at the same time, notice
that `0 spare` is a fleet limit rather than a prefix statistic, and know that the prefix is what
partners allowlist. The sentence is on the surface — it is the last paragraph of a panel about
IP addresses, where it reads as a footnote to a number.

This change redraws the surface as **one schematic**: traffic arriving at the load balancer,
crossing the proxy nodes, leaving through the addresses in the egress prefix. Every number the
five panels carried is kept and attached to the stage it describes, so the relationship between
stages becomes visible rather than inferred.

It was asked for in review of the console and deferred rather than improvised, because the
mockups are this console's visual specification ([D9 of the archived console
change](../archive/2026-08-03-management-portal-console/design.md)) and a diagram that replaces
working panels deserves one first. [`mockups/runtime.html`](mockups/runtime.html) is that
mockup, and it is the visual specification for this change.

## What Changes

- **Replace the five Runtime panels with one card** holding a left-to-right process line —
  workloads → load balancer → scale set → egress prefix → partner endpoints — over an
  instrument deck in which every panel's contents sit in a lane directly beneath its own stage.
- **Promote the load balancer to a stage of its own.** Its data-path and health-probe readings
  are currently two rows in the footer of the fleet card, which is not where the first stage of
  the path belongs.
- **Draw the load-balancer series, not just their latest sample.** Both metrics already arrive
  as full one-hour series and everything but `Latest` is discarded today. Rendering them costs
  no additional query and turns *66%* into *since when*.
- **Give the consequence the width of the path.** "Zero spare" is a fact about the prefix; "the
  fleet cannot grow" is a fact about the path. The sentence moves out of the address panel and
  spans the deck, in four tones — constrained, recoverable, quiet, and unread.
- **Add a state vocabulary the panels did not need**: three lamp states per stage, where unread
  is hatched and unlit rather than coloured; and four duct states, where only a duct carrying
  traffic moves.
- **Keep four independent swap targets**, one per stage plus the consequence, so the property
  that made the panels worth splitting — a slow ARM read degrades its own stage while the rest
  of the card keeps rendering — survives the merge into one card.
- **Vendor the official Azure architecture icons** for Load Balancer, Virtual Machine Scale Set,
  Public IP Prefix and Virtual Network, served as files from `wwwroot` under the existing CSP.

## What Does Not Change

- **No client changes.** `RuntimeClient`, `ArmDirectClient` and `ConsoleData` are untouched, and
  no new Azure query is introduced. This is presentation and derivation over data the surface
  already holds.
- **No new permissions.** The console keeps `Reader` + `Monitoring Reader` and no write role.
- **The other five surfaces**, including `_OverviewRuntime`. Shrinking the schematic into the
  Overview is a separate question and a separate change.
- **The freshness contract.** Every stage keeps its own stamp, because the stages are fed by
  different sources at different ages and one stamp for the card would be the wrong claim.

## Non-goals

- Live data. Azure Monitor's grain is one minute and this change does not alter that; the
  schematic states its recency exactly as the panels did (D4 of the console change).
- A topology view. This is the egress path, not the deployment: no VNet peering, no NSG, no
  spoke. Stages are the four things traffic passes through on its way out.
- Interactivity. Nothing on the schematic is clickable. If a stage should drill into something,
  that is a later change with its own reason.

## Impact

- **Affected specs:** `management-portal`
- **Affected code:** `src/Portal/Pages/Runtime.cshtml`, `Runtime.cshtml.cs`, the
  `_Runtime*.cshtml` partial set (three replaced by six), `wwwroot/css/portal.css`, a new
  `wwwroot/img/azure/`, and `src/Portal.Tests/RuntimeTests.cs`
- **Affected docs:** `src/Portal/README.md`, `docs/control-plane.md` § The management console,
  and `ROADMAP.md` — this change closes the deferred item that asked for it
