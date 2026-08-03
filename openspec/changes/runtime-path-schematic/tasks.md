One worker, sequential. The change is small enough that parallelising it would cost more in
contract-writing than it saves, and groups 3–5 all touch the same two files.

Before starting, read `proposal.md` and `design.md`, and **open
[`mockups/runtime.html`](mockups/runtime.html) in a browser** — it is the visual specification,
and it wins over prose where the two disagree. Scroll past the first card: the two state studies
below it are what the implementation has to survive.

---

## 1. Assets

- [ ] 1.1 Vendor the four icons from `mockups/icons/` into `src/Portal/wwwroot/img/azure/` as
      `load-balancer.svg`, `vm-scale-set.svg`, `public-ip-prefix.svg`, `virtual-network.svg`
- [ ] 1.2 Keep the patch to `public-ip-prefix.svg` — Microsoft's original ships two
      `linearGradient` elements with no stops and renders two thirds invisible — and comment it
      in the file so the next person does not "fix" it back
- [ ] 1.3 Record provenance and licensing in the repo's notice/attribution surface: these are
      Microsoft's Azure architecture icons, licensed for architecture diagrams and documentation
- [ ] 1.4 Confirm no CSP change is needed (`img-src 'self' data:` already covers `<img>` from
      `wwwroot`) — and if one turns out to be, treat it as a `SECURITY_GUIDELINES.md` review item

## 2. Derivations in the page model

Pure functions on `RuntimeModel`, each testable without a render. Nothing here reaches Azure.

- [ ] 2.1 `LampState` — `Ok` / `Warn` / `Bad` / `Unread`, derived per stage; unread is a state,
      not a colour choice (design.md D4)
- [ ] 2.2 A stage view model: tag, resource name, icon, headline value, unit, sub-line, lamp,
      and whether it is unread. Reshape `FleetTitle` and `PoolTitle` into value + unit rather
      than a sentence
- [ ] 2.3 A load-balancer stage headline, from the two signals `LoadBalancer` already carries
- [ ] 2.4 Duct state — chip text plus one of the four tones in design.md D5 — with each stage
      owning the duct on its left, and the prefix additionally owning the outlet duct (D2)
- [ ] 2.5 Generalise `PoolConsequence` into a card-level consequence composing fleet, pool and
      readability, in the four tones of design.md D3. **Keep the quiet-day tone** — the bar must
      render when nothing is wrong
- [ ] 2.6 Split `Charts`: network out becomes the wide trend recorder, CPU and VM availability
      become the two small gauges in the node lane. They are different shapes now, not one list
- [ ] 2.7 Keep the full `MetricSeries` on `LoadBalancerSignal` so the readouts can draw the
      series instead of only `Latest` — the data is already fetched and discarded today

## 3. Tests

- [ ] 3.1 A stage whose source is unread never yields a lamp state used for a healthy stage —
      the stage-level counterpart to `Health_never_reads_as_healthy_without_a_verdict`
- [ ] 3.2 An unread stage is not reported as reporting zero
- [ ] 3.3 The consequence composes: exhausted pool → constrained; degraded fleet with spare
      addresses → recoverable; everything readable and unremarkable → still renders, with the
      spare-node count; any source unread → unread, and neither constrained nor unconstrained
- [ ] 3.4 Duct tone: a full prefix constrains without stopping — the duct between the scale set
      and the prefix is the constrained tone, not the stopped one (design.md D5)
- [ ] 3.5 The seven existing `RuntimeTests` still pass unmodified. If one needs changing, stop —
      the change has started rewriting arithmetic it was supposed to leave alone

## 4. Markup

- [ ] 4.1 Replace `_RuntimeFleet`, `_RuntimePool` and `_RuntimeCharts` with the partial set the
      mockup implies: the path container, a stage, a duct, the three lanes, the consequence bar,
      the trend recorder
- [ ] 4.2 Wire the four swap targets of design.md D1 — `LoadBalancerStage`, `FleetStage`,
      `PrefixStage`, `Consequence` — each a `display: contents` wrapper over children the grid
      places, each on its own 60-second trigger against the server-side cache
- [ ] 4.3 Emit document order stage-major (station, then its lane) while the grid places them
      row-major (design.md D7)
- [ ] 4.4 Keep a freshness stamp per stage; do not collapse them into one stamp for the card
- [ ] 4.5 Update `Runtime.cshtml` — the page head comment about where the freshness stamp lives
      is still true and should survive

## 5. Styling

- [ ] 5.1 Port the `.sx-*` rules from the mockup into `wwwroot/css/portal.css`, in the mockup's
      order, with its comments
- [ ] 5.2 Update the stylesheet's header comment: it currently names the archived
      `management-portal-console` mockup as its sole source and now has two
- [ ] 5.3 Verify the narrow layout below 1100px — ducts and caps drop, each stage stacks above
      its own instruments
- [ ] 5.4 Verify `prefers-reduced-motion`: ducts hold their colour and stop moving

## 6. Verify

- [ ] 6.1 `dotnet build AzureEgressProxy.slnx --configuration Release` then
      `dotnet test AzureEgressProxy.slnx --configuration Release --no-build`
- [ ] 6.2 Run the local stack (`dotnet run --project src/AppHost/AppHost.csproj`) and open
      Runtime. Compare against the mockup's first card side by side
- [ ] 6.3 Force the unread state — deny the portal's identity ARM, or point it at a
      non-existent scale set — and confirm the stages hatch rather than reporting zero
- [ ] 6.4 Confirm the console still issues no write: `ReadOnlyTests` unchanged and passing

## 7. Documentation

- [ ] 7.1 `src/Portal/README.md` — the Runtime surface description, and the partial set
- [ ] 7.2 `docs/control-plane.md` § The management console — the surface summary
- [ ] 7.3 `ROADMAP.md` — delete the deferred "A schematic on the console's Runtime surface"
      item; this change is it
- [ ] 7.4 Run the `SECURITY_GUIDELINES.md` review checklist before reporting the work complete
