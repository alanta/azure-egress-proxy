@description('Local virtual network name.')
param localVnetName string

@description('Peering resource name.')
param peeringName string

@description('Remote virtual network resource ID.')
param remoteVnetResourceId string

resource localVnet 'Microsoft.Network/virtualNetworks@2025-01-01' existing = {
  name: localVnetName
}

resource peering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2025-01-01' = {
  name: peeringName
  parent: localVnet
  properties: {
    remoteVirtualNetwork: {
      id: remoteVnetResourceId
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}

output peeringId string = peering.id
