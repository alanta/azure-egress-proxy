@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Sample app container image.')
param sampleAppImage string

@description('Name of the container registry hosting the sample app image. It lives in the HUB resource group — it serves all three zones, so it is platform infrastructure — and this module needs only its name, to compose the login server and open the NSG. The AcrPull grant is in hub.bicep, next to the registry. Empty disables ACR wiring.')
param containerRegistryName string = ''

@description('Log Analytics workspace resource id backing the sample app Application Insights. Sharing the hub workspace keeps proxy decisions and app telemetry queryable side by side.')
param appInsightsWorkspaceResourceId string

@description('Expected proxy audience.')
param expectAud string

@description('Spoke VNet CIDR.')
param spokeVnetCidr string

@description('Spoke apps subnet CIDR.')
param spokeAppsSubnetCidr string

@description('Hub proxy subnet CIDR allowed as egress target.')
param proxySubnetCidr string

@description('Resource id of the sample-app user-assigned identity (spoke-identity.bicep). This is the identity the proxy authenticates: the `appid` claim the allowlist keys on IS this principal.')
param sampleAppIdentityResourceId string

@description('Client id of the sample-app user-assigned identity.')
param sampleAppIdentityClientId string

@description('Resource id of this environment\'s dedicated image-pull identity (spoke-identity.bicep). Attached to the sample app in addition to its functional identity, and the only principal holding AcrPull for this environment.')
param acrPullIdentityResourceId string

// Image pulls from ACR need AzureContainerRegistry plus Storage.<region>:443 — ACR
// Basic/Standard serve layer data from shared Azure Storage (per MS Learn "Securing a
// virtual network in Azure Container Apps with NSGs"). The Storage allow softens the
// exfiltration floor (any in-region storage account becomes reachable); the hardened
// alternative is ACR Premium with a private endpoint, which needs neither rule — see
// docs/production-hardening.md.
var acrNsgRules = containerRegistryName == '' ? [] : [
  {
    name: 'allow-acr'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 170
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzureContainerRegistry'
      destinationPortRange: '443'
      description: 'Sample app image pulls from ACR.'
    }
  }
  {
    name: 'allow-acr-storage'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 180
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'Storage.${location}'
      destinationPortRange: '443'
      description: 'ACR layer data is served from Azure Storage (no dedicated data endpoints below Premium).'
    }
  }
]

var appsNsgRules = [
  {
    name: 'allow-proxy-egress'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 100
      protocol: 'Tcp'
      sourceAddressPrefix: spokeAppsSubnetCidr
      sourcePortRange: '*'
      destinationAddressPrefix: proxySubnetCidr
      destinationPortRange: '4750'
      description: 'Route external HTTPS through the egress proxy.'
    }
  }
  {
    name: 'allow-mcr'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 110
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'MicrosoftContainerRegistry'
      destinationPortRange: '443'
      description: 'ACA platform dependency.'
    }
  }
  {
    name: 'allow-afd-firstparty'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 120
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzureFrontDoor.FirstParty'
      destinationPortRange: '443'
      description: 'ACA platform dependency.'
    }
  }
  {
    name: 'allow-aad'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 130
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzureActiveDirectory'
      destinationPortRange: '443'
      description: 'Managed identity token acquisition.'
    }
  }
  {
    name: 'allow-azure-monitor'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 140
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzureMonitor'
      destinationPortRange: '443'
      description: 'Logging and diagnostics.'
    }
  }
  {
    name: 'allow-dns'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 150
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: '168.63.129.16/32'
      destinationPortRange: '53'
      description: 'Azure DNS.'
    }
  }
  {
    name: 'allow-vnet'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 160
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'VirtualNetwork'
      destinationPortRange: '*'
      description: 'Intra-VNet and peered traffic.'
    }
  }
  {
    name: 'deny-internet'
    properties: {
      access: 'Deny'
      direction: 'Outbound'
      priority: 4000
      protocol: '*'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'Internet'
      destinationPortRange: '*'
      description: 'Egress floor: block direct internet.'
    }
  }
  // ACA external ingress is delivered through Azure's managed edge. These rules are an ACA
  // platform dependency for external ingress, NOT related to fronting anything with our own Front
  // Door — the standalone Front Door profile this repo once had was removed, and these are not it.
  //
  // TESTED, AND INERT ON THIS ENVIRONMENT. Deleting all three and re-probing an unauthenticated
  // endpoint returned 200 throughout: on an EXTERNAL workload-profile environment inbound traffic
  // reaches the app through the managed resource group's public IP, not through this subnet, so
  // these rules are never consulted. They are kept anyway, deliberately: they are the documented
  // rule set for an INTERNAL environment, which is the production posture for the console
  // (docs/production-hardening.md), and deleting them would plant a broken ingress for whoever
  // flips internal: true. Microsoft's reference names 31443 as the edge-proxy port behind the
  // internal load balancer, but gives the source as client IPs — AzureFrontDoor.Backend is this
  // repo's inference, and it has never had to be right because nothing evaluates it today.
  {
    name: 'allow-afd-backend-443'
    properties: {
      access: 'Allow'
      direction: 'Inbound'
      priority: 200
      protocol: 'Tcp'
      sourceAddressPrefix: 'AzureFrontDoor.Backend'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '443'
      description: 'ACA external ingress delivery (managed Front Door layer).'
    }
  }
  {
    name: 'allow-afd-backend-31443'
    properties: {
      access: 'Allow'
      direction: 'Inbound'
      priority: 210
      protocol: 'Tcp'
      sourceAddressPrefix: 'AzureFrontDoor.Backend'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '31443'
      description: 'ACA NSG evaluation occurs post-DNAT.'
    }
  }
  {
    name: 'allow-azure-load-balancer-inbound'
    properties: {
      access: 'Allow'
      direction: 'Inbound'
      priority: 220
      protocol: '*'
      sourceAddressPrefix: 'AzureLoadBalancer'
      sourcePortRange: '*'
      destinationAddressPrefix: '*'
      destinationPortRange: '*'
      description: 'ACA platform load-balancer health flows.'
    }
  }
]

module appsNsg 'br/public:avm/res/network/network-security-group:0.5.0' = {
  name: 'apps-nsg'
  params: {
    name: '${namePrefix}-apps-nsg'
    location: location
    securityRules: concat(appsNsgRules, acrNsgRules)
  }
}

module spokeVnet 'br/public:avm/res/network/virtual-network:0.9.0' = {
  name: 'spoke-vnet'
  params: {
    name: '${namePrefix}-spoke-vnet'
    location: location
    addressPrefixes: [
      spokeVnetCidr
    ]
    subnets: [
      {
        name: 'snet-apps'
        addressPrefix: spokeAppsSubnetCidr
        networkSecurityGroupResourceId: appsNsg.outputs.resourceId
        defaultOutboundAccess: false
        delegation: 'Microsoft.App/environments'
      }
    ]
  }
}

module appInsights 'br/public:avm/res/insights/component:0.6.0' = {
  name: 'sample-app-insights'
  params: {
    name: '${namePrefix}-sample-app-ai'
    location: location
    workspaceResourceId: appInsightsWorkspaceResourceId
    applicationType: 'web'
    kind: 'web'
  }
}

module managedEnvironment 'br/public:avm/res/app/managed-environment:0.13.0' = {
  name: 'managed-env'
  params: {
    name: '${namePrefix}-cae'
    location: location
    publicNetworkAccess: 'Enabled'
    internal: false
    zoneRedundant: false
    infrastructureSubnetResourceId: '${spokeVnet.outputs.resourceId}/subnets/snet-apps'
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
  }
}

// Console and platform logs to the hub workspace.
//
// destination 'azure-monitor', NOT 'log-analytics': the latter authenticates with the workspace
// SHARED KEY, and observability.bicep disables local auth on that workspace precisely because the
// key is a write credential for the audit table. 'azure-monitor' routes through the diagnostic
// setting below instead, which is Entra-authorised and carries no key.
//
// What this adds that OpenTelemetry does not: the applications already export logs, metrics and
// traces to Application Insights via ServiceDefaults (.UseAzureMonitor), so structured ILogger
// output is already queryable. ContainerAppSystemLogs is the part OTEL structurally cannot
// provide — image pull failures, probe failures, restarts and scaling events all happen when the
// application is not running, so nothing in-process can report them.

resource caeExisting 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: '${namePrefix}-cae'
  dependsOn: [
    managedEnvironment
  ]
}

resource caeDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-hub-workspace'
  scope: caeExisting
  properties: {
    workspaceId: appInsightsWorkspaceResourceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
  }
}


var sampleAppName = '${namePrefix}-sample-app'

module sampleApp 'br/public:avm/res/app/container-app:0.22.0' = {
  name: 'sample-app'
  params: {
    // Image pull is configured per application — Container Apps has no environment-level
    // equivalent — so the pull identity is referenced here even though it belongs to the
    // environment. The AcrPull grant behind it lives in hub.bicep, next to the registry.
    registries: containerRegistryName == '' ? [] : [
      {
        server: '${containerRegistryName}.azurecr.io'
        identity: acrPullIdentityResourceId
      }
    ]
    name: sampleAppName
    location: location
    environmentResourceId: managedEnvironment.outputs.resourceId
    ingressExternal: true
    ingressTargetPort: 8080
    ingressTransport: 'http'
    ingressAllowInsecure: false
    // Two user-assigned identities: the functional one and the environment's pull identity.
    // AZURE_CLIENT_ID below is what selects the functional one, and getting it wrong changes what
    // the proxy authenticates the app AS — which changes what the allowlist matches. With more
    // than one identity attached, an unset or wrong client id silently resolves to the other
    // principal.
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        sampleAppIdentityResourceId
        acrPullIdentityResourceId
      ]
    }
    // The app is reached directly on its ACA external ingress FQDN. This is a demo
    // whose subject is egress control, so ingress is intentionally left open rather
    // than fronted by Front Door / a WAF.
    // Scale to zero when idle (15 min cooldown) — this is a demo app, and a single
    // replica also keeps the proxy audit log to one source IP per revision.
    scaleSettings: {
      minReplicas: 0
      maxReplicas: 1
      cooldownPeriod: 900
    }
    containers: [
      {
        name: 'sample-app'
        image: sampleAppImage
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        env: [
          {
            name: 'HTTPS_PROXY'
            value: 'http://proxy.egress.internal:4750'
          }
          {
            name: 'NO_PROXY'
            // Platform telemetry goes direct (NSG allows the AzureMonitor tag); the
            // AI ingestion endpoints live under applicationinsights.azure.com and
            // livediagnostics.monitor.azure.com, not just monitor.azure.com.
            value: '169.254.169.254,localhost,${managedEnvironment.outputs.defaultDomain},.${managedEnvironment.outputs.defaultDomain},.monitor.azure.com,.applicationinsights.azure.com,.livediagnostics.monitor.azure.com,.blob.core.windows.net'
          }
          {
            // .NET config key EgressProxy:Audience — double underscore, not
            // SCREAMING_SNAKE (the app never reads EGRESS_PROXY_AUDIENCE).
            name: 'EgressProxy__Audience'
            value: expectAud
          }
          {
            name: 'EgressProxy__ClientId'
            value: sampleAppIdentityClientId
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: sampleAppIdentityClientId
          }
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: appInsights.outputs.connectionString
          }
        ]
      }
    ]
  }
}

output spokeVnetName string = spokeVnet.outputs.name
output spokeVnetResourceId string = spokeVnet.outputs.resourceId
output sampleAppManagedIdentityClientId string = sampleAppIdentityClientId
output sampleAppFqdn string = sampleApp.outputs.fqdn
// Public entry point for the demo — the app's own ACA external ingress.
output sampleAppUrl string = 'https://${sampleApp.outputs.fqdn}'
output caeDefaultDomain string = managedEnvironment.outputs.defaultDomain
