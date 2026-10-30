targetScope = 'resourceGroup'

param location string

@minLength(2)
@maxLength(60)
param appName string

param runtimeIdentityResourceId string
param applicationSettings object

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${appName}-plan'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
}

// Windows/IIS hosting preserves the request scheme for same-origin API enforcement.
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      healthCheckPath: '/health'
      appSettings: [for setting in items(applicationSettings): {
        name: setting.key
        value: string(setting.value)
      }]
    }
  }
}

output url string = 'https://${app.properties.defaultHostName}'
output resourceId string = app.id
