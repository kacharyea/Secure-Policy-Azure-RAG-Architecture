targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Tenant approved for this isolated model-gateway identity deployment.')
param tenantId string

@description('Existing user object ID responsible for managing the demo gateway identity; ownership does not grant model access.')
param ownerPrincipalId string

var suffix = uniqueString(tenantId, resourceGroup().id)
var gatewayUniqueName = 'policy-rag-gateway-${suffix}'
var gatewayIdentifierUri = 'api://${tenantId}/${gatewayUniqueName}'
var invokeScopeId = guid(tenantId, gatewayUniqueName, 'ModelGateway.Invoke')
var invokeRoleId = guid(tenantId, gatewayUniqueName, 'ModelGateway.Invoke.Application')
// Microsoft first-party Azure PowerShell client; pre-authorizing it lets the
// already-authenticated operator request a token locally (Get-AzAccessToken)
// without any interactive consent prompt or stored credential.
var azurePowerShellAppId = '1950a258-227b-4e31-a9cf-717495945fc2'

resource gatewayApi 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: gatewayUniqueName
  displayName: 'PolicyRag-ModelGateway-Demo'
  description: 'Single-tenant fictional policy-gateway resource. Authorizes only the backend service to call the APIM-fronted model endpoints; grants no document or directory authority.'
  signInAudience: 'AzureADMyOrg'
  appRoles: [
    {
      id: invokeRoleId
      value: 'ModelGateway.Invoke'
      displayName: 'Invoke the model gateway as a backend'
      description: 'Allows an explicitly assigned backend identity to call the model gateway without a user.'
      allowedMemberTypes: [
        'Application'
      ]
      isEnabled: true
    }
  ]
  identifierUris: [
    gatewayIdentifierUri
  ]
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: invokeScopeId
        value: 'ModelGateway.Invoke'
        type: 'Admin'
        isEnabled: true
        adminConsentDisplayName: 'Invoke the fictional policy model gateway'
        adminConsentDescription: 'Legacy delegated gateway permission. The app-only gateway policy requires an application role instead.'
      }
    ]
    preAuthorizedApplications: [
      {
        appId: azurePowerShellAppId
        delegatedPermissionIds: [
          invokeScopeId
        ]
      }
    ]
  }
  requiredResourceAccess: []
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource gatewayApiPrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: gatewayApi.appId
  accountEnabled: true
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

output configuration object = {
  tenantId: tenantId
  gateway: {
    clientId: gatewayApi.appId
    objectId: gatewayApi.id
    servicePrincipalId: gatewayApiPrincipal.id
    audience: gatewayApi.appId
    identifierUri: gatewayIdentifierUri
    delegatedScope: '${gatewayIdentifierUri}/.default'
    scopeId: invokeScopeId
    applicationRoleId: invokeRoleId
  }
}
