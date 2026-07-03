// Reads the Front Door profile's frontDoorId GUID (the value sent in X-Azure-FDID —
// NOT the ARM resource id). This lives in its own module so the GET runs only after
// the profile exists: an `existing` reference in the parent template is resolved by
// ARM at deployment start, regardless of dependsOn on the consuming resources.
@description('Front Door profile name to read.')
param profileName string

resource profile 'Microsoft.Cdn/profiles@2024-02-01' existing = {
  name: profileName
}

output frontDoorId string = profile.properties.frontDoorId
