# Roadmap

Beyond the v1 reference implementation, in rough priority order.

## Tracked as issues

Well-scoped items have been forked into GitHub issues:

- [#3 — Dashboard over `EgressProxy_CL`](https://github.com/alanta/azure-egress-proxy/issues/3)
- [#4 — Containerized proxy for Kubernetes](https://github.com/alanta/azure-egress-proxy/issues/4)
- [#5 — Event-driven allowlist reload (Event Grid)](https://github.com/alanta/azure-egress-proxy/issues/5)
- [#6 — Publish `EgressProxy.Client` as a NuGet package](https://github.com/alanta/azure-egress-proxy/issues/6)
- [#7 — B2pts ARM64 burstable VM cost experiment](https://github.com/alanta/azure-egress-proxy/issues/7)
- [#8 — Distinct event type for the 407 pre-auth challenge](https://github.com/alanta/azure-egress-proxy/issues/8)

## Still shaping

Larger or underspecified items, kept here until they're ready to become issues:

- **Control-plane API + management portal** — a thin validating API in front of the
  allowlist (per-team self-service: add FQDNs to your own module only, forced `report`
  on new endpoints), replacing direct blob writes.
- **Per-module allowlist blobs** — one blob per team/module with path-scoped RBAC,
  enabling write isolation without ABAC; the renderer stays the trust boundary.
- **Allow mixing different types of proxies and proxy auth per module.**
- **Allow default outbound connectivity to trusted services** — to reduce the number of
  non-proxied domains in application environment settings.
- **Tighten up network security** — do not allow load balancer bypass, disable spoke
  access to other ports, etc.
