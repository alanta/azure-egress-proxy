# Roadmap

Beyond the v1 reference implementation, in rough priority order:

- **Dashboard** — a view over `EgressProxy_CL`: per-workload traffic, denies,
  `report`-mode findings (`EnforceWouldDeny`), fallback usage as a migration backlog.
- **Control-plane API + management portal** — a thin validating API in front of the
  allowlist (per-team self-service: add FQDNs to your own module only, forced `report`
  on new endpoints), replacing direct blob writes.
- **Per-module allowlist blobs** — one blob per team/module with path-scoped RBAC,
  enabling write isolation without ABAC; the renderer stays the trust boundary.
- **Containerized proxy for Kubernetes** — the same binary as a container + Helm chart;
  identity via federated workload identity.
- **Event-driven reload** — Event Grid blob-change push instead of ETag polling, if
  sub-second propagation is ever needed (measured propagation today is ~poll interval).
- **NuGet package** for `EgressProxy.Client` once the API has survived real use.
- **B2pts VM experiment** — try running the proxy on ARM64 burstable `B2pts`
  instances to validate whether the demo workload stays responsive at the
  lowest practical cost.
- **Distinct event type for the 407 pre-auth challenge** — the proxy currently logs the
  credential-less first CONNECT of every tunnel as a `CANONICAL-PROXY-DECISION`
  (`"Client role cannot be determined"`), doubling decision rows. Emitting it as its own
  event type would keep the decision stream clean while preserving the probing signal.
