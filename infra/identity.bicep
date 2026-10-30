targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Tenant approved for this isolated demo identity deployment.')
param tenantId string

@description('Existing user object ID responsible for managing the demo identities; ownership does not grant document access.')
param ownerPrincipalId string

@description('Enable the SPA and emit approved users for the separate user-specific consent step.')
param enableBrowserSignIn bool = false

@description('Approved SPA callback URIs. Use HTTPS for hosted applications.')
@minLength(1)
param browserRedirectUris string[] = [
  'http://localhost:5280/auth-callback.html'
]

@description('Explicitly approved existing employee user object IDs; appended, never inferred from ownership.')
param employeeMemberIds string[] = []

@description('Explicitly approved existing reviewer user object IDs; appended without removing other members.')
param reviewerMemberIds string[] = []

var suffix = uniqueString(tenantId, resourceGroup().id)
var apiUniqueName = 'policy-rag-api-${suffix}'
var apiIdentifierUri = 'api://${tenantId}/${apiUniqueName}'
var invokeScopeId = guid(tenantId, apiUniqueName, 'PolicyReview.Invoke')
// Same technique as the model-gateway identity: pre-authorize only the
// Microsoft first-party Azure PowerShell client for the already
// admin-consented PolicyReview.Invoke scope. This does not broaden the
// scope or add a new permission; it only lets the already-authenticated
// operator script a real authorized end-to-end API call (bearer token,
// real group claims) without a browser, for infrastructure verification.
// It does not replace or substitute for actual human browser/MFA acceptance.
var azurePowerShellAppId = '1950a258-227b-4e31-a9cf-717495945fc2'

resource policyApi 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: apiUniqueName
  displayName: 'PolicyRag-Api-Demo'
  description: 'Single-tenant fictional policy review demo API. No model or directory authority is granted by this registration.'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    apiIdentifierUri
  ]
  groupMembershipClaims: 'SecurityGroup'
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: invokeScopeId
        value: 'PolicyReview.Invoke'
        type: 'Admin'
        isEnabled: true
        adminConsentDisplayName: 'Use the Policy RAG demo'
        adminConsentDescription: 'Allow an approved client to invoke the fictional policy review API on behalf of a signed-in user.'
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

resource policyApiPrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: policyApi.appId
  accountEnabled: true
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource employees 'Microsoft.Graph/groups@v1.0' = {
  uniqueName: 'policy-rag-employees-${suffix}'
  displayName: 'PolicyRag-Employees'
  description: 'Assigned-membership group mapped to the fictional employees document entitlement.'
  mailEnabled: false
  mailNickname: 'policy-rag-employees-${suffix}'
  securityEnabled: true
  groupTypes: []
  members: {
    relationships: employeeMemberIds
    relationshipSemantics: 'Append'
  }
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource reviewers 'Microsoft.Graph/groups@v1.0' = {
  uniqueName: 'policy-rag-reviewers-${suffix}'
  displayName: 'PolicyRag-SecurityReviewers'
  description: 'Assigned-membership group mapped to the fictional security-reviewers document entitlement.'
  mailEnabled: false
  mailNickname: 'policy-rag-reviewers-${suffix}'
  securityEnabled: true
  groupTypes: []
  members: {
    relationships: reviewerMemberIds
    relationshipSemantics: 'Append'
  }
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource browserClient 'Microsoft.Graph/applications@v1.0' = if (enableBrowserSignIn) {
  uniqueName: 'policy-rag-browser-${suffix}'
  displayName: 'PolicyRag-Browser-Demo'
  description: 'Secretless SPA for the fictional policy demo; no Microsoft Graph permissions.'
  signInAudience: 'AzureADMyOrg'
  isFallbackPublicClient: false
  spa: {
    redirectUris: browserRedirectUris
  }
  requiredResourceAccess: [
    {
      resourceAppId: policyApi.appId
      resourceAccess: [
        {
          id: invokeScopeId
          type: 'Scope'
        }
      ]
    }
  ]
  owners: {
    relationships: [
      ownerPrincipalId
    ]
    relationshipSemantics: 'Append'
  }
}

resource browserPrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = if (enableBrowserSignIn) {
  appId: browserClient!.appId
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
  api: {
    clientId: policyApi.appId
    objectId: policyApi.id
    servicePrincipalId: policyApiPrincipal.id
    audience: policyApi.appId
    identifierUri: apiIdentifierUri
    delegatedScope: '${apiIdentifierUri}/PolicyReview.Invoke'
    scopeId: invokeScopeId
  }
  groups: {
    employees: employees.id
    securityReviewers: reviewers.id
  }
  browser: enableBrowserSignIn ? {
    clientId: browserClient!.appId
    objectId: browserClient!.id
    servicePrincipalId: browserPrincipal!.id
    redirectUri: browserRedirectUris[0]
    scope: '${apiIdentifierUri}/PolicyReview.Invoke'
    consentPrincipalIds: union(employeeMemberIds, reviewerMemberIds)
    redirectUris: browserRedirectUris
  } : null
}
