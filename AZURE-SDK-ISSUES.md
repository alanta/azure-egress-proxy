# Azure SDK issues found by this repo

Two defects in `Azure.ResourceManager.Network` that the management console hit when it was first
deployed. Both are worked around in
[`src/Portal/Clients/ArmDirectClient.cs`](src/Portal/Clients/ArmDirectClient.cs), which exists
only because of them and should be deleted if they are fixed.

Both are filed upstream, and the workarounds carry the issue number at the point of use so a
reader who finds one can find the other:

| Issue | Defect | Workaround |
|---|---|---|
| [Azure/azure-sdk-for-net#61648](https://github.com/Azure/azure-sdk-for-net/issues/61648) | `PublicIPPrefixData.PublicIPAddresses` always empty (`CodeGenSuppress` shim) | `ArmDirectClient.PrefixAsync` |
| [Azure/azure-sdk-for-net#61649](https://github.com/Azure/azure-sdk-for-net/issues/61649) | `GetVirtualMachineScaleSetPublicIPAddressesAsync` sends an unavailable API version; `SetApiVersion` cannot override it | `ArmDirectClient.ForScaleSetAsync` |

Not a task list for this repo — the console already behaves correctly.

**Environment for both.** `Azure.ResourceManager.Network` 1.16.1, `Azure.ResourceManager` 1.15.0,
`Azure.Identity` 1.21.0, .NET 10, region `swedencentral`, observed 2026-08-01. Both were
reproduced outside the console, in a standalone console app authenticating with
`AzureCliCredential`, so neither depends on anything in this repository.

---

## 1. `PublicIPPrefixData.PublicIPAddresses` is always empty

**Filed as [Azure/azure-sdk-for-net#61648](https://github.com/Azure/azure-sdk-for-net/issues/61648).**

**The serious one**, because it fails silently and in the dangerous direction.

The REST API returns `properties.publicIPAddresses` populated at every API version the region
supports. The SDK model surfaces none of them, with no error and no warning.

### Reproduction

```csharp
var arm = new ArmClient(new AzureCliCredential());
var group = arm.GetResourceGroupResource(
    ResourceGroupResource.CreateResourceIdentifier(subscriptionId, "rg-egress-hub"));

var data = (await group.GetPublicIPPrefixes().GetAsync("egress-proxy-egress-prefix")).Value.Data;

Console.WriteLine(data.IPPrefix);                 // 4.166.55.208/31   — correct
Console.WriteLine(data.PrefixLength);             // 31                — correct
Console.WriteLine(data.PublicIPAddresses.Count);  // 0                 — WRONG, should be 2
```

### What the service actually returns

```console
$ az rest --method GET --url ".../publicIPPrefixes/egress-proxy-egress-prefix?api-version=2024-07-01"
"publicIPAddresses": [
  { "id": ".../virtualMachineScaleSets/egress-proxy-vmss/virtualMachines/1/.../publicIPAddresses/proxy-pip" },
  { "id": ".../virtualMachineScaleSets/egress-proxy-vmss/virtualMachines/0/.../publicIPAddresses/proxy-pip" }
]
```

Checked at `2024-07-01`, `2024-10-01`, `2025-07-01` and `2026-01-01` — the field is present and
populated at all of them, so this is not a version-specific response shape. `properties` carries
no `referencedPublicIPAddresses` key at any of those versions. Reflecting over `PublicIPPrefixData`
shows `PublicIPAddresses` as the only candidate property, and it is empty.

### Why it matters

The addresses allocated from a prefix are how you know whether a prefix is exhausted. An empty
list is indistinguishable from an unused prefix, so a **fully consumed** prefix reads as a
**completely free** one. In this repo the panel told an operator the fleet could grow by two nodes
when it could grow by none — and growing past the prefix means egressing from an address no
partner has allowlisted, which is the exact condition the panel exists to warn about.

Wrong, confident, and in the direction that prompts nobody to check.

### Workaround

`GET` the prefix directly and read `properties.publicIPAddresses`
(`ArmDirectClient.PrefixAsync`).

---

## 2. `GetVirtualMachineScaleSetPublicIPAddressesAsync` sends an unavailable API version

**Filed as [Azure/azure-sdk-for-net#61649](https://github.com/Azure/azure-sdk-for-net/issues/61649).**

The extension method sends whichever API version the package was built against — `2025-07-01` in
1.16.1 — for an operation whose regional rollout lags that version.

### Reproduction

```csharp
await foreach (var ip in group.GetVirtualMachineScaleSetPublicIPAddressesAsync("egress-proxy-vmss"))
{
    // never reached
}
```

```
Azure.RequestFailedException: No registered resource provider found for location 'swedencentral'
and API version '2025-07-01' for type 'virtualMachineScaleSets/publicIPAddresses'.
The supported api-versions are '2017-03-30, ..., 2024-11-01, 2025-04-01, 2025-11-01, 2026-03-01'.
Status: 400 (Bad Request)
```

Note the supported list: `2025-07-01` is absent while both older *and newer* versions are present,
so this is a gap rather than a "too new" problem, and waiting for rollout does not obviously fix
it.

### `ArmClientOptions.SetApiVersion` does not reach it

The natural workaround does not work, which is the part worth reporting. Both of these were tried
and neither changed the version on the wire:

```csharp
options.SetApiVersion(new ResourceType("Microsoft.Network/publicIPAddresses"), "2024-07-01");
options.SetApiVersion(new ResourceType("Microsoft.Compute/virtualMachineScaleSets/publicIPAddresses"), "2024-07-01");
```

The call is an extension method on `ResourceGroupResource` whose version does not appear to be
resolved through the type keys that option accepts, leaving no supported way to override it.

### Why it matters

A 400 naming a "registered resource provider" reads as a permissions or registration problem. The
actual cause is a client-side version default, and the error gives no hint of that — an
expensive hour for whoever meets it next.

### Workaround

`GET
/subscriptions/{s}/resourceGroups/{g}/providers/Microsoft.Compute/virtualMachineScaleSets/{n}/publicIPAddresses`
at a pinned version (`ArmDirectClient.ForScaleSetAsync`).

---

## Re-checking these

Both workarounds are in one file and neither is load-bearing beyond the Runtime surface. To test a
newer package: bump `Azure.ResourceManager.Network` in `Directory.Packages.props`, regenerate the
lock files, and run the two reproductions above against a real deployment — a unit test cannot see
either defect, since both are about what Azure returns and how the SDK deserialises it.

Check #61648 against a prefix whose addresses are **actually assigned**. Its failure mode is a
silent zero, so a prefix with nothing allocated to it returns the same empty list whether the bug
is fixed or not, and the test would pass either way.

When both close, delete `ArmDirectClient` and this file with it.
