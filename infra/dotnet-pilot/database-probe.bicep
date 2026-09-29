targetScope = 'resourceGroup'

@secure()
param setupScriptBase64 string

param location string = 'eastus2'
param containerGroupName string = 'aci-ofmfleet-dotnet-foundation'
param postgresHost string = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'
param administratorIdentityName string = 'id-ofmfleet-pgverify'
param runtimeIdentityName string = 'id-ofmfleet-dotnet-dev-ykbpnrpd'
param migrationIdentityName string = 'id-ofmfleet-web-dev-ykbpnrpd'
param administratorClientId string
param runtimeClientId string
param migrationClientId string
param targetDatabaseName string = 'ofm_dotnet_pilot'

resource administratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: administratorIdentityName
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: runtimeIdentityName
}

resource migrationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: migrationIdentityName
}

resource probe 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: containerGroupName
  location: location
  tags: {
    workload: 'oracle-forms-migration-fleet'
    environment: 'dev'
    component: 'dotnet-pilot-foundation-verification'
    'managed-by': 'bicep'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${administratorIdentity.id}': {}
      '${runtimeIdentity.id}': {}
      '${migrationIdentity.id}': {}
    }
  }
  properties: {
    osType: 'Linux'
    restartPolicy: 'Never'
    containers: [
      {
        name: 'database-foundation'
        properties: {
          image: 'postgres:16-alpine'
          command: [
            'sh'
            '-c'
            'set -eu; echo "EGRESS_IP=$(wget -qO- https://api.ipify.org)"; attempt=0; until pg_isready -h "$PGHOST" -p 5432 -t 2 >/dev/null 2>&1; do attempt=$((attempt + 1)); if [ "$attempt" -ge 60 ]; then echo "PostgreSQL did not become reachable through an exact firewall rule." >&2; exit 10; fi; sleep 5; done; printf \'%s\' "$SETUP_SCRIPT_B64" | base64 -d > /tmp/setup.sh; MIGRATION_USER="$MIGRATION_USER" PLATFORM_DATABASE=ofm_platform sh /tmp/setup.sh'
          ]
          environmentVariables: [
            {
              name: 'ADMIN_CLIENT_ID'
              value: administratorClientId
            }
            {
              name: 'TARGET_CLIENT_ID'
              value: runtimeClientId
            }
            {
              name: 'PGHOST'
              value: postgresHost
            }
            {
              name: 'ADMIN_USER'
              value: administratorIdentityName
            }
            {
              name: 'TARGET_USER'
              value: runtimeIdentityName
            }
            {
              name: 'MIGRATION_USER'
              value: migrationIdentityName
            }
            {
              name: 'MIGRATION_CLIENT_ID'
              value: migrationClientId
            }
            {
              name: 'TARGET_DATABASE'
              value: targetDatabaseName
            }
            {
              name: 'SETUP_SCRIPT_B64'
              secureValue: setupScriptBase64
            }
          ]
          resources: {
            requests: {
              cpu: 1
              memoryInGB: json('1.5')
            }
          }
        }
      }
    ]
  }
}

output containerGroupName string = probe.name