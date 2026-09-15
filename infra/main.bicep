targetScope = 'resourceGroup'

@description('Prefix for generated resource names.')
@minLength(3)
@maxLength(12)
param namePrefix string = 'tickets'

param location string = resourceGroup().location

@description('Container image. Build and push to the registry, then redeploy with this set.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Entra app registration (client) ID used by Container Apps built-in auth.')
param authClientId string

@description('Client secret for the Entra app registration used by built-in auth.')
@secure()
param authClientSecret string

param databricksWorkspaceUrl string = 'https://adb-7405619192018422.2.azuredatabricks.net'
param lakebaseEndpointName string = 'projects/lakebaseproject1/branches/production/endpoints/primary'

@description('Direct endpoint host. The -pooler host rejects OAuth credentials.')
param lakebaseHost string = 'ep-twilight-rice-e1ipdt0o.database.eastus2.azuredatabricks.net'

param lakebaseDatabase string = 'databricks_postgres'
param lakebaseSchema string = 'dbdemos_aibi_customer_support'
param lakebaseTable string = 'lb_tickets_clean'

@description('Existing managed identity already registered in Databricks and granted SELECT in Lakebase.')
param identityName string = 'tickets-id'

@description('Resource group holding that identity.')
param identityResourceGroup string = 'tickets-dashboard-rg'

@description('Tags applied to every resource, for cost attribution and governance.')
param tags object = {
  workload: 'tickets-dashboard'
  environment: 'demo'
  dataClassification: 'confidential'
  managedBy: 'bicep'
}

@description('''
Zone redundancy cannot be turned on for an existing environment: it needs a
VNet-integrated environment, so changing this recreates the environment and
changes the ingress FQDN, which then requires updating the auth redirect URI.
''')
param zoneRedundant bool = false

var suffix = uniqueString(resourceGroup().id, namePrefix)
var registryName = toLower('${namePrefix}acr${suffix}')

// Referenced, never created: its client ID is baked into the Databricks service
// principal and the Postgres role, so a new identity would silently lose all access.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
  scope: resourceGroup(identityResourceGroup)
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, 'AcrPull')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d'
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  tags: tags
  properties: {
    zoneRedundant: zoneRedundant
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-dashboard'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          // The only secret in the system; Lakebase access itself is entirely Entra-based.
          name: 'auth-client-secret'
          value: authClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'Lakebase__WorkspaceUrl', value: databricksWorkspaceUrl }
            { name: 'Lakebase__EndpointName', value: lakebaseEndpointName }
            { name: 'Lakebase__Host', value: lakebaseHost }
            { name: 'Lakebase__Database', value: lakebaseDatabase }
            { name: 'Lakebase__Schema', value: lakebaseSchema }
            { name: 'Lakebase__Table', value: lakebaseTable }
            // The Postgres role for a service principal is its client ID.
            { name: 'Lakebase__Role', value: identity.properties.clientId }
            { name: 'Identity__ManagedIdentityClientId', value: identity.properties.clientId }
            { name: 'Identity__UseDeveloperCredential', value: 'false' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 15
              periodSeconds: 30
              failureThreshold: 10
            }
          ]
        }
      ]
      scale: {
        // Keep one replica warm so the rotating Lakebase credential stays cached.
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = {
  parent: app
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: '${az.environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
          clientId: authClientId
          clientSecretSettingName: 'auth-client-secret'
        }
        validation: {
          allowedAudiences: [
            'api://${authClientId}'
          ]
        }
      }
    }
    login: {
      preserveUrlFragmentsForLogins: true
    }
  }
}

output dashboardUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output registryLoginServer string = registry.properties.loginServer
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output redirectUri string = 'https://${app.properties.configuration.ingress.fqdn}/.auth/login/aad/callback'
