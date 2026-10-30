targetScope = 'resourceGroup'

@description('Existing Search service name, computed the same deterministic way as services.bicep.')
param searchName string

@description('Approved operator user object ID receiving narrow, index-management-only Search data roles.')
param operatorPrincipalId string

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: searchName
}

// Lets the approved operator create/manage the index schema locally
// (PolicyRag.Indexer --ingest), matching the indexer managed identity's
// existing control-plane role. No broader (e.g. Owner/Contributor on the
// resource) grant is added.
resource operatorSearchServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, operatorPrincipalId, 'search-service-contributor')
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
    principalId: operatorPrincipalId
    principalType: 'User'
    description: 'Approved operator may create/manage the policy-chunks index schema for local ingestion only.'
  }
}

// Lets the approved operator upload/merge the approved fictional policy
// chunks, matching the indexer managed identity's existing data-write role.
resource operatorSearchIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, operatorPrincipalId, 'search-index-data-contributor')
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: operatorPrincipalId
    principalType: 'User'
    description: 'Approved operator may upload the approved fictional policy chunks for local ingestion only.'
  }
}

output searchResourceId string = search.id
