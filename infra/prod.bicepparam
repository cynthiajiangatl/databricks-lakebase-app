using './main.bicep'

// Production shape. NOT yet deployed.
//
// zoneRedundant requires a VNet-integrated Container Apps environment, which
// cannot be converted from an existing non-VNet one. Deploying this creates a
// new environment with a new ingress FQDN, so the auth app registration's
// redirect URI must be updated to match before sign-in will work.
//
// Still outstanding for a production posture, and deliberately not faked here:
//   - VNet + private endpoints to Lakebase and the registry
//   - Container Registry Premium (Basic cannot do Private Link)
//   - Key Vault for the auth secret, reachable over a private endpoint

param namePrefix = 'ticketsprod'

param tags = {
  workload: 'tickets-dashboard'
  environment: 'production'
  dataClassification: 'confidential'
  managedBy: 'bicep'
}

param authClientId = '00000000-0000-0000-0000-000000000000'
param authClientSecret = readEnvironmentVariable('AUTH_CLIENT_SECRET')

param containerImage = 'REPLACE.azurecr.io/tickets-dashboard@sha256:REPLACE'

param identityName = 'tickets-prod-id'
param identityResourceGroup = 'tickets-prod-rg'

param zoneRedundant = true
