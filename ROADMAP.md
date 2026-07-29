# Roadmap

Beyond the v1 reference implementation, in rough priority order.

## Tracked as issues

Well-scoped items have been forked into GitHub issues:

- [#3 — Dashboard over `EgressProxy_CL`](https://github.com/alanta/azure-egress-proxy/issues/3)
- [#4 — Containerized proxy for Kubernetes](https://github.com/alanta/azure-egress-proxy/issues/4)
- [#5 — Event-driven allowlist reload (Event Grid)](https://github.com/alanta/azure-egress-proxy/issues/5)
- [#6 — Publish `EgressProxy.Client` as a NuGet package](https://github.com/alanta/azure-egress-proxy/issues/6)
- [#7 — B2pts ARM64 burstable VM cost experiment](https://github.com/alanta/azure-egress-proxy/issues/7)

## Still shaping

Larger or underspecified items, kept here until they're ready to become issues:

- **Management portal** — the human half of the control plane (Mode 3): edit rulesets in an
  app with per-ruleset RBAC, and administer the platform grants through the API rather than
  by hand. The validating **control-plane API** underneath it has shipped — per-team pipeline
  self-service, forced `report` at onboard, blob writes only through the API. See
  [docs/control-plane.md](docs/control-plane.md).
- **Per-module allowlist blobs** — one blob per team/module with path-scoped RBAC,
  enabling write isolation without ABAC; the renderer stays the trust boundary.
- **Allow mixing different types of proxies and proxy auth per module.**
- **Allow default outbound connectivity to trusted services** — to reduce the number of
  non-proxied domains in application environment settings.
- **Tighten up network security** — do not allow load balancer bypass, disable spoke
  access to other ports, etc.
