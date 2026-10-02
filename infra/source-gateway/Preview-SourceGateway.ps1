[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Foundation', 'Application')]
    [string] $Stage,

    [string] $SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',
    [string] $TenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86',
    [string] $ResourceGroupName = 'rg-oracle-forms-migration-fleet-dev-b9f0e875',

    [string] $ManagedEnvironmentDefaultDomain = '',

    [string] $ManagedEnvironmentInboundStaticIp = '',

    [string] $PrivateEndpointSubnetId = '',

    [string] $ContainerImage = '',

    [guid] $SourceGatewayApplicationClientId = [guid]::Empty,

    [string] $FoundryAgentEndpoint = '',

    [guid[]] $OperatorPrincipalObjectIds = @(),

    [guid[]] $ValidationPrincipalObjectIds = @(),

    [guid[]] $ValidationClientApplicationIds = @(),

    [string] $PlatformDatabaseHost = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com',

    [string] $PlatformDatabaseName = 'ofm_platform',

    [string] $SandboxDatabaseHost = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com',

    [string] $SandboxDatabaseName = 'ofm_dotnet_pilot',

    [string] $EvidencePath = '',

    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-BicepExecutable {
    $azureConfigDirectory = if ([string]::IsNullOrWhiteSpace($env:AZURE_CONFIG_DIR)) {
        Join-Path $HOME '.azure'
    } else {
        $env:AZURE_CONFIG_DIR
    }
    $executable = Join-Path $azureConfigDirectory 'bin\bicep.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The Azure CLI Bicep compiler was not found at $executable. The release workflow must install the pinned compiler first."
    }
    return $executable
}

function Invoke-BicepCompilation {
    param(
        [Parameter(Mandatory)] [string] $BicepExecutable,
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $OutputPath
    )

    & $BicepExecutable build $TemplatePath --outfile $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Bicep compilation failed for $TemplatePath with exit code $LASTEXITCODE."
    }
}

$expectedSubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255'
$forbiddenDefaultSubscriptionId = '0832b3b6-22b3-4c47-8d8b-572054b97257'
$expectedTenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$template = if ($Stage -eq 'Foundation') {
    Join-Path $PSScriptRoot 'main.bicep'
} else {
    Join-Path $PSScriptRoot 'application.bicep'
}

if ($Apply -and $Stage -ne 'Application') {
    throw '-Apply is supported only for the separately reviewed Application stage.'
}

if ($SubscriptionId -ne $expectedSubscriptionId -or $SubscriptionId -eq $forbiddenDefaultSubscriptionId) {
    throw "This preview is pinned to lab subscription $expectedSubscriptionId. The corporate default is never used or changed."
}
if ($TenantId -ne $expectedTenantId) {
    throw "This preview is pinned to tenant $expectedTenantId."
}
if (-not (Test-Path -LiteralPath $template -PathType Leaf)) {
    throw "Template not found: $template"
}

$account = az account show --subscription $SubscriptionId --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $account.id -ne $expectedSubscriptionId -or $account.tenantId -ne $expectedTenantId) {
    throw 'Azure CLI authentication does not match the pinned lab subscription and tenant.'
}

$bicep = Resolve-BicepExecutable
$compiledTemplate = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-$([guid]::NewGuid().ToString('N')).json"
try {
    Invoke-BicepCompilation -BicepExecutable $bicep -TemplatePath $template -OutputPath $compiledTemplate
}
finally {
    Remove-Item -LiteralPath $compiledTemplate -Force -ErrorAction SilentlyContinue
}

$commonArguments = @(
    'deployment', 'group', 'what-if',
    '--name', "ofm-source-gateway-$($Stage.ToLowerInvariant())-preview",
    '--resource-group', $ResourceGroupName,
    '--subscription', $SubscriptionId,
    '--template-file', $template,
    '--result-format', 'FullResourcePayloads',
    '--output', 'json',
    '--no-pretty-print',
    '--only-show-errors'
)

if ($Stage -eq 'Foundation') {
    $foundationWhatIf = & az @commonArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Foundation what-if failed. Nothing was applied.'
    }
    $foundationWhatIf
    return
}

if ($SourceGatewayApplicationClientId -eq [guid]::Empty -or $OperatorPrincipalObjectIds.Count -eq 0) {
    throw 'Application preview requires SourceGatewayApplicationClientId and at least one OperatorPrincipalObjectId.'
}
if ($ContainerImage -cnotmatch '^acrofmfleedevykbpnrpd\.azurecr\.io/migration-fleet-workbench-source-gateway@sha256:[0-9a-f]{64}$') {
    throw 'ContainerImage must be the trusted source-gateway trust-overlay repository pinned by an immutable lowercase SHA-256 digest.'
}
if ($ManagedEnvironmentDefaultDomain -notmatch '^[a-z0-9.-]+\.azurecontainerapps\.io$') {
    throw 'ManagedEnvironmentDefaultDomain must be the exact defaultDomain output of the internal ACA environment.'
}
$expectedPrivateEndpointSubnetId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/virtualNetworks/vnet-ofm-forms6i-j6mrrerz/subnets/snet-ofmfleet-private-endpoints"
if ($PrivateEndpointSubnetId -cne $expectedPrivateEndpointSubnetId) {
    throw 'PrivateEndpointSubnetId must be the exact privateEndpointSubnetId output of the approved foundation deployment.'
}
$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($ManagedEnvironmentInboundStaticIp, [ref] $parsedIp) -or
    $parsedIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'ManagedEnvironmentInboundStaticIp must be the exact IPv4 staticIp output of the internal ACA environment.'
}
if ($PlatformDatabaseHost -eq $SandboxDatabaseHost -and $PlatformDatabaseName -eq $SandboxDatabaseName) {
    throw 'Platform authorization state and migration sandbox must use different databases.'
}
$foundryUri = $null
if (-not [Uri]::TryCreate($FoundryAgentEndpoint, [UriKind]::Absolute, [ref] $foundryUri) -or
    $foundryUri.Scheme -ne 'https') {
    throw 'FoundryAgentEndpoint must be an absolute HTTPS endpoint.'
}
if ([string]::IsNullOrWhiteSpace($env:OFM_WORKBENCH_AUTH_CLIENT_SECRET)) {
    throw 'Set OFM_WORKBENCH_AUTH_CLIENT_SECRET only in the trusted release process. Its value is never printed or passed as a command-line argument.'
}

$parameterPath = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-$([guid]::NewGuid().ToString('N')).parameters.json"
try {
    $parameters = [ordered]@{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = [ordered]@{
            managedEnvironmentDefaultDomain = @{ value = $ManagedEnvironmentDefaultDomain }
            managedEnvironmentInboundStaticIp = @{ value = $ManagedEnvironmentInboundStaticIp }
            privateEndpointSubnetId = @{ value = $PrivateEndpointSubnetId }
            containerImage = @{ value = $ContainerImage }
            workbenchAuthClientSecret = @{ value = $env:OFM_WORKBENCH_AUTH_CLIENT_SECRET }
            operatorPrincipalObjectIds = @{ value = @($OperatorPrincipalObjectIds.Guid) }
            validationPrincipalObjectIds = @{ value = @($ValidationPrincipalObjectIds.Guid) }
            validationClientApplicationIds = @{ value = @($ValidationClientApplicationIds.Guid) }
            sourceGatewayApplicationClientId = @{ value = $SourceGatewayApplicationClientId.Guid }
            foundryAgentEndpoint = @{ value = $FoundryAgentEndpoint }
            platformDatabaseHost = @{ value = $PlatformDatabaseHost }
            platformDatabaseName = @{ value = $PlatformDatabaseName }
            sandboxDatabaseHost = @{ value = $SandboxDatabaseHost }
            sandboxDatabaseName = @{ value = $SandboxDatabaseName }
        }
    }
    $parameters | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $parameterPath -Encoding utf8NoBOM
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $parameterPath /setowner "*$currentSid" /inheritance:r /grant:r "*$currentSid`:(R,W)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not restrict the temporary secure parameter file.'
    }

    $resourceChanges = @(& az @commonArguments `
        --query 'properties.changes[].{resourceId:resourceId,changeType:changeType,resourceType:after.type}' `
        --parameters "@$parameterPath" | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) {
        throw 'Application what-if failed. Nothing was applied.'
    }

    $expectedResourceIds = @(
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.App/containerApps/ca-ofmfleet-private-dev"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.App/containerApps/ca-ofmfleet-private-dev/authConfigs/current"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/networkSecurityGroups/nsg-ofm-forms6i-j6mrrerz/securityRules/Allow-Private-Workbench-Source-Gateway"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/$ManagedEnvironmentDefaultDomain"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/$ManagedEnvironmentDefaultDomain/A/*"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/$ManagedEnvironmentDefaultDomain/virtualNetworkLinks/link-vnet-ofm-forms6i-j6mrrerz"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/ofm.source.internal"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/ofm.source.internal/A/gateway"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/ofm.source.internal/virtualNetworkLinks/link-vnet-ofm-forms6i-j6mrrerz"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/privatelink.postgres.database.azure.com"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateDnsZones/privatelink.postgres.database.azure.com/virtualNetworkLinks/link-vnet-ofm-forms6i-j6mrrerz"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateEndpoints/pe-ofmfleet-postgres-dev"
        "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Network/privateEndpoints/pe-ofmfleet-postgres-dev/privateDnsZoneGroups/default"
    ) | Sort-Object
    $actualResourceIds = @($resourceChanges | ForEach-Object { $_.resourceId }) | Sort-Object
    $unexpectedChange = $resourceChanges | Where-Object { $_.changeType -cne 'Create' } | Select-Object -First 1
    $resourceIdDifferences = @(Compare-Object -CaseSensitive $expectedResourceIds $actualResourceIds)
    if ($null -ne $unexpectedChange -or
        $resourceIdDifferences.Count -ne 0) {
        $counts = $resourceChanges | Group-Object changeType | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }
        throw "Application what-if exceeded the approved create-only scope. Change counts: $($counts -join ', '). Nothing was applied."
    }

    $publicBindings = [ordered]@{
        managedEnvironmentDefaultDomain = $ManagedEnvironmentDefaultDomain
        managedEnvironmentInboundStaticIp = $ManagedEnvironmentInboundStaticIp
        privateEndpointSubnetId = $PrivateEndpointSubnetId
        containerImage = $ContainerImage
        operatorPrincipalObjectIds = @($OperatorPrincipalObjectIds.Guid | Sort-Object)
        validationPrincipalObjectIds = @($ValidationPrincipalObjectIds.Guid | Sort-Object)
        validationClientApplicationIds = @($ValidationClientApplicationIds.Guid | Sort-Object)
        sourceGatewayApplicationClientId = $SourceGatewayApplicationClientId.Guid
        foundryAgentEndpoint = $FoundryAgentEndpoint
        platformDatabaseHost = $PlatformDatabaseHost
        platformDatabaseName = $PlatformDatabaseName
        sandboxDatabaseHost = $SandboxDatabaseHost
        sandboxDatabaseName = $SandboxDatabaseName
        secureParameterSource = 'ca-ofmfleet-dev-ykbpnrpd/microsoft-provider-authentication-secret'
    }
    $bindingJson = $publicBindings | ConvertTo-Json -Depth 8 -Compress
    $bindingSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($bindingJson))).ToLowerInvariant()
    $templateSha256 = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash.ToLowerInvariant()
    $changes = @($resourceChanges | Sort-Object resourceId | ForEach-Object {
        [ordered]@{
            resourceId = $_.resourceId
            changeType = $_.changeType
            resourceType = $_.resourceType
        }
    })
    $evidence = [ordered]@{
        schemaVersion = 1
        stage = 'Application'
        subscriptionId = $SubscriptionId
        tenantId = $TenantId
        resourceGroup = $ResourceGroupName
        templateSha256 = $templateSha256
        publicBindingSha256 = $bindingSha256
        publicBindings = $publicBindings
        changeCounts = [ordered]@{ Create = $resourceChanges.Count }
        changes = $changes
        whatIfSucceeded = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $evidenceDirectory = Split-Path $EvidencePath -Parent
        if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
            New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
        }
        $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
    }
    $evidence | ConvertTo-Json -Depth 10

    if ($Apply) {
        if (-not $PSCmdlet.ShouldProcess(
                "$SubscriptionId/$ResourceGroupName",
                'Create the approved private source-gateway workbench resources after the successful full what-if')) {
            throw 'Application apply was not confirmed. Nothing was applied.'
        }
        $applyArguments = @(
            'deployment', 'group', 'create',
            '--name', 'ofm-source-gateway-application',
            '--resource-group', $ResourceGroupName,
            '--subscription', $SubscriptionId,
            '--template-file', $template,
            '--parameters', "@$parameterPath",
            '--output', 'none',
            '--only-show-errors'
        )
        & az @applyArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'Application deployment failed after the successful what-if.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $parameterPath -PathType Leaf) {
        Remove-Item -LiteralPath $parameterPath -Force -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $parameterPath) {
        throw 'The temporary secure parameter file could not be removed.'
    }
}

if ($Apply) {
    Write-Output 'Application deployment completed after the successful create-only what-if.'
} else {
    Write-Output 'What-if completed without apply. The exact create-only resource allowlist was verified.'
}
Write-Output "Repository root: $repositoryRoot"