// Identities for the management zone. One resource each, in their own file — see the header of
// hub-identity.bicep for why the identities are a phase of their own rather than declarations
// inside the modules that use them.
//
// The whole file is deployed only when the control plane is (main.bicep guards the module on
// deployControlPlane), because a zone with no residents should cost nothing: Mode 1 is the default
// deployment and creates no management resource group, network, environment or identity.

@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

@description('Deploy the console\'s identity. The console is optional on top of the control plane; its identity is the only one here that is conditional within the zone.')
param deployPortal bool = false

// The sole writer of the allowlist blobs. Storage Blob Data Contributor on the config account is
// granted in hub.bicep, next to the account — the control plane's compute is here, but the
// resource it writes is platform infrastructure and the grant lives with it.
module controlPlaneIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'control-plane-uami'
  params: {
    name: '${namePrefix}-control-plane-uami'
    location: location
  }
}

// The console's identity. It is the single most informative principal in the deployment to
// compromise — policy, traffic and infrastructure through one identity — so what it does NOT hold
// is as much of the design as what it does. It gets Reader + Monitoring Reader on the HUB resource
// group and nothing else: no role on its own resource group, none on any workload group, and
// deliberately none on the configuration storage account. The console reaches policy through the
// control-plane API's read endpoints and cannot touch the blobs at all.
module portalIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = if (deployPortal) {
  name: 'portal-uami'
  params: {
    name: '${namePrefix}-portal-uami'
    location: location
  }
}

// Image pull for this environment, and nothing else — the counterpart of spoke-acr-pull. Container
// Apps has no environment-level pull configuration (it is a per-application setting under
// properties.configuration.registries), so this is a convention held by the modules rather than a
// platform property, and a reviewer looking for an environment setting will not find one. It is
// distinct from the spoke's for the reason the zones are distinct: one shared pull identity would
// put the same credential on both sides of the boundary.
module acrPullIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'mgmt-acr-pull-uami'
  params: {
    name: '${namePrefix}-mgmt-acr-pull-uami'
    location: location
  }
}

output controlPlanePrincipalId string = controlPlaneIdentity.outputs.principalId
output controlPlaneClientId string = controlPlaneIdentity.outputs.clientId
output controlPlaneResourceId string = controlPlaneIdentity.outputs.resourceId

output portalPrincipalId string = deployPortal ? portalIdentity!.outputs.principalId : ''
output portalClientId string = deployPortal ? portalIdentity!.outputs.clientId : ''
output portalResourceId string = deployPortal ? portalIdentity!.outputs.resourceId : ''

output acrPullPrincipalId string = acrPullIdentity.outputs.principalId
output acrPullClientId string = acrPullIdentity.outputs.clientId
output acrPullResourceId string = acrPullIdentity.outputs.resourceId
