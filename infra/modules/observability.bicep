@description('Location for observability resources.')
param location string = resourceGroup().location

@description('Name prefix for observability resources.')
param namePrefix string

@description('VMSS name to attach AMA extension to.')
param vmssName string

@description('User-assigned identity resource ID used by AMA.')
param proxyIdentityResourceId string

var workspaceName = '${namePrefix}-law'
var dcrName = '${namePrefix}-proxy-dcr'
var tableName = 'EgressProxy_CL'

var proxyTransform = '''
source
| where SyslogMessage has "CANONICAL-PROXY"
| extend d = parse_json(SyslogMessage)
| project TimeGenerated, Computer,
          EventType = tostring(d.msg),
          ReqId = tostring(d.id),
          Role = tostring(d.role),
          ProxyType = tostring(d.proxy_type),
          SrcIp = extract("^([^:]+):", 1, tostring(d.inbound_remote_addr)),
          Host = tostring(d.requested_host),
          Allow = tobool(d.allow),
          DecisionReason = tostring(d.decision_reason),
          EnforceWouldDeny = tobool(d.enforce_would_deny),
          DnsLookupMs = todouble(d.dns_lookup_time_ms),
          BytesIn = tolong(d.bytes_in),
          BytesOut = tolong(d.bytes_out),
          DurationMs = todouble(d.duration) * 1000,
          ConnEstablishMs = todouble(d.conn_establish_time_ms),
          Error = tostring(d.error)
'''

var diagTransform = '''
source
| where SyslogMessage !has "CANONICAL-PROXY"
| where ProcessName == "egress-proxy"
     or (ProcessName == "systemd" and SyslogMessage has "egress-proxy")
     or SeverityLevel in ("err", "crit", "alert", "emerg")
'''

module workspace 'br/public:avm/res/operational-insights/workspace:0.15.0' = {
  name: 'workspace'
  params: {
    name: workspaceName
    location: location
    skuName: 'PerGB2018'
    dataRetention: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
      disableLocalAuth: false
    }
  }
}

// AVM workspace module does not currently expose transform-based custom table creation with stream mapping.
resource customTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  name: '${workspaceName}/${tableName}'
  // The parent is referenced by name string, so the dependency must be explicit.
  dependsOn: [
    workspace
  ]
  properties: {
    plan: 'Analytics'
    retentionInDays: 30
    schema: {
      name: tableName
      columns: [
        { name: 'TimeGenerated', type: 'datetime' }
        { name: 'Computer', type: 'string' }
        { name: 'EventType', type: 'string' }
        { name: 'ReqId', type: 'string' }
        { name: 'Role', type: 'string' }
        { name: 'ProxyType', type: 'string' }
        { name: 'SrcIp', type: 'string' }
        { name: 'Host', type: 'string' }
        { name: 'Allow', type: 'boolean' }
        { name: 'DecisionReason', type: 'string' }
        { name: 'EnforceWouldDeny', type: 'boolean' }
        { name: 'DnsLookupMs', type: 'real' }
        { name: 'BytesIn', type: 'long' }
        { name: 'BytesOut', type: 'long' }
        { name: 'DurationMs', type: 'real' }
        { name: 'ConnEstablishMs', type: 'real' }
        { name: 'Error', type: 'string' }
      ]
    }
  }
}

module dcr 'br/public:avm/res/insights/data-collection-rule:0.11.0' = {
  name: 'dcr'
  params: {
    name: dcrName
    location: location
    dataCollectionRuleProperties: {
      kind: 'Linux'
      dataSources: {
        syslog: [
          {
            name: 'proxy-syslog'
            streams: [
              'Microsoft-Syslog'
            ]
            facilityNames: [
              'daemon'
            ]
            logLevels: [
              'Info'
              'Notice'
              'Warning'
              'Error'
              'Critical'
              'Alert'
              'Emergency'
            ]
          }
        ]
      }
      destinations: {
        logAnalytics: [
          {
            name: 'la'
            workspaceResourceId: workspace.outputs.resourceId
          }
        ]
      }
      dataFlows: [
        {
          streams: [
            'Microsoft-Syslog'
          ]
          destinations: [
            'la'
          ]
          transformKql: proxyTransform
          outputStream: 'Custom-${tableName}'
        }
        {
          streams: [
            'Microsoft-Syslog'
          ]
          destinations: [
            'la'
          ]
          transformKql: diagTransform
          outputStream: 'Microsoft-Syslog'
        }
      ]
    }
  }
  dependsOn: [
    customTable
  ]
}

resource vmss 'Microsoft.Compute/virtualMachineScaleSets@2024-11-01' existing = {
  name: vmssName
}

resource amaExtension 'Microsoft.Compute/virtualMachineScaleSets/extensions@2024-11-01' = {
  name: 'AzureMonitorLinuxAgent'
  parent: vmss
  properties: {
    publisher: 'Microsoft.Azure.Monitor'
    type: 'AzureMonitorLinuxAgent'
    typeHandlerVersion: '1.0'
    autoUpgradeMinorVersion: true
    enableAutomaticUpgrade: true
    settings: {
      authentication: {
        managedIdentity: {
          'identifier-name': 'mi_res_id'
          'identifier-value': proxyIdentityResourceId
        }
      }
    }
  }
}

// AVM does not provide a dedicated module for DCR associations.
resource dcrAssociation 'Microsoft.Insights/dataCollectionRuleAssociations@2022-06-01' = {
  name: 'proxy-dcra'
  scope: vmss
  properties: {
    dataCollectionRuleId: dcr.outputs.resourceId
  }
}

output workspaceName string = workspace.outputs.name
output workspaceResourceId string = workspace.outputs.resourceId
output workspaceCustomerId string = workspace.outputs.logAnalyticsWorkspaceId
