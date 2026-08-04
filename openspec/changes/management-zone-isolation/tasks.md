One worker, sequential. The Bicep groups have a hard ordering (shared modules, then identities,
then the grants that use both, then compute) and groups 3–7 all touch `main.bicep`, so
parallelising would cost more in contract-writing than it saves.

Before starting, read `proposal.md` and `design.md`. The § *Dependency ordering* section of the
design is the part that is easy to get wrong — the module order there is not a suggestion.

**Two gates, and only one of them is cheap.** CI already compiles every `.bicep` under `infra/`,
and group 10 turns its silent linter into a real one. Everything past compilation needs a real
`deploy.sh` into a scratch subscription; nothing here is exercised by `dotnet test` or `go test`.

---

## 1. Artifact phase

- [ ] 1.1 Create `infra/bootstrap.bicep` at **subscription** scope — it has to create the hub
      resource group, because `main.bicep` creating the groups is what left phase 1 with nowhere
      to deploy
- [ ] 1.2 Bootstrap storage account: `allowSharedKeyAccess: false`, `minimumTlsVersion: TLS1_2`,
      `supportsHttpsTrafficOnly: true`, one container for the binary
- [ ] 1.3 Keep the container's **anonymous blob read**. `cloud-init.yaml` fetches the binary with
      a plain unauthenticated `curl`, so disabling it breaks scale-out. Integrity comes from the
      pinned SHA256, not from access control, and the binary is public anyway. Comment it where
      the container is declared, so it is not mistaken for an oversight and "fixed"
- [ ] 1.4 Assign the deploying principal `Storage Blob Data Contributor` on it, with no
      `principalType` hint — `deploy.sh` passes a User locally and a Service Principal from CI,
      and a mismatched hint fails the assignment (same reason as the config account)
- [ ] 1.5 Container registry: Basic, public network access, admin user disabled. Carry across
      whatever `ensure_demo_acr` sets today rather than re-deciding it — this is a placement
      change, not a hardening one
- [ ] 1.6 Make the registry **conditional** on a parameter. Setting `SAMPLE_APP_IMAGE`,
      `CONTROL_PLANE_IMAGE` and `PORTAL_IMAGE` to already-pullable references skips the registry
      entirely today; phase 1 runs before image preparation, but `deploy.sh` knows by then
      whether it needs one, so it passes the decision in
- [ ] 1.7 In `deploy.sh`, replace `ensure_bootstrap_storage` and `ensure_demo_acr` with one
      `bootstrap.bicep` deployment. Note that `ensure_demo_acr` also creates the **spoke**
      resource group as a side effect — `main.bicep` still creates it, so confirm nothing
      between the two phases depended on it existing early
- [ ] 1.8 Switch the binary upload to `--auth-mode login`, and wrap it in a bounded retry loop
      with a clear message — the role assignment immediately precedes it and Entra propagation
      is not instant
- [ ] 1.9 Leave the image path as `az acr import` from GHCR. It is a server-side pull, so it
      needs no subnet and does not care which resource group the registry is in — only the
      `--resource-group` argument changes. **Do not substitute `az acr build`**: `import_image`
      documents that it does not work with these Dockerfiles. The
      `BUILD_IMAGES_LOCALLY=true` local build-and-push branch stays as it is
- [ ] 1.10 Fail loudly if the upload or any image import does not succeed. A missing artifact
      surfaces as compute that starts and never serves, which is expensive to diagnose from the
      symptom
- [ ] 1.11 Update the resource-group assumptions around `BOOTSTRAP_STORAGE_ACCOUNT`, `acr_name`,
      `az acr import` and `az acr login` — the name derivations stay, the resource group changes
- [ ] 1.12 In `main.bicep`, reference the hub resource group as existing rather than creating it

## 2. Shared role-assignment modules

- [ ] 2.1 Check the AVM index for a resource-scoped role-assignment module. The repo's convention
      is AVM where one exists; write local modules only if it does not cover this
- [ ] 2.2 Create `infra/modules/shared/acr-role-assignment.bicep` — `existing` registry by name,
      one assignment scoped to it, parameters for `principalId`, `roleDefinitionId` and
      `principalType`
- [ ] 2.3 Create `infra/modules/shared/rg-role-assignment.bicep` for the console's `Reader` and
      `Monitoring Reader`, which are resource-group scoped and have no resource to hang off
- [ ] 2.4 Name assignments `guid(<scope>.id, principalId, roleDefinitionId)`. The existing ones
      use string literals for the principal and the role, so changing a principal updates in
      place instead of replacing — the module is the place to fix that convention
- [ ] 2.5 Comment that one generic module is impossible: a role assignment's `scope` must be a
      typed `existing` resource in the same file, so one module per target type is the floor
- [ ] 2.6 Leave grants that AVM's `roleAssignments` parameter already carries where they are.
      This is for the two families it cannot reach, not a migration of every assignment

## 3. Identities — one module per resource group

- [ ] 3.1 Create `infra/modules/hub-identity.bicep` (proxy), `spoke-identity.bicep` (sample app
      + `spoke-acr-pull`), `mgmt-identity.bicep` (control plane, console + `mgmt-acr-pull`) —
      identities only, nothing else
- [ ] 3.2 Each outputs `principalId`, `clientId` and `resourceId` per identity
- [ ] 3.3 In `main.bicep`, add the `mgmt` resource group and deploy all three identity modules
      before `hub`, `spoke` and `mgmt`. Both the group and `mgmt-identity.bicep` are conditional
      on `deployControlPlane` — see group 5
- [ ] 3.4 Comment in each module why it exists as its own file — it is not obvious from one
      resource, and the reason (grants live with their target, so identities must precede them)
      is what stops someone folding them back in
- [ ] 3.5 Comment on each pull identity that Container Apps has no environment-level pull
      configuration — this is a convention held by the modules, and a reviewer looking for an
      environment property will not find one

## 4. Hub — all grants collect here

- [ ] 4.1 Remove all identity creation from `hub.bicep`; take as parameters only the principal
      IDs it grants on — proxy, control plane, console, and the two pull identities. The sample
      app's principal is no longer among them, because it no longer holds anything here
- [ ] 4.2 Move the console's `Reader` and `Monitoring Reader` onto `rg-role-assignment.bicep`.
      Keep both comments — they explain why `Reader` alone does not grant workspace data access,
      and why the scope stops at this resource group
- [ ] 4.3 Keep the config account's `Blob Data Reader` (proxy) and `Blob Data Contributor`
      (control plane, deployer) on the AVM `roleAssignments` parameter where they already are
- [ ] 4.4 Add two `AcrPull` assignments via `acr-role-assignment.bicep`, one per environment pull
      identity. Carry across what is worth keeping from the comments on the assignments currently
      in `spoke.bicep`, and state why the grantee is a pull identity rather than the applications
- [ ] 4.5 Keep both assignments conditional on there being a registry, the way `spoke.bicep`
      guards them today. With all three image variables pointing at pullable references there is
      no registry to grant on
- [ ] 4.6 Reference the bootstrap account as `existing` where the VMSS cloud-init configuration
      needs its blob URL
- [ ] 4.7 Adjust hub outputs: drop identity client IDs the modules now source directly, add what
      `mgmt.bicep` needs (config blob service URL, workspace GUID, hub resource-group name,
      registry login server)

## 5. Management zone

- [ ] 5.1 Create `infra/modules/mgmt.bicep`: NSG, virtual network with `snet-mgmt`, Container
      Apps environment (workload-profile, `Consumption`), and the two container apps
- [ ] 5.2 Make the entire zone conditional on `deployControlPlane`, which defaults to `false` —
      **Mode 1 is the default deployment** and must create no management resource group, network,
      environment or identity. The condition is `deployControlPlane` rather than either flag,
      because the console requires the control plane
- [ ] 5.3 Make the console within the zone conditional on `deployPortal`, as it is today
- [ ] 5.4 Build the NSG from the table in `design.md` § *NSG deltas*. `allow-azure-resource-manager`
      is present here; there is deliberately no proxy-egress rule
- [ ] 5.5 Carry across **both** registry rules — the live spoke NSG has `allow-acr`
      (`AzureContainerRegistry`) and `allow-acr-storage` (`Storage.<region>`) as separate
      entries, not one. Change the latter's description: in this subnet it serves both registry
      layer pulls and the control plane's blob writes, and a stale description is how a rule
      outlives its reason
- [ ] 5.6 Move the control-plane container app across verbatim, including its `NO_PROXY` value,
      its environment-variable wiring and the comment explaining why it does not use the proxy
- [ ] 5.7 Move the console container app across verbatim, including `authConfigs`, the
      `unauthenticatedClientAction` behaviour, the optional source-IP restriction, and all the
      comments explaining why external ingress is acceptable for it
- [ ] 5.8 Point the console's control-plane URL at the new environment's internal name — both
      apps are in this environment, so it stays an app name and not an ingress FQDN
- [ ] 5.9 Assign each app two user-assigned identities — its own functional one and
      `mgmt-acr-pull` — and point `properties.configuration.registries[].identity` at the pull
      identity's `resourceId`. Wire `registries` only when there is a registry; with images
      pulled from a public reference there is nothing to authenticate to
- [ ] 5.10 Confirm `AZURE_CLIENT_ID` still selects the functional identity in each app. With more
      than one user-assigned identity attached, an unset or wrong client ID resolves to the wrong
      principal, and the failure appears as an authorization error far from its cause

## 6. Spoke — lose the platform apps, narrow the floor

- [ ] 6.1 Delete the control-plane and console container apps and their `authConfigs`
- [ ] 6.2 Delete `allow-azure-resource-manager` from the apps NSG, and delete the comment
      block above it — it describes a widening that no longer exists
- [ ] 6.3 Delete `controlPlaneAcrPull`, `portalAcrPull` and `sampleAppAcrPull` — all three now
      live in `hub.bicep` next to the registry — and the `existing` registry reference with them
- [ ] 6.4 Remove the sample-app identity module; take the sample-app and `spoke-acr-pull`
      `resourceId`s and `clientId`s as parameters from `spoke-identity.bicep`
- [ ] 6.5 Attach both identities to the sample app, point its `registries[].identity` at the
      pull identity, and confirm `AZURE_CLIENT_ID` still selects the sample-app identity — this
      is the identity the proxy authenticates, so getting it wrong changes what the allowlist
      matches
- [ ] 6.6 Delete the now-unused control-plane parameters that only fed the moved apps, and the
      two management principal-ID parameters the spoke no longer needs at all. Keep what the
      sample app still uses, including the registry login server for its image reference
- [ ] 6.7 Move `controlPlaneUrl` and `portalUrl` outputs from `spoke` to `mgmt` in `main.bicep`

## 7. Wiring and teardown

- [ ] 7.1 In `main.bicep`, order the modules exactly as `design.md` § *Dependency ordering*
      sets out, and add `mgmtVnetCidr` / `mgmtSubnetCidr` parameters defaulting to
      `10.2.0.0/22` and `10.2.0.0/23`
- [ ] 7.2 Leave the private DNS zone linked to hub and spoke only. The management network is not
      linked to it — that is the design, not an omission, and it deserves a comment saying so
- [ ] 7.3 Leave hub ↔ spoke peering unchanged
- [ ] 7.4 Teach `teardown.sh` about the third resource group, and confirm it removes the hub
      group even though phase 1 creates it. An unknown or unowned group is a silent recurring
      cost
- [ ] 7.5 No migration path. Resources change resource groups, and the agreed approach is a full
      `teardown.sh` followed by a clean deploy. Say so in `infra/README.md` rather than building
      detection for a case nobody will run
- [ ] 7.6 Check `demo.sh` and the identity scripts for hard-coded resource-group assumptions
      about where the control plane, the console, the registry, or either storage account lives
- [ ] 7.7 Check `.github/workflows/` for the same assumptions — the registry moving groups is
      exactly the kind of thing a release job hard-codes. `SECURITY_GUIDELINES.md` is binding on
      anything changed there

## 8. Confirm the pre-existing requirements

The four requirements marked *(pre-existing)* in the spec describe topology this change does not
modify. **They can be checked against the deployment running now, before any of this is
implemented** — and mostly have been, during exploration. **Where one turns out to be wrong, fix
the requirement, not the infrastructure**: they are a description, and anything they get wrong is
a defect in the description until someone decides otherwise.

Confirmed live in subscription `Playground`, resource groups `rg-egress-hub` / `rg-egress-spoke`:

- [x] 8.1 The proxy NSG (`egress-proxy-nsg`) carries **zero** rules, so the proxy subnet runs on
      Azure defaults and is the only subnet without the floor — as the spec states
- [x] 8.2 The spoke NSG carries `deny-internet` at priority 4000 beneath eleven named allowances
- [x] 8.3 `hub-to-spoke` and `spoke-to-hub` peerings are `Connected`; the `egress.internal`
      private zone has two virtual-network links
- [x] 8.4 The load balancer `egress-proxy-ilb` serves 4750 → 4750, matching `allow-proxy-egress`
- [x] 8.5 The egress prefix `egress-proxy-egress-prefix` is a `/31` — two addresses, which is the
      ceiling the console's runtime surface is built to show

Still open, and needing a live check rather than an ARM read:

- [ ] 8.6 From the sample app, a direct outbound connection to an Internet destination bypassing
      `HTTPS_PROXY` is refused
- [ ] 8.7 Proxied traffic is observed leaving from an address inside the `/31`
- [ ] 8.8 `proxy.egress.internal` resolves from the spoke to the internal load-balancer frontend
- [ ] 8.9 Comment the empty `proxyNsgRules` where it is declared. An empty list reads as
      unfinished; it is the deliberate exemption the spec now records
- [ ] 8.10 Of the spoke's eleven allowances, confirm `allow-azure-resource-manager` is the only
      one losing its resident. `allow-acr` and `allow-acr-storage` both stay for the sample app,
      and `allow-mcr` and `allow-afd-firstparty` are Container Apps platform dependencies

## 9. Docs

- [ ] 9.1 `docs/architecture.md` — redraw the topology as three zones and state why the
      management zone peers with nothing
- [ ] 9.2 `infra/README.md` — the third resource group, the new parameters, and the two-phase
      deployment
- [ ] 9.3 `docs/production-hardening.md` — add the trade-offs from the proposal's *What This
      Deliberately Does Not Change*, each with its production counterpart: public ingress on
      both management apps, `defaultAction: Allow` on storage, and Basic public ACR with its
      `Storage.<region>` NSG consequence. Remove any entry describing the registry's spoke
      placement as a trade-off — it is now fixed, not accepted
- [ ] 9.4 `docs/control-plane.md` — the control plane's deployment location and the fact that no
      network path to it exists from the proxy or from any workload
- [ ] 9.5 `README.md` — any deployment walkthrough or output name that moved

## 10. Verify — against a real deployment

- [ ] 10.1 Add `bicepconfig.json` with `no-unused-params` and `no-unused-vars` at `error`, and
      review the rest of the linter's rule set for anything else worth failing on. This change is
      mostly deletion and rewiring, so unused leftovers are its most likely defect
- [ ] 10.2 Make the whole `infra/` tree clean under it — this is a repo-wide gate arriving with
      this change, so pre-existing warnings in files this change does not otherwise touch are in
      scope
- [ ] 10.3 Confirm the existing `bicep` job in `.github/workflows/ci.yml` now fails on a
      deliberately introduced unused parameter, then remove it. A gate nobody has seen fail is
      not known to be a gate
- [ ] 10.4 `az bicep build` clean on every file, including the new subscription-scoped
      `bootstrap.bicep` — the `find` in CI picks new files up without a workflow change, which is
      worth confirming rather than assuming
- [ ] 10.5 Full `deploy.sh` into a scratch subscription, from nothing, in Mode 2 with the console
- [ ] 10.6 Deploy Mode 1 into a clean subscription and confirm **no management resource group,
      network, environment or identity is created** — this is the default deployment, so a zone
      appearing here is a cost every default user pays for nothing. Note that the deployment
      running today is Mode 3, so Mode 1 is the path with no live evidence behind it
- [ ] 10.7 Confirm the new bootstrap account reports `allowSharedKeyAccess: false` and
      `minimumTlsVersion: TLS1_2`. The live one reports *unset* and **`TLS1_0`**, which is the
      concrete thing this half of the change fixes
- [ ] 10.8 Confirm the scale set fetched its binary and the proxy serves — the artifact phase and
      the Entra-only upload are the highest-risk part of this change
- [ ] 10.9 Confirm all three container apps pulled their images from the hub registry, on the
      default GHCR-import path
- [ ] 10.10 Deploy once with `BUILD_IMAGES_LOCALLY=true` and once with all three image variables
      set to pullable references — the first exercises the local build-and-push branch, the
      second proves the registry is still genuinely optional
- [ ] 10.11 Confirm both storage accounts refuse shared-key authentication
- [ ] 10.12 Confirm no peering exists on the management virtual network
- [ ] 10.13 From the sample app, confirm an outbound connection to Azure Resource Manager is now
      denied, and that allowed and denied FQDNs through the proxy still behave as before
- [ ] 10.14 Exercise Mode 2 end to end: a ruleset push through the control-plane API, the rendered
      blob, the proxy picking it up
- [ ] 10.15 Open the console and confirm every surface still reads — policy through the
      control-plane API, decisions from Log Analytics, and the runtime schematic from ARM
- [ ] 10.16 `teardown.sh` removes all three resource groups
- [ ] 10.17 Run the review checklist in `SECURITY_GUIDELINES.md`
