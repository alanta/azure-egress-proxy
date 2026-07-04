# azure-egress-proxy

A reference implementation of a **shared egress proxy for Azure** that enforces a
**per-workload FQDN allowlist** on outbound HTTPS — the "control egress / prevent data
exfiltration" pattern, without Azure Firewall.

Built on [Stripe's Smokescreen](https://github.com/stripe/smokescreen) (embedded as a Go
library) with three additions:

1. **Workload identity via Entra managed identity** — each workload proves who it is by
   presenting its managed-identity JWT in the password of HTTP Basic **proxy** auth on the
   `CONNECT`. The proxy validates the token (RS256/JWKS, `iss`/`aud`/`exp`) and applies that
   workload's allowlist. No client certs, no secrets. See [docs/identity.md](docs/identity.md).
2. **Allowlist as config-as-code** — a single JSON blob in a locked-down storage account;
   the proxy polls the blob ETag via its own managed identity and hot-reloads in ~5 s.
   Fail-closed by construction. See [docs/allowlist.md](docs/allowlist.md).
3. **A first-class audit trail** — one structured JSON line per egress decision, shipped to
   Log Analytics (`EgressProxy_CL`) with the workload identity on every row.
   See [docs/observability.md](docs/observability.md).

The repo contains everything to see it work:

| Piece | Where |
|---|---|
| The proxy (Go, single static binary) | [`proxy/`](proxy/) |
| Hub + spoke Azure deployment (Bicep, AVM modules) | [`infra/`](infra/), [`scripts/`](scripts/) |
| Sample workload on Azure Container Apps | [`src/SampleApp/`](src/) |
| Lift-ready .NET client library (proxy + credential wiring) | [`src/EgressProxy.Client/`](src/) |
| Local dev loop (Aspire: proxy + Azurite + mock IdP + sample) | [`src/AppHost/`](src/) |
| The allowlist (published to the blob by CI) | [`allowlist/allowlist.json`](allowlist/allowlist.json) |
| GitHub Actions (CI, release, deploy, allowlist publish) | [`.github/workflows/`](.github/workflows/) |

## Why explicit CONNECT, not a transparent proxy?

With an explicit CONNECT proxy, **the proxy resolves and dials the destination it was asked
for by name**. A compromised workload cannot SNI-spoof its way to an attacker IP under an
allowed hostname — the class of bypass that defeats transparent SNI-peeking proxies. The
workload side is just `HTTPS_PROXY`/`NO_PROXY`; an NSG that denies direct Internet egress
makes the proxy the *only* way out, so ignoring it fails closed rather than leaking.

See [docs/architecture.md](docs/architecture.md) for the full design and its trade-offs.

## Quickstart — local (Docker + .NET SDK only)

1. Start the Aspire stack (Azurite + mock-idp + proxy + SampleApp):
   `dotnet run --project src/AppHost/AppHost.csproj`
2. Hit the demo endpoints:
   - `curl http://localhost:5028/try/allowed`
   - `curl http://localhost:5028/try/denied`
3. Edit `allowlist/allowlist.json`, then re-seed:
   `ALLOWLIST_CONNECTION_STRING='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;' ALLOWLIST_FILE="$PWD/allowlist/allowlist.json" dotnet run --project src/AppHost/AllowlistSeeder/AllowlistSeeder.csproj`
4. Wait ~5–10s (`POLL_SECONDS=5`) and hit the endpoint again to watch allow/deny flip.

Proxy decision logs are JSON lines in the proxy container output:
`docker logs $(docker ps --format '{{.Names}}' | grep '^proxy-' | head -n1)`.

For report-mode onboarding, set a module `"action": "report"` with a host omitted from
`allowed_hosts`; traffic is allowed and the decision log includes
`"enforce_would_deny": true`.

Local identity note: mock-idp supports `GET /token?appid=<id>` as a local-trust shortcut
so the sample can request its own identity explicitly in Aspire (standing in for the
platform guarantee in Azure).

## Quickstart — Azure

One command deploys the whole demo (hub + spoke, proxy VMSS, sample app behind Front
Door): `./scripts/deploy.sh` — see [infra/README.md](infra/README.md) for parameters,
the monthly cost table, and `./scripts/teardown.sh` (the off switch). To run it from
GitHub Actions instead, set up OIDC per [docs/github-setup.md](docs/github-setup.md) and
use the **Deploy** workflow. Then `./scripts/demo.sh` exercises allow/deny and prints the
KQL for the audit trail in `EgressProxy_CL`.

## FAQ / expected behaviours

These all showed up during live validation — they're normal:

- **`curl` against a denied host shows `000` / exit 56.** The proxy denies the CONNECT
  with `407` (its JWT-auth mode surfaces denies as `407 Proxy Authentication Required`);
  curl reports the aborted tunnel, not the status. The status is in the proxy log (and in
  `.NET`, in the `HttpRequestException`).
- **The denied-request error message varies** (`407`, a `403 tunnel failure`, or a
  timeout/cancellation). Resilience handlers (e.g. `Microsoft.Extensions.Http.Resilience`)
  retry the failed tunnel; whichever attempt's failure surfaces last is what you see. The
  outcome is stable: the request never leaves the network.
- **Half the decision log is `"Client role cannot be determined"`.** That's the HTTP 407
  proxy-auth handshake — clients send the first CONNECT of every connection without
  credentials. See [docs/observability.md](docs/observability.md) for the KQL filter and
  why you shouldn't drop those rows.
- **`SrcIp` in the audit log rotates between subnet IPs** for a single Container App
  replica — ACA infrastructure nodes carry the egress. Identity comes from the JWT
  (`Role` column), never the source address.
- **The proxy starts deny-all until the allowlist blob is seeded** (fail-closed);
  `deploy.sh` seeds it as its last step.
- **The first request after ~15 idle minutes is slow (or one 504).** The sample app
  scales to zero, so the first request pays a cold start. Retry.
- **Telemetry exporters need a `NO_PROXY` carve-out.** Anything that honours
  `HTTPS_PROXY` without carrying proxy credentials (like the App Insights exporter) will
  be denied — platform telemetry is deliberately routed direct and allowed at the NSG
  (`AzureMonitor` service tag) instead.

## Roadmap

Dashboard, control-plane API + management portal, and a containerized proxy for
Kubernetes are planned — see [ROADMAP.md](ROADMAP.md).

## License

MIT — see [LICENSE](LICENSE). Embeds Stripe Smokescreen (MIT); see [NOTICE](NOTICE).
