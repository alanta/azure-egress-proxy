# Production hardening — deltas from this demo

This repo optimises for **reproducibility by a reader with one subscription and an
afternoon**. Running the pattern for real, change these — each is a deliberate,
documented simplification here:

| Demo choice | Production posture |
|---|---|
| VMSS installs the proxy via **cloud-init + GitHub Release binary** (checksum-pinned) | Bake a **versioned golden image** (Compute Gallery) and roll it with VMSS rolling upgrades — immutable infrastructure, nothing fetched at boot |
| Allowlist storage: **public endpoint, Entra-only RBAC** (`allowSharedKeyAccess: false`) | `publicNetworkAccess: Disabled` + **private endpoint** (`privatelink.blob.core.windows.net`); allowlist writes then need network reach (VNet-integrated runner/agent or deployment script) |
| Sample app **openly exposed on its ACA external ingress** (no ingress gate — this demo is about *egress*, so ingress is intentionally left simple) | Put a WAF in front (**Front Door Premium + Private Link origin** to an internal-only Container Apps environment — no public origin at all), or restrict ingress and reach the app over private connectivity |
| One shared allowlist document, centrally written | Per-module blobs with path-scoped RBAC (write isolation), or a validating control-plane API (see [ROADMAP](../ROADMAP.md)) |
| Sample image in a **Basic ACR** with NSG allows for `AzureContainerRegistry` **and** `Storage.<region>` (below Premium, ACR serves layer data from shared Azure Storage — the Storage allow makes any in-region storage account reachable, softening the egress floor) | **ACR Premium + private endpoint**: pulls stay on the VNet and both NSG allows disappear |
| Single region, small VMSS | ≥2 instances across availability zones (already the default here), CPU/connection autoscale, prefix sized for SNAT (64k ports per instance IP) |
| `encryptionAtHost` defaults **off** (deploys on any subscription without feature registration) | Register `Microsoft.Compute/EncryptionAtHost` and deploy with `encryptionAtHost=true` |

Unchanged from production intent: explicit CONNECT only (no transparent fallback), the
NSG deny-Internet floor with `defaultOutboundAccess: false`, fail-closed allowlist
handling, managed-identity-only data plane, structured audit logging.
