#requires -Version 7.2

[CmdletBinding()]
param([switch] $ValidateOnly)

$ErrorActionPreference = 'Stop'
$subscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255'
$tenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
$resourceGroup = 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
$vaultName = 'kv-ofm-forms6i-j6mrrerz'
$secretName = 'forms6i-vm-admin-password'
$template = @{
    '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
    contentVersion = '1.0.0.0'
    parameters = @{ adminPassword = @{ type = 'secureString' }; expires = @{ type = 'int' } }
    resources = @(@{
        type = 'Microsoft.KeyVault/vaults/secrets'
        apiVersion = '2023-07-01'
        name = "$vaultName/$secretName"
        properties = @{
            value = "[parameters('adminPassword')]"
            contentType = 'text/plain'
            attributes = @{ enabled = $true; exp = "[parameters('expires')]" }
        }
        tags = @{ purpose = 'forms6i-bastion'; vm = 'vm-ofm-forms6i-j6mrrerz' }
    })
}
$stateRoot = Join-Path $env:LOCALAPPDATA 'OracleFormsMigrationFleet\forms6i-source-lab'
$credentialPath = Join-Path $stateRoot 'admin-credential.dpapi.json'
$credential = Get-Content -LiteralPath $credentialPath -Raw | ConvertFrom-Json
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
if ($credential.schemaVersion -ne 1 -or $credential.subscriptionId -ne $subscriptionId -or
    $credential.tenantId -ne $tenantId -or $credential.resourceGroup -ne $resourceGroup -or
    $credential.username -cne 'ofmlabadmin' -or $credential.protectedForSid -ne $identity.Value) {
    throw 'Saved credential does not match the authorized source lab and current Windows user.'
}
$securePassword = ConvertTo-SecureString -String $credential.encryptedPassword
if ($securePassword.Length -lt 12) { throw 'Saved credential is invalid.' }
if ($ValidateOnly) {
    $securePassword.Dispose()
    $roundTrip = $template | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    if ($roundTrip.parameters.adminPassword.type -ne 'secureString' -or
        @($roundTrip.resources).Count -ne 1 -or $roundTrip.resources[0].type -ne 'Microsoft.KeyVault/vaults/secrets' -or
        $roundTrip.resources[0].properties.value -ne "[parameters('adminPassword')]") {
        throw 'ARM template does not satisfy the secret-only secure-parameter contract.'
    }
    Write-Output 'PASS retained credential identity and DPAPI decryption; no secret was displayed or uploaded.'
    Write-Output 'PASS secret-only ARM template with secureString parameter and no outputs.'
    return
}

$az = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
$account = & $az account show --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $account.id -ne $subscriptionId -or $account.tenantId -ne $tenantId) {
    throw 'Azure CLI account does not match the authorized lab.'
}
$vm = & $az vm show --subscription $subscriptionId --resource-group $resourceGroup --name vm-ofm-forms6i-j6mrrerz --query '{username:osProfile.adminUsername,id:id}' --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $vm.username -cne $credential.username) { throw 'VM administrator identity does not match the retained credential.' }
$vault = & $az keyvault show --subscription $subscriptionId --resource-group $resourceGroup --name $vaultName --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $vault.properties.tenantId -ne $tenantId -or
    -not $vault.properties.enableRbacAuthorization -or $vault.properties.networkAcls.defaultAction -ne 'Deny' -or
    $vault.properties.publicNetworkAccess -ne 'Disabled' -or $vault.properties.networkAcls.bypass -ne 'None') {
    throw 'Dedicated vault identity or security configuration does not match the lab contract.'
}

$tempRoot = Join-Path $stateRoot ('secret-transfer-' + [Guid]::NewGuid().ToString('N'))
$tempFile = Join-Path $tempRoot 'secure-parameters.json'
$templateFile = Join-Path $tempRoot 'secret-template.json'
$deploymentName = 'forms6i-bastion-credential-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
try {
    [void](New-Item -ItemType Directory -Path $tempRoot)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($identity)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $tempRoot -AclObject $acl
    $observedAcl = Get-Acl -LiteralPath $tempRoot
    $rules = @($observedAcl.Access)
    if (-not $observedAcl.AreAccessRulesProtected -or $rules.Count -ne 1 -or
        $rules[0].IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne $identity.Value) {
        throw 'Temporary credential directory ACL is not restricted to the current Windows user.'
    }
    $parameters = @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            adminPassword = @{ value = [Net.NetworkCredential]::new('', $securePassword).Password }
            expires = @{ value = [DateTimeOffset]::UtcNow.AddDays(7).ToUnixTimeSeconds() }
        }
    }
    [IO.File]::WriteAllText($tempFile, ($parameters | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $parameters = $null
    [IO.File]::WriteAllText($templateFile, ($template | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    $null = & $az deployment group create --subscription $subscriptionId --resource-group $resourceGroup --name $deploymentName --mode Incremental --template-file $templateFile --parameters "@$tempFile" --output none --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'ARM secret provisioning failed; output suppressed to protect credential material.' }
}
finally {
    $securePassword.Dispose()
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop }
}
$secretResourceId = "$($vault.id)/secrets/$secretName"
$metadata = & $az resource show --subscription $subscriptionId --ids $secretResourceId --api-version 2023-07-01 --query '{id:id,secretUri:properties.secretUriWithVersion,enabled:properties.attributes.enabled,expires:properties.attributes.exp}' --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $metadata.id -ine $secretResourceId -or -not $metadata.enabled -or
    [string]::IsNullOrWhiteSpace($metadata.secretUri)) { throw 'Control-plane secret metadata verification failed.' }
[pscustomobject]@{ username = $credential.username; secretId = $metadata.secretUri; expires = $metadata.expires; deploymentName = $deploymentName; plaintextTemporaryFileRemoved = -not (Test-Path -LiteralPath $tempFile); status = 'StoredNotYetReadableFromBastion' }