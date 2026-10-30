using './identity.bicep'

// Copy to identity.local.bicepparam and replace every example object ID.
param tenantId = '00000000-0000-0000-0000-000000000000'
param ownerPrincipalId = '00000000-0000-0000-0000-000000000000'
param enableBrowserSignIn = true
param employeeMemberIds = [
  '00000000-0000-0000-0000-000000000000'
]
param reviewerMemberIds = [
  '00000000-0000-0000-0000-000000000000'
]
