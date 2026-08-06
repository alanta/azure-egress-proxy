// ============================================================================================
// The management zone.
//
// Three populations with different trust postures live in this deployment, and hub/spoke is the
// wrong axis for them: the spoke runs untrusted code, the hub's proxy parses attacker-controlled
// CONNECTs for a living, and the control plane writes the allowlist while the console reads every
// audit row and all ARM state. "Move the management plane to the hub" would have replaced one bad
// adjacency with another, so it is separated from BOTH.
//
// NOTHING PEERS WITH THIS NETWORK, and that is the design rather than an omission. Everything the
// management plane reaches is a PaaS endpoint — blob storage, Log Analytics, ARM, Entra, ACR — so
// no route to the hub or the spoke is required and none is created. The console reaches policy
// through the control-plane API, and both are applications of the environment declared here, so
// that call never leaves it. That is stronger than an NSG deny rule: both other NSGs carry an
// allow-vnet any/any, so a same-VNet placement would have depended on a deny rule sitting above it
// and never being reordered. No route is not a rule that can be got wrong.
//
// It also makes a property true by construction: the control plane cannot depend on the data plane
// it configures, because there is no path from here to the proxy.
//
// The whole module is conditional on deployControlPlane in main.bicep. Mode 1 is the default
// deployment and creates no management resource group, network, environment or identity.
// ============================================================================================

@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Management VNet CIDR.')
param mgmtVnetCidr string

@description('Management subnet CIDR.')
param mgmtSubnetCidr string

@description('Container image for the control-plane API.')
param controlPlaneImage string

@description('Deploy the read-only management console (Mode 3) alongside the control plane.')
param deployPortal bool = false

@description('Container image for the management console.')
param portalImage string = ''

@description('Name of the container registry in the hub resource group. Empty when every image is given as an already-pullable reference, in which case no registry wiring is emitted at all.')
param containerRegistryName string = ''

@description('Resource id of the control-plane user-assigned identity (mgmt-identity.bicep).')
param controlPlaneIdentityResourceId string

@description('Client id of the control-plane user-assigned identity.')
param controlPlaneIdentityClientId string

@description('Resource id of the console user-assigned identity (mgmt-identity.bicep).')
param portalIdentityResourceId string = ''

@description('Client id of the console user-assigned identity.')
param portalIdentityClientId string = ''

@description('Resource id of this environment\'s dedicated image-pull identity (mgmt-identity.bicep). Attached to every application here in addition to its functional identity, and the only principal holding AcrPull for this environment.')
param acrPullIdentityResourceId string

@description('Source IP ranges permitted to reach the console. Empty means no network restriction.')
param portalAllowedSourceIps array = []

@description('Application (client) ID of the Entra app registration the console signs operators in with. Created out of band — an app registration is not an ARM resource. REQUIRED when deployPortal is true: without it the platform performs no authentication and the console would serve the whole egress posture to anyone who reaches it.')
param portalAuthClientId string = ''

@description('Client secret for the console\'s Entra app registration. Required alongside portalAuthClientId.')
@secure()
param portalAuthClientSecret string = ''

@description('Log Analytics workspace resource id (in the hub) backing this zone\'s Application Insights. Sharing the hub workspace keeps proxy decisions and management telemetry queryable side by side.')
param appInsightsWorkspaceResourceId string

@description('Blob service endpoint holding the allowlist and ruleset blobs.')
param storageServiceUrl string

@description('Name of the blob holding the control plane state (rulesets + platform grants).')
param rulesetsBlobName string = 'rulesets.json'

@description('Name of the rendered blob the proxy reads.')
param allowlistBlobName string = 'allowlist.json'

@description('Name of the container holding both blobs.')
param allowlistContainerName string = 'egress-config'

@description('JWKS endpoint the control plane validates caller tokens against.')
param jwksUrl string

@description('Expected issuer for caller tokens.')
param expectIss string

@description('Expected token audience.')
param expectAud string

@description('Hub resource group holding the proxy scale set, egress prefix, load balancer and workspace — the only scope the console identity holds a role on.')
param hubResourceGroupName string = ''

@description('Log Analytics workspace GUID (not the ARM resource id) that the console queries EgressProxy_CL through.')
param workspaceCustomerId string = ''

@description('Proxy scale set name, for the console runtime surface.')
param proxyVmssName string = ''

@description('Egress public IP prefix name, for the console IP-pool panel.')
param proxyPublicIpPrefixName string = ''

@description('Proxy internal load balancer name, for the console health panels.')
param proxyLoadBalancerName string = ''

// ── Network security group ─────────────────────────────────────────────────────────────────────
// The same egress floor the workload subnet carries, minus one rule and plus one:
//
//   allow-azure-resource-manager   ADDED here, REMOVED from the spoke. This is the point of the
//                                  change. An NSG sees a subnet, not a container app, so while the
//                                  console lived in the spoke the sample workload inherited its
//                                  ARM allowance. Now the allowance is where its only user is.
//   allow-proxy-egress             DELIBERATELY ABSENT. There is no route to the proxy from here
//                                  and no rule that would use one. The management plane does not
//                                  send its traffic through the data plane it configures.

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
      // The registry is NOT integrated with this or any virtual network: it is Basic, with a
      // public endpoint and no private endpoint. So the pull is ordinary public-bound egress
      // leaving this subnet, and deny-internet below would kill it — the service tag is a set of
      // the registry service's PUBLIC prefixes, sitting above that floor. The rule exists
      // BECAUSE the registry is not VNet-integrated, not despite it. Give the registry a private
      // endpoint and this rule and allow-acr-storage both disappear, which is the production
      // posture recorded in docs/production-hardening.md.
      description: 'Control-plane and console image pulls from the registry\'s public endpoint.'
    }
  }
]

var mgmtNsgRules = [
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
      description: 'Microsoft Artifact Registry, for the Container Apps system containers.'
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
      // Nothing to do with ingress, and nothing to do with fronting anything with Front Door.
      // Microsoft's NSG reference lists this tag as a dependency OF MicrosoftContainerRegistry
      // above: the artifact registry for system containers is served through it. The rule name is
      // inherited from the spoke and reads as an ingress rule, which it is not.
      description: 'Outbound dependency of the MicrosoftContainerRegistry tag (system container pulls).'
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
      description: 'Managed identity token acquisition, and the JWKS the control plane validates caller tokens against.'
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
      description: 'Console queries over EgressProxy_CL, and this zone\'s own telemetry.'
    }
  }
  {
    // The rule the spoke used to carry for a resident it no longer has. The console reads the
    // deployment's runtime state from ARM — scale-set capacity and instance view, public-IP-prefix
    // consumption, and the Azure Monitor metric queries, which are ARM calls too. Without it the
    // console does not fail, it hangs: a denied outbound connection is a silent one.
    name: 'allow-azure-resource-manager'
    properties: {
      access: 'Allow'
      direction: 'Outbound'
      priority: 145
      protocol: 'Tcp'
      sourceAddressPrefix: '*'
      sourcePortRange: '*'
      destinationAddressPrefix: 'AzureResourceManager'
      destinationPortRange: '443'
      description: 'Management console: VMSS, public IP prefix, and Azure Monitor metric reads.'
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
      // Carries the console's call to the control plane over the environment's internal network.
      // Note that this VNet is peered with nothing, so the VirtualNetwork tag here means THIS
      // network and no other — unlike in the hub and spoke, where it also spans the peering.
      // (Kept short: ARM rejects a security rule description over 140 characters, and a comment
      // costs nothing.)
      description: 'Intra-VNet traffic, including the console\'s call to the control plane.'
    }
  }
  {
    // Two destinations behind one service tag, and the second is why this rule is unconditional
    // where the spoke's equivalent is guarded on there being a registry.
    //
    // ACR Basic and Standard serve layer data from shared Azure Storage (no dedicated data
    // endpoints below Premium), so an image pull needs this alongside AzureContainerRegistry. But
    // the control plane also WRITES the allowlist and ruleset blobs to the configuration storage
    // account in the hub, over the public blob endpoint, and that is this tag too. Even with every
    // image pulled from a public reference and no registry deployed, the control plane cannot do
    // its job without this rule.
    //
    // It softens the exfiltration floor — any in-region storage account becomes reachable — and
    // the hardened alternative for the registry half is ACR Premium with a private endpoint, which
    // needs neither this nor allow-acr. See docs/production-hardening.md.
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
      description: 'Control-plane allowlist and ruleset blob writes, and ACR layer data (served from Azure Storage below Premium).'
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

module mgmtNsg 'br/public:avm/res/network/network-security-group:0.5.0' = {
  name: 'mgmt-nsg'
  params: {
    name: '${namePrefix}-mgmt-nsg'
    location: location
    securityRules: concat(mgmtNsgRules, acrNsgRules)
  }
}

module mgmtVnet 'br/public:avm/res/network/virtual-network:0.9.0' = {
  name: 'mgmt-vnet'
  params: {
    name: '${namePrefix}-mgmt-vnet'
    location: location
    addressPrefixes: [
      mgmtVnetCidr
    ]
    subnets: [
      {
        name: 'snet-mgmt'
        addressPrefix: mgmtSubnetCidr
        networkSecurityGroupResourceId: mgmtNsg.outputs.resourceId
        defaultOutboundAccess: false
        delegation: 'Microsoft.App/environments'
      }
    ]
  }
}

module appInsights 'br/public:avm/res/insights/component:0.6.0' = {
  name: 'mgmt-app-insights'
  params: {
    name: '${namePrefix}-mgmt-ai'
    location: location
    workspaceResourceId: appInsightsWorkspaceResourceId
    applicationType: 'web'
    kind: 'web'
  }
}

// The environment that hosts no workload. That is the requirement it exists to satisfy: management
// compute is not co-tenant with the code the proxy exists to constrain.
module managedEnvironment 'br/public:avm/res/app/managed-environment:0.13.0' = {
  name: 'mgmt-managed-env'
  params: {
    name: '${namePrefix}-mgmt-cae'
    location: location
    publicNetworkAccess: 'Enabled'
    internal: false
    zoneRedundant: false
    infrastructureSubnetResourceId: '${mgmtVnet.outputs.resourceId}/subnets/snet-mgmt'
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

resource mgmtCaeExisting 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: '${namePrefix}-mgmt-cae'
  dependsOn: [
    managedEnvironment
  ]
}

resource mgmtCaeDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-hub-workspace'
  scope: mgmtCaeExisting
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


// Image pull is configured per application (properties.configuration.registries) — Container Apps
// has no environment-level equivalent — so "one pull identity per environment" is a convention
// held here rather than a platform setting: the same identity, referenced by both applications.
var registries = containerRegistryName == '' ? [] : [
  {
    server: '${containerRegistryName}.azurecr.io'
    identity: acrPullIdentityResourceId
  }
]

var noProxyBase = '169.254.169.254,localhost,${managedEnvironment.outputs.defaultDomain},.${managedEnvironment.outputs.defaultDomain},.monitor.azure.com,.applicationinsights.azure.com,.livediagnostics.monitor.azure.com'

// Hosts rather than URLs, because NO_PROXY matches on host suffixes. Derived from environment()
// instead of written out, so the two entries stay correct in clouds where the suffix is not
// core.windows.net / management.azure.com.
var blobHostSuffix = '.blob.${environment().suffixes.storage}'
var resourceManagerHost = replace(replace(environment().resourceManager, 'https://', ''), '/', '')

// The control plane is the sole writer of the allowlist blobs. Its own egress goes direct to
// storage and the IdP (both in NO_PROXY) rather than through the proxy — the control plane must
// not depend on the data plane it configures. In this topology that is no longer only a
// convention: there is no network path from this zone to the proxy at all.
module controlPlane 'br/public:avm/res/app/container-app:0.22.0' = {
  name: 'control-plane'
  params: {
    registries: registries
    name: '${namePrefix}-control-plane'
    location: location
    environmentResourceId: managedEnvironment.outputs.resourceId
    ingressExternal: true
    ingressTargetPort: 8080
    ingressTransport: 'http'
    ingressAllowInsecure: false
    // Two user-assigned identities: the functional one the application authenticates with, and the
    // environment's pull identity. AZURE_CLIENT_ID below is what selects the functional one — with
    // more than one attached, an unset or wrong client id resolves to the other principal and the
    // failure surfaces as an authorization error far from its cause.
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        controlPlaneIdentityResourceId
        acrPullIdentityResourceId
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
            value: '${noProxyBase},${blobHostSuffix}'
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

// ============================================================================================
// The management console (Mode 3, read-only)
// ============================================================================================

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
  params: {
    registries: registries
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
    // Functional identity plus the environment's pull identity; AZURE_CLIENT_ID below selects the
    // functional one. See the note on the control plane above.
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        portalIdentityResourceId
        acrPullIdentityResourceId
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
            // data plane whose health it is there to report. From this zone there is no route to
            // the proxy in any case.
            name: 'NO_PROXY'
            value: '${noProxyBase},.loganalytics.io,.${resourceManagerHost}'
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: portalIdentityClientId
          }
          {
            name: 'CONTROL_PLANE_URL'
            // The app name, not the ingress FQDN. Both services sit in this Container Apps
            // environment — they moved here as a pair precisely so this stays true — so the name
            // resolves on the environment's internal network and the call never leaves the VNet.
            // The FQDN resolves to the environment's *public* address, which would send an
            // internal call out to the Internet and straight into the egress floor's deny rule;
            // the console would simply hang until it timed out, which is exactly what it did the
            // first time this was deployed.
            value: 'http://${controlPlane.outputs.name}'
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

output mgmtVnetName string = mgmtVnet.outputs.name
output mgmtVnetResourceId string = mgmtVnet.outputs.resourceId
output caeDefaultDomain string = managedEnvironment.outputs.defaultDomain
output controlPlaneFqdn string = controlPlane.outputs.fqdn
output controlPlaneUrl string = 'https://${controlPlane.outputs.fqdn}'
output portalFqdn string = deployPortal ? portal!.outputs.fqdn : ''
output portalUrl string = deployPortal ? 'https://${portal!.outputs.fqdn}' : ''
