# Azure deployment (WP5)

This directory deploys the full hub/spoke demo with one command:

```bash
scripts/deploy.sh
```

## What gets deployed

- **Hub RG**: proxy subnet, internal LB, VMSS (Ubuntu 24.04 arm64), UAMI, public IP prefix, allowlist storage, Log Analytics + `EgressProxy_CL` table + DCR + AMA.
- **Spoke RG**: ACA environment (workload profiles), NSG egress floor, sample app (exposed on its ACA external ingress), a Basic ACR hosting the sample app image.
- **Cross-RG**: hub↔spoke peering and private DNS zone `egress.internal` with `proxy` A record.

## Parameters

`infra/main.bicep` key parameters:

| Parameter | Default | Purpose |
|---|---|---|
| `location` | _(required)_ | Azure region for both RGs |
| `hubResourceGroupName` | `rg-egress-hub` | Hub RG |
| `spokeResourceGroupName` | `rg-egress-spoke` | Spoke RG |
| `namePrefix` | `egress` | Resource naming prefix |
| `deployerPrincipalId` | _(required)_ | Blob write RBAC target |
| `tenantId` | _(required)_ | Entra tenant used for JWT validation |
| `jwksUrl` | _(required)_ | JWKS endpoint for proxy JWT verification |
| `expectIss` | _(required)_ | Expected JWT issuer |
| `expectAud` | _(required)_ | Expected JWT audience |
| `proxyBinaryUrl` | latest release URL | linux-arm64 proxy binary URL |
| `proxyBinarySha256` | _(required)_ | SHA256 for binary integrity check |
| `vmAdminPublicKey` | _(required)_ | SSH public key for break-glass VM access |
| `sampleAppImage` | `mcr.microsoft.com/dotnet/samples:aspnetapp` | Sample app image (deploy.sh overrides this with the ACR-imported release image) |
| `containerRegistryName` | `''` | Existing ACR in the spoke RG hosting the sample image; empty disables ACR wiring |
| `proxyVmSku` | `Standard_D2pls_v6` | VMSS instance SKU |
| `proxyInstanceCount` | `2` | VMSS instance count |
| `proxyPublicIpPrefixLength` | `31` | Known egress CIDR size |

## Identity bootstrap

`scripts/setup-identity.sh` creates or reuses the proxy app registration and writes:

- `infra/identity.generated.json`

It uses **token version v2** (`requestedAccessTokenVersion=2`) and emits:

- `EXPECT_AUD`: app registration client ID (GUID)
- `EXPECT_ISS`: `https://login.microsoftonline.com/<tenant>/v2.0`
- `JWKS_URL`: `https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys`

## Scripts

- `scripts/setup-identity.sh`: idempotent app registration + generated identity file.
- `scripts/deploy.sh`: runs identity setup, creates a Basic ACR and imports the sample-app image from GHCR (set `GHCR_USERNAME`/`GHCR_TOKEN` while the source image is private, or `SAMPLE_APP_IMAGE` to skip the ACR), deploys infra, patches `allowlist/allowlist.json` with sample-app MI client ID, uploads blob (`--auth-mode login`), prints the sample app URL.
- `scripts/demo.sh`: exercises allowed/denied endpoints and prints KQL; optional `ADD_DENIED_HOST=1` shows allowlist propagation and reverts.
- `scripts/teardown.sh`: deletes both RGs; optional `DELETE_APP_REGISTRATION=1` removes the app registration.

## NSG dependency note

The ACA subnet NSG allow-list follows the current documented workload-profiles dependencies (`MicrosoftContainerRegistry`, `AzureFrontDoor.FirstParty`, `AzureActiveDirectory`, `AzureMonitor`, Azure DNS, intra-VNet), plus inbound `AzureFrontDoor.Backend` on `443` and `31443`. The two `AzureFrontDoor.*` rules are **ACA platform dependencies** — ACA delivers external ingress through Azure's managed Front Door layer (inbound arrives from `AzureFrontDoor.Backend`, post-DNAT on `31443`) — and are unrelated to fronting the app with a standalone Front Door.

Pulling the sample image from ACR adds two more outbound allows: `AzureContainerRegistry:443` and `Storage.<region>:443` — below the Premium SKU, ACR serves layer data from shared Azure Storage. **The `Storage.<region>` allow softens the egress floor**: any in-region storage account becomes directly reachable from the subnet, bypassing the proxy. For a demo that trade-off is acceptable and honest to state; the production posture is ACR Premium with a private endpoint, which needs neither rule (see `docs/production-hardening.md`).

Because Microsoft occasionally updates service-tag guidance, re-check the latest ACA custom-VNet NSG documentation before production rollout.

## Monthly cost guide (ballpark, West Europe)

| Resource | Approx monthly |
|---|---:|
| VMSS (2× `Standard_D2pls_v6`) | €90–€140 |
| Standard internal LB | €18–€25 |
| Log Analytics (light demo ingestion) | €5–€25 |
| ACA environment + sample app (consumption/light usage) | €5–€20 |
| ACR Basic | ~€5 |
| **Estimated total** | **€123–€215** |

Use `scripts/teardown.sh` as the off switch to avoid idle spend.

## Low-cost ARM64 burst matrix

The table below compares the most relevant low-cost ARM64 sizes for the proxy. Network figures are the published Azure series ceilings; in practice, the proxy will usually become CPU-credit or single-core limited before it reaches the NIC ceiling.

| SKU | Published price | Published max network | Practical burst profile | Notes |
|---|---:|---:|---|---|
| `B2pts v2` | $6.1320/month | 6,250 Mbps | Best for very light, intermittent demo traffic | Smallest burstable option; limited memory headroom |
| `B2pls v2` | $24.5280/month | 6,250 Mbps | Better fit for a public demo with occasional spikes | Good balance of cost, memory, and burstability |
| `D2pls v6` | $45.2600/month | 60,000 Mbps | Comfortable multi-Gbps bursts without relying on CPU credits | Best default when predictability matters more than absolute minimum cost |

Suggested default for a public demo: `B2pls v2`. It keeps the ARM64 and burstable profile, is materially cheaper than `B2ps v2`, and leaves more memory room than `B2pts v2`. Use `D2pls v6` if the goal is to minimize performance surprises rather than cost.

Measured on the live `D2pls_v6` deployment (2 instances, 4h uptime): the `egress-proxy`
process holds ~20 MiB RSS (cgroup peak ~9 MiB), OS + Azure Monitor agent ~575 MiB, load
average 0.00. Memory is never the constraint at this SKU class — `B2pls v2` (4 GiB) has
~3.3 GiB of headroom, while `B2pts v2` (1 GiB) would leave <450 MiB after base load.
The B-series question is CPU credits under sustained tunnel traffic, not memory.
