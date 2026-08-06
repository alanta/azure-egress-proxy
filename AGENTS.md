# Agent instructions

## What this repo is

A reference implementation of a **shared egress proxy for Azure** that enforces a
**per-workload FQDN allowlist** on outbound HTTPS. Workloads reach the internet only through
an explicit `CONNECT` proxy (Stripe's Smokescreen, embedded as a Go library); they prove who
they are with their Entra **managed-identity JWT** carried in the password of Basic proxy
auth; policy comes from a **single JSON blob** the proxy hot-reloads; every decision lands in
a **Log Analytics audit table**.

It is a security control. Most of the behaviour that looks odd here is deliberate, and
several defaults are load-bearing — read [§ Invariants](#invariants) before you decide
something is a bug.

## Read before you write

Find the row matching your task and read those documents **first**. They carry the reasoning
behind the code; changing this repo from the code alone tends to break a stated design
decision.

| If you are working on… | Read | Then look at |
|---|---|---|
| Anything at all, first time in the repo | [README.md](README.md), [docs/architecture.md](docs/architecture.md) | — |
| The proxy itself, ACL rendering, reload loop | [docs/allowlist.md](docs/allowlist.md), [docs/architecture.md](docs/architecture.md) | `proxy/` |
| Allowlist schema, semantics, `action` modes, fallback | [docs/allowlist.md](docs/allowlist.md) | `allowlist/allowlist.schema.json` |
| Auth, tokens, JWKS, identity modes, the 407 handshake | [docs/identity.md](docs/identity.md) | `proxy/main.go`, `src/EgressProxy.Client/` |
| Control-plane API, rulesets, RBAC verbs, onboarding | [docs/control-plane.md](docs/control-plane.md), [docs/allowlist.md](docs/allowlist.md) § Write path | `src/ControlPlane/`, `allowlist/rulesets.schema.json` |
| The management console — surfaces, data clients, the design system | [src/Portal/README.md](src/Portal/README.md), [docs/control-plane.md](docs/control-plane.md) § The management console | `src/Portal/`, `src/Portal.Tests/` |
| Logging, DCR, KQL, the audit table | [docs/observability.md](docs/observability.md) | `infra/modules/` |
| Bicep, networking, NSG, VMSS, deployment | [infra/README.md](infra/README.md), [docs/architecture.md](docs/architecture.md) | `infra/`, `scripts/` |
| The .NET client library or sample app | [src/README.md](src/README.md), [docs/identity.md](docs/identity.md) | `src/EgressProxy.Client/`, `src/SampleApp/` |
| Local dev loop (Aspire, Azurite, mock IdP) | [README.md](README.md) § Quickstart — local, [docs/control-plane.md](docs/control-plane.md) § Local development | `src/AppHost/`, `mock-idp/` |
| CI/CD, OIDC, repo variables, releases | [docs/github-setup.md](docs/github-setup.md), [SECURITY_GUIDELINES.md](SECURITY_GUIDELINES.md) | `.github/workflows/` |
| Dependencies, package versions, lock files | [SECURITY_GUIDELINES.md](SECURITY_GUIDELINES.md) § 2 | `Directory.Packages.props` |
| Anything proposed as "we should also…" | [ROADMAP.md](ROADMAP.md), [docs/production-hardening.md](docs/production-hardening.md) | — |

Two documents are easy to miss and answer most "is this a bug?" questions:

- **[docs/production-hardening.md](docs/production-hardening.md)** — every deliberate
  demo-grade simplification, with its production counterpart. If you are about to flag
  something as insecure (public storage endpoint, open sample-app ingress, Basic ACR,
  cloud-init binary fetch), check here first: it is probably a documented trade-off, not an
  oversight.
- **[README.md](README.md) § FAQ / expected behaviours** — observed behaviours that look
  broken and are not. Check here before "fixing" `curl` returning `000`, a
  `CANONICAL-PROXY-AUTH-REQUIRED` row per connection, or `SrcIp` changing within one replica.

## Where the code lives

| Path | What |
|---|---|
| `proxy/` | The proxy. Go, single static binary, embeds Smokescreen. Managed mode (blob watch → render ACL → in-process reload) is in `managed.go` |
| `src/ControlPlane/` | Optional validating write API (Mode 2) — renders `allowlist.json` from rulesets |
| `src/Portal/` | Optional read-only management console (Mode 3, read half) — Razor Pages + vendored htmx; joins policy, decisions, and runtime state. Writes nothing |
| `src/EgressProxy.Client/` | Lift-ready .NET client: proxy + managed-identity credential wiring |
| `src/SampleApp/`, `src/ServiceDefaults/` | Demo workload and shared telemetry/resilience setup |
| `src/AppHost/` | Aspire local stack: proxy + Azurite + mock IdP + sample + control plane + console |
| `mock-idp/` | Local stand-in for the Entra token endpoint and JWKS (Python) |
| `allowlist/` | The allowlist document and the ruleset document, each with a JSON Schema |
| `infra/`, `scripts/` | Bicep (AVM modules) and the deploy/teardown/demo/identity scripts |
| `openspec/` | Spec-driven change workflow; `specs/` holds `control-plane-api` and `ruleset-model` |

## Invariants

Do not weaken these without being asked to, explicitly. Each is a documented decision, not an
accident — if one seems wrong, say so and stop rather than "fixing" it.

**Fail closed.** No reachable config means a deny-all ACL, not an open proxy. Once the proxy
has config it holds last-known-good through transient blob outages. An absent or empty
`fallback` is deny-all.

**Defaults never widen.** An omitted, empty, or unrecognised `action` normalises to
`enforce`; `report` and `open` are never implicit. The control plane never lowers an existing
ruleset's action on its own.

**Identity comes from the validated JWT, never the network.** The allowlist keys on the
`appid`/`azp` claim. Source IP is not an identity — on VNet-integrated Container Apps a single
replica's traffic arrives from multiple rotating node IPs.

**Explicit CONNECT only.** No transparent-proxy fallback. The proxy resolves the destination
it was *asked for by name*, which is what makes SNI spoofing a non-issue. Smokescreen's
anti-SSRF blocking of private ranges stays on, which is why `NO_PROXY` is load-bearing on the
client side.

**Enforcement is the NSG, not proxy opt-in.** `HTTPS_PROXY` is honour-system; the
deny-Internet NSG floor is what makes the proxy the only route out.

**Control-plane authorization.** Writer ≠ subject (a workload can never widen its own
allowlist). Subjects are write-once at onboard and belong to at most one ruleset. Pushes are
audited full-replace.

**The control-plane API is a machine interface.** Its shape is owed to pipelines, not to a UI —
do not reshape an endpoint, a status code, or a response body to make a screen easier to build.
Human identity lives in the console (`src/Portal/`) and nowhere else; the API's identity model
stays one RS256/JWKS check over service-principal tokens. The console reads that API and renders
a *candidate* change; applying policy stays with the audited machine path, so it holds `Reader` +
`Log Analytics Reader` and no write role anywhere, and `POST /rulesets/{name}:check` — the dry run —
is the only non-`GET` it may ever make. Giving the console a write is not a UI change; it is a
change to who can widen an allowlist.

**The rendered `allowlist.json` schema is a contract.** Mode 2 renders one `modules[]` entry
per subject precisely so the proxy does not have to change. Do not alter the rendered shape
to suit the control plane.

**Do not drop the pre-auth rows at the DCR.** A stream of credential-less `CONNECT`s that never
converts to an authenticated row is what probing looks like. The 407 handshake is *reclassified*
as `CANONICAL-PROXY-AUTH-REQUIRED`, never discarded — filter it in queries, not at ingestion.
The split keys on whether the client sent `Proxy-Authorization` at all, so a rejected credential
stays a `CANONICAL-PROXY-DECISION` denial; do not widen it to a `DecisionReason` match, which
would hide authentication failures. The proxy also rewrites Smokescreen's opaque
`Client role cannot be determined` with the role func's actual error — keep identity failures
self-explaining in the audit row itself, and never let a client-supplied header reach it.

## Commands

```bash
# .NET — the sequence CI runs
dotnet restore AzureEgressProxy.slnx --locked-mode
dotnet build AzureEgressProxy.slnx --configuration Release --no-restore
dotnet test AzureEgressProxy.slnx --configuration Release --no-build

# after any NuGet package change, regenerate lock files (else CI fails NU1004)
dotnet restore AzureEgressProxy.slnx --force-evaluate

# Go proxy
cd proxy && go build ./... && go test ./...

# Local end-to-end stack, then exercise allow/deny
dotnet run --project src/AppHost/AppHost.csproj
curl http://localhost:5028/try/allowed
curl http://localhost:5028/try/denied
```

Aspire package versions are **not** in `Directory.Packages.props` — they come from
`Sdk="Aspire.AppHost.Sdk/<version>"` in `src/AppHost/AppHost.csproj`.

## Conventions

- **[SECURITY_GUIDELINES.md](SECURITY_GUIDELINES.md) is binding** for anything under
  `.github/workflows/` or any dependency change. Its review checklist is the last thing to
  run before reporting work complete. The rules broken most often by accident: no `${{ }}`
  inside a `run:` body, actions pinned to 40-char SHAs, NuGet versions only in
  `Directory.Packages.props`, lock files committed alongside the change that caused them.
- **Docs are part of the change.** These documents explain *why*, and they are the reason the
  repo is navigable. If you change behaviour, update the document that describes it in the
  same commit — especially [docs/allowlist.md](docs/allowlist.md) for schema or semantics and
  [docs/control-plane.md](docs/control-plane.md) for API surface.
- **Larger changes use the OpenSpec workflow** in `openspec/`. Check `openspec/specs/` for an
  existing spec covering your area before designing something new.
- **Verify, don't assume.** Run the command, read the output, report what it actually said —
  including when it failed.
