// One resource, its own file, on purpose.
//
// Two rules pull against each other in this deployment. Identities follow the compute they serve,
// so they scatter across all three resource groups; role assignments belong at the scope of the
// resource being granted, and the hub owns nearly every such resource. Naively that is circular —
// hub would need principal IDs from spoke and mgmt while both need hub outputs.
//
// User-assigned identities have no dependencies of any kind, so creating them all in a first phase
// breaks the cycle and lets one rule hold everywhere: identities are created in the resource group
// of their compute, in a first-phase module per group, and every role assignment lives in the
// module that owns the resource it grants on. Folding these back into hub.bicep / spoke.bicep /
// mgmt.bicep re-introduces the cycle. See design.md § Dependency ordering.

@description('Deployment location.')
param location string = resourceGroup().location

@description('Name prefix.')
param namePrefix string

// The proxy's identity: it reads the allowlist blob (Storage Blob Data Reader, granted in
// hub.bicep next to the account) and it is what the Azure Monitor agent authenticates as.
module proxyIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  name: 'proxy-uami'
  params: {
    name: '${namePrefix}-proxy-uami'
    location: location
  }
}

output proxyPrincipalId string = proxyIdentity.outputs.principalId
output proxyClientId string = proxyIdentity.outputs.clientId
output proxyResourceId string = proxyIdentity.outputs.resourceId
