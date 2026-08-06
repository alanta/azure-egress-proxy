@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Principal object ID that should have write access to allowlist blobs. In the GitOps topology this is the CI identity that publishes allowlist.json; with the control plane deployed it is the PLATFORM team identity that seeds the state blob and edits its grants section — team pipelines never get a blob role in either case.')
param deployerPrincipalId string

@description('Deploy the control plane (Mode 2). Its identity is created in the management resource group and becomes the only other writer of the allowlist blobs; the proxy stays read-only.')
param deployControlPlane bool = false

@description('Deploy the read-only management console (Mode 3). Its identity is created in the management resource group and granted Reader + Log Analytics Reader on THIS resource group and NO write role anywhere — in particular no role on the allowlist storage, and no path to the workspace shared key.')
param deployPortal bool = false

// ── Principals granted on the resources this module owns ───────────────────────────────────────
// Every identity is created in the resource group of its own compute, by the *-identity.bicep
// modules that run before this one; every role assignment lives in the module that owns the
// resource it grants on. The hub owns the config storage account, the registry and the platform
// the console reads, so nearly every grant in the deployment collects here — which is the intent:
// one place to read to learn who can touch the shared resources. See design.md § Dependency
// ordering.

@description('Principal id of the proxy identity (hub-identity.bicep). Granted Storage Blob Data Reader on the config account.')
param proxyIdentityPrincipalId string

@description('Client id of the proxy identity, baked into cloud-init so the proxy selects the right user-assigned identity.')
param proxyIdentityClientId string

@description('Resource id of the proxy identity, attached to the scale set.')
param proxyIdentityResourceId string

@description('Principal id of the control-plane identity (mgmt-identity.bicep). Granted Storage Blob Data Contributor on the config account. Empty when the control plane is not deployed.')
param controlPlaneIdentityPrincipalId string = ''

@description('Principal id of the console identity (mgmt-identity.bicep). Granted Reader + Log Analytics Reader on this resource group. Empty when the console is not deployed.')
param portalIdentityPrincipalId string = ''

@description('Principal id of the workload environment\'s image-pull identity (spoke-identity.bicep). Granted AcrPull on the registry.')
param spokeAcrPullPrincipalId string = ''

@description('Principal id of the management environment\'s image-pull identity (mgmt-identity.bicep). Granted AcrPull on the registry. Empty when the management zone is not deployed.')
param mgmtAcrPullPrincipalId string = ''

@description('Name of the container registry created by bootstrap.bicep in this resource group. Empty when every image is given as an already-pullable reference, in which case there is no registry and nothing to grant on.')
param containerRegistryName string = ''

@description('Name of the bootstrap storage account created by bootstrap.bicep in this resource group, holding the proxy binary.')
param bootstrapStorageAccountName string

@description('Container in the bootstrap account holding the proxy binary.')
param bootstrapContainerName string = 'proxy-bin'

@description('Blob name of the proxy binary in the bootstrap container.')
param bootstrapBlobName string = 'egress-proxy_linux_arm64'

@description('JWKS URL used for proxy identity validation.')
param jwksUrl string

@description('Expected token issuer used by the proxy.')
param expectIss string

@description('Expected token audience used by the proxy.')
param expectAud string

@description('Override for the URL cloud-init fetches the linux-arm64 egress-proxy binary from. Empty (the default) composes it from the bootstrap account below, which is where deploy.sh seeds it. Set it only to point at a binary you host yourself; it must be an http(s) URL reachable from the proxy subnet, because the VM fetches it with an unauthenticated curl before it holds any token.')
param proxyBinaryUrl string = ''

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

@description('Idle timeout (minutes) for both legs of a CONNECT tunnel: the internal LB rule (client → proxy) and the instance public IP SNAT (proxy → destination). Clients must close pooled tunnels sooner than this — see docs/production-hardening.md § Idle timeouts.')
@minValue(4)
@maxValue(30)
param proxyIdleTimeoutInMinutes int = 4

var allowlistContainerName = 'egress-config'
var allowlistBlobName = 'allowlist.json'
// The control plane's own state (rulesets + platform grants). Only it writes this; the proxy
// never reads it — the proxy consumes the rendered allowlistBlobName above.
var rulesetsBlobName = 'rulesets.json'
var proxyPort = 4750

// Empty on purpose, and it is the one exemption in the deployment. The proxy is the component
// whose whole job is to reach arbitrary allowed destinations on the Internet, so its policy is the
// allowlist it enforces rather than a network rule — putting the workload egress floor here would
// mean an NSG deciding what the allowlist is for. Every other subnet carries the floor; this
// subnet runs on Azure defaults. An empty list reads as unfinished, so: it is not.
var proxyNsgRules = []

// The registry is created by bootstrap.bicep (phase 1) in this resource group, because the images
// must be imported into it before any application references them. Referenced as existing so the
// AcrPull assignments below can be scoped to it.
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (containerRegistryName != '') {
  name: containerRegistryName
}

// Likewise the bootstrap storage account, so the binary URL is composed from the account rather
// than string-built by the caller — which also keeps it correct in sovereign clouds, where the
// blob suffix is not blob.core.windows.net.
resource bootstrapStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: bootstrapStorageAccountName
}

var resolvedProxyBinaryUrl = proxyBinaryUrl != ''
  ? proxyBinaryUrl
  : '${bootstrapStorage.properties.primaryEndpoints.blob}${bootstrapContainerName}/${bootstrapBlobName}'

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
                resolvedProxyBinaryUrl
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
      proxyIdentityClientId
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

// ============================================================================================
// Grants on the shared platform resources
//
// The console's identity is created in the management resource group, with its compute. Its roles
// are scoped HERE, because this is where everything it reads lives: the proxy scale set, the
// egress prefix, the load balancer and the Log Analytics workspace. It holds nothing on its own
// resource group, which is deliberate — the console must not be able to read the deployment of the
// thing that authenticates it — and nothing on the configuration storage account either (see the
// roleAssignments on allowlistStorage below): it reaches policy through the control-plane API's
// read endpoints and cannot touch the blobs at all.
//
// Both are declared inline rather than through a module: this template is already resource-group
// scoped, so a resource-group-scoped assignment needs no scope indirection at all. The two
// AcrPull grants further down do need one, and use the AVM module for it.
//
// The names are guid(scope, principal, role) — deterministic on the three things that actually
// identify a grant. They used to be guid(resourceGroup().id, 'portal', 'Reader') with string
// literals standing in for the principal and the role, which meant changing the principal left the
// assignment name unchanged and ARM updated it in place instead of replacing it.
// ============================================================================================

var readerRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'acdd72a7-3385-48ef-bd42-f606fba81ae7' // Reader
)
// Log Analytics Reader, NOT Monitoring Reader — and the difference is the whole point.
//
// Both are "read-only" roles built on `*/read`. But `*/read` matches
// `Microsoft.OperationalInsights/workspaces/sharedKeys/read`, and the workspace shared key is a
// WRITE credential: it authenticates the legacy Data Collector API, which appends rows to custom
// tables. A principal that can read the key can forge rows into EgressProxy_CL — the audit trail
// this whole deployment exists to produce.
//
// Monitoring Reader has no `notActions`, so it grants that key read. Log Analytics Reader excludes
// it explicitly, which is the tell that the operation exists and that `*/read` would otherwise
// cover it. Its action set is otherwise a superset of Monitoring Reader's (it adds
// `workspaces/analytics/query/action`), so the console loses nothing it uses: metrics, scale-set
// state and the KQL over EgressProxy_CL all still work.
//
// Paired with `disableLocalAuth: true` on the workspace (observability.bicep), which stops the key
// working at all. Two independent controls, because "the console writes nothing" is an invariant
// and one mechanism is not a guarantee.
var monitoringReaderRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '73c42c96-874c-492b-b04d-ab87d138a893' // Log Analytics Reader
)
var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull
)

// Reader: the ARM configuration the runtime surface renders — scale-set capacity and instance
// view, public-IP-prefix consumption, load-balancer shape. Scoped to this resource group and no
// wider, so the console can see the egress platform and nothing else in the subscription.
resource portalReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPortal) {
  name: guid(resourceGroup().id, portalIdentityPrincipalId, readerRoleDefinitionId)
  properties: {
    roleDefinitionId: readerRoleDefinitionId
    principalId: portalIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The workspace data access: the metric series and the KQL over EgressProxy_CL. `Reader` alone
// does not grant it, which is why this is a second assignment rather than an oversight. See the
// note on monitoringReaderRoleDefinitionId above for why it is Log Analytics Reader specifically.
resource portalMonitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPortal) {
  name: guid(resourceGroup().id, portalIdentityPrincipalId, monitoringReaderRoleDefinitionId)
  properties: {
    roleDefinitionId: monitoringReaderRoleDefinitionId
    principalId: portalIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// AcrPull, once per Container Apps environment. The grantee is each environment's dedicated pull
// identity, not the applications: image pull is a property of the hosting environment, and the
// sample app's identity in particular is the one the proxy authenticates — the `appid` claim the
// allowlist keys on IS that principal, so it must not also mean "may read the registry".
//
// Both assignments live here rather than beside the applications because the registry lives here,
// and every grant belongs in the module that owns the resource being granted on. Both are
// conditional on there being a registry at all: with every image given as an already-pullable
// reference, bootstrap.bicep deploys none and there is nothing to grant.
//
// The AVM pattern module is what makes a resource-scoped assignment expressible without declaring
// the registry's type in a wrapper of our own — it does the scoping through a nested ARM
// deployment, which plain Bicep cannot express. Its default assignment name is exactly
// guid(resourceId, principalId, roleDefinitionId).
module spokeAcrPull 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.2' = if (containerRegistryName != '') {
  name: 'spoke-acr-pull'
  params: {
    resourceId: containerRegistry.id
    principalId: spokeAcrPullPrincipalId
    roleDefinitionId: acrPullRoleDefinitionId
    principalType: 'ServicePrincipal'
    roleName: 'AcrPull'
    description: 'Image pull for the workload Container Apps environment.'
  }
}

module mgmtAcrPull 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.2' = if (containerRegistryName != '' && deployControlPlane) {
  name: 'mgmt-acr-pull'
  params: {
    resourceId: containerRegistry.id
    principalId: mgmtAcrPullPrincipalId
    roleDefinitionId: acrPullRoleDefinitionId
    principalType: 'ServicePrincipal'
    roleName: 'AcrPull'
    description: 'Image pull for the management Container Apps environment.'
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
          // A CONNECT tunnel that idles past this is reaped by the LB. Without
          // enableTcpReset that reap is silent: both sockets stay ESTABLISHED and the
          // client's next write black-holes until TCP retransmit gives up, minutes later.
          // With it, the client fails fast with ECONNRESET — the NAT Gateway analog.
          idleTimeoutInMinutes: proxyIdleTimeoutInMinutes
          enableTcpReset: true
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
    // The proxy only ever reads; with the control plane deployed, its identity is the only
    // service that writes. Workload team pipelines hold NO role here in either topology —
    // under Mode 2 they reach the config exclusively through the control-plane API.
    roleAssignments: concat(
      [
        {
          principalId: proxyIdentityPrincipalId
          roleDefinitionIdOrName: 'Storage Blob Data Reader'
          principalType: 'ServicePrincipal'
        }
        {
          // No principalType: deploy.sh passes a User locally and a Service Principal
          // from CI (OIDC); a mismatched hint fails the role assignment.
          principalId: deployerPrincipalId
          roleDefinitionIdOrName: 'Storage Blob Data Contributor'
        }
      ],
      deployControlPlane
        ? [
            {
              principalId: controlPlaneIdentityPrincipalId
              roleDefinitionIdOrName: 'Storage Blob Data Contributor'
              principalType: 'ServicePrincipal'
            }
          ]
        : []
    )
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
              // Second idle timer: the SNAT flow out to the destination. Kept equal to the
              // LB rule's so both legs of a tunnel expire together rather than leaving a
              // half-dead tunnel the client still believes in.
              idleTimeoutInMinutes: proxyIdleTimeoutInMinutes
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
    // Non-zonal (regional) VMSS: the smallest burstable SKUs aren't offered in every
    // availability zone (e.g. B2pts_v2 is zone 3 only in swedencentral), and logical
    // zone numbers are per-subscription mappings, so pinning specific zones is fragile
    // across SKUs/regions. The Standard LB frontend stays zone-redundant regardless.
    availabilityZones: []
    overprovision: false
    singlePlacementGroup: false
    vmNamePrefix: 'egproxy'
    // Azure Linux 3.0 (arm64, Gen2) — a minimal single-purpose appliance base with a
    // small attack surface and a leaner idle footprint than Ubuntu, which matters most
    // on the smallest burstable SKU (see proxyVmSku / infra/README.md). cloud-init drives
    // tdnf, not apt. NB: Azure Linux *4.0* is intentionally not used yet — the Azure
    // Monitor agent (AMA, feeds EgressProxy_CL) does not support azurelinux 4 as of AMA
    // 1.42.0 and terminal-fails the VMSS; 3.0 is the newest AMA-supported Azure Linux.
    imageReference: {
      publisher: 'MicrosoftCBLMariner'
      offer: 'azure-linux-3'
      sku: 'azure-linux-3-arm64'
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
        proxyIdentityResourceId
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
    proxyIdentityResourceId: proxyIdentityResourceId
  }
}

output hubVnetName string = hubVnet.outputs.name
output hubVnetResourceId string = hubVnet.outputs.resourceId
output proxyLoadBalancerPrivateIp string = proxyLoadBalancerPrivateIp
output proxyVmssName string = proxyVmss.outputs.name
output allowlistStorageAccountName string = allowlistStorage.outputs.name
output allowlistContainerName string = allowlistContainerName
output allowlistBlobName string = allowlistBlobName
output allowlistBlobUrl string = 'https://${allowlistStorage.outputs.name}.blob.${environment().suffixes.storage}/${allowlistContainerName}/${allowlistBlobName}'
output rulesetsBlobName string = rulesetsBlobName
output storageServiceUrl string = 'https://${allowlistStorage.outputs.name}.blob.${environment().suffixes.storage}'
output workspaceResourceId string = observability.outputs.workspaceResourceId

// The URL cloud-init actually fetched the binary from, so deploy.sh's --refresh-binary hot-swap
// targets the same bytes the scale set booted on.
output proxyBinaryUrl string = resolvedProxyBinaryUrl

// The names of the four hub resources the console reads. Passed through by name rather than
// resource id because the portal's ARM client composes ids from its own subscription and
// resource-group configuration. The identity itself is no longer here: it is created in the
// management resource group, with the compute it belongs to, and only its GRANTS are in this
// module (see portalReader / portalMonitoringReader above).
output proxyPublicIpPrefixName string = proxyPublicIpPrefix.outputs.name
output proxyLoadBalancerName string = loadBalancerName

// The workspace GUID the Log Analytics query API takes, which is NOT the ARM resource id.
output workspaceCustomerId string = observability.outputs.workspaceCustomerId
