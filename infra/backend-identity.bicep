targetScope = 'resourceGroup'

extension microsoftGraphV1

param tenantId string
param ownerPrincipalId string

@allowed([
  'runtime'
  'indexer'
])
param purpose string = 'runtime'

@description('Base64 DER public certificate only. Never supply a PFX or private key.')
param certificatePublicKey string

@description('Certificate validity start in ISO 8601 format.')
param certificateStartDateTime string

@description('Certificate expiry in ISO 8601 format; use short validity for development.')
param certificateEndDateTime string

var suffix = uniqueString(tenantId, resourceGroup().id)

resource backendApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'policy-rag-${purpose}-backend-${suffix}'
  displayName: 'PolicyRag-${purpose}-Development'
  description: 'Dedicated certificate-authenticated local backend. No browser or user sign-in.'
  signInAudience: 'AzureADMyOrg'
  keyCredentials: [
    {
      type: 'AsymmetricX509Cert'
      usage: 'Verify'
      key: certificatePublicKey
      displayName: 'Local backend certificate'
      startDateTime: certificateStartDateTime
      endDateTime: certificateEndDateTime
    }
  ]
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource backendPrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: backendApp.appId
  accountEnabled: true
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

output clientId string = backendApp.appId
output servicePrincipalId string = backendPrincipal.id
