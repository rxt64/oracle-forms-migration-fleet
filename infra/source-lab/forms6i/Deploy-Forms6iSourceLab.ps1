#requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Preview', 'Deploy', 'Verify')]
    [string] $Operation,

    [ValidatePattern('^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
    [string] $SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',

    [ValidatePattern('^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
    [string] $TenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86',

    [ValidatePattern('^[A-Za-z0-9._()\-]{1,90}$')]
    [string] $ResourceGroupName = 'rg-oracle-forms-migration-fleet-dev-b9f0e875',

    [ValidateSet('eastus2', 'centralus')]
    [string] $Location = 'eastus2',

    [ValidatePattern('^[A-Za-z][A-Za-z0-9._-]{2,19}$')]
    [string] $AdminUsername = 'ofmlabadmin'
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$vmTemplateFile = Join-Path $PSScriptRoot 'vm.bicep'
$deploymentName = 'forms6i-source-lab-foundation'
$localStateRoot = Join-Path $env:LOCALAPPDATA 'OracleFormsMigrationFleet\forms6i-source-lab'
$credentialPath = Join-Path $localStateRoot 'admin-credential.dpapi.json'
$evidenceRoot = Join-Path $localStateRoot 'evidence'
$temporaryStateRoot = $null
$temporaryParameters = $null
$plainPassword = $null
$securePassword = $null
$recoveryContext = $null
$operationFailed = $false

function Resolve-AzureCli {
    $command = Get-Command az -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $knownPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
    if (Test-Path -LiteralPath $knownPath) {
        return $knownPath
    }

    throw 'Azure CLI was not found.'
}

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $output = & $script:azureCli @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed ($Label)."
    }
    return $output | ConvertFrom-Json -Depth 100
}

function New-CryptographicPassword {
    $characterSets = @(
        'ABCDEFGHJKLMNPQRSTUVWXYZ',
        'abcdefghijkmnopqrstuvwxyz',
        '23456789',
        '!#$%&*+-=?@^_'
    )
    $characters = [System.Collections.Generic.List[char]]::new()
    foreach ($characterSet in $characterSets) {
        $characters.Add($characterSet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($characterSet.Length)])
    }

    $allCharacters = [string]::Concat($characterSets)
    while ($characters.Count -lt 32) {
        $characters.Add($allCharacters[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($allCharacters.Length)])
    }
    for ($index = $characters.Count - 1; $index -gt 0; $index--) {
        $swapIndex = [System.Security.Cryptography.RandomNumberGenerator]::GetInt32($index + 1)
        ($characters[$index], $characters[$swapIndex]) = ($characters[$swapIndex], $characters[$index])
    }
    return [string]::new($characters.ToArray())
}

function Initialize-UserOnlyDirectory {
    param([Parameter(Mandatory)] [string] $Path)

    [void](New-Item -ItemType Directory -Path $Path -Force)
    Set-UserOnlyAcl -Path $Path -Directory
}

function Set-UserOnlyFileAcl {
    param([Parameter(Mandatory)] [string] $Path)

    Set-UserOnlyAcl -Path $Path
}

function Set-UserOnlyAcl {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $Directory
    )

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $grant = if ($Directory) { "*$($identity.Value):(OI)(CI)F" } else { "*$($identity.Value):F" }
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $Path '/inheritance:r' '/grant:r' $grant *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to apply a current-user-only ACL to local deployment state.'
    }

    $unexpectedRules = @((Get-Acl -LiteralPath $Path).Access | Where-Object {
            try {
                $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -ne $identity.Value
            }
            catch {
                $true
            }
        })
    if ($unexpectedRules.Count -ne 0) {
        throw 'Local deployment state ACL contains a principal other than the current user.'
    }
}

function Protect-LocalCredential {
    param(
        [Parameter(Mandatory)] [securestring] $Password
    )

    Initialize-UserOnlyDirectory -Path $localStateRoot
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User

    $credential = [ordered]@{
        schemaVersion = 1
        subscriptionId = $SubscriptionId
        tenantId = $TenantId
        resourceGroup = $ResourceGroupName
        username = $AdminUsername
        encryptedPassword = ConvertFrom-SecureString -SecureString $Password
        protectedForSid = $identity.Value
        createdUtc = [DateTime]::UtcNow.ToString('O')
    }
    $credential | ConvertTo-Json | Set-Content -LiteralPath $credentialPath -Encoding utf8

    Set-UserOnlyFileAcl -Path $credentialPath
}

function Get-DeploymentPassword {
    if (Test-Path -LiteralPath $credentialPath) {
        $credential = Get-Content -LiteralPath $credentialPath -Raw | ConvertFrom-Json
        if (-not [string]::Equals([string] $credential.subscriptionId, $SubscriptionId, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string] $credential.resourceGroup, $ResourceGroupName, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string] $credential.username, $AdminUsername, [StringComparison]::Ordinal)) {
            throw "The retained Forms6i credential at '$credentialPath' belongs to a different deployment context."
        }
        $script:securePassword = ConvertTo-SecureString -String ([string] $credential.encryptedPassword)
        return [System.Net.NetworkCredential]::new('', $script:securePassword).Password
    }

    $password = New-CryptographicPassword
    $script:securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    Protect-LocalCredential -Password $script:securePassword
    return $password
}

function Write-ParameterFile {
    param(
        [Parameter(Mandatory)] [string] $Password
    )

    $parameterValues = [ordered]@{
        location = @{ value = $Location }
        adminUsername = @{ value = $AdminUsername }
        adminPassword = @{ value = $Password }
        vmSize = @{ value = 'Standard_D2as_v7' }
        imageVersion = @{ value = '20348.5622.260906' }
    }
    if ($null -ne $script:recoveryContext) {
        $parameterValues.vmName = @{ value = $script:recoveryContext.vmName }
        $parameterValues.networkInterfaceResourceId = @{ value = $script:recoveryContext.networkInterfaceResourceId }
        $parameterValues.virtualNetworkResourceId = @{ value = $script:recoveryContext.virtualNetworkResourceId }
        $parameterValues.subnetResourceId = @{ value = $script:recoveryContext.subnetResourceId }
        $parameterValues.networkSecurityGroupResourceId = @{ value = $script:recoveryContext.networkSecurityGroupResourceId }
        $parameterValues.publicIpResourceId = @{ value = $script:recoveryContext.publicIpResourceId }
        $parameterValues.tags = @{ value = $script:recoveryContext.tags }
    }

    Initialize-UserOnlyDirectory -Path $localStateRoot
    $script:temporaryStateRoot = Join-Path $localStateRoot "temporary-$([Guid]::NewGuid().ToString('N'))"
    Initialize-UserOnlyDirectory -Path $script:temporaryStateRoot
    $script:temporaryParameters = Join-Path $script:temporaryStateRoot 'deployment.parameters.json'
    $stream = [System.IO.File]::Open($script:temporaryParameters, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $stream.Dispose()
    Set-UserOnlyFileAcl -Path $script:temporaryParameters

    $parameters = [ordered]@{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = $parameterValues
    }
    $parameters | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryParameters -Encoding utf8
}

function Write-JsonEvidence {
    param(
        [Parameter(Mandatory)] [string] $BaseName,
        [Parameter(Mandatory)] [object] $Value
    )

    Initialize-UserOnlyDirectory -Path $evidenceRoot
    $path = Join-Path $evidenceRoot "$BaseName-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Get-ExpectedNetworkSecurityRules {
    return @(
        [pscustomobject]@{ Name = 'Allow-Bastion-Developer-RDP'; Priority = 100; Access = 'Allow'; Direction = 'Inbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('3389'); SourceAddresses = @('168.63.129.16/32'); DestinationAddresses = @('10.246.0.4/32') }
        [pscustomobject]@{ Name = 'Deny-All-Inbound'; Priority = 110; Access = 'Deny'; Direction = 'Inbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('*') }
        [pscustomobject]@{ Name = 'Allow-Azure-DNS-UDP'; Priority = 100; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Udp'; SourcePorts = @('*'); DestinationPorts = @('53'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-DNS-TCP'; Priority = 110; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('53'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-Guest-Agent'; Priority = 120; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('80', '32526'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-IMDS'; Priority = 130; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('80'); SourceAddresses = @('*'); DestinationAddresses = @('169.254.169.254') }
        [pscustomobject]@{ Name = 'Allow-Windows-Activation'; Priority = 140; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('1688'); SourceAddresses = @('*'); DestinationAddresses = @('AzureCloud') }
        [pscustomobject]@{ Name = 'Allow-Azure-HTTPS'; Priority = 200; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('443'); SourceAddresses = @('*'); DestinationAddresses = @('AzureCloud') }
        [pscustomobject]@{ Name = 'Allow-Internet-HTTPS'; Priority = 210; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('443'); SourceAddresses = @('*'); DestinationAddresses = @('Internet') }
        [pscustomobject]@{ Name = 'Allow-Oracle9i-SqlNet-Outbound'; Priority = 225; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('1521'); SourceAddresses = @('*'); DestinationAddresses = @('10.246.0.37/32') }
        [pscustomobject]@{ Name = 'Allow-Oracle9i-Redirect-Outbound'; Priority = 226; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('49152-65535'); SourceAddresses = @('10.246.0.4/32'); DestinationAddresses = @('10.246.0.37/32') }
        [pscustomobject]@{ Name = 'Deny-Virtual-Network-Outbound'; Priority = 300; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('VirtualNetwork') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-10'; Priority = 310; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('10.0.0.0/8') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-172'; Priority = 320; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('172.16.0.0/12') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-192'; Priority = 330; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('192.168.0.0/16') }
        [pscustomobject]@{ Name = 'Deny-Shared-Address-Space'; Priority = 340; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('100.64.0.0/10') }
        [pscustomobject]@{ Name = 'Deny-Link-Local'; Priority = 350; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('169.254.0.0/16') }
        [pscustomobject]@{ Name = 'Deny-All-Other-Outbound'; Priority = 4096; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('*') }
    )
}

function Get-NetworkRuleValues {
    param(
        [Parameter(Mandatory)] [object] $Rule,
        [Parameter(Mandatory)] [string] $PluralName,
        [Parameter(Mandatory)] [string] $SingularName
    )

    $property = $Rule.PSObject.Properties[$PluralName]
    if ($null -eq $property -or $null -eq $property.Value -or @($property.Value).Count -eq 0) {
        $property = $Rule.PSObject.Properties[$SingularName]
    }
    if ($null -eq $property -or $null -eq $property.Value) {
        return @()
    }
    return @($property.Value | ForEach-Object { [string] $_ } | Sort-Object -Unique)
}

function Assert-NetworkRuleValuesEqual {
    param(
        [Parameter(Mandatory)] [string] $RuleName,
        [Parameter(Mandatory)] [string] $FieldName,
        [Parameter(Mandatory)] [string[]] $Expected,
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] [string[]] $Actual
    )

    if (-not [string]::Equals((@($Expected | Sort-Object -Unique) -join ','), (@($Actual | Sort-Object -Unique) -join ','), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Network security rule '$RuleName' has an unexpected $FieldName value."
    }
}

function Get-ExpectedOracleXeNetworkSecurityRules {
    return @(
        [pscustomobject]@{ Name = 'Allow-Forms6i-SqlNet-Inbound-Oracle9i'; Priority = 105; Access = 'Allow'; Direction = 'Inbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('1521'); SourceAddresses = @('10.246.0.4/32'); DestinationAddresses = @('10.246.0.37/32') }
        [pscustomobject]@{ Name = 'Allow-Forms6i-Redirect-Inbound'; Priority = 106; Access = 'Allow'; Direction = 'Inbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('49152-65535'); SourceAddresses = @('10.246.0.4/32'); DestinationAddresses = @('10.246.0.37/32') }
        [pscustomobject]@{ Name = 'Deny-All-Inbound'; Priority = 110; Access = 'Deny'; Direction = 'Inbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('*') }
        [pscustomobject]@{ Name = 'Allow-Azure-DNS-UDP'; Priority = 100; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Udp'; SourcePorts = @('*'); DestinationPorts = @('53'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-DNS-TCP'; Priority = 110; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('53'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-Guest-Agent'; Priority = 120; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('80', '32526'); SourceAddresses = @('*'); DestinationAddresses = @('168.63.129.16') }
        [pscustomobject]@{ Name = 'Allow-Azure-IMDS'; Priority = 130; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('80'); SourceAddresses = @('*'); DestinationAddresses = @('169.254.169.254') }
        [pscustomobject]@{ Name = 'Allow-Container-Registry-HTTPS'; Priority = 190; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('443'); SourceAddresses = @('*'); DestinationAddresses = @('AzureContainerRegistry') }
        [pscustomobject]@{ Name = 'Allow-Azure-HTTPS'; Priority = 200; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('443'); SourceAddresses = @('*'); DestinationAddresses = @('AzureCloud') }
        [pscustomobject]@{ Name = 'Allow-Internet-HTTPS'; Priority = 210; Access = 'Allow'; Direction = 'Outbound'; Protocol = 'Tcp'; SourcePorts = @('*'); DestinationPorts = @('443'); SourceAddresses = @('*'); DestinationAddresses = @('Internet') }
        [pscustomobject]@{ Name = 'Deny-Virtual-Network-Outbound'; Priority = 300; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('VirtualNetwork') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-10'; Priority = 310; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('10.0.0.0/8') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-172'; Priority = 320; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('172.16.0.0/12') }
        [pscustomobject]@{ Name = 'Deny-RFC1918-192'; Priority = 330; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('192.168.0.0/16') }
        [pscustomobject]@{ Name = 'Deny-Shared-Address-Space'; Priority = 340; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('100.64.0.0/10') }
        [pscustomobject]@{ Name = 'Deny-Link-Local'; Priority = 350; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('169.254.0.0/16') }
        [pscustomobject]@{ Name = 'Deny-All-Other-Outbound'; Priority = 4096; Access = 'Deny'; Direction = 'Outbound'; Protocol = '*'; SourcePorts = @('*'); DestinationPorts = @('*'); SourceAddresses = @('*'); DestinationAddresses = @('*') }
    )
}

function Test-NetworkSecurityRuleContract {
    param(
        [Parameter(Mandatory)] [object[]] $Rules,
        [object[]] $ExpectedRules,
        [string] $ContractName = 'Forms6i',
        [switch] $EffectiveProjection
    )

    $expectedRules = if ($null -ne $ExpectedRules -and @($ExpectedRules).Count -gt 0) { @($ExpectedRules) } else { @(Get-ExpectedNetworkSecurityRules) }
    foreach ($expected in $expectedRules) {
        $matches = @($Rules | Where-Object { (([string] $_.name -split '/')[-1]) -ieq $expected.Name })
        if ($matches.Count -ne 1) {
            throw "Network security rule '$($expected.Name)' is missing or duplicated."
        }
        $actual = $matches[0]
        if (-not [string]::Equals([string] $expected.Access, [string] $actual.access, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Network security rule '$($expected.Name)' has an unexpected Access value '$($actual.access)'."
        }
        if (-not [string]::Equals([string] $expected.Direction, [string] $actual.direction, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Network security rule '$($expected.Name)' has an unexpected Direction value '$($actual.direction)'."
        }
        $expectedProtocol = if ($EffectiveProjection -and $expected.Protocol -eq '*') { 'All' } else { $expected.Protocol }
        if (-not [string]::Equals([string] $expectedProtocol, [string] $actual.protocol, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Network security rule '$($expected.Name)' has an unexpected Protocol value '$($actual.protocol)'."
        }
        if ([int] $actual.priority -ne [int] $expected.Priority) {
            throw "Network security rule '$($expected.Name)' has an unexpected Priority value."
        }
        if (-not $EffectiveProjection) {
            Assert-NetworkRuleValuesEqual -RuleName $expected.Name -FieldName 'source ports' -Expected $expected.SourcePorts -Actual (Get-NetworkRuleValues -Rule $actual -PluralName 'sourcePortRanges' -SingularName 'sourcePortRange')
            Assert-NetworkRuleValuesEqual -RuleName $expected.Name -FieldName 'destination ports' -Expected $expected.DestinationPorts -Actual (Get-NetworkRuleValues -Rule $actual -PluralName 'destinationPortRanges' -SingularName 'destinationPortRange')
            Assert-NetworkRuleValuesEqual -RuleName $expected.Name -FieldName 'source addresses' -Expected $expected.SourceAddresses -Actual (Get-NetworkRuleValues -Rule $actual -PluralName 'sourceAddressPrefixes' -SingularName 'sourceAddressPrefix')
            Assert-NetworkRuleValuesEqual -RuleName $expected.Name -FieldName 'destination addresses' -Expected $expected.DestinationAddresses -Actual (Get-NetworkRuleValues -Rule $actual -PluralName 'destinationAddressPrefixes' -SingularName 'destinationAddressPrefix')
        }
    }

    if (-not $EffectiveProjection) {
        $unexpectedRules = @($Rules | Where-Object { (([string] $_.name -split '/')[-1]) -notin $expectedRules.Name })
        if ($unexpectedRules.Count -ne 0) {
            throw "The $ContractName NSG contains an unexpected custom security rule."
        }
    }
}

function Export-ExistingDeploymentOperationsEvidence {
    foreach ($existingDeploymentName in @('forms6i-source-lab-foundation', 'forms6i-source-lab-vm-recovery')) {
        $deploymentSummaryJson = & $script:azureCli deployment group show `
            --subscription $SubscriptionId `
            --resource-group $ResourceGroupName `
            --name $existingDeploymentName `
            --query '{id:id,name:name,provisioningState:properties.provisioningState,timestamp:properties.timestamp,mode:properties.mode}' `
            --output json `
            --only-show-errors 2>$null
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        $operations = Invoke-AzJson -Label "read deployment operations for $existingDeploymentName" -Arguments @(
            'deployment', 'operation', 'group', 'list',
            '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroupName,
            '--name', $existingDeploymentName,
            '--query', '[].{operationId:operationId,provisioningState:properties.provisioningState,timestamp:properties.timestamp,duration:properties.duration,statusCode:properties.statusCode,targetResource:properties.targetResource}'
        )
        $evidence = [ordered]@{
            capturedUtc = [DateTime]::UtcNow.ToString('O')
            source = 'Azure Resource Manager deployment operation records'
            deployment = $deploymentSummaryJson | ConvertFrom-Json -Depth 20
            operations = @($operations)
        }
        $path = Write-JsonEvidence -BaseName "deployment-operations-$existingDeploymentName" -Value $evidence
        Write-Output "Recovered existing ARM deployment-operation evidence: $path"
    }
}

function Assert-AzureContext {
    $account = Invoke-AzJson -Label 'read current account' -Arguments @('account', 'show')
    if (-not [string]::Equals([string] $account.id, $SubscriptionId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Azure CLI is using subscription '$($account.id)', not '$SubscriptionId'."
    }
    if (-not [string]::Equals([string] $account.tenantId, $TenantId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Azure CLI is using tenant '$($account.tenantId)', not '$TenantId'."
    }

    $resourceGroup = Invoke-AzJson -Label 'resolve resource group' -Arguments @(
        'group', 'show', '--subscription', $SubscriptionId, '--name', $ResourceGroupName
    )
    if ([string]::IsNullOrWhiteSpace([string] $resourceGroup.id)) {
        throw "Resource group '$ResourceGroupName' was not found."
    }
}

function Select-DeploymentTemplate {
    $resources = @(Invoke-AzJson -Label 'inventory Forms6i source-lab resources' -Arguments @(
        'resource', 'list', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--query', "[?tags.component=='forms6i-source-lab']"
    ))
    # The lab owns two hosts, so every inventory probe is scoped by role-specific name prefix.
    $virtualMachines = @($resources | Where-Object { $_.type -ieq 'Microsoft.Compute/virtualMachines' -and $_.name -like 'vm-ofm-forms6i-*' })
    $oracleXeVirtualMachines = @($resources | Where-Object { $_.type -ieq 'Microsoft.Compute/virtualMachines' -and $_.name -like 'vm-ofm-oraclexe-*' })
    $oracle9iVirtualMachines = @($resources | Where-Object { $_.type -ieq 'Microsoft.Compute/virtualMachines' -and $_.name -like 'vm-ofm-oracle9i-*' })
    $networkInterfaces = @($resources | Where-Object { $_.type -ieq 'Microsoft.Network/networkInterfaces' -and $_.name -like 'nic-ofm-forms6i-*' })
    $networkSecurityGroups = @($resources | Where-Object { $_.type -ieq 'Microsoft.Network/networkSecurityGroups' -and $_.name -like 'nsg-ofm-forms6i-*' })
    $publicIpAddresses = @($resources | Where-Object { $_.type -ieq 'Microsoft.Network/publicIPAddresses' -and $_.name -like 'pip-ofm-forms6i-*' })
    $virtualNetworks = @($resources | Where-Object { $_.type -ieq 'Microsoft.Network/virtualNetworks' })

    if ($virtualMachines.Count -eq 1) {
        if ($oracleXeVirtualMachines.Count -gt 0) {
            throw 'The retired Oracle Linux XE host is still present; remove it before verifying the lab.'
        }
        if ($oracle9iVirtualMachines.Count -eq 0) {
            # The database host is missing. The full template extends the lab; the what-if guard in
            # Invoke-TemplateValidation is what protects the existing Forms host.
            return 'Full'
        }
        # Both hosts exist, so the full foundation deployment is the authoritative source of outputs.
        return 'VerifyOnly'
    }

    $networkCounts = @(
        $networkInterfaces.Count,
        $networkSecurityGroups.Count,
        $publicIpAddresses.Count,
        $virtualNetworks.Count
    )
    if (($networkCounts | Measure-Object -Sum).Sum -eq 0) {
        return 'Full'
    }
    if (($networkCounts | Where-Object { $_ -ne 1 }).Count -ne 0) {
        throw 'Forms6i source-lab resources are partially present in an unexpected shape; refusing to modify them.'
    }

    $networkInterface = Invoke-AzJson -Label 'read recovery network interface' -Arguments @(
        'network', 'nic', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $networkInterfaces[0].name
    )
    $ipConfiguration = $networkInterface.ipConfigurations[0]
    $subnetResourceId = [string] $ipConfiguration.subnet.id
    $script:templateFile = $vmTemplateFile
    $script:deploymentName = 'forms6i-source-lab-vm-recovery'
    $script:recoveryContext = [ordered]@{
        vmName = ([string] $networkInterface.name -replace '^nic-', 'vm-')
        networkInterfaceResourceId = [string] $networkInterface.id
        virtualNetworkResourceId = $subnetResourceId.Substring(0, $subnetResourceId.LastIndexOf('/subnets/', [StringComparison]::OrdinalIgnoreCase))
        subnetResourceId = $subnetResourceId
        networkSecurityGroupResourceId = [string] $networkInterface.networkSecurityGroup.id
        publicIpResourceId = [string] $ipConfiguration.publicIpAddress.id
        tags = [ordered]@{
            workload = 'oracle-forms-migration-fleet'
            component = 'forms6i-source-lab'
            environment = 'dev'
            'managed-by' = 'bicep'
            isolation = 'deny-inbound-private-egress'
        }
    }
    return 'VmRecovery'
}

function Invoke-TemplateValidation {
    & $script:azureCli bicep build --file $templateFile --stdout --only-show-errors *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Bicep compilation failed.'
    }

    [void](Invoke-AzJson -Label 'validate deployment' -Arguments @(
        'deployment', 'group', 'validate',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', "$deploymentName-validation",
        '--template-file', $templateFile,
        '--parameters', "@$temporaryParameters"
    ))

    $whatIf = Invoke-AzJson -Label 'preview deployment' -Arguments @(
        'deployment', 'group', 'what-if',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', "$deploymentName-preview",
        '--template-file', $templateFile,
        '--parameters', "@$temporaryParameters",
        '--result-format', 'FullResourcePayloads',
        '--no-pretty-print'
    )
    $changes = @($whatIf.changes)
    $whatIfEvidence = [ordered]@{
        capturedUtc = [DateTime]::UtcNow.ToString('O')
        deploymentName = $deploymentName
        templateFile = [System.IO.Path]::GetFileName($templateFile)
        result = $whatIf
    }
    $whatIfPath = Write-JsonEvidence -BaseName "what-if-$deploymentName" -Value $whatIfEvidence
    # Extending the lab edits the shared VNet and NSG in place, so Modify is expected. Deletion and
    # replacement stay refused so an extension can never destroy the existing Forms host.
    $unsafeChanges = @($changes | Where-Object { $_.changeType -notin @('Create', 'NoChange', 'Ignore', 'Modify') })
    if ($unsafeChanges.Count -gt 0) {
        $changeSummary = ($unsafeChanges | ForEach-Object { "[$($_.changeType)] $($_.resourceId)" }) -join '; '
        throw "What-if includes a destructive change: $changeSummary"
    }

    Write-Output "Validation passed with $($changes.Count) create/no-change item(s). Evidence: $whatIfPath"
    foreach ($change in $changes) {
        Write-Output "[$($change.changeType)] $($change.resourceId)"
    }
}

function Get-DeploymentOutputs {
    $deployment = Invoke-AzJson -Label 'read deployment outputs' -Arguments @(
        'deployment', 'group', 'show',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', $deploymentName,
        '--query', 'properties.outputs'
    )
    return $deployment
}

function Test-OracleXeHost {
    param([Parameter(Mandatory)] [object] $Outputs)

    $databaseVmName = [string] $Outputs.oracle9iVmName.value
    if ([string]::IsNullOrWhiteSpace($databaseVmName)) {
        throw 'Deployment outputs do not contain the Oracle database host name.'
    }
    $expectedPrivateIp = [string] $Outputs.oracle9iPrivateIpAddress.value
    if ($expectedPrivateIp -ne '10.246.0.37') {
        throw 'The Oracle database private IP no longer matches the exact SQL*Net rule destination.'
    }

    $databaseVm = Invoke-AzJson -Label 'read Oracle database VM configuration' -Arguments @(
        'vm', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $databaseVmName
    )
    if ($null -ne $databaseVm.identity) {
        throw 'The Oracle database host unexpectedly has a managed identity.'
    }
    if (-not [string]::Equals([string] $databaseVm.id, [string] $Outputs.oracle9iVmResourceId.value, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Deployment outputs do not identify the Oracle database host that was read from Azure.'
    }

    $databaseNicName = (([string] $Outputs.oracle9iNetworkInterfaceResourceId.value -split '/')[-1])
    $databaseNic = Invoke-AzJson -Label 'read Oracle database network interface' -Arguments @(
        'network', 'nic', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $databaseNicName
    )
    $databaseIpConfigurations = @($databaseNic.ipConfigurations)
    if ($databaseIpConfigurations.Count -ne 1) {
        throw 'The Oracle database network interface must expose exactly one IP configuration.'
    }
    $databaseIpConfiguration = $databaseIpConfigurations[0]
    if ([string] $databaseIpConfiguration.privateIPAddress -ne $expectedPrivateIp -or
        [string] $databaseIpConfiguration.privateIPAllocationMethod -ne 'Static') {
        throw 'The Oracle database host must hold its pinned static private IP.'
    }
    if (-not [string]::Equals([string] $databaseIpConfiguration.subnet.id, [string] $Outputs.oracleXeSubnetResourceId.value, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string] $databaseNic.networkSecurityGroup.id, [string] $Outputs.oracleXeNetworkSecurityGroupResourceId.value, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Oracle database network interface is not bound to the deployment-owned subnet and NSG.'
    }

    $databaseNsgName = (([string] $Outputs.oracleXeNetworkSecurityGroupResourceId.value -split '/')[-1])
    $databaseNsg = Invoke-AzJson -Label 'read Oracle database NSG rules' -Arguments @(
        'network', 'nsg', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $databaseNsgName
    )
    Test-NetworkSecurityRuleContract -Rules @($databaseNsg.securityRules) `
        -ExpectedRules @(Get-ExpectedOracleXeNetworkSecurityRules) -ContractName 'Oracle database'
}

function Test-DeployedFoundation {
    param([switch] $ControlPlaneOnly)

    $outputs = Get-DeploymentOutputs
    $vmName = [string] $outputs.vmName.value
    if ([string]::IsNullOrWhiteSpace($vmName)) {
        throw 'Deployment outputs do not contain the VM name.'
    }

    $vm = Invoke-AzJson -Label 'read VM configuration' -Arguments @(
        'vm', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $vmName, '--show-details'
    )
    if ($null -ne $vm.identity) {
        throw 'The Forms6i VM unexpectedly has a managed identity.'
    }

    $expectedVmResourceId = [string] $outputs.vmResourceId.value
    $expectedNicResourceId = [string] $outputs.networkInterfaceResourceId.value
    $expectedSubnetResourceId = [string] $outputs.subnetResourceId.value
    $expectedNsgResourceId = [string] $outputs.networkSecurityGroupResourceId.value
    $expectedPublicIpResourceId = [string] $outputs.publicIpResourceId.value
    if (-not [string]::Equals([string] $vm.id, $expectedVmResourceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Deployment outputs do not identify the VM that was read from Azure.'
    }
    $vmNetworkInterfaces = @($vm.networkProfile.networkInterfaces)
    if ($vmNetworkInterfaces.Count -ne 1 -or
        -not [string]::Equals([string] $vmNetworkInterfaces[0].id, $expectedNicResourceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Forms6i VM must have exactly the deployment-owned network interface attached.'
    }

    $resourceGroupNics = @(Invoke-AzJson -Label 'read resource-group network interfaces' -Arguments @(
        'network', 'nic', 'list', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName
    ))
    $attachedNics = @($resourceGroupNics | Where-Object { $_.virtualMachine.id -ieq $expectedVmResourceId })
    if ($attachedNics.Count -ne 1 -or
        -not [string]::Equals([string] $attachedNics[0].id, $expectedNicResourceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Azure reports an unexpected network-interface attachment for the Forms6i VM.'
    }

    $networkInterfaceName = ($expectedNicResourceId -split '/')[-1]
    $networkInterface = Invoke-AzJson -Label 'read attached network interface' -Arguments @(
        'network', 'nic', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $networkInterfaceName
    )
    $ipConfigurations = @($networkInterface.ipConfigurations)
    if (-not [string]::Equals([string] $networkInterface.virtualMachine.id, $expectedVmResourceId, [StringComparison]::OrdinalIgnoreCase) -or
        $ipConfigurations.Count -ne 1) {
        throw 'The deployment-owned network interface is not bound exclusively to the Forms6i VM and one IP configuration.'
    }
    $ipConfiguration = $ipConfigurations[0]
    if ([string] $ipConfiguration.privateIPAddress -ne '10.246.0.4') {
        throw 'The Forms6i private IP no longer matches the exact Bastion Developer RDP destination.'
    }
    if (-not [string]::Equals([string] $ipConfiguration.subnet.id, $expectedSubnetResourceId, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string] $ipConfiguration.publicIpAddress.id, $expectedPublicIpResourceId, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string] $networkInterface.networkSecurityGroup.id, $expectedNsgResourceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Forms6i VM network interface is not bound to the deployment-owned subnet, NSG, and public IP.'
    }

    $virtualNetworkName = (([string] $outputs.virtualNetworkResourceId.value -split '/')[-1])
    $virtualNetwork = Invoke-AzJson -Label 'read virtual network and subnet associations' -Arguments @(
        'network', 'vnet', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $virtualNetworkName
    )
    $subnets = @($virtualNetwork.subnets | Where-Object { $_.id -ieq $expectedSubnetResourceId })
    if ($subnets.Count -ne 1 -or
        -not [string]::Equals([string] $subnets[0].networkSecurityGroup.id, $expectedNsgResourceId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The deployment-owned subnet is missing or is not bound to the deployment-owned NSG.'
    }

    $publicIpName = ($expectedPublicIpResourceId -split '/')[-1]
    $publicIp = Invoke-AzJson -Label 'read attached public IP' -Arguments @(
        'network', 'public-ip', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $publicIpName
    )
    if (-not [string]::Equals([string] $publicIp.ipConfiguration.id, [string] $ipConfiguration.id, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The deployment-owned public IP is not attached to the Forms6i VM network interface.'
    }

    Test-OracleXeHost -Outputs $outputs

    $instanceView = Invoke-AzJson -Label 'read VM Agent state' -Arguments @(
        'vm', 'get-instance-view', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $vmName
    )
    $vmAgentStatus = @($instanceView.instanceView.vmAgent.statuses)[0]
    if ([string] $vmAgentStatus.code -notmatch 'ProvisioningState/succeeded') {
        throw 'The Azure VM Agent did not report a successful provisioning state.'
    }
    $powerState = @($instanceView.instanceView.statuses | Where-Object { $_.code -like 'PowerState/*' })[0]

    $egressMatch = $null
    if (-not $ControlPlaneOnly) {
        $runCommand = Invoke-AzJson -Label 'run harmless VM Agent readiness command' -Arguments @(
            'vm', 'run-command', 'invoke', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
            '--name', $vmName, '--command-id', 'RunPowerShellScript',
            '--scripts', "`$egress = (Invoke-RestMethod -Uri https://api.ipify.org -UseBasicParsing).Trim(); Write-Output 'FORMS6I_SOURCE_LAB_VM_AGENT_READY'; Write-Output `"FORMS6I_EGRESS_IP=`$egress`"; Get-Service RdAgent,WindowsAzureGuestAgent | Select-Object Name,Status"
        )
        $runCommandText = [string] ($runCommand.value.message -join "`n")
        if ($runCommandText -notmatch 'FORMS6I_SOURCE_LAB_VM_AGENT_READY') {
            throw 'The VM Agent readiness command did not return its expected marker.'
        }
        $egressMatch = [regex]::Match($runCommandText, 'FORMS6I_EGRESS_IP=([0-9.]+)')
        if (-not $egressMatch.Success -or $egressMatch.Groups[1].Value -ne [string] $outputs.publicIpAddress.value) {
            throw 'Observed HTTPS egress does not match the attached Standard public IP.'
        }
    }

    $nsg = Invoke-AzJson -Label 'read NSG rules' -Arguments @(
        'network', 'nsg', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', (([string] $outputs.networkSecurityGroupResourceId.value -split '/')[-1])
    )
    Test-NetworkSecurityRuleContract -Rules @($nsg.securityRules)

    $effectiveNsg = Invoke-AzJson -Label 'read effective NSG rules' -Arguments @(
        'network', 'nic', 'list-effective-nsg', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName, '--name', $networkInterfaceName
    )
    $effectiveRules = @($effectiveNsg.value.effectiveSecurityRules)
    Test-NetworkSecurityRuleContract -Rules $effectiveRules -EffectiveProjection

    $peerings = @(Invoke-AzJson -Label 'read virtual-network peerings' -Arguments @(
        'network', 'vnet', 'peering', 'list', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName, '--vnet-name', $virtualNetworkName
    ))
    if ($peerings.Count -ne 0) {
        throw 'The Forms6i virtual network unexpectedly has a peering.'
    }

    $evidence = [ordered]@{
        verifiedUtc = [DateTime]::UtcNow.ToString('O')
        subscriptionId = $SubscriptionId
        tenantId = $TenantId
        resourceGroup = $ResourceGroupName
        location = $vm.location
        vmName = $vmName
        vmResourceId = $outputs.vmResourceId.value
        vmSize = $outputs.vmSize.value
        imageUrn = $outputs.imageUrn.value
        provisioningState = $vm.provisioningState
        powerState = $powerState.displayStatus
        vmAgentStatus = $vmAgentStatus.displayStatus
        verificationMode = $(if ($ControlPlaneOnly) { 'ControlPlaneOnly' } else { 'ControlPlaneAndGuestReadiness' })
        managedIdentity = $null
        attachedNetworkInterfaceCount = $attachedNics.Count
        configuredNetworkSecurityRuleCount = @($nsg.securityRules).Count
        effectiveExpectedNetworkSecurityRuleCount = @(Get-ExpectedNetworkSecurityRules).Count
        virtualNetworkPeeringCount = $peerings.Count
        virtualNetworkResourceId = $outputs.virtualNetworkResourceId.value
        subnetResourceId = $outputs.subnetResourceId.value
        networkSecurityGroupResourceId = $outputs.networkSecurityGroupResourceId.value
        networkInterfaceResourceId = $outputs.networkInterfaceResourceId.value
        publicIpResourceId = $outputs.publicIpResourceId.value
        publicIpAddress = $outputs.publicIpAddress.value
        credentialPath = $credentialPath
    }
    if (-not $ControlPlaneOnly) {
        $evidence.runCommandReady = $true
        $evidence.observedEgressIp = $egressMatch.Groups[1].Value
    }
    $verificationPath = Write-JsonEvidence -BaseName 'verification' -Value $evidence
    $evidence.evidencePath = $verificationPath
    $evidence
}

try {
    $script:azureCli = Resolve-AzureCli
    Assert-AzureContext
    $deploymentMode = Select-DeploymentTemplate
    Export-ExistingDeploymentOperationsEvidence

    if ($Operation -eq 'Verify') {
        if ($deploymentMode -ne 'VerifyOnly') {
            throw 'The Forms6i VM is not present to verify.'
        }
        Test-DeployedFoundation
        return
    }

    if ($deploymentMode -eq 'VerifyOnly' -and $Operation -ne 'Deploy') {
        Test-DeployedFoundation -ControlPlaneOnly
        Write-Output 'Preview only. Existing foundation was inspected through read-only Azure control-plane calls.'
        return
    }

    if ($Operation -eq 'Deploy' -or (Test-Path -LiteralPath $credentialPath)) {
        # Preview must reuse the stored credential; a fresh password would surface a spurious VM diff.
        $plainPassword = Get-DeploymentPassword
    }
    else {
        $plainPassword = New-CryptographicPassword
    }
    Write-ParameterFile -Password $plainPassword
    Invoke-TemplateValidation

    if ($Operation -eq 'Preview') {
        Write-Output 'Preview only. No Azure resource was changed.'
        return
    }

    $deployment = Invoke-AzJson -Label 'deploy Forms6i source lab' -Arguments @(
        'deployment', 'group', 'create',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', $deploymentName,
        '--template-file', $templateFile,
        '--parameters', "@$temporaryParameters"
    )
    [void](New-Item -ItemType Directory -Path $evidenceRoot -Force)
    $deployment.properties.outputs | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'deployment-outputs.json') -Encoding utf8
    Test-DeployedFoundation
}
catch {
    $script:operationFailed = $true
    throw
}
finally {
    $plainPassword = $null
    $securePassword = $null
    if ($null -ne $temporaryStateRoot -and (Test-Path -LiteralPath $temporaryStateRoot)) {
        try {
            Remove-Item -LiteralPath $temporaryStateRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupMessage = 'Failed to securely remove temporary deployment parameter state.'
            if ($operationFailed) {
                Write-Warning $cleanupMessage
            }
            else {
                throw $cleanupMessage
            }
        }
    }
    [GC]::Collect()
}