using './main.bicep'

// Deploy with:
//   $env:AUTH_CLIENT_SECRET = "<secret>"
//   az deployment group create -g tickets-dashboard-rg --parameters infra/demo.bicepparam
//
// The secret is read from the environment so it is never committed and never
// stored in the parameter file.

param namePrefix = 'tickets'

param tags = {
  workload: 'tickets-dashboard'
  environment: 'demo'
  dataClassification: 'confidential'
  managedBy: 'bicep'
}

param authClientId = '23b870db-bb0b-433e-822c-71fe161819a6'
param authClientSecret = readEnvironmentVariable('AUTH_CLIENT_SECRET')

// Pin by digest, not by tag: tags are mutable and a rebuild would silently
// change what runs. Replace on each release.
param containerImage = 'ticketsacrjuimgm6jl3a66.azurecr.io/tickets-dashboard@sha256:b879f27bbc4845b842980735634f97a131d4e69635deb9e69262f7235dc73007'

param identityName = 'tickets-id'
param identityResourceGroup = 'tickets-dashboard-rg'

// Single-zone: this environment predates VNet integration and cannot be
// converted in place. See prod.bicepparam for the zone-redundant shape.
param zoneRedundant = false
