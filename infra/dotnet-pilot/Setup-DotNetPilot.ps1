#requires -Version 7.2

<#
.SYNOPSIS
    Previews or applies the generated .NET pilot platform foundation.

.DESCRIPTION
    The default mode compiles Bicep and runs ARM what-if only. ApplyFoundation creates the additive
    identity, Azure Files share, Container Apps environment storage link, and ACR pull assignment,
    then creates the dedicated PostgreSQL database and its managed-identity principal.

    This script never builds an image and always passes deployTarget=false. The product must later
    authorize a verified digest and deploy it through its own controlled deployment gateway.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
    [string] $SubscriptionId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._()\-]{1,90}$')]
    [string] $ResourceGroupName,

    [ValidatePattern('^[A-Za-z0-9._@\-]{1,128}$')]
    [string] $PostgresEntraAdministratorName = '',

    [switch] $ApplyFoundation
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'
$compiledParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "ofm-dotnet-pilot-$([Guid]::NewGuid().ToString('N')).parameters.json"
$deploymentName = 'ofm-dotnet-pilot-foundation'
$token = $null
$previousPgPassword = $env:PGPASSWORD

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $output = & az @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed ($Label)."
    }
    return $output | ConvertFrom-Json -Depth 100
}

function Invoke-PostgresScalar {
    param(
        [Parameter(Mandatory)] [string] $Database,
        [Parameter(Mandatory)] [string] $Sql
    )

    $result = & $script:psql.Source `
        "host=$script:postgresHost port=5432 dbname=$Database user=$PostgresEntraAdministratorName sslmode=require" `
        --no-psqlrc `
        --tuples-only `
        --no-align `
        --set ON_ERROR_STOP=1 `
        --command $Sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL setup failed in database '$Database'."
    }
    return ([string] ($result -join '')).Trim()
}

function Quote-PostgresIdentifier {
    param([Parameter(Mandatory)] [string] $Value)
    return '"' + $Value.Replace('"', '""', [StringComparison]::Ordinal) + '"'
}

function Quote-PostgresLiteral {
    param([Parameter(Mandatory)] [string] $Value)
    return "'" + $Value.Replace("'", "''", [StringComparison]::Ordinal) + "'"
}

try {
    & az bicep build --file $templateFile --stdout *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Local Bicep compilation failed.'
    }

    & az bicep build-params --file $parameterFile --outfile $compiledParameterFile --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Local Bicep parameter compilation failed.'
    }

    $parameters = Get-Content -LiteralPath $compiledParameterFile -Raw | ConvertFrom-Json -Depth 100
    $script:postgresHost = [string] $parameters.parameters.postgresHost.value
    $targetDatabaseName = [string] $parameters.parameters.targetDatabaseName.value
    $containerRegistryName = [string] $parameters.parameters.containerRegistryName.value
    $containerAppEnvironmentName = [string] $parameters.parameters.containerAppEnvironmentName.value
    if ($targetDatabaseName -notmatch '^[a-z][a-z0-9_]{2,62}$') {
        throw 'targetDatabaseName must be a lowercase PostgreSQL identifier.'
    }
    if ($targetDatabaseName -in @('postgres', 'ofm_platform', 'azure_sys', 'azure_maintenance')) {
        throw "Database '$targetDatabaseName' is reserved and cannot host the generated application."
    }

    $account = Invoke-AzJson -Label 'read current subscription' -Arguments @('account', 'show')
    if (-not [string]::Equals([string] $account.id, $SubscriptionId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Azure CLI is using subscription '$($account.id)', not the requested subscription '$SubscriptionId'."
    }

    [void](Invoke-AzJson -Label 'resolve resource group' -Arguments @(
        'group', 'show', '--subscription', $SubscriptionId, '--name', $ResourceGroupName
    ))
    [void](Invoke-AzJson -Label 'resolve registry' -Arguments @(
        'acr', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $containerRegistryName
    ))
    [void](Invoke-AzJson -Label 'resolve Container Apps environment' -Arguments @(
        'containerapp', 'env', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
        '--name', $containerAppEnvironmentName
    ))

    $whatIf = Invoke-AzJson -Label 'preview foundation' -Arguments @(
        'deployment', 'group', 'what-if',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', "$deploymentName-preview",
        '--template-file', $templateFile,
        '--parameters', $compiledParameterFile, 'deployTarget=false',
        '--result-format', 'ResourceIdOnly',
        '--no-pretty-print'
    )
    $changes = @($whatIf.changes)
    Write-Output "Foundation what-if completed with $($changes.Count) resource change(s)."
    foreach ($change in $changes) {
        $resourceName = ([string] $change.resourceId -split '/')[-1]
        Write-Output "[$($change.changeType)] $resourceName"
    }

    if (-not $ApplyFoundation) {
        Write-Output 'Preview only. No Azure resource, role assignment, database, or principal was changed.'
        return
    }

    if ([string]::IsNullOrWhiteSpace($PostgresEntraAdministratorName)) {
        throw 'PostgresEntraAdministratorName is required with ApplyFoundation.'
    }
    $postgresServerName = ([UriBuilder]::new('https', $script:postgresHost)).Host.Split('.')[0]
    $entraAdministrators = @(Invoke-AzJson -Label 'list PostgreSQL Entra administrators' -Arguments @(
        'postgres', 'flexible-server', 'microsoft-entra-admin', 'list',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--server-name', $postgresServerName
    ))
    if (@($entraAdministrators | Where-Object {
                [string]::Equals(
                    [string] $_.principalName,
                    $PostgresEntraAdministratorName,
                    [StringComparison]::OrdinalIgnoreCase)
            }).Count -ne 1) {
        throw "PostgresEntraAdministratorName is not an administrator on '$postgresServerName'."
    }
    $script:psql = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -eq $script:psql) {
        throw 'psql is required to create the dedicated database and grants without placing a token on a command line.'
    }

    $deployment = Invoke-AzJson -Label 'apply foundation' -Arguments @(
        'deployment', 'group', 'create',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', $deploymentName,
        '--template-file', $templateFile,
        '--parameters', $compiledParameterFile, 'deployTarget=false'
    )
    $outputs = $deployment.properties.outputs
    $targetIdentityName = [string] $outputs.targetIdentityName.value
    if ($targetIdentityName -notmatch '^[A-Za-z0-9._\-]{3,128}$') {
        throw 'The deployed managed identity name is invalid.'
    }

    $token = & az account get-access-token `
        --subscription $SubscriptionId `
        --resource-type oss-rdbms `
        --query accessToken `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw 'A PostgreSQL Microsoft Entra access token could not be acquired.'
    }
    $env:PGPASSWORD = ([string] $token).Trim()

    $identityLiteral = Quote-PostgresLiteral $targetIdentityName
    $identityIdentifier = Quote-PostgresIdentifier $targetIdentityName
    $databaseLiteral = Quote-PostgresLiteral $targetDatabaseName
    $databaseIdentifier = Quote-PostgresIdentifier $targetDatabaseName

    $principalExists = Invoke-PostgresScalar -Database 'postgres' -Sql "select 1 from pg_roles where rolname = $identityLiteral;"
    if ($principalExists -ne '1') {
        [void](Invoke-PostgresScalar -Database 'postgres' -Sql "select * from pgaadauth_create_principal($identityLiteral, false, false);")
    }

    $databaseOwner = Invoke-PostgresScalar -Database 'postgres' -Sql "select pg_catalog.pg_get_userbyid(datdba) from pg_database where datname = $databaseLiteral;"
    if ([string]::IsNullOrWhiteSpace($databaseOwner)) {
        [void](Invoke-PostgresScalar -Database 'postgres' -Sql "create database $databaseIdentifier owner $identityIdentifier;")
    }
    elseif (-not [string]::Equals($databaseOwner, $targetIdentityName, [StringComparison]::Ordinal)) {
        throw "Database '$targetDatabaseName' already exists with owner '$databaseOwner'; refusing to take ownership."
    }

    [void](Invoke-PostgresScalar -Database $targetDatabaseName -Sql "revoke connect on database $databaseIdentifier from public;")
    [void](Invoke-PostgresScalar -Database $targetDatabaseName -Sql "grant connect, create, temporary on database $databaseIdentifier to $identityIdentifier;")
    [void](Invoke-PostgresScalar -Database $targetDatabaseName -Sql 'revoke create on schema public from public;')
    [void](Invoke-PostgresScalar -Database $targetDatabaseName -Sql "grant usage, create on schema public to $identityIdentifier;")

    Write-Output "Foundation prepared for database '$targetDatabaseName' and identity '$targetIdentityName'."
    Write-Output 'No generated application image was built or deployed.'
}
finally {
    $token = $null
    if ($null -eq $previousPgPassword) {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    else {
        $env:PGPASSWORD = $previousPgPassword
    }
    Remove-Item -LiteralPath $compiledParameterFile -Force -ErrorAction SilentlyContinue
}