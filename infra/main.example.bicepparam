using 'main.bicep'

// Copy to main.local.bicepparam and replace every example value before deployment.
param resourceGroupName = 'rg-secure-policy-rag'
param location = 'eastus2'
param searchLocation = 'eastus2'
param publisherEmail = 'azure-admin@example.com'
param operatorPrincipalId = '00000000-0000-0000-0000-000000000000'

param modelGatewayTenantId = '00000000-0000-0000-0000-000000000000'
param modelGatewayAudience = '00000000-0000-0000-0000-000000000000'
