@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Sample app container image.')
param sampleAppImage string

@description('Name of an existing ACR in this resource group hosting the sample app image; empty disables ACR wiring.')
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

@description('Deploy the control-plane API (Mode 2) into this managed environment.')
param deployControlPlane bool = false

@description('Container image for the control-plane API.')
param controlPlaneImage string = ''

@description('Resource id of the control-plane user-assigned identity (created in the hub, next to the storage it owns).')
param controlPlaneIdentityResourceId string = ''

@description('Client id of the control-plane user-assigned identity.')
param controlPlaneIdentityClientId string = ''

@description('Principal id of the control-plane user-assigned identity, used for the ACR pull role assignment.')
param controlPlaneIdentityPrincipalId string = ''

@description('Deploy the read-only management console (Mode 3) into this managed environment.')
param deployPortal bool = false

@description('Container image for the management console.')
param portalImage string = ''

@description('Resource id of the portal user-assigned identity (created in the hub, alongside everything it reads).')
param portalIdentityResourceId string = ''

@description('Client id of the portal user-assigned identity.')
param portalIdentityClientId string = ''

@description('Principal id of the portal user-assigned identity, used for the ACR pull role assignment.')
param portalIdentityPrincipalId string = ''

@description('Source IP ranges permitted to reach the console. Empty means no network restriction.')
param portalAllowedSourceIps array = []

@description('Application (client) ID of the Entra app registration the console signs operators in with. Created out of band — an app registration is not an ARM resource. REQUIRED when deployPortal is true: without it the platform performs no authentication and the console would serve the whole egress posture to anyone who reaches it.')
param portalAuthClientId string = ''

@description('Client secret for the console\'s Entra app registration. Required alongside portalAuthClientId.')
@secure()
param portalAuthClientSecret string = ''

@description('Hub resource group holding the proxy scale set, egress prefix, load balancer and workspace — the only scope the portal identity holds a role on.')
param hubResourceGroupName string = ''

@description('Log Analytics workspace GUID (not the ARM resource id) that the console queries EgressProxy_CL through.')
param workspaceCustomerId string = ''

@description('Proxy scale set name, for the console runtime surface.')
param proxyVmssName string = ''

@description('Egress public IP prefix name, for the console IP-pool panel.')
param proxyPublicIpPrefixName string = ''

@description('Proxy internal load balancer name, for the console health panels.')
param proxyLoadBalancerName string = ''

@description('Blob service endpoint holding the allowlist and ruleset blobs.')
param storageServiceUrl string = ''

@description('Name of the blob holding the control plane state (rulesets + platform grants).')
param rulesetsBlobName string = 'rulesets.json'

@description('Name of the rendered blob the proxy reads.')
param allowlistBlobName string = 'allowlist.json'

@description('Name of the container holding both blobs.')
param allowlistContainerName string = 'egress-config'

@description('JWKS endpoint the control plane validates caller tokens against.')
param jwksUrl string = ''

@description('Expected issuer for caller tokens.')
param expectIss string = ''

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
  // ACA external ingress is delivered to the subnet through Azure's managed Front Door
  // layer, so inbound traffic to the app arrives from the AzureFrontDoor.Backend service
  // tag (post-DNAT on 31443). These rules are an ACA platform dependency for external
  // ingress — NOT related to fronting the app with our own Front Door — so they stay even
  // though the standalone Front Door profile was removed.
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

module sampleAppIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'sample-app-uami'
  params: {
    name: '${namePrefix}-sample-app-uami'
    location: location
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
  }
}

var sampleAppName = '${namePrefix}-sample-app'

// The ACR is created by deploy.sh before this deployment runs (the image must exist
// before the container app first pulls it), so it is referenced as existing here.
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (containerRegistryName != '') {
  name: containerRegistryName
}

var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull
)

resource sampleAppAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (containerRegistryName != '') {
  name: guid(subscription().id, resourceGroup().name, containerRegistryName, 'sample-app-acr-pull')
  scope: containerRegistry
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: sampleAppIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

module sampleApp 'br/public:avm/res/app/container-app:0.22.0' = {
  name: 'sample-app'
  dependsOn: [
    sampleAppAcrPull // pull auth must exist before the app first provisions
  ]
  params: {
    registries: containerRegistryName == '' ? [] : [
      {
        server: '${containerRegistryName}.azurecr.io'
        identity: sampleAppIdentity.outputs.resourceId
      }
    ]
    name: sampleAppName
    location: location
    environmentResourceId: managedEnvironment.outputs.resourceId
    ingressExternal: true
    ingressTargetPort: 8080
    ingressTransport: 'http'
    ingressAllowInsecure: false
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        sampleAppIdentity.outputs.resourceId
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
            value: sampleAppIdentity.outputs.clientId
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: sampleAppIdentity.outputs.clientId
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

resource controlPlaneAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployControlPlane && containerRegistryName != '') {
  name: guid(subscription().id, resourceGroup().name, containerRegistryName, 'control-plane-acr-pull')
  scope: containerRegistry
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: controlPlaneIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The control plane shares the spoke's managed environment for demo economy, but it is platform
// infrastructure: its identity lives in the hub and it is the sole writer of the allowlist blobs.
// Its own egress goes direct to storage and the IdP (both in NO_PROXY) rather than through the
// proxy — the control plane must not depend on the data plane it configures.
module controlPlane 'br/public:avm/res/app/container-app:0.22.0' = if (deployControlPlane) {
  name: 'control-plane'
  dependsOn: [
    controlPlaneAcrPull
  ]
  params: {
    registries: containerRegistryName == '' ? [] : [
      {
        server: '${containerRegistryName}.azurecr.io'
        identity: controlPlaneIdentityResourceId
      }
    ]
    name: '${namePrefix}-control-plane'
    location: location
    environmentResourceId: managedEnvironment.outputs.resourceId
    ingressExternal: true
    ingressTargetPort: 8080
    ingressTransport: 'http'
    ingressAllowInsecure: false
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        controlPlaneIdentityResourceId
      ]
    }
    scaleSettings: {
      minReplicas: 0
      maxReplicas: 1
      cooldownPeriod: 900
    }
    containers: [
      {
        name: 'control-plane'
        image: controlPlaneImage
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        env: [
          {
            name: 'ASPNETCORE_URLS'
            value: 'http://+:8080'
          }
          {
            name: 'NO_PROXY'
            value: '169.254.169.254,localhost,${managedEnvironment.outputs.defaultDomain},.${managedEnvironment.outputs.defaultDomain},.monitor.azure.com,.applicationinsights.azure.com,.livediagnostics.monitor.azure.com,.blob.core.windows.net'
          }
          {
            // Managed-identity access to the blobs; AZURE_CLIENT_ID selects the user-assigned one.
            name: 'STORAGE_SERVICE_URL'
            value: storageServiceUrl
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: controlPlaneIdentityClientId
          }
          {
            name: 'ALLOWLIST_CONTAINER'
            value: allowlistContainerName
          }
          {
            name: 'ALLOWLIST_BLOB'
            value: allowlistBlobName
          }
          {
            name: 'RULESETS_BLOB'
            value: rulesetsBlobName
          }
          {
            // The same token validation the proxy performs, so one identity model covers both planes.
            name: 'JWKS_URL'
            value: jwksUrl
          }
          {
            name: 'EXPECT_ISS'
            value: expectIss
          }
          {
            name: 'EXPECT_AUD'
            value: expectAud
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

output controlPlaneFqdn string = deployControlPlane ? controlPlane!.outputs.fqdn : ''
output controlPlaneUrl string = deployControlPlane ? 'https://${controlPlane!.outputs.fqdn}' : ''
output spokeVnetName string = spokeVnet.outputs.name
output spokeVnetResourceId string = spokeVnet.outputs.resourceId
output sampleAppManagedIdentityClientId string = sampleAppIdentity.outputs.clientId
output sampleAppFqdn string = sampleApp.outputs.fqdn
// Public entry point for the demo — the app's own ACA external ingress.
output sampleAppUrl string = 'https://${sampleApp.outputs.fqdn}'
output caeDefaultDomain string = managedEnvironment.outputs.defaultDomain

// ============================================================================================
// The management console (Mode 3, read-only)
// ============================================================================================

resource portalAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPortal && containerRegistryName != '') {
  name: guid(subscription().id, resourceGroup().name, containerRegistryName, 'portal-acr-pull')
  scope: containerRegistry
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: portalIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The console shares the spoke's managed environment for demo economy, like the control plane,
// but it is platform infrastructure: its identity lives in the hub next to everything it reads,
// and it is read-only in all three directions.
//
// INBOUND EXPOSURE. External ingress, and two things in front of it.
//
// First, the platform's built-in authentication (below) with unauthenticatedClientAction set to
// RedirectToLoginPage, so an unauthenticated request never reaches the container at all — which
// is also what makes the X-MS-CLIENT-PRINCIPAL headers the app reads trustworthy, since the auth
// sidecar strips any client-supplied copy.
//
// Second, an optional source-IP restriction. It is optional because this is a reference
// implementation that has to be runnable from a laptop; it is *present* because the console is an
// admin surface for a security control rather than a sample workload, and it concentrates more
// read power than anything else in the deployment. Internal-only ingress is the production
// counterpart and is recorded as such in docs/production-hardening.md.
module portal 'br/public:avm/res/app/container-app:0.22.0' = if (deployPortal) {
  name: 'portal'
  dependsOn: [
    portalAcrPull
  ]
  params: {
    registries: containerRegistryName == '' ? [] : [
      {
        server: '${containerRegistryName}.azurecr.io'
        identity: portalIdentityResourceId
      }
    ]
    name: '${namePrefix}-portal'
    location: location
    environmentResourceId: managedEnvironment.outputs.resourceId
    secrets: portalAuthClientSecret == '' ? [] : [
      {
        name: 'portal-auth-client-secret'
        value: portalAuthClientSecret
      }
    ]
    ingressExternal: true
    ingressTargetPort: 8080
    ingressTransport: 'http'
    ingressAllowInsecure: false
    // Empty means unrestricted. A non-empty list is Allow-only, which Container Apps completes
    // with an implicit deny — so naming one range restricts the console to that range.
    ipSecurityRestrictions: [
      for (range, index) in portalAllowedSourceIps: {
        name: 'operators-${index}'
        action: 'Allow'
        ipAddressRange: range
        description: 'Platform team source range permitted to reach the console.'
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        portalIdentityResourceId
      ]
    }
    // Scale to zero between sessions: a console nobody is looking at should cost nothing. The
    // cold start is a few seconds, which is acceptable for an operator tool and is not acceptable
    // for the proxy — which is why the proxy is a VMSS and this is not.
    scaleSettings: {
      minReplicas: 0
      maxReplicas: 1
      cooldownPeriod: 900
    }
    containers: [
      {
        name: 'portal'
        image: portalImage
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        env: [
          {
            name: 'ASPNETCORE_URLS'
            value: 'http://+:8080'
          }
          {
            // The console's own egress goes direct to the control plane, ARM, Log Analytics and
            // the registry rather than through the proxy: an operator tool must not depend on the
            // data plane whose health it is there to report.
            name: 'NO_PROXY'
            value: '169.254.169.254,localhost,${managedEnvironment.outputs.defaultDomain},.${managedEnvironment.outputs.defaultDomain},.monitor.azure.com,.applicationinsights.azure.com,.livediagnostics.monitor.azure.com,.loganalytics.io,.management.azure.com'
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: portalIdentityClientId
          }
          {
            name: 'CONTROL_PLANE_URL'
            value: deployControlPlane ? 'https://${controlPlane!.outputs.fqdn}' : ''
          }
          {
            // The console calls the control-plane API as ITSELF, with its own managed identity,
            // never with the operator's token — a user token could not satisfy the API's
            // iss/aud validation, and pass-through would pin the console to Entra forever.
            name: 'CONTROL_PLANE_SCOPE'
            value: '${expectAud}/.default'
          }
          {
            name: 'LOG_ANALYTICS_WORKSPACE_ID'
            value: workspaceCustomerId
          }
          {
            name: 'HUB_SUBSCRIPTION_ID'
            value: subscription().subscriptionId
          }
          {
            name: 'HUB_RESOURCE_GROUP'
            value: hubResourceGroupName
          }
          {
            name: 'PROXY_SCALE_SET_NAME'
            value: proxyVmssName
          }
          {
            name: 'EGRESS_IP_PREFIX_NAME'
            value: proxyPublicIpPrefixName
          }
          {
            name: 'PROXY_LOAD_BALANCER_NAME'
            value: proxyLoadBalancerName
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

output portalFqdn string = deployPortal ? portal!.outputs.fqdn : ''
output portalUrl string = deployPortal ? 'https://${portal!.outputs.fqdn}' : ''

// Container Apps' built-in authentication, in front of the container.
//
// This is what makes the console safe to expose: unauthenticatedClientAction is
// RedirectToLoginPage, so an unauthenticated request is turned away at the platform edge and
// never reaches the app. It is also what makes the X-MS-CLIENT-PRINCIPAL headers the app reads
// trustworthy — the auth sidecar strips any client-supplied copy before forwarding.
//
// The app registration itself is created out of band, because an app registration is not an ARM
// resource (scripts/deploy.sh does it with `az ad app create`).
//
// If portalAuthClientId is empty this resource is not created, and the console has no
// authentication in front of it. That does NOT leave it open: the app's own SessionMiddleware
// finds no principal, so every request is turned away and the console serves nothing. Broken
// rather than exposed is the correct failure for an admin surface on a security control.
resource portalAuth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (deployPortal && portalAuthClientId != '') {
  name: '${namePrefix}-portal/current'
  dependsOn: [
    portal
  ]
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: expectIss
          clientId: portalAuthClientId
          clientSecretSettingName: 'portal-auth-client-secret'
        }
        validation: {
          allowedAudiences: [
            'api://${portalAuthClientId}'
          ]
        }
      }
    }
    login: {
      preserveUrlFragmentsForLogins: true
      // No token store. It exists so an app can retrieve the signed-in user's access and refresh
      // tokens later, and the console never acts as the user: it calls the control-plane API with
      // its own managed identity (design.md D2) and reads nothing else on the operator's behalf.
      // Enabling it would put user refresh tokens at rest in a blob this deployment would then
      // have to protect, in the one component that already concentrates the most read power —
      // and the platform requires a SAS URL setting for that blob, which the deployment refuses
      // to create without ('SasUrlSettingName for BlobStorage must be set ... if token store is
      // enabled'). The identity the app reads is the X-MS-CLIENT-PRINCIPAL header, which the auth
      // sidecar sets on every forwarded request regardless.
      tokenStore: {
        enabled: false
      }
    }
  }
}
