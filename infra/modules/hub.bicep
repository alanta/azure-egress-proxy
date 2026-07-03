@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Principal object ID that should have write access to allowlist blobs.')
param deployerPrincipalId string

@description('JWKS URL used for proxy identity validation.')
param jwksUrl string

@description('Expected token issuer used by the proxy.')
param expectIss string

@description('Expected token audience used by the proxy.')
param expectAud string

@description('Public release URL for the linux-arm64 egress-proxy binary.')
param proxyBinaryUrl string

@description('SHA256 hash for the proxy binary.')
@secure()
param proxyBinarySha256 string

@description('SSH public key for break-glass access.')
param vmAdminPublicKey string

@description('Proxy VM size.')
param proxyVmSku string

@description('Enable encryption at host on the VMSS. Requires the Microsoft.Compute/EncryptionAtHost feature to be registered on the subscription.')
param encryptionAtHost bool = false

@description('Proxy VMSS instance count.')
param proxyInstanceCount int

@description('Public IP prefix length used for proxy egress.')
param proxyPublicIpPrefixLength int

@description('Hub VNet CIDR.')
param hubVnetCidr string

@description('Hub proxy subnet CIDR.')
param hubProxySubnetCidr string

@description('Internal LB private frontend IP.')
param proxyLoadBalancerPrivateIp string

var allowlistContainerName = 'egress-config'
var allowlistBlobName = 'allowlist.json'
var proxyPort = 4750

var proxyNsgRules = []

var cloudInitTemplate = loadTextContent('../assets/cloud-init.yaml')
var cloudInit = replace(
  replace(
    replace(
      replace(
        replace(
          replace(
            replace(
              replace(
                cloudInitTemplate,
                '__PROXY_BINARY_URL__',
                proxyBinaryUrl
              ),
              '__PROXY_BINARY_SHA256__',
              proxyBinarySha256
            ),
            '__JWKS_URL__',
            jwksUrl
          ),
          '__EXPECT_ISS__',
          expectIss
        ),
        '__EXPECT_AUD__',
        expectAud
      ),
      '__AZURE_CLIENT_ID__',
      proxyIdentity.outputs.clientId
    ),
    '__ALLOWLIST_BLOB_URL__',
    'https://${allowlistStorage.outputs.name}.blob.${environment().suffixes.storage}/${allowlistContainerName}/${allowlistBlobName}'
  ),
  '__POLL_SECONDS__',
  '5'
)

module proxyNsg 'br/public:avm/res/network/network-security-group:0.5.0' = {
  name: 'proxy-nsg'
  params: {
    name: '${namePrefix}-proxy-nsg'
    location: location
    securityRules: proxyNsgRules
  }
}

module hubVnet 'br/public:avm/res/network/virtual-network:0.9.0' = {
  name: 'hub-vnet'
  params: {
    name: '${namePrefix}-hub-vnet'
    location: location
    addressPrefixes: [
      hubVnetCidr
    ]
    subnets: [
      {
        name: 'snet-proxy'
        addressPrefix: hubProxySubnetCidr
        networkSecurityGroupResourceId: proxyNsg.outputs.resourceId
      }
    ]
  }
}

module proxyIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'proxy-uami'
  params: {
    name: '${namePrefix}-proxy-uami'
    location: location
  }
}

module proxyPublicIpPrefix 'br/public:avm/res/network/public-ip-prefix:0.8.0' = {
  name: 'proxy-public-prefix'
  params: {
    name: '${namePrefix}-proxy-egress-prefix'
    location: location
    prefixLength: proxyPublicIpPrefixLength
  }
}

// Raw resource instead of AVM (documented exception): the AVM load-balancer module
// PUTs backend pools as standalone child resources, which fails on re-deploy with
// ModificationOfNICIpConfigBackendPoolNotSupported once Uniform-VMSS NICs have joined
// the pool. Inline pools in the LB body don't touch NIC-side membership.
var loadBalancerName = '${namePrefix}-proxy-ilb'

resource proxyLoadBalancer 'Microsoft.Network/loadBalancers@2024-05-01' = {
  name: loadBalancerName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    frontendIPConfigurations: [
      {
        name: 'proxy-frontend'
        zones: [
          '1'
          '2'
          '3'
        ]
        properties: {
          subnet: {
            id: '${hubVnet.outputs.resourceId}/subnets/snet-proxy'
          }
          privateIPAddress: proxyLoadBalancerPrivateIp
          privateIPAllocationMethod: 'Static'
        }
      }
    ]
    backendAddressPools: [
      {
        name: 'proxy-backend'
      }
    ]
    probes: [
      {
        name: 'proxy-tcp-probe'
        properties: {
          protocol: 'Tcp'
          port: proxyPort
          intervalInSeconds: 5
          numberOfProbes: 2
        }
      }
    ]
    loadBalancingRules: [
      {
        name: 'proxy-tcp-rule'
        properties: {
          frontendIPConfiguration: {
            id: resourceId('Microsoft.Network/loadBalancers/frontendIPConfigurations', loadBalancerName, 'proxy-frontend')
          }
          backendAddressPool: {
            id: resourceId('Microsoft.Network/loadBalancers/backendAddressPools', loadBalancerName, 'proxy-backend')
          }
          probe: {
            id: resourceId('Microsoft.Network/loadBalancers/probes', loadBalancerName, 'proxy-tcp-probe')
          }
          protocol: 'Tcp'
          frontendPort: proxyPort
          backendPort: proxyPort
        }
      }
    ]
  }
}

module allowlistStorage 'br/public:avm/res/storage/storage-account:0.32.0' = {
  name: 'allowlist-storage'
  params: {
    name: take(replace('${namePrefix}${uniqueString(subscription().id, resourceGroup().name, 'allowlist')}', '-', ''), 24)
    location: location
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    // The demo posture is public endpoint + Entra-only RBAC; the AVM default
    // (defaultAction Deny) silently blocks the proxy's reads and the seed upload.
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    supportsHttpsTrafficOnly: true
    blobServices: {
      containers: [
        {
          name: allowlistContainerName
        }
      ]
      isVersioningEnabled: true
      deleteRetentionPolicyEnabled: true
      deleteRetentionPolicyDays: 14
      containerDeleteRetentionPolicyEnabled: true
      containerDeleteRetentionPolicyDays: 14
    }
    roleAssignments: [
      {
        principalId: proxyIdentity.outputs.principalId
        roleDefinitionIdOrName: 'Storage Blob Data Reader'
        principalType: 'ServicePrincipal'
      }
      {
        // No principalType: deploy.sh passes a User locally and a Service Principal
        // from CI (OIDC); a mismatched hint fails the role assignment.
        principalId: deployerPrincipalId
        roleDefinitionIdOrName: 'Storage Blob Data Contributor'
      }
    ]
  }
}

var vmssNicConfiguration = [
  {
    name: 'proxy-nic'
    enableAcceleratedNetworking: true
    ipConfigurations: [
      {
        name: 'proxy-ipconfig'
        properties: {
          subnet: {
            id: '${hubVnet.outputs.resourceId}/subnets/snet-proxy'
          }
          loadBalancerBackendAddressPools: [
            {
              id: '${proxyLoadBalancer.id}/backendAddressPools/proxy-backend'
            }
          ]
          publicIPAddressConfiguration: {
            name: 'proxy-pip'
            sku: {
              name: 'Standard'
              tier: 'Regional'
            }
            properties: {
              publicIPAddressVersion: 'IPv4'
              publicIPPrefix: {
                id: proxyPublicIpPrefix.outputs.resourceId
              }
            }
          }
        }
      }
    ]
  }
]

module proxyVmss 'br/public:avm/res/compute/virtual-machine-scale-set:0.11.0' = {
  name: 'proxy-vmss'
  params: {
    name: '${namePrefix}-proxy-vmss'
    location: location
    osType: 'Linux'
    skuName: proxyVmSku
    skuCapacity: proxyInstanceCount
    orchestrationMode: 'Uniform'
    // Manual (the default) strands existing instances on the old model when the
    // deployment later adds the AMA extension — logs silently never arrive.
    upgradePolicyMode: 'Automatic'
    availabilityZones: [
      1
      2
    ]
    overprovision: false
    singlePlacementGroup: false
    vmNamePrefix: 'egproxy'
    imageReference: {
      publisher: 'Canonical'
      offer: 'ubuntu-24_04-lts'
      sku: 'server-arm64'
      version: 'latest'
    }
    osDisk: {
      caching: 'ReadWrite'
      createOption: 'FromImage'
      managedDisk: {
        storageAccountType: 'Premium_LRS'
      }
    }
    adminUsername: 'proxyadmin'
    adminPassword: ''
    // Guest patching isn't supported on Uniform VMSS; the AVM default
    // (AutomaticByPlatform) fails deployment with "patchMode is not allowed".
    patchMode: ''
    // The AVM default (true) fails on subscriptions without the
    // Microsoft.Compute/EncryptionAtHost feature registered.
    encryptionAtHost: encryptionAtHost
    disablePasswordAuthentication: true
    publicKeys: [
      {
        path: '/home/proxyadmin/.ssh/authorized_keys'
        keyData: vmAdminPublicKey
      }
    ]
    customData: cloudInit
    nicConfigurations: vmssNicConfiguration
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        proxyIdentity.outputs.resourceId
      ]
    }
    extensionHealthConfig: {
      enabled: true
      protocol: 'tcp'
      port: proxyPort
      intervalInSeconds: 5
      numberOfProbes: 2
    }
  }
}

module observability 'observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    namePrefix: namePrefix
    vmssName: proxyVmss.outputs.name
    proxyIdentityResourceId: proxyIdentity.outputs.resourceId
  }
}

output hubVnetName string = hubVnet.outputs.name
output hubVnetResourceId string = hubVnet.outputs.resourceId
output proxyLoadBalancerPrivateIp string = proxyLoadBalancerPrivateIp
output proxyUamiClientId string = proxyIdentity.outputs.clientId
output proxyVmssName string = proxyVmss.outputs.name
output allowlistStorageAccountName string = allowlistStorage.outputs.name
output allowlistContainerName string = allowlistContainerName
output allowlistBlobName string = allowlistBlobName
output allowlistBlobUrl string = 'https://${allowlistStorage.outputs.name}.blob.${environment().suffixes.storage}/${allowlistContainerName}/${allowlistBlobName}'
output workspaceResourceId string = observability.outputs.workspaceResourceId
