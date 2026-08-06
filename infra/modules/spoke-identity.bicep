// Identities for the workload zone. One resource each, in their own file — see the header of
// hub-identity.bicep for why the identities are a phase of their own rather than declarations
// inside the modules that use them.

@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

// The identity the proxy authenticates. The `appid` claim the allowlist keys on IS this principal,
// which is why it carries no infrastructure grant of any kind: an identity that means "which
// workload is asking" must not also mean "may read the registry". Image pull is the pull identity
// below.
module sampleAppIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'sample-app-uami'
  params: {
    name: '${namePrefix}-sample-app-uami'
    location: location
  }
}

// Image pull for this environment, and nothing else. It holds AcrPull on the registry (granted in
// hub.bicep, next to the registry) and is attached to every application in the workload
// environment alongside that application's functional identity.
//
// A reviewer looking for an environment-level pull setting will not find one: Container Apps
// configures image pull per application, in properties.configuration.registries, with no
// environment-wide equivalent. "One pull identity per environment" is therefore a convention held
// by these modules, not a platform feature. It is per environment rather than one shared across
// both because the environment is the trust boundary — a single identity would place the same
// credential in the workload zone and the management zone, re-crossing the line this deployment
// exists to draw.
module acrPullIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'spoke-acr-pull-uami'
  params: {
    name: '${namePrefix}-spoke-acr-pull-uami'
    location: location
  }
}

output sampleAppPrincipalId string = sampleAppIdentity.outputs.principalId
output sampleAppClientId string = sampleAppIdentity.outputs.clientId
output sampleAppResourceId string = sampleAppIdentity.outputs.resourceId

output acrPullPrincipalId string = acrPullIdentity.outputs.principalId
output acrPullClientId string = acrPullIdentity.outputs.clientId
output acrPullResourceId string = acrPullIdentity.outputs.resourceId
