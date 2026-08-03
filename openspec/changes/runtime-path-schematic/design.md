# Design — the Runtime surface as one schematic

**Mockup:** [`mockups/runtime.html`](mockups/runtime.html) — self-contained, open it in a
browser. It is the visual specification for this change, and where it and this document
disagree about layout, **the mockup wins**. It embeds `src/Portal/wwwroot/css/portal.css`
verbatim and renders its charts with `ChartModel`'s own geometry, so the shapes in it are the
shapes the server will emit. `mockups/build.py` regenerates it; the generator is provenance,
not a dependency of the implementation.

The mockup carries three state studies — a healthy fleet at its address ceiling, a node out of
the pool with headroom to replace it, and Azure Resource Manager not answering. The second and
third exist because the panels this change replaces were good at one thing the picture must not
lose: never presenting a source it cannot read as a healthy one.

---

## The layout

```
 ┌ EGRESS PATH ──────────────────────────────── FLEET JUST NOW · MONITOR JUST NOW ┐
 │  Both nodes healthy — and every address in the prefix already assigned         │
 │                                                                                │
 │   ┌────┐  ═══▶  ┌─────────┐ ═══▶ ┌─────────┐ ═══▶ ┌─────────┐ ═══▶  ┌────┐     │  ← process line
 │   │vnet│ :4750  │   LB  ● │ POOL │ VMSS  ● │ FULL │PREFIX ● │  /31  │ 🌐 │     │
 │   └────┘        └─────────┘      └─────────┘      └─────────┘       └────┘     │
 │  ───────────────────┬───────────────┬───────────────┬────────────────────────  │
 │                     ┆               ┆               ┆                          │  ← instrument
 │              data path/probes   nodes, CPU,     addresses,                      │    deck
 │              + sparklines      availability     legend                          │
 │                                                                                │
 │  ┌──────────────────────────────────────────────────────────────────────────┐  │
 │  │ ⛔ The prefix is the fleet's ceiling. …                                   │  │  ← consequence
 │  └──────────────────────────────────────────────────────────────────────────┘  │
 │  ┌──────────────────────────────────────────────────────────────────────────┐  │
 │  │ THROUGHPUT LEAVING THE PREFIX  0.2 MB/min  ∿∿∿∿∿∿∿∿∧∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿   │  │  ← trend recorder
 │  └──────────────────────────────────────────────────────────────────────────┘  │
 └────────────────────────────────────────────────────────────────────────────────┘
```

The column *is* the association between a stage and its instruments. No legend, no
cross-referencing, no repeated headings — a dashed instrument riser ties each lane up to the
stage it belongs to, and that is the whole navigational apparatus.

---

## D1 — One card, four swap targets

The three panels this change merges are three htmx targets on three handlers, and
`Runtime.cshtml.cs` states why: *"ARM being slow degrades the fleet card while the metric cards
keep rendering, which for a console whose job is to be readable during an incident is the whole
point."* Merging them into one card must not cost that.

It does not have to. A wrapper with `display: contents` disappears from layout while its
children are still placed by the schematic's grid, so **one swap target can own a station in
the process line and a lane in the deck** even though the two sit in different grid rows:

```html
<div hx-get="/Runtime?handler=PrefixStage" hx-trigger="every 60s" hx-swap="outerHTML"
     style="display:contents">
    <div class="sx-pipe">…</div>       <!-- grid row 1, col 6 -->
    <div class="sx-station a-pip">…</div>   <!-- grid row 1, col 7 -->
    <div class="sx-lane a-pip">…</div>      <!-- grid row 3, col 7 -->
</div>
```

The wrapper is a plain `div` with no semantics, which is the case where `display: contents` is
uncontroversial — the accessibility problems it has had historically are about elements whose
role would be dropped, and this one has none.

**Four targets:**

| Target | Owns | Sources |
|---|---|---|
| `LoadBalancerStage` | inlet duct, LB station, LB lane | Azure Monitor — `VipAvailability`, `DipAvailability` |
| `FleetStage` | LB→VMSS duct, VMSS station, node lane, CPU + availability gauges, trend recorder | ARM instance view; Azure Monitor — CPU, `VmAvailabilityMetric`, network out |
| `PrefixStage` | VMSS→prefix duct, prefix station, prefix lane, outlet duct | ARM prefix; ARM instance view for address attribution |
| `Consequence` | the consequence bar | everything above |

The static caps — the workloads inlet and the partner-endpoints outlet — belong to the parent
page and never swap.

## D2 — A stage owns the duct on its left

Duct state is derived from *two* stations, which makes it the one element with no obvious
owner. The rule that resolves it: **a duct is about entering the stage to its right**, so the
stage on the right owns it. The prefix additionally owns the trailing duct out to the partner
endpoints, because there is no stage beyond it.

This is not an arbitrary tiebreak — it matches what the ducts say. `FULL` sits between the
scale set and the prefix, and it is a fact about the *prefix*. `2 OF 3` sits between the load
balancer and the scale set, and it is a fact about the *scale set*.

## D3 — The consequence spans the deck, in four tones

`PoolConsequence` today switches on `Pool` alone and renders inside the address panel. It moves
to the foot of the card and composes across stages:

| Tone | When | What it says |
|---|---|---|
| `bad` | pool exhausted | the prefix is the fleet's ceiling; a further node egresses from outside the block |
| `ok` | fleet degraded, pool has room | the fleet can recover on its own; the replacement lands inside the block |
| plain | everything readable and unremarkable | how many nodes the fleet can still add |
| `dim` | a source is unread | how many stages are unread, and that this is not a claim about their health |

**The quiet-day tone is deliberate and must not be dropped.** A bar that disappears when
nothing is wrong trains an operator not to look at it, and this bar is the accessible carrier
of the schematic's thesis: the ducts are CSS decoration and are invisible to assistive
technology, so the relationship they draw exists in the accessibility tree only as this
sentence.

## D4 — Three lamp states, because unread is not healthy

`Health()` already refuses to paint a node green without a verdict, and
`Health_never_reads_as_healthy_without_a_verdict` guards it. The schematic needs the same
refusal one level up, at the stage: **lit green, lit amber or red, or unlit and hatched.** A
stage the console could not read is never coloured, its station is drawn with a dashed border
over a hatch, and its Azure icon is desaturated.

This is the invariant the change most risks breaking — a picture wants to be complete — so it
is promoted from a code comment to a spec requirement and a test.

## D5 — Motion is a reading, not decoration

```
   ▶▶▶▶▶▶▶▶   blue,  moving   traffic passing
   ▶▶▶▶▶▶▶▶   amber, moving   passing, but constrained
   ▨▨▨▨▨▨▨▨   red,   still    nothing passing
   ▨▨▨▨▨▨▨▨   grey,  still    nothing known
```

The distinction between the first two rows is load-bearing. **A full prefix caps growth; it
does not stop today's requests.** Hatching that duct red would say traffic has stopped, which
is false and is exactly the kind of over-claim a diagram makes easily. Suppressed under
`prefers-reduced-motion`, where the ducts hold their colour and stop moving.

## D6 — Official Azure icons, as files rather than inline

Four icons: Load Balancer, Virtual Machine Scale Set, Public IP Prefix, Virtual Network. Served
from `wwwroot/img/azure/` and referenced with `<img>`, not inlined into the markup, for one
concrete reason: **each SVG document scopes its own gradient ids**, so four icons on one page
cannot collide. Inlining them requires namespacing every `id` and `url(#…)` by hand, which the
mockup generator does and which nothing in the Razor pipeline would.

`img-src 'self' data:` already permits this; the CSP needs no change.

Microsoft's own `Public-IP-Prefixes.svg` ships two `linearGradient` elements with no stops and
no base gradient, and renders two thirds invisible. The vendored copy carries the fix and a
comment saying so — which is its own small argument for treating these as vendored assets with
recorded provenance rather than as opaque downloads.

## D7 — DOM order is stage-major, visual order is row-major

A consequence of D1 worth stating on its own. The grid places stations in row 1 and lanes in
row 3, but the document order is *station, lane, station, lane* — so a screen reader hears each
stage followed immediately by its own instruments, which is the order the schematic is arguing
for anyway. Nothing on the surface is focusable, so the divergence between visual and document
order costs no keyboard user a sensible tab sequence today; it would need revisiting if a stage
ever became a link.

## D8 — What stays exactly where it is

Everything not named above. `Slots`, `Health`, `PowerColour`, `NodeName`, `ImageLabel`,
`TryAsync`, the per-source error isolation, the 60-second poll against the server-side cache,
and all seven existing tests. The change is a re-layout with four new derivations on top; if it
starts rewriting the model's arithmetic, something has gone wrong.

## Open

- **The trend recorder is a scale-set metric with a path-level label.** "Throughput leaving the
  prefix" is what an operator wants to read; `Network Out Total` on the VMSS is what Azure
  measures. They are the same bytes, which is why the copy stands, but the recorder is owned by
  `FleetStage` rather than by the prefix and the two do not agree on the surface. Left as-is
  deliberately; worth revisiting if it ever confuses anyone.
- **`Consequence` re-reads every source on each poll.** All of them go through the response
  cache, so this costs no Azure call — but it does mean four targets polling three caches, and
  if the cache is ever bypassed this becomes the first place to look.
