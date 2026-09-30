[CmdletBinding()]
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

    [string] $SandboxDatabaseName = 'ofm_dotnet_pilot'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255'
$forbiddenDefaultSubscriptionId = '0832b3b6-22b3-4c47-8d8b-572054b97257'
$expectedTenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$template = if ($Stage -eq 'Foundation') {
    Join-Path $PSScriptRoot 'main.bicep'
} else {
    Join-Path $PSScriptRoot 'application.bicep'
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

$bicep = Join-Path $HOME '.azure\bin\bicep.exe'
if (-not (Test-Path -LiteralPath $bicep -PathType Leaf)) {
    throw "The Azure CLI Bicep compiler was not found at $bicep. Run 'az bicep install' outside this validation script first."
}
$compiledTemplate = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-$([guid]::NewGuid().ToString('N')).json"
try {
    & $bicep build $template --outfile $compiledTemplate
    if ($LASTEXITCODE -ne 0) {
        throw "Bicep compilation failed for $template."
    }
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
    '--no-pretty-print',
    '--only-show-errors'
)

if ($Stage -eq 'Foundation') {
    & az @commonArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Foundation what-if failed. Nothing was applied.'
    }
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
    & icacls.exe $parameterPath /inheritance:r /grant:r "${env:USERNAME}:(R,W)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not restrict the temporary secure parameter file.'
    }

    & az @commonArguments --parameters "@$parameterPath"
    if ($LASTEXITCODE -ne 0) {
        throw 'Application what-if failed. Nothing was applied.'
    }
}
finally {
    Remove-Item -LiteralPath $parameterPath -Force -ErrorAction SilentlyContinue
}

Write-Output 'What-if completed without apply. Review the diff for creates only, the single approved NSG rule addition, and no PostgreSQL server modification.'
Write-Output "Repository root: $repositoryRoot"