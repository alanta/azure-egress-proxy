# Roadmap

Beyond the v1 reference implementation, in rough priority order.

## Tracked as issues

Well-scoped items have been forked into GitHub issues:

- [#3 — Dashboard over `EgressProxy_CL`](https://github.com/alanta/azure-egress-proxy/issues/3)
  — largely answered by the management console's Traffic and Overview surfaces (per-workload
  denials, `report`-mode findings, traffic attributed to the fallback). What it does not give you
  is a view inside the workspace itself, for someone who has Log Analytics but not the console.
- [#4 — Containerized proxy for Kubernetes](https://github.com/alanta/azure-egress-proxy/issues/4)
- [#5 — Event-driven allowlist reload (Event Grid)](https://github.com/alanta/azure-egress-proxy/issues/5)
- [#6 — Publish `EgressProxy.Client` as a NuGet package](https://github.com/alanta/azure-egress-proxy/issues/6)
- [#7 — B2pts ARM64 burstable VM cost experiment](https://github.com/alanta/azure-egress-proxy/issues/7)

## Still shaping

Larger or underspecified items, kept here until they're ready to become issues:

- **Management portal, the writing half** — the read-only console has shipped
  ([`src/Portal/`](src/Portal/): Overview, Rulesets, Traffic, Lookup, Platform, Runtime, joining
  authored policy, proxy decisions, and runtime state, and closing the denial → owning-ruleset
  loop). It writes nothing: it renders a candidate change, validates it through `:check`, and
  emits the snippet the pipeline applies.

  What remains is **Mode 3 proper** — humans editing rulesets in the app, under per-ruleset RBAC,
  and administering the platform grants through the API rather than by hand. Both are deferred on
  purpose, and together: an editor implies a rule for who may edit *what*, and that user→ruleset
  association exists in no document today. Designing it is the work, not the form. The console was
  scoped to one audience tier precisely so it does not pre-empt that design, and the shape it
  pre-figures — the console as a delegating principal, holding its own grant, with the acting user
  travelling as an audited assertion — is recorded in
  [the change's design record](openspec/changes/management-portal-console/design.md) § D2 to be
  confirmed or overturned rather than inherited by accident. See
  [docs/control-plane.md](docs/control-plane.md) § The management console.
- **Console: the proxy's idle timeout on the Runtime surface.** The panel reports what ARM and
  Azure Monitor expose; `proxyIdleTimeoutInMinutes` is deployment configuration neither of them
  returns, so the console cannot show the stale-tunnel contract that
  [docs/production-hardening.md](docs/production-hardening.md) tells every client to design
  around. Showing it means reading the load-balancing rule and the instance public IP through
  ARM — a client change, small but real, and worth doing because the value differs per
  deployment and hardcoding it would be wrong for all but one.
- **A schematic on the console's Runtime surface** — traffic arriving at the load balancer,
  crossing the nodes, leaving through the addresses in the egress prefix, drawn as one picture
  instead of four panels. It would say what the panels only imply: that these are stages of one
  path, and that a number in one of them constrains the others. Asked for in review, and deferred
  rather than improvised — the mockups are this console's visual specification
  ([design.md § D9](openspec/changes/management-portal-console/design.md)), so a diagram that
  replaces working panels deserves one first. The data is already on the surface; this is a design
  question, not a client change.
- **Console read auditing.** Sign-ins are Entra's record; what an operator looked at is recorded
  nowhere. Cheap to add, and the kind of thing that is missed until it is needed.
- **Per-module allowlist blobs** — one blob per team/module with path-scoped RBAC,
  enabling write isolation without ABAC; the renderer stays the trust boundary.
- **Allow mixing different types of proxies and proxy auth per module.**
- **Allow default outbound connectivity to trusted services** — to reduce the number of
  non-proxied domains in application environment settings.
- **Tighten up network security** — do not allow load balancer bypass, disable spoke
  access to other ports, etc.
