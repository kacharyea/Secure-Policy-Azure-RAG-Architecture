targetScope = 'subscription'

@description('Name of the demo resource group.')
@minLength(1)
@maxLength(90)
param resourceGroupName string = 'rg-secure-policy-rag'

@description('Azure region for the resource group metadata.')
param location string = 'eastus2'

@description('Search service region. May differ when the primary region has no capacity.')
param searchLocation string = location

@description('Contact email for the API Management publisher.')
param publisherEmail string

@description('Object ID of the approved demo source uploader; container-scoped access only.')
param operatorPrincipalId string

@description('Tenant ID used to validate backend JWTs at the gateway; matches the model-gateway app registration tenant.')
param modelGatewayTenantId string

@description('Model-gateway application (client) ID used as the validated access-token audience.')
param modelGatewayAudience string

module demoResourceGroup 'br/public:avm/res/resources/resource-group:0.4.4' = {
  name: '${deployment().name}-rg'
  params: {
    name: resourceGroupName
    location: location
    enableTelemetry: false
    tags: {
      workload: 'secure-policy-rag'
      environment: 'demo'
      purpose: 'customer-sample'
    }
  }
}

module services 'services.bicep' = {
  name: '${deployment().name}-services'
  scope: resourceGroup(resourceGroupName)
  params: {
    location: location
    searchLocation: searchLocation
    publisherEmail: publisherEmail
    modelGatewayTenantId: modelGatewayTenantId
    modelGatewayAudience: modelGatewayAudience
  }
  dependsOn: [
    demoResourceGroup
  ]
}

output resourceGroupId string = demoResourceGroup.outputs.resourceId
output resourceGroupLocation string = demoResourceGroup.outputs.location
output services object = services.outputs.configuration

module sourceStorage 'storage.bicep' = {
  name: '${deployment().name}-storage'
  scope: resourceGroup(resourceGroupName)
  params: {
    location: location
    indexerPrincipalId: services.outputs.configuration.indexerIdentity.principalId
    operatorPrincipalId: operatorPrincipalId
  }
}

output sourceStorage object = sourceStorage.outputs.configuration
