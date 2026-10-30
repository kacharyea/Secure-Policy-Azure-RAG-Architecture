targetScope = 'resourceGroup'

param location string
param searchLocation string
param publisherEmail string

@description('Tenant ID used to validate backend JWTs at the gateway; matches the model-gateway app registration tenant.')
param modelGatewayTenantId string

@description('Model-gateway application (client) ID used as the validated access-token audience.')
param modelGatewayAudience string

var suffix = uniqueString(subscription().id, resourceGroup().name)
var searchName = 'srch-policy-${suffix}'
var foundryName = 'ai-policy-${suffix}'
var apimName = 'apim-policy-${suffix}'
var projectName = 'secure-policy-rag'
var generationDeployment = 'gpt-5-mini'
var embeddingDeployment = 'text-embedding-3-small'
var tags = {
  workload: 'secure-policy-rag'
  environment: 'demo'
  purpose: 'customer-sample'
}

module runtimeIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: '${deployment().name}-runtime'
  params: {
    name: 'id-policy-runtime'
    location: location
    enableTelemetry: false
    tags: tags
  }
}

module indexerIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: '${deployment().name}-indexer'
  params: {
    name: 'id-policy-indexer'
    location: location
    enableTelemetry: false
    tags: tags
  }
}

module search 'br/public:avm/res/search/search-service:0.13.0' = {
  name: '${deployment().name}-search'
  params: {
    name: searchName
    location: searchLocation
    sku: 'basic'
    replicaCount: 1
    partitionCount: 1
    semanticSearch: 'disabled'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    enableTelemetry: false
    tags: tags
    roleAssignments: [
      {
        roleDefinitionIdOrName: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
        principalId: runtimeIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        description: 'Runtime may read indexed policies; it cannot ingest or change the index.'
      }
      {
        roleDefinitionIdOrName: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
        principalId: indexerIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        description: 'Standalone indexer may create the policy index schema.'
      }
      {
        roleDefinitionIdOrName: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
        principalId: indexerIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        description: 'Standalone indexer may upload the approved fictional policy chunks.'
      }
    ]
  }
}

module foundry 'br/public:avm/res/cognitive-services/account:0.19.1' = {
  name: '${deployment().name}-foundry'
  params: {
    name: foundryName
    location: location
    kind: 'AIServices'
    sku: 'S0'
    customSubDomainName: foundryName
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: true
    managedIdentities: {
      systemAssigned: true
    }
    enableTelemetry: false
    tags: tags
    deployments: [
      {
        name: generationDeployment
        model: {
          format: 'OpenAI'
          name: 'gpt-5-mini'
          version: '2025-08-07'
        }
        sku: {
          name: 'GlobalStandard'
          capacity: 10
        }
        raiPolicyName: 'Microsoft.DefaultV2'
        versionUpgradeOption: 'OnceCurrentVersionExpired'
      }
      {
        name: embeddingDeployment
        model: {
          format: 'OpenAI'
          name: 'text-embedding-3-small'
          version: '1'
        }
        sku: {
          name: 'Standard'
          capacity: 30
        }
        raiPolicyName: 'Microsoft.DefaultV2'
        versionUpgradeOption: 'OnceCurrentVersionExpired'
      }
    ]
  }
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryName
}

// AVM's account module does not expose a project collection.
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundryAccount
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    displayName: 'Secure Policy RAG'
    description: 'Fictional-data secure RAG sample. Fixed RAG workflow; no hosted agent.'
  }
  dependsOn: [
    foundry
  ]
}

module apim 'br/public:avm/res/api-management/service:0.15.0' = {
  name: '${deployment().name}-gateway'
  params: {
    name: apimName
    location: location
    publisherEmail: publisherEmail
    publisherName: 'Secure Policy RAG Sample'
    sku: 'Consumption'
    skuCapacity: 0
    availabilityZones: []
    virtualNetworkType: 'None'
    publicNetworkAccess: 'Enabled'
    managedIdentities: {
      systemAssigned: true
    }
    enableTelemetry: false
    tags: tags
    policies: [
      {
        name: 'policy'
        format: 'rawxml'
        value: replace(
          replace(loadTextContent('gateway-active-policy.xml'), '__TENANT_ID__', modelGatewayTenantId),
          '__GATEWAY_AUDIENCE__', modelGatewayAudience)
      }
    ]
    apis: [
      {
        name: 'policy-models'
        displayName: 'Policy RAG model gateway'
        path: 'models'
        protocols: [
          'https'
        ]
        subscriptionRequired: false
        serviceUrl: 'https://${foundryName}.openai.azure.com'
        operations: [
          {
            name: 'embeddings'
            displayName: 'Embeddings'
            method: 'POST'
            urlTemplate: '/embeddings'
          }
          {
            name: 'chat-completions'
            displayName: 'Chat completions'
            method: 'POST'
            urlTemplate: '/chat/completions'
          }
        ]
      }
    ]
  }
}

resource gatewayModelAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, apimName, 'openai-user')
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: apim.outputs.systemAssignedMIPrincipalId!
    principalType: 'ServicePrincipal'
    description: 'Only the gateway identity receives model inference access; no model management.'
  }
  dependsOn: [
    foundry
  ]
}

output configuration object = {
  search: {
    resourceId: search.outputs.resourceId
    location: search.outputs.location
    endpoint: search.outputs.endpoint
    indexName: 'policy-chunks'
  }
  foundry: {
    resourceId: foundry.outputs.resourceId
    projectId: project.id
    openAiEndpoint: 'https://${foundryName}.openai.azure.com/'
    generationDeployment: generationDeployment
    embeddingDeployment: embeddingDeployment
    embeddingDimensions: 1536
  }
  gateway: {
    resourceId: apim.outputs.resourceId
    endpoint: 'https://${apimName}.azure-api.net/'
    authorizationState: 'Entra application-role validation (ModelGateway.Invoke) and Foundry managed-identity routing active'
  }
  runtimeIdentity: {
    resourceId: runtimeIdentity.outputs.resourceId
    clientId: runtimeIdentity.outputs.clientId
    principalId: runtimeIdentity.outputs.principalId
  }
  indexerIdentity: {
    resourceId: indexerIdentity.outputs.resourceId
    clientId: indexerIdentity.outputs.clientId
    principalId: indexerIdentity.outputs.principalId
  }
}
