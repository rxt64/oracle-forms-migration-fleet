targetScope = 'resourceGroup'

@description('Azure region for the isolated Forms6i source lab.')
param location string = 'eastus2'

@description('Administrator account retained locally with DPAPI protection.')
@minLength(3)
@maxLength(20)
param adminUsername string = 'ofmlabadmin'

@description('Cryptographically generated administrator password. Never persist this value in the repository.')
@secure()
param adminPassword string

@description('Selected VM size validated for this subscription and region.')
param vmSize string = 'Standard_D2as_v7'

@description('Pinned Windows Server image version validated in the selected region.')
param imageVersion string = '20348.5622.260906'

@description('Size for the Windows Oracle 9i host; large enough to host an older Windows guest under Hyper-V if the 9i installer refuses Server 2022.')
param oracle9iVmSize string = 'Standard_D4as_v7'

@description('OS disk size for the Windows Oracle 9i host; holds the installation media, the database, and an optional nested guest.')
param oracle9iOsDiskSizeGb int = 256

var suffix = take(uniqueString(subscription().id, resourceGroup().id, 'forms6i-source-lab'), 8)
var vmName = 'vm-ofm-forms6i-${suffix}'
var vnetName = 'vnet-ofm-forms6i-${suffix}'
var subnetName = 'snet-forms6i-isolated'
var nsgName = 'nsg-ofm-forms6i-${suffix}'
var publicIpName = 'pip-ofm-forms6i-${suffix}'
var nicName = 'nic-ofm-forms6i-${suffix}'
var oracleXeSubnetName = 'snet-oraclexe-isolated'
var oracleXeNsgName = 'nsg-ofm-oraclexe-${suffix}'
var oracle9iVmName = 'vm-ofm-oracle9i-${suffix}'
var oracle9iPublicIpName = 'pip-ofm-oracle9i-${suffix}'
var oracle9iNicName = 'nic-ofm-oracle9i-${suffix}'

// Both addresses are pinned so every security rule can name an exact /32 instead of a range.
var formsPrivateIpAddress = '10.246.0.4'
var oracle9iPrivateIpAddress = '10.246.0.37'

var tags = {
  workload: 'oracle-forms-migration-fleet'
  component: 'forms6i-source-lab'
  environment: 'dev'
  'managed-by': 'bicep'
  isolation: 'deny-inbound-private-egress'
}

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: nsgName
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'Allow-Bastion-Developer-RDP'
        properties: {
          priority: 100
          access: 'Allow'
          direction: 'Inbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '3389'
          sourceAddressPrefix: '168.63.129.16/32'
          destinationAddressPrefix: '10.246.0.4/32'
          description: 'Bastion Developer platform RDP to the source VM only.'
        }
      }
      {
        name: 'Deny-All-Inbound'
        properties: {
          priority: 110
          access: 'Deny'
          direction: 'Inbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
          description: 'Deny all ingress except the exact Bastion Developer RDP rule.'
        }
      }
      {
        name: 'Allow-Azure-DNS-UDP'
        properties: {
          priority: 100
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Udp'
          sourcePortRange: '*'
          destinationPortRange: '53'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'Azure platform DNS only.'
        }
      }
      {
        name: 'Allow-Azure-DNS-TCP'
        properties: {
          priority: 110
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '53'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'Azure platform DNS fallback only.'
        }
      }
      {
        name: 'Allow-Azure-Guest-Agent'
        properties: {
          priority: 120
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRanges: [
            '80'
            '32526'
          ]
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'WireServer and HostGAPlugin communication required by the Azure VM Agent.'
        }
      }
      {
        name: 'Allow-Azure-IMDS'
        properties: {
          priority: 130
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '80'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '169.254.169.254'
          description: 'Azure Instance Metadata Service only; the VM has no managed identity.'
        }
      }
      {
        name: 'Allow-Windows-Activation'
        properties: {
          priority: 140
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '1688'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'AzureCloud'
          description: 'Windows Azure KMS activation.'
        }
      }
      {
        name: 'Allow-Azure-HTTPS'
        properties: {
          priority: 200
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'AzureCloud'
          description: 'Azure management, extension, and platform HTTPS endpoints.'
        }
      }
      {
        name: 'Allow-Internet-HTTPS'
        properties: {
          priority: 210
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'Internet'
          description: 'HTTPS-only public egress through the attached Standard public IP.'
        }
      }
      {
        name: 'Allow-Oracle9i-SqlNet-Outbound'
        properties: {
          priority: 225
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '1521'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '${oracle9iPrivateIpAddress}/32'
          description: 'Forms 6i Net8 client to the Oracle 9i host only.'
        }
      }
      {
        name: 'Allow-Oracle9i-Redirect-Outbound'
        properties: {
          priority: 226
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '49152-65535'
          sourceAddressPrefix: '${formsPrivateIpAddress}/32'
          destinationAddressPrefix: '${oracle9iPrivateIpAddress}/32'
          description: 'Legacy Oracle dedicated-server redirects between the two lab VMs only.'
        }
      }
      {
        name: 'Deny-Virtual-Network-Outbound'
        properties: {
          priority: 300
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          description: 'No access to this or any connected virtual network.'
        }
      }
      {
        name: 'Deny-RFC1918-10'
        properties: {
          priority: 310
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '10.0.0.0/8'
        }
      }
      {
        name: 'Deny-RFC1918-172'
        properties: {
          priority: 320
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '172.16.0.0/12'
        }
      }
      {
        name: 'Deny-RFC1918-192'
        properties: {
          priority: 330
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '192.168.0.0/16'
        }
      }
      {
        name: 'Deny-Shared-Address-Space'
        properties: {
          priority: 340
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '100.64.0.0/10'
        }
      }
      {
        name: 'Deny-Link-Local'
        properties: {
          priority: 350
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '169.254.0.0/16'
          description: 'Platform endpoint exceptions are explicitly allowed at higher priority.'
        }
      }
      {
        name: 'Deny-All-Other-Outbound'
        properties: {
          priority: 4096
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
          description: 'Fail closed for every egress path not explicitly authorized above.'
        }
      }
    ]
  }
}

resource oracleXeNetworkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: oracleXeNsgName
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'Allow-Forms6i-SqlNet-Inbound-Oracle9i'
        properties: {
          priority: 105
          access: 'Allow'
          direction: 'Inbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '1521'
          sourceAddressPrefix: '${formsPrivateIpAddress}/32'
          destinationAddressPrefix: '${oracle9iPrivateIpAddress}/32'
          description: 'Oracle 9i SQL*Net listener reachable from the Forms 6i host only.'
        }
      }
      {
        name: 'Allow-Forms6i-Redirect-Inbound'
        properties: {
          priority: 106
          access: 'Allow'
          direction: 'Inbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '49152-65535'
          sourceAddressPrefix: '${formsPrivateIpAddress}/32'
          destinationAddressPrefix: '${oracle9iPrivateIpAddress}/32'
          description: 'Legacy Oracle dedicated-server redirects from the Forms lab VM only.'
        }
      }
      {
        name: 'Deny-All-Inbound'
        properties: {
          priority: 110
          access: 'Deny'
          direction: 'Inbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
          description: 'Deny all ingress except SQL*Net from the Forms host; administration is Run Command over the agent outbound channel.'
        }
      }
      {
        name: 'Allow-Azure-DNS-UDP'
        properties: {
          priority: 100
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Udp'
          sourcePortRange: '*'
          destinationPortRange: '53'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'Azure platform DNS only.'
        }
      }
      {
        name: 'Allow-Azure-DNS-TCP'
        properties: {
          priority: 110
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '53'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'Azure platform DNS fallback only.'
        }
      }
      {
        name: 'Allow-Azure-Guest-Agent'
        properties: {
          priority: 120
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRanges: [
            '80'
            '32526'
          ]
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '168.63.129.16'
          description: 'WireServer and HostGAPlugin communication required by the Azure VM Agent.'
        }
      }
      {
        name: 'Allow-Azure-IMDS'
        properties: {
          priority: 130
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '80'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '169.254.169.254'
          description: 'Azure Instance Metadata Service for managed identity tokens.'
        }
      }
      {
        name: 'Allow-Container-Registry-HTTPS'
        properties: {
          priority: 190
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'AzureContainerRegistry'
          description: 'Pull the verified Oracle XE installer artifact from the lab registry.'
        }
      }
      {
        name: 'Allow-Azure-HTTPS'
        properties: {
          priority: 200
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'AzureCloud'
          description: 'Azure management, extension, and platform HTTPS endpoints.'
        }
      }
      {
        name: 'Allow-Internet-HTTPS'
        properties: {
          priority: 210
          access: 'Allow'
          direction: 'Outbound'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'Internet'
          description: 'Oracle Linux package repositories over HTTPS only.'
        }
      }
      {
        name: 'Deny-Virtual-Network-Outbound'
        properties: {
          priority: 300
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          description: 'The database never initiates traffic into the virtual network.'
        }
      }
      {
        name: 'Deny-RFC1918-10'
        properties: {
          priority: 310
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '10.0.0.0/8'
        }
      }
      {
        name: 'Deny-RFC1918-172'
        properties: {
          priority: 320
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '172.16.0.0/12'
        }
      }
      {
        name: 'Deny-RFC1918-192'
        properties: {
          priority: 330
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '192.168.0.0/16'
        }
      }
      {
        name: 'Deny-Shared-Address-Space'
        properties: {
          priority: 340
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '100.64.0.0/10'
        }
      }
      {
        name: 'Deny-Link-Local'
        properties: {
          priority: 350
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '169.254.0.0/16'
          description: 'Platform endpoint exceptions are explicitly allowed at higher priority.'
        }
      }
      {
        name: 'Deny-All-Other-Outbound'
        properties: {
          priority: 4096
          access: 'Deny'
          direction: 'Outbound'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
          description: 'Fail closed for every egress path not explicitly authorized above.'
        }
      }
    ]
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    privateEndpointVNetPolicies: 'Disabled'
    addressSpace: {
      addressPrefixes: [
        '10.246.0.0/24'
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: '10.246.0.0/27'
          defaultOutboundAccess: false
          networkSecurityGroup: {
            id: networkSecurityGroup.id
          }
          privateEndpointNetworkPolicies: 'Enabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
      {
        name: oracleXeSubnetName
        properties: {
          addressPrefix: '10.246.0.32/27'
          defaultOutboundAccess: false
          networkSecurityGroup: {
            id: oracleXeNetworkSecurityGroup.id
          }
          privateEndpointNetworkPolicies: 'Enabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

resource publicIpAddress 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: publicIpName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    ddosSettings: {
      protectionMode: 'VirtualNetworkInherited'
    }
    ipTags: [
      {
        ipTagType: 'FirstPartyUsage'
        tag: '/Unprivileged'
      }
    ]
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    idleTimeoutInMinutes: 4
  }
}

resource networkInterface 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: nicName
  location: location
  kind: 'Regular'
  tags: tags
  properties: {
    allowPort25Out: true
    auxiliaryMode: 'None'
    auxiliarySku: 'None'
    disableTcpStateTracking: false
    enableAcceleratedNetworking: false
    enableIPForwarding: false
    networkSecurityGroup: {
      id: networkSecurityGroup.id
    }
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          // Pinned because the Bastion RDP rule and the Oracle XE SQL*Net rules name this exact address.
          privateIPAllocationMethod: 'Static'
          privateIPAddress: formsPrivateIpAddress
          privateIPAddressVersion: 'IPv4'
          subnet: {
            id: resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, subnetName)
          }
          publicIPAddress: {
            id: publicIpAddress.id
          }
        }
      }
    ]
  }
}

module virtualMachine './vm.bicep' = {
  name: 'deploy-${vmName}'
  params: {
    location: location
    vmName: vmName
    adminUsername: adminUsername
    adminPassword: adminPassword
    vmSize: vmSize
    imageVersion: imageVersion
    networkInterfaceResourceId: networkInterface.id
    virtualNetworkResourceId: virtualNetwork.id
    subnetResourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, subnetName)
    networkSecurityGroupResourceId: networkSecurityGroup.id
    publicIpResourceId: publicIpAddress.id
    tags: tags
  }
}

resource oracle9iPublicIpAddress 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: oracle9iPublicIpName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    ddosSettings: {
      protectionMode: 'VirtualNetworkInherited'
    }
    ipTags: [
      {
        ipTagType: 'FirstPartyUsage'
        tag: '/Unprivileged'
      }
    ]
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    idleTimeoutInMinutes: 4
  }
}

resource oracle9iNetworkInterface 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: oracle9iNicName
  location: location
  kind: 'Regular'
  tags: tags
  properties: {
    allowPort25Out: false
    auxiliaryMode: 'None'
    auxiliarySku: 'None'
    disableTcpStateTracking: false
    enableAcceleratedNetworking: false
    enableIPForwarding: false
    networkSecurityGroup: {
      id: oracleXeNetworkSecurityGroup.id
    }
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          privateIPAllocationMethod: 'Static'
          privateIPAddress: oracle9iPrivateIpAddress
          privateIPAddressVersion: 'IPv4'
          subnet: {
            id: resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, oracleXeSubnetName)
          }
          publicIPAddress: {
            id: oracle9iPublicIpAddress.id
          }
        }
      }
    ]
  }
}

module oracle9iVirtualMachine './oracle9i-vm.bicep' = {
  name: 'deploy-${oracle9iVmName}'
  params: {
    location: location
    vmName: oracle9iVmName
    adminUsername: adminUsername
    adminPassword: adminPassword
    vmSize: oracle9iVmSize
    imageVersion: imageVersion
    osDiskSizeGb: oracle9iOsDiskSizeGb
    networkInterfaceResourceId: oracle9iNetworkInterface.id
    tags: tags
  }
}

output vmName string = virtualMachine.outputs.vmName
output vmResourceId string = virtualMachine.outputs.vmResourceId
output vmSize string = virtualMachine.outputs.vmSize
output imageUrn string = virtualMachine.outputs.imageUrn
output virtualNetworkResourceId string = virtualMachine.outputs.virtualNetworkResourceId
output subnetResourceId string = virtualMachine.outputs.subnetResourceId
output networkSecurityGroupResourceId string = virtualMachine.outputs.networkSecurityGroupResourceId
output networkInterfaceResourceId string = virtualMachine.outputs.networkInterfaceResourceId
output publicIpResourceId string = virtualMachine.outputs.publicIpResourceId
output publicIpAddress string = virtualMachine.outputs.publicIpAddress
output oracleXeSubnetResourceId string = resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, oracleXeSubnetName)
output oracleXeNetworkSecurityGroupResourceId string = oracleXeNetworkSecurityGroup.id
output oracle9iVmName string = oracle9iVirtualMachine.outputs.vmName
output oracle9iVmResourceId string = oracle9iVirtualMachine.outputs.vmResourceId
output oracle9iImageUrn string = oracle9iVirtualMachine.outputs.imageUrn
output oracle9iPrivateIpAddress string = oracle9iPrivateIpAddress
output oracle9iNetworkInterfaceResourceId string = oracle9iNetworkInterface.id