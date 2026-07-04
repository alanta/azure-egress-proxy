targetScope = 'subscription'

@description('Primary deployment location.')
param location string

@description('Hub resource group name.')
param hubResourceGroupName string = 'rg-egress-hub'

@description('Spoke resource group name.')
param spokeResourceGroupName string = 'rg-egress-spoke'

@description('Name prefix for deployed resources.')
param namePrefix string = 'egress'

@description('Principal object ID that should be able to update allowlist blobs.')
param deployerPrincipalId string

@description('Tenant ID used for identity validation.')
param tenantId string

@description('JWKS endpoint used by the proxy for token validation.')
param jwksUrl string = '${environment().authentication.loginEndpoint}${tenantId}/discovery/v2.0/keys'

@description('Expected issuer used by the proxy for token validation.')
param expectIss string = '${environment().authentication.loginEndpoint}${tenantId}/v2.0'

@description('Expected audience used by the proxy for token validation.')
param expectAud string

@description('Public release URL for the linux-arm64 egress-proxy binary.')
param proxyBinaryUrl string = 'https://github.com/alanta/azure-egress-proxy/releases/latest/download/egress-proxy_linux_arm64'

@description('SHA256 for the proxy binary.')
param proxyBinarySha256 string

@description('SSH public key for break-glass access over private network paths.')
param vmAdminPublicKey string

@description('Container image used by the sample app. Must listen on 8080 (the ingress target port) and be pullable under the egress floor — MCR and the ACR named in containerRegistryName are allowed by the NSG service tags, GHCR is not (deploy.sh imports the GHCR image into the ACR).')
param sampleAppImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Name of an existing Azure Container Registry in the spoke resource group that hosts the sample app image. Empty means no ACR wiring (sampleAppImage must then be pullable from MCR).')
param containerRegistryName string = ''

@description('Proxy VM size.')
param proxyVmSku string = 'Standard_D2pls_v6'

@description('Enable encryption at host on the proxy VMSS (requires the Microsoft.Compute/EncryptionAtHost subscription feature).')
param encryptionAtHost bool = false

@description('Proxy VMSS instance count.')
@minValue(1)
param proxyInstanceCount int = 2

@description('Public IP prefix length for proxy egress addresses.')
@minValue(28)
@maxValue(31)
param proxyPublicIpPrefixLength int = 31

@description('Hub VNet CIDR.')
param hubVnetCidr string = '10.0.0.0/22'

@description('Hub proxy subnet CIDR.')
param hubProxySubnetCidr string = '10.0.0.0/24'

@description('Spoke VNet CIDR.')
param spokeVnetCidr string = '10.1.0.0/22'

@description('Spoke apps subnet CIDR.')
param spokeAppsSubnetCidr string = '10.1.0.0/23'

@description('Static private LB frontend IP for the proxy.')
param proxyLoadBalancerPrivateIp string = '10.0.0.4'

@description('Tags applied to both resource groups. Handy where subscription policy mandates tags (Owner, Purpose, ...).')
param resourceGroupTags object = {}

module hubRg 'br/public:avm/res/resources/resource-group:0.4.0' = {
  name: 'hub-rg'
  params: {
    name: hubResourceGroupName
    location: location
    tags: resourceGroupTags
  }
}

module spokeRg 'br/public:avm/res/resources/resource-group:0.4.0' = {
  name: 'spoke-rg'
  params: {
    name: spokeResourceGroupName
    location: location
    tags: resourceGroupTags
  }
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
    hubVnetCidr: hubVnetCidr
    hubProxySubnetCidr: hubProxySubnetCidr
    proxyLoadBalancerPrivateIp: proxyLoadBalancerPrivateIp
  }
  dependsOn: [
    hubRg
  ]
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
  }
  dependsOn: [
    spokeRg
  ]
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
output allowlistStorageAccountName string = hub.outputs.allowlistStorageAccountName
output allowlistContainerName string = hub.outputs.allowlistContainerName
output allowlistBlobName string = hub.outputs.allowlistBlobName
output allowlistBlobUrl string = hub.outputs.allowlistBlobUrl
output proxyUamiClientId string = hub.outputs.proxyUamiClientId
output sampleAppManagedIdentityClientId string = spoke.outputs.sampleAppManagedIdentityClientId
output sampleAppFqdn string = spoke.outputs.sampleAppFqdn
output sampleAppUrl string = spoke.outputs.sampleAppUrl
output caeDefaultDomain string = spoke.outputs.caeDefaultDomain
