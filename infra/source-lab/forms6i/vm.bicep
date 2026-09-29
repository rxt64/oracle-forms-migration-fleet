targetScope = 'resourceGroup'

param location string
param vmName string
param adminUsername string

@secure()
param adminPassword string

param vmSize string
param imageVersion string
param networkInterfaceResourceId string
param virtualNetworkResourceId string
param subnetResourceId string
param networkSecurityGroupResourceId string
param publicIpResourceId string
param tags object

resource virtualMachine 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: vmName
  location: location
  tags: tags
  properties: {
    hardwareProfile: {
      vmSize: vmSize
    }
    osProfile: {
      computerName: 'ofmforms6i'
      adminUsername: adminUsername
      adminPassword: adminPassword
      allowExtensionOperations: true
      windowsConfiguration: {
        provisionVMAgent: true
        enableAutomaticUpdates: true
        patchSettings: {
          assessmentMode: 'AutomaticByPlatform'
          patchMode: 'AutomaticByPlatform'
          enableHotpatching: false
        }
      }
    }
    securityProfile: {
      securityType: 'TrustedLaunch'
      uefiSettings: {
        secureBootEnabled: true
        vTpmEnabled: true
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'MicrosoftWindowsServer'
        offer: 'WindowsServer'
        sku: '2022-datacenter-azure-edition-smalldisk'
        version: imageVersion
      }
      osDisk: {
        name: '${vmName}-osdisk'
        createOption: 'FromImage'
        caching: 'ReadWrite'
        diskSizeGB: 32
        deleteOption: 'Delete'
        managedDisk: {
          storageAccountType: 'StandardSSD_LRS'
        }
      }
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: networkInterfaceResourceId
          properties: {
            deleteOption: 'Delete'
            primary: true
          }
        }
      ]
    }
    diagnosticsProfile: {
      bootDiagnostics: {
        enabled: true
      }
    }
  }
}

resource publicIpAddress 'Microsoft.Network/publicIPAddresses@2024-05-01' existing = {
  name: last(split(publicIpResourceId, '/'))
}

output vmName string = virtualMachine.name
output vmResourceId string = virtualMachine.id
output vmSize string = vmSize
output imageUrn string = 'MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure-edition-smalldisk:${imageVersion}'
output virtualNetworkResourceId string = virtualNetworkResourceId
output subnetResourceId string = subnetResourceId
output networkSecurityGroupResourceId string = networkSecurityGroupResourceId
output networkInterfaceResourceId string = networkInterfaceResourceId
output publicIpResourceId string = publicIpResourceId
output publicIpAddress string = publicIpAddress.properties.ipAddress