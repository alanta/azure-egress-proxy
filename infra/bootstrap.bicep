// ============================================================================================
// Phase 1 — the artifact phase.
//
// Compute cannot boot until the artifacts it fetches at start-up already exist, and neither a
// blob upload nor an image push is an ARM resource that main.bicep could sequence:
//
//   binary  ──must exist before──▶  the proxy VMSS runs cloud-init
//   images  ──must exist before──▶  the container apps' first pull
//
// So this template creates the two artifact stores, deploy.sh fills them, and main.bicep
// consumes both as `existing`. It is subscription-scoped and it creates the hub resource group,
// because main.bicep creating the resource groups is what left phase 1 with nowhere to deploy.
// main.bicep still creates the spoke and management groups; it references the hub as existing.
// ============================================================================================

targetScope = 'subscription'

@description('Primary deployment location.')
param location string

@description('Hub resource group name. Created here, referenced as existing by main.bicep.')
param hubResourceGroupName string = 'rg-egress-hub'

@description('Tags applied to the hub resource group. Handy where subscription policy mandates tags (Owner, Purpose, ...).')
param resourceGroupTags object = {}

@description('Principal object ID running the deployment. It is granted Storage Blob Data Contributor on the bootstrap account so deploy.sh can upload the proxy binary with --auth-mode login; shared keys are disabled, so this grant is the only way in.')
param deployerPrincipalId string

@description('Name of the storage account holding the proxy binary. Derived by deploy.sh, which also uploads to it.')
@minLength(3)
@maxLength(24)
param bootstrapStorageAccountName string

@description('Blob container holding the proxy binary.')
param bootstrapContainerName string = 'proxy-bin'

@description('Deploy the container registry. deploy.sh sets this false when SAMPLE_APP_IMAGE, CONTROL_PLANE_IMAGE and PORTAL_IMAGE all name references that are already pullable — there is then nothing to import and no registry to pull from.')
param deployContainerRegistry bool = true

@description('Name of the container registry. Required when deployContainerRegistry is true.')
param containerRegistryName string = ''

module hubRg 'br/public:avm/res/resources/resource-group:0.4.0' = {
  name: 'bootstrap-hub-rg'
  params: {
    name: hubResourceGroupName
    location: location
    tags: resourceGroupTags
  }
}

// The proxy binary's home. Declared here rather than created imperatively by deploy.sh, which is
// what fixes the setting the CLI defaults got wrong: shared-key access was left unset, so it
// worked — a bearer credential usable from anywhere on the internet, opening the blob that holds
// the binary running as the security control. The allowlist account next to it in hub.bicep has
// always disabled it explicitly; now they match.
//
// An earlier draft also claimed the CLI-created account was on TLS1_0. Checked against the live
// account, it was not: `az storage account create` has defaulted to TLS1_2 for years. Kept
// explicit here anyway, because a default is not a guarantee.
module bootstrapStorage 'br/public:avm/res/storage/storage-account:0.32.0' = {
  name: 'bootstrap-storage'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    name: bootstrapStorageAccountName
    location: location
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    // ANONYMOUS BLOB READ IS DELIBERATE, and it is not the same decision as shared-key access.
    //
    // cloud-init.yaml fetches the binary with a plain unauthenticated curl before anything on the
    // box holds a token, so disabling anonymous read breaks first boot and every scale-out after
    // it. Integrity comes from the SHA256 that cloud-init pins to the exact bytes uploaded, not
    // from access control, and the binary is a published release artifact anyway — there is
    // nothing here to keep secret. What was worth closing is the WRITE path: shared keys above.
    allowBlobPublicAccess: true
    // The demo posture is public endpoint + Entra-only RBAC; the AVM default (defaultAction Deny)
    // silently blocks the upload. See docs/production-hardening.md.
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    blobServices: {
      containers: [
        {
          name: bootstrapContainerName
          publicAccess: 'Blob'
        }
      ]
    }
    roleAssignments: [
      {
        // No principalType: deploy.sh passes a User locally and a Service Principal from CI
        // (OIDC); a mismatched hint fails the role assignment. Same reason as the config account.
        principalId: deployerPrincipalId
        roleDefinitionIdOrName: 'Storage Blob Data Contributor'
      }
    ]
  }
  dependsOn: [
    hubRg
  ]
}

// The registry serves all three zones, so it is platform infrastructure and belongs in the hub.
// It is in phase 1 for a second reason the storage account does not have: the images are imported
// INTO it before any deployment references them, so it must exist before the images exist, which
// must be before the applications do.
//
// SKU and public endpoint carry across from the imperative `az acr create` unchanged — this is a
// placement fix, not a hardening one. Premium with a private endpoint is the production
// counterpart and is recorded in docs/production-hardening.md; it is also what would delete the
// Storage.<region> rule every subnet carries today.
module containerRegistry 'br/public:avm/res/container-registry/registry:0.9.3' = if (deployContainerRegistry) {
  name: 'bootstrap-acr'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    name: containerRegistryName
    location: location
    acrSku: 'Basic'
    acrAdminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
  dependsOn: [
    hubRg
  ]
}

output hubResourceGroupName string = hubResourceGroupName
output bootstrapStorageAccountName string = bootstrapStorage.outputs.name
output bootstrapContainerName string = bootstrapContainerName
output bootstrapBlobEndpoint string = 'https://${bootstrapStorage.outputs.name}.blob.${environment().suffixes.storage}'
output containerRegistryName string = deployContainerRegistry ? containerRegistry!.outputs.name : ''
output containerRegistryLoginServer string = deployContainerRegistry ? containerRegistry!.outputs.loginServer : ''
