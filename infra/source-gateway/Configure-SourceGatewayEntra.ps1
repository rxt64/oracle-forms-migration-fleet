[CmdletBinding()]
param(
    [switch] $Apply,

    [string] $SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',

    [string] $TenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86',

    [string] $OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255'
$forbiddenDefaultSubscriptionId = '0832b3b6-22b3-4c47-8d8b-572054b97257'
$expectedTenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
$applicationDisplayName = 'api-ofmfleet-source-gateway-dev'
$expectedApplicationObjectId = '1841ae69-9889-48fc-aa56-f1cfdf2c7203'
$expectedApplicationClientId = 'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
$expectedServicePrincipalId = 'c7f29be4-bffe-44e9-8108-1449571a031c'
$expectedRoleId = 'b03da987-2091-4443-86bc-5c3e9569a91a'
$callerPrincipalId = '595d3a27-a2a8-4ac4-b33d-98715cbcd684'
$callerClientId = 'f4492b02-ee64-4295-a1db-7c7677134e42'
$roleValue = 'SourceGateway.Invoke'
$expectedRoleAllowedMemberTypes = @('Application')
$graphRoot = 'https://graph.microsoft.com/v1.0'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $OutputPath = Join-Path $repositoryRoot 'artifacts/source-gateway-entra/identity.json'
}

function Invoke-AzAccountJson {
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = & az @Arguments --subscription $SubscriptionId --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ([string] ($output -join [Environment]::NewLine)).Trim()
        throw "$Label failed: $detail"
    }

    $text = ([string] ($output -join [Environment]::NewLine)).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }
    return $text | ConvertFrom-Json -Depth 100
}

function Invoke-GraphJson {
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [Parameter(Mandatory)]
        [ValidateSet('GET', 'POST', 'PATCH')]
        [string] $Method,

        [Parameter(Mandatory)]
        [string] $Uri,

        [string] $Body = ''
    )

    $request = @{
        Method = $Method
        Uri = $Uri
        Headers = @{ Authorization = "Bearer $script:graphAccessToken" }
        ErrorAction = 'Stop'
    }
    if (-not [string]::IsNullOrWhiteSpace($Body)) {
        $request.ContentType = 'application/json'
        $request.Body = $Body
    }

    try {
        return Invoke-RestMethod @request
    } catch {
        $detail = if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            $_.ErrorDetails.Message
        } else {
            $_.Exception.Message
        }
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        $statusCode = if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            [int] $responseProperty.Value.StatusCode
        } else { 0 }
        if ($statusCode -eq 403 -or $detail -match '(?i)(Authorization_RequestDenied|insufficient privileges)') {
            throw "$Label failed with the directory authorization error returned by Microsoft Graph: $detail Required administrator action: an existing authorized Entra administrator must perform this exact operation with Application.ReadWrite.All and AppRoleAssignment.ReadWrite.All. No workaround or privilege broadening was attempted."
        }
        throw "$Label failed: $detail"
    }
}

function Write-PublicEvidence {
    param([Parameter(Mandatory)] [Collections.IDictionary] $Evidence)

    $parent = Split-Path $OutputPath -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
}

if ($SubscriptionId -ne $expectedSubscriptionId -or $SubscriptionId -eq $forbiddenDefaultSubscriptionId) {
    throw "This operation is pinned to lab subscription $expectedSubscriptionId. The corporate default is never used or changed."
}
if ($TenantId -ne $expectedTenantId) {
    throw "This operation is pinned to tenant $expectedTenantId."
}

$account = Invoke-AzAccountJson -Label 'Pinned subscription preflight' -Arguments @('account', 'show')
if ($account.id -ne $expectedSubscriptionId -or $account.tenantId -ne $expectedTenantId) {
    throw 'Azure CLI authentication does not match the pinned lab subscription and tenant.'
}

$graphTokenOutput = & az account get-access-token `
    --subscription $SubscriptionId `
    --resource-type ms-graph `
    --query accessToken `
    --output tsv `
    --only-show-errors
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($graphTokenOutput)) {
    throw 'A Microsoft Graph token could not be acquired for the pinned subscription and tenant.'
}
$script:graphAccessToken = ([string] $graphTokenOutput).Trim()

$caller = Invoke-GraphJson -Label 'Managed identity preflight' -Method GET `
    -Uri "$graphRoot/servicePrincipals/$callerPrincipalId`?`$select=id,appId,servicePrincipalType,appOwnerOrganizationId,alternativeNames"
if ($caller.id -ne $callerPrincipalId -or
    $caller.appId -ne $callerClientId -or
    $caller.servicePrincipalType -ne 'ManagedIdentity' -or
    ($null -ne $caller.appOwnerOrganizationId -and $caller.appOwnerOrganizationId -ne $expectedTenantId)) {
    throw 'The approved caller object ID, client ID, tenant, and ManagedIdentity service-principal type do not match.'
}
$callerSubscriptionBindings = @($caller.alternativeNames | Where-Object {
        $_ -like "/subscriptions/$expectedSubscriptionId/*"
    })
if ($callerSubscriptionBindings.Count -eq 0) {
    throw 'The approved managed identity is not bound to the pinned lab subscription.'
}

$applicationFilter = [Uri]::EscapeDataString("displayName eq '$applicationDisplayName'")
$applicationMatchesResponse = Invoke-GraphJson -Label 'Application exact-name preflight' -Method GET `
    -Uri "$graphRoot/applications?`$filter=$applicationFilter&`$select=id,appId,displayName"
$matches = @($applicationMatchesResponse.value | Where-Object { $_.displayName -ceq $applicationDisplayName })
if ($matches.Count -gt 1) {
    throw "More than one application has the exact display name '$applicationDisplayName'; refusing to select one."
}
if ($matches.Count -eq 0) {
    throw "The authorized gateway application object $expectedApplicationObjectId is absent; refusing to create or select a replacement."
}
$application = $matches[0]
if ($application.id -ne $expectedApplicationObjectId -or $application.appId -ne $expectedApplicationClientId) {
    throw 'The exact-name application does not match the authorized gateway application object and client IDs.'
}
$createdApplication = $false

$application = Invoke-GraphJson -Label 'Read gateway application configuration' -Method GET `
    -Uri "$graphRoot/applications/$($application.id)`?`$select=id,appId,displayName,signInAudience,identifierUris,requiredResourceAccess,passwordCredentials,keyCredentials,web,spa,publicClient,api,appRoles"
$ownersResponse = Invoke-GraphJson -Label 'Read gateway application owners' -Method GET `
    -Uri "$graphRoot/applications/$($application.id)/owners?`$select=id"
$owners = @($ownersResponse.value)

$expectedIdentifierUri = "api://$($application.appId)"
$identifierUris = @($application.identifierUris)
$requiredResourceAccess = @($application.requiredResourceAccess)
$passwordCredentials = @($application.passwordCredentials)
$keyCredentials = @($application.keyCredentials)
$webRedirectUris = @($application.web.redirectUris)
$spaRedirectUris = @($application.spa.redirectUris)
$publicRedirectUris = @($application.publicClient.redirectUris)
$tokenVersion = $application.api.requestedAccessTokenVersion
$delegatedScopes = @($application.api.oauth2PermissionScopes)
$preAuthorizedApplications = @($application.api.preAuthorizedApplications)
$knownClientApplications = @($application.api.knownClientApplications)

if ($application.signInAudience -ne 'AzureADMyOrg') {
    throw 'The exact-name application is not single-tenant; refusing to modify it.'
}
if ($identifierUris.Count -gt 0 -and $identifierUris -cnotcontains $expectedIdentifierUri) {
    throw 'The exact-name application has a conflicting identifier URI; refusing to modify unrelated configuration.'
}
if ($requiredResourceAccess.Count -gt 0 -or $passwordCredentials.Count -gt 0 -or $keyCredentials.Count -gt 0 -or
    $delegatedScopes.Count -gt 0 -or $preAuthorizedApplications.Count -gt 0 -or $knownClientApplications.Count -gt 0 -or
    $webRedirectUris.Count -gt 0 -or $spaRedirectUris.Count -gt 0 -or $publicRedirectUris.Count -gt 0) {
    throw 'The exact-name application has API permissions, credentials, certificates, or redirect URIs; refusing to treat it as the reviewed gateway API.'
}
if ($null -ne $tokenVersion -and [int] $tokenVersion -ne 2) {
    throw 'The exact-name application has a conflicting access-token version; refusing to modify it.'
}

$roles = @($application.appRoles)
$sameValueRoles = @($roles | Where-Object { $_.value -ceq $roleValue })
$sameNameRoles = @($roles | Where-Object { $_.displayName -ceq $roleValue })
if ($sameValueRoles.Count -gt 1 -or $sameNameRoles.Count -gt 1) {
    throw "The exact-name application has duplicate definitions for '$roleValue'."
}
if ($sameValueRoles.Count -eq 1) {
    $role = $sameValueRoles[0]
    if ($role.id -ne $expectedRoleId -or
        $role.displayName -cne $roleValue -or
        @($role.allowedMemberTypes).Count -ne $expectedRoleAllowedMemberTypes.Count -or
        @($role.allowedMemberTypes)[0] -cne $expectedRoleAllowedMemberTypes[0] -or
        -not $role.isEnabled) {
        throw "The existing '$roleValue' role is not the reviewed application-only role."
    }
} elseif ($sameNameRoles.Count -gt 0) {
    throw "The exact-name application has a conflicting role named '$roleValue'."
} else {
    throw "The authorized gateway application role $expectedRoleId is absent; refusing to create a replacement."
}

$configurationChanges = [Collections.Generic.List[string]]::new()
if ($identifierUris -cnotcontains $expectedIdentifierUri) { $configurationChanges.Add('IdentifierUri') }
if ($null -eq $tokenVersion -or [int] $tokenVersion -ne 2) { $configurationChanges.Add('AccessTokenVersion') }
if ($sameValueRoles.Count -eq 0) { $configurationChanges.Add('ApplicationRole') }

if ($configurationChanges.Count -gt 0) {
    if (-not $Apply) {
        throw "The application exists but requires reviewed changes: $($configurationChanges -join ', '). Rerun with -Apply only after reviewing its object ID $($application.id), owner count $($owners.Count), and current configuration."
    }
    $updatedRoles = @($roles | ForEach-Object {
            [ordered]@{
                allowedMemberTypes = @($_.allowedMemberTypes)
                description = $_.description
                displayName = $_.displayName
                id = $_.id
                isEnabled = $_.isEnabled
                value = $_.value
            }
        }) + @($role)
    $updateApplicationBody = [ordered]@{
        identifierUris = @($expectedIdentifierUri)
        api = @{ requestedAccessTokenVersion = 2 }
        appRoles = @($updatedRoles)
    } | ConvertTo-Json -Depth 10 -Compress
    [void](Invoke-GraphJson -Label 'Configure gateway application' -Method PATCH `
            -Uri "$graphRoot/applications/$($application.id)" -Body $updateApplicationBody)
}

$servicePrincipalFilter = [Uri]::EscapeDataString("appId eq '$($application.appId)'")
$servicePrincipalResponse = Invoke-GraphJson -Label 'Gateway service-principal preflight' -Method GET `
    -Uri "$graphRoot/servicePrincipals?`$filter=$servicePrincipalFilter&`$select=id,appId,servicePrincipalType,appOwnerOrganizationId,appRoleAssignmentRequired"
$servicePrincipals = @($servicePrincipalResponse.value)
if ($servicePrincipals.Count -gt 1) {
    throw 'More than one service principal exists for the gateway application in this tenant.'
}
if ($servicePrincipals.Count -eq 0) {
    throw "The authorized gateway service principal $expectedServicePrincipalId is absent; refusing to create a replacement."
}
$resourceServicePrincipal = $servicePrincipals[0]
if ($resourceServicePrincipal.id -ne $expectedServicePrincipalId -or
    $resourceServicePrincipal.appId -ne $application.appId -or
    $resourceServicePrincipal.servicePrincipalType -ne 'Application' -or
    $resourceServicePrincipal.appOwnerOrganizationId -ne $expectedTenantId) {
    throw 'The gateway service principal does not match the reviewed application and tenant.'
}

$appRoleAssignmentRequiredChanged = $resourceServicePrincipal.appRoleAssignmentRequired -ne $true
if ($appRoleAssignmentRequiredChanged) {
    if (-not $Apply) {
        throw "The authorized gateway service principal $expectedServicePrincipalId does not require app-role assignment. Rerun with -Apply to set only appRoleAssignmentRequired=true."
    }
    $servicePrincipalGuardBody = @{ appRoleAssignmentRequired = $true } | ConvertTo-Json -Compress
    [void](Invoke-GraphJson -Label 'Require gateway app-role assignment' -Method PATCH `
            -Uri "$graphRoot/servicePrincipals/$expectedServicePrincipalId" -Body $servicePrincipalGuardBody)
}

$resourceServicePrincipal = Invoke-GraphJson -Label 'Read gateway service-principal roles' -Method GET `
    -Uri "$graphRoot/servicePrincipals/$($resourceServicePrincipal.id)`?`$select=id,appId,servicePrincipalType,appOwnerOrganizationId,appRoleAssignmentRequired,appRoles"
if ($resourceServicePrincipal.appRoleAssignmentRequired -ne $true) {
    throw 'The authorized gateway service principal still does not require app-role assignment after reconciliation.'
}
$servicePrincipalRoles = @($resourceServicePrincipal.appRoles | Where-Object { $_.value -ceq $roleValue })
if ($servicePrincipalRoles.Count -ne 1 -or $servicePrincipalRoles[0].id -ne $role.id) {
    throw 'The gateway service principal does not expose the exact reviewed application role ID.'
}

$assignmentsResponse = Invoke-GraphJson -Label 'Read gateway app-role assignments' -Method GET `
    -Uri "$graphRoot/servicePrincipals/$($resourceServicePrincipal.id)/appRoleAssignedTo"
$assignments = @($assignmentsResponse.value)
$exactAssignments = @($assignments | Where-Object {
        $_.principalId -eq $callerPrincipalId -and
        $_.resourceId -eq $resourceServicePrincipal.id -and
        $_.appRoleId -eq $role.id
    })
if ($assignments.Count -ne $exactAssignments.Count -or $exactAssignments.Count -gt 1) {
    throw 'A conflicting, duplicate, or additional gateway app-role assignment exists; only the approved UAMI tuple is allowed.'
}
if ($exactAssignments.Count -eq 0) {
    if (-not $Apply) {
        throw 'The exact app-role assignment is absent. Rerun with -Apply to create only the confirmed resource/principal/role tuple.'
    }
    $assignmentBody = [ordered]@{
        principalId = $callerPrincipalId
        resourceId = $resourceServicePrincipal.id
        appRoleId = $role.id
    } | ConvertTo-Json -Compress
    $assignment = Invoke-GraphJson -Label 'Create gateway app-role assignment' -Method POST `
        -Uri "$graphRoot/servicePrincipals/$($resourceServicePrincipal.id)/appRoleAssignedTo" -Body $assignmentBody
} else {
    $assignment = $exactAssignments[0]
}

$evidence = [ordered]@{
    schemaVersion = 1
    mode = if ($Apply) { 'AppliedAndVerified' } else { 'Verified' }
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    subscriptionId = $expectedSubscriptionId
    tenantId = $expectedTenantId
    applicationDisplayName = $applicationDisplayName
    applicationObjectId = $application.id
    applicationClientId = $application.appId
    identifierUri = $expectedIdentifierUri
    ownerCount = $owners.Count
    ownerObjectIds = @($owners.id)
    servicePrincipalId = $resourceServicePrincipal.id
    servicePrincipalType = $resourceServicePrincipal.servicePrincipalType
    appRoleAssignmentRequired = $resourceServicePrincipal.appRoleAssignmentRequired
    appRoleAssignmentRequiredChanged = $appRoleAssignmentRequiredChanged
    roleValue = $roleValue
    roleId = $role.id
    roleAllowedMemberTypes = @($role.allowedMemberTypes)
    callerPrincipalId = $callerPrincipalId
    callerClientId = $callerClientId
    callerServicePrincipalType = $caller.servicePrincipalType
    assignmentId = $assignment.id
    assignmentTupleVerified = (
        $assignment.principalId -eq $callerPrincipalId -and
        $assignment.resourceId -eq $resourceServicePrincipal.id -and
        $assignment.appRoleId -eq $role.id)
    createdApplication = $createdApplication
    credentialsCreated = $false
    graphPermissionsAdded = $false
    redirectsChanged = $false
    defaultSubscriptionChanged = $false
}
if (-not $evidence.assignmentTupleVerified) {
    throw 'Microsoft Graph returned an app-role assignment that does not match the approved tuple.'
}

Write-PublicEvidence -Evidence $evidence
Write-Output ($evidence | ConvertTo-Json -Depth 10)
Write-Output "Nonsecret verification artifact: $OutputPath"