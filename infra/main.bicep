targetScope = 'subscription'

@description('Primary deployment location.')
param location string

@description('Hub resource group name. Created by infra/bootstrap.bicep (phase 1, the artifact phase) and referenced as existing here — the proxy binary and the container images must exist before this deployment creates the compute that fetches them.')
param hubResourceGroupName string = 'rg-egress-hub'

@description('Spoke resource group name.')
param spokeResourceGroupName string = 'rg-egress-spoke'

@description('Management resource group name. Created only when the control plane is deployed.')
param mgmtResourceGroupName string = 'rg-egress-mgmt'

@description('Name prefix for deployed resources.')
param namePrefix string = 'egress'

@description('Principal object ID that should be able to update allowlist blobs. Without the control plane this is the GitOps CI identity; with it, the platform-team identity that seeds the state blob and owns its grants section.')
param deployerPrincipalId string

@description('Deploy the control-plane API (Mode 2). Its identity becomes the only service that writes the allowlist blobs; the proxy stays read-only and workload pipelines get no blob role. Leave false for the GitOps topology (Mode 1).')
param deployControlPlane bool = false

@description('Container image for the control-plane API. Required when deployControlPlane is true; must be pullable under the egress floor (see containerRegistryName).')
param controlPlaneImage string = ''

@description('Deploy the read-only management console (Mode 3). It is a backend-for-frontend: it holds Reader + Monitoring Reader on the hub resource group and queries the control plane, Log Analytics and ARM on the operator\'s behalf. It writes nothing, and the platform is fully functional without it. Requires deployControlPlane.')
param deployPortal bool = false

@description('Container image for the management console. Required when deployPortal is true; must be pullable under the egress floor (see containerRegistryName).')
param portalImage string = ''

@description('Source IP ranges (CIDR) permitted to reach the management console, e.g. the platform team\'s office egress. Empty means no network restriction — the console is still behind Entra sign-in, but it is an admin surface for a security control, so restricting it is the production posture (see docs/production-hardening.md).')
param portalAllowedSourceIps array = []

@description('Application (client) ID of the Entra app registration operators sign in to the console with. Created out of band by scripts/deploy.sh — an app registration is not an ARM resource. Required when deployPortal is true. Bringing your own registration means it must also have a service principal in this tenant and ID token issuance enabled: the platform signs in with the hybrid flow, and both failures land after the credential prompt rather than before it.')
param portalAuthClientId string = ''

@description('Client secret for the console\'s Entra app registration.')
@secure()
param portalAuthClientSecret string = ''

@description('Tenant ID used for identity validation.')
param tenantId string

@description('JWKS endpoint used by the proxy for token validation.')
param jwksUrl string = '${environment().authentication.loginEndpoint}${tenantId}/discovery/v2.0/keys'

@description('Expected issuer used by the proxy for token validation.')
param expectIss string = '${environment().authentication.loginEndpoint}${tenantId}/v2.0'

@description('Expected audience used by the proxy for token validation.')
param expectAud string

@description('Override for the URL cloud-init fetches the proxy binary from. Empty (the default) composes it from the bootstrap account above, which is where deploy.sh seeds it.')
param proxyBinaryUrl string = ''

@description('SHA256 for the proxy binary.')
param proxyBinarySha256 string

@description('SSH public key for break-glass access over private network paths.')
param vmAdminPublicKey string

@description('Container image used by the sample app. Must listen on 8080 (the ingress target port) and be pullable under the egress floor — MCR and the ACR named in containerRegistryName are allowed by the NSG service tags, GHCR is not (deploy.sh imports the GHCR image into the ACR).')
param sampleAppImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Name of the Azure Container Registry created by bootstrap.bicep in the HUB resource group, hosting all three platform images. Empty means no ACR wiring, and every image parameter must then name an already-pullable reference.')
param containerRegistryName string = ''

@description('Name of the bootstrap storage account created by bootstrap.bicep in the hub resource group, holding the proxy binary the scale set fetches at boot.')
param bootstrapStorageAccountName string

@description('Container in the bootstrap account holding the proxy binary.')
param bootstrapContainerName string = 'proxy-bin'

@description('Blob name of the proxy binary in the bootstrap container.')
param bootstrapBlobName string = 'egress-proxy_linux_arm64'

@description('Proxy VM size. Defaults to the smallest ARM64 burstable size (B2pts_v2) — the lowest-cost option for a light demo; see infra/README.md for the burst matrix and headroom notes.')
param proxyVmSku string = 'Standard_B2pts_v2'

@description('Enable encryption at host on the proxy VMSS (requires the Microsoft.Compute/EncryptionAtHost subscription feature).')
param encryptionAtHost bool = false

@description('Proxy VMSS instance count.')
@minValue(1)
param proxyInstanceCount int = 2

@description('Public IP prefix length for proxy egress addresses.')
@minValue(28)
@maxValue(31)
param proxyPublicIpPrefixLength int = 31

@description('Idle timeout (minutes) for both legs of a CONNECT tunnel. Clients must retire pooled tunnels sooner — see docs/production-hardening.md § Idle timeouts.')
@minValue(4)
@maxValue(30)
param proxyIdleTimeoutInMinutes int = 4

@description('Hub VNet CIDR.')
param hubVnetCidr string = '10.0.0.0/22'

@description('Hub proxy subnet CIDR.')
param hubProxySubnetCidr string = '10.0.0.0/24'

@description('Spoke VNet CIDR.')
param spokeVnetCidr string = '10.1.0.0/22'

@description('Spoke apps subnet CIDR.')
param spokeAppsSubnetCidr string = '10.1.0.0/23'

@description('Management VNet CIDR. Peered with nothing, so no CIDR conflict is possible — the range continues the hub/spoke pattern for legibility, not out of necessity.')
param mgmtVnetCidr string = '10.2.0.0/22'

@description('Management subnet CIDR. A /23 matches the spoke\'s apps subnet; a workload-profile environment needs far less, but matching the sibling subnet is worth more than the addresses saved.')
param mgmtSubnetCidr string = '10.2.0.0/23'

@description('Static private LB frontend IP for the proxy.')
param proxyLoadBalancerPrivateIp string = '10.0.0.4'

@description('Tags applied to the resource groups this template creates. Handy where subscription policy mandates tags (Owner, Purpose, ...). The hub group is tagged by bootstrap.bicep, which creates it.')
param resourceGroupTags object = {}

// ============================================================================================
// PHASE 2 — the deployment.
//
// Phase 1 (infra/bootstrap.bicep) has already created the hub resource group, the bootstrap
// storage account holding the proxy binary, and the container registry; deploy.sh has already
// filled both. This template consumes them.
//
// The module order below is not stylistic. Identities follow the compute they serve, so they
// scatter across all three resource groups; role assignments belong at the scope of the resource
// being granted, and the hub owns nearly every such resource. That is circular unless identity
// creation is its own phase — user-assigned identities have no dependencies of any kind, so all of
// them can be created before anything grants on them. See design.md § Dependency ordering.
//
//   2a  spokeRg, mgmtRg                     (hubRg already exists)
//   2b  hub/spoke/mgmt-identity             every user-assigned identity in the deployment
//   2c  hub                                 config storage, registry grants, VMSS, LB, prefix, logs
//   2d  spoke                               vnet, NSG, ACA env, sample app
//   2e  mgmt                                vnet, NSG, ACA env, control plane + console
//   2f  peering, private DNS                hub <-> spoke only
// ============================================================================================

// The hub group is created by phase 1, so it is referenced rather than declared. Split ownership
// of resource-group creation is a thing to get wrong on a partial teardown: teardown.sh deletes
// all three regardless of which phase made them.
resource hubRg 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: hubResourceGroupName
}

module spokeRg 'br/public:avm/res/resources/resource-group:0.4.0' = {
  name: 'spoke-rg'
  params: {
    name: spokeResourceGroupName
    location: location
    tags: resourceGroupTags
  }
}

// A zone with no residents is still a resource group, a virtual network, an NSG and a Container
// Apps environment. Mode 1 — proxy, allowlist blob, sample app, no management plane — is the
// DEFAULT deployment, so none of that may exist unless the control plane does. The condition is
// deployControlPlane rather than either flag because the console requires the control plane: it
// reads policy through the API and holds no role on the blob, so deployPortal alone is not a valid
// deployment and does not need to bring a zone into existence on its own.
module mgmtRg 'br/public:avm/res/resources/resource-group:0.4.0' = if (deployControlPlane) {
  name: 'mgmt-rg'
  params: {
    name: mgmtResourceGroupName
    location: location
    tags: resourceGroupTags
  }
}

module hubIdentity 'modules/hub-identity.bicep' = {
  name: 'hub-identity'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
  }
  dependsOn: [
    hubRg
  ]
}

module spokeIdentity 'modules/spoke-identity.bicep' = {
  name: 'spoke-identity'
  scope: resourceGroup(spokeResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
  }
  dependsOn: [
    spokeRg
  ]
}

module mgmtIdentity 'modules/mgmt-identity.bicep' = if (deployControlPlane) {
  name: 'mgmt-identity'
  scope: resourceGroup(mgmtResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
    deployPortal: deployPortal
  }
  dependsOn: [
    mgmtRg
  ]
}

module hub 'modules/hub.bicep' = {
  name: 'hub'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
    deployerPrincipalId: deployerPrincipalId
    jwksUrl: jwksUrl
    expectIss: expectIss
    expectAud: expectAud
    proxyBinaryUrl: proxyBinaryUrl
    proxyBinarySha256: proxyBinarySha256
    vmAdminPublicKey: vmAdminPublicKey
    proxyVmSku: proxyVmSku
    encryptionAtHost: encryptionAtHost
    proxyInstanceCount: proxyInstanceCount
    proxyPublicIpPrefixLength: proxyPublicIpPrefixLength
    proxyIdleTimeoutInMinutes: proxyIdleTimeoutInMinutes
    hubVnetCidr: hubVnetCidr
    hubProxySubnetCidr: hubProxySubnetCidr
    proxyLoadBalancerPrivateIp: proxyLoadBalancerPrivateIp
    deployControlPlane: deployControlPlane
    deployPortal: deployPortal
    containerRegistryName: containerRegistryName
    bootstrapStorageAccountName: bootstrapStorageAccountName
    bootstrapContainerName: bootstrapContainerName
    bootstrapBlobName: bootstrapBlobName
    // Every principal the hub grants on. The sample app's is not among them: it holds nothing here
    // now that image pull is the spoke's dedicated pull identity.
    proxyIdentityPrincipalId: hubIdentity.outputs.proxyPrincipalId
    proxyIdentityClientId: hubIdentity.outputs.proxyClientId
    proxyIdentityResourceId: hubIdentity.outputs.proxyResourceId
    spokeAcrPullPrincipalId: spokeIdentity.outputs.acrPullPrincipalId
    controlPlaneIdentityPrincipalId: deployControlPlane ? mgmtIdentity!.outputs.controlPlanePrincipalId : ''
    portalIdentityPrincipalId: deployPortal ? mgmtIdentity!.outputs.portalPrincipalId : ''
    mgmtAcrPullPrincipalId: deployControlPlane ? mgmtIdentity!.outputs.acrPullPrincipalId : ''
  }
}

module spoke 'modules/spoke.bicep' = {
  name: 'spoke'
  scope: resourceGroup(spokeResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
    sampleAppImage: sampleAppImage
    containerRegistryName: containerRegistryName
    appInsightsWorkspaceResourceId: hub.outputs.workspaceResourceId
    expectAud: expectAud
    spokeVnetCidr: spokeVnetCidr
    spokeAppsSubnetCidr: spokeAppsSubnetCidr
    proxySubnetCidr: hubProxySubnetCidr
    sampleAppIdentityResourceId: spokeIdentity.outputs.sampleAppResourceId
    sampleAppIdentityClientId: spokeIdentity.outputs.sampleAppClientId
    acrPullIdentityResourceId: spokeIdentity.outputs.acrPullResourceId
  }
}

module mgmt 'modules/mgmt.bicep' = if (deployControlPlane) {
  name: 'mgmt'
  scope: resourceGroup(mgmtResourceGroupName)
  params: {
    location: location
    namePrefix: namePrefix
    mgmtVnetCidr: mgmtVnetCidr
    mgmtSubnetCidr: mgmtSubnetCidr
    containerRegistryName: containerRegistryName
    controlPlaneImage: controlPlaneImage
    deployPortal: deployPortal
    portalImage: portalImage
    portalAllowedSourceIps: portalAllowedSourceIps
    portalAuthClientId: portalAuthClientId
    portalAuthClientSecret: portalAuthClientSecret
    controlPlaneIdentityResourceId: deployControlPlane ? mgmtIdentity!.outputs.controlPlaneResourceId : ''
    controlPlaneIdentityClientId: deployControlPlane ? mgmtIdentity!.outputs.controlPlaneClientId : ''
    portalIdentityResourceId: deployPortal ? mgmtIdentity!.outputs.portalResourceId : ''
    portalIdentityClientId: deployPortal ? mgmtIdentity!.outputs.portalClientId : ''
    acrPullIdentityResourceId: deployControlPlane ? mgmtIdentity!.outputs.acrPullResourceId : ''
    appInsightsWorkspaceResourceId: hub.outputs.workspaceResourceId
    storageServiceUrl: hub.outputs.storageServiceUrl
    rulesetsBlobName: hub.outputs.rulesetsBlobName
    allowlistBlobName: hub.outputs.allowlistBlobName
    allowlistContainerName: hub.outputs.allowlistContainerName
    jwksUrl: jwksUrl
    expectIss: expectIss
    expectAud: expectAud
    hubResourceGroupName: hubResourceGroupName
    workspaceCustomerId: hub.outputs.workspaceCustomerId
    proxyVmssName: hub.outputs.proxyVmssName
    proxyPublicIpPrefixName: hub.outputs.proxyPublicIpPrefixName
    proxyLoadBalancerName: hub.outputs.proxyLoadBalancerName
  }
}

module hubToSpokePeering 'modules/peering.bicep' = {
  name: 'hub-to-spoke-peering'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    localVnetName: hub.outputs.hubVnetName
    peeringName: 'hub-to-spoke'
    remoteVnetResourceId: spoke.outputs.spokeVnetResourceId
  }
}

module spokeToHubPeering 'modules/peering.bicep' = {
  name: 'spoke-to-hub-peering'
  scope: resourceGroup(spokeResourceGroupName)
  params: {
    localVnetName: spoke.outputs.spokeVnetName
    peeringName: 'spoke-to-hub'
    remoteVnetResourceId: hub.outputs.hubVnetResourceId
  }
}

module privateDns 'br/public:avm/res/network/private-dns-zone:0.8.0' = {
  name: 'private-dns'
  scope: resourceGroup(hubResourceGroupName)
  params: {
    name: 'egress.internal'
    a: [
      {
        name: 'proxy'
        ttl: 60
        aRecords: [
          {
            ipv4Address: hub.outputs.proxyLoadBalancerPrivateIp
          }
        ]
      }
    ]
    // Linked to the hub and the spoke only. The management network is deliberately absent: it is
    // peered with nothing, reaches every dependency as a PaaS endpoint, and has no reason to
    // resolve proxy.egress.internal — the management plane must not route through the data plane
    // it configures. An unlinked network here is the design, not an omission.
    virtualNetworkLinks: [
      {
        name: 'hub-link'
        virtualNetworkResourceId: hub.outputs.hubVnetResourceId
        registrationEnabled: false
      }
      {
        name: 'spoke-link'
        virtualNetworkResourceId: spoke.outputs.spokeVnetResourceId
        registrationEnabled: false
      }
    ]
  }
  dependsOn: [
    hubToSpokePeering
    spokeToHubPeering
  ]
}

output hubResourceGroup string = hubResourceGroupName
output spokeResourceGroup string = spokeResourceGroupName
output mgmtResourceGroup string = deployControlPlane ? mgmtResourceGroupName : ''
output allowlistStorageAccountName string = hub.outputs.allowlistStorageAccountName
output allowlistContainerName string = hub.outputs.allowlistContainerName
output allowlistBlobName string = hub.outputs.allowlistBlobName
output allowlistBlobUrl string = hub.outputs.allowlistBlobUrl
output proxyUamiClientId string = hubIdentity.outputs.proxyClientId
output proxyBinaryUrl string = hub.outputs.proxyBinaryUrl
output sampleAppManagedIdentityClientId string = spoke.outputs.sampleAppManagedIdentityClientId
output sampleAppFqdn string = spoke.outputs.sampleAppFqdn
output sampleAppUrl string = spoke.outputs.sampleAppUrl
output caeDefaultDomain string = spoke.outputs.caeDefaultDomain
output rulesetsBlobName string = hub.outputs.rulesetsBlobName
output controlPlaneUrl string = deployControlPlane ? mgmt!.outputs.controlPlaneUrl : ''
output portalUrl string = deployPortal ? mgmt!.outputs.portalUrl : ''
