targetScope = 'resourceGroup'

@description('Region approved for this synthetic-demo telemetry. Deploy separately; existing resources are not modified.')
param location string = resourceGroup().location

param workspaceName string = 'log-policy-${uniqueString(resourceGroup().id)}'
param insightsName string = 'appi-policy-${uniqueString(resourceGroup().id)}'

@description('Optional approved developer/runtime object ID for Entra telemetry ingestion. Never infer from ownership.')
param telemetryPublisherObjectId string = ''

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
  }
}

resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(telemetryPublisherObjectId)) {
  name: guid(insights.id, telemetryPublisherObjectId, 'telemetry-publisher')
  scope: insights
  properties: {
    principalId: telemetryPublisherObjectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

output applicationInsightsName string = insights.name
output logAnalyticsName string = workspace.name
// Fetch the connection string directly into an environment/user-secrets setting, not a deployment output.
