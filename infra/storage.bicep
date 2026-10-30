targetScope = 'resourceGroup'

param location string = 'eastus2'
param indexerPrincipalId string
param operatorPrincipalId string

var accountName = 'stpolicy${uniqueString(subscription().id, resourceGroup().name)}'
var containerName = 'policy-documents'

module storage 'br/public:avm/res/storage/storage-account:0.33.1' = {
  name: '${deployment().name}-account'
  params: {
    name: accountName
    location: location
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    accessTier: 'Hot'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'None'
    }
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowCrossTenantReplication: false
    enableTelemetry: false
    tags: {
      workload: 'secure-policy-rag'
      environment: 'demo'
      purpose: 'customer-sample'
    }
    blobServices: {
      containers: [
        {
          name: containerName
          publicAccess: 'None'
          roleAssignments: [
            {
              principalId: indexerPrincipalId
              principalType: 'ServicePrincipal'
              roleDefinitionIdOrName: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
              description: 'Indexer reads approved source files; it cannot upload or approve files.'
            }
            {
              principalId: operatorPrincipalId
              principalType: 'User'
              roleDefinitionIdOrName: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
              description: 'Demo operator uploads and verifies source files only in this container.'
            }
          ]
        }
      ]
    }
  }
}

output configuration object = {
  resourceId: storage.outputs.resourceId
  accountName: accountName
  endpoint: 'https://${accountName}.blob.${environment().suffixes.storage}'
  container: containerName
}
