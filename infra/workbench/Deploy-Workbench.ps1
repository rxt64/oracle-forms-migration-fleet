#requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://[^/]+\.services\.ai\.azure\.com/.+/agents/.+/endpoint/protocols/openai/responses(?:\?.*)?$')]
    [string] $FoundryAgentEndpoint,

    [ValidatePattern('^[0-9a-f]{12}$')]
    [string] $ImageTag,

    [switch] $FoundationOnly
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'
$compiledParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "oracle-forms-migration-fleet-$([Guid]::NewGuid().ToString('N')).parameters.json"
$deploymentParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "oracle-forms-migration-fleet-$([Guid]::NewGuid().ToString('N')).deployment.parameters.json"
$deploymentName = 'migration-fleet-workbench'
$appDisplayName = 'Migration Fleet Workbench - dev'
$clientSecret = $null
$deploymentParameters = $null
$deploymentParametersJson = $null

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $result = & az @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments[0..1] -join ' ')"
    }
    return $result | ConvertFrom-Json -Depth 100
}

function Set-ArmParameter {
    param(
        [Parameter(Mandatory)] [object] $Document,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [AllowEmptyString()] [object] $Value
    )

    $Document.parameters | Add-Member `
        -MemberType NoteProperty `
        -Name $Name `
        -Value ([pscustomobject]@{ value = $Value }) `
        -Force
}

function Write-CurrentUserOnlyFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content
    )

    [System.IO.File]::WriteAllText($Path, '{}', [System.Text.UTF8Encoding]::new($false))
    if ($IsWindows) {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $acl = [System.Security.AccessControl.FileSecurity]::new()
        $acl.SetOwner($identity.User)
        $acl.SetAccessRuleProtection($true, $false)
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $identity.User,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void] $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    else {
        & chmod 600 $Path
        if ($LASTEXITCODE -ne 0) {
            throw 'Securing the temporary deployment parameter file failed.'
        }
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

try {
    & az bicep build-params --file $parameterFile --outfile $compiledParameterFile *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Local Bicep parameter compilation failed.'
    }

    Write-Output 'Deploying Container Apps foundation...'
    $foundation = Invoke-AzJson -Arguments @(
        'deployment', 'group', 'create',
        '--name', "$deploymentName-foundation",
        '--resource-group', $ResourceGroupName,
        '--template-file', $templateFile,
        '--parameters', $compiledParameterFile, 'deployWorkbench=false'
    )

    $outputs = $foundation.properties.outputs
    $registryName = $outputs.registryName.value
    $imageRepository = $outputs.imageRepository.value
    $workbenchUrl = $outputs.workbenchUrl.value
    $redirectUri = "$workbenchUrl/.auth/login/aad/callback"

    $agentUri = [Uri] $FoundryAgentEndpoint
    $expectedAgentHost = "$($outputs.foundryAccountName.value).services.ai.azure.com"
    $expectedProjectPath = "/api/projects/$($outputs.foundryProjectName.value)/agents/"
    $endpointQueryIsAllowed = [string]::IsNullOrEmpty($agentUri.Query) -or $agentUri.Query -ceq '?api-version=v1'
    if ($agentUri.Scheme -cne 'https' -or
        !$agentUri.IsDefaultPort -or
        ![string]::IsNullOrEmpty($agentUri.UserInfo) -or
        ![string]::IsNullOrEmpty($agentUri.Fragment) -or
        !$endpointQueryIsAllowed -or
        $agentUri.Host -ine $expectedAgentHost -or
        !$agentUri.AbsolutePath.StartsWith($expectedProjectPath, [StringComparison]::Ordinal) -or
        !$agentUri.AbsolutePath.EndsWith('/endpoint/protocols/openai/responses', [StringComparison]::Ordinal)) {
        throw 'The Foundry endpoint does not match the canonical hosted-agent endpoint for the account and project declared by this deployment.'
    }

    if ($FoundationOnly) {
        Write-Output "Foundation ready. Build a commit-addressed image with the GitHub workflow before deploying the authenticated app."
        return
    }

    if ([string]::IsNullOrWhiteSpace($ImageTag)) {
        throw 'ImageTag is required unless FoundationOnly is specified. Use the 12-character commit tag built by the GitHub workflow.'
    }

    $credentialDisplayName = "container-app-auth-$ImageTag"
    $image = "$($outputs.registryLoginServer.value)/$imageRepository`:$ImageTag"
    & az acr repository show `
        --name $registryName `
        --image "$imageRepository`:$ImageTag" `
        --output none `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "The commit-addressed image '$imageRepository`:$ImageTag' is not in ACR. Build it with the GitHub runner before bootstrap deploys the app."
    }

    $applications = Invoke-AzJson -Arguments @('ad', 'app', 'list', '--display-name', $appDisplayName)
    $application = @($applications) | Where-Object displayName -eq $appDisplayName | Select-Object -First 1
    if ($null -eq $application) {
        Write-Output 'Creating the dedicated single-tenant Entra application...'
        $application = Invoke-AzJson -Arguments @(
            'ad', 'app', 'create',
            '--display-name', $appDisplayName,
            '--sign-in-audience', 'AzureADMyOrg',
            '--enable-id-token-issuance', 'true',
            '--web-redirect-uris', $redirectUri
        )
    }

    Write-Output 'Configuring the dedicated Entra application for Container Apps authentication...'
    & az ad app update `
        --id $application.appId `
        --enable-id-token-issuance true `
        --web-redirect-uris $redirectUri `
        --only-show-errors 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Updating the Entra application failed.'
    }

    $servicePrincipals = Invoke-AzJson -Arguments @(
        'ad', 'sp', 'list',
        '--filter', "appId eq '$($application.appId)'"
    )
    if (@($servicePrincipals).Count -eq 0) {
        [void](Invoke-AzJson -Arguments @('ad', 'sp', 'create', '--id', $application.appId))
    }

    $credentials = Invoke-AzJson -Arguments @('ad', 'app', 'credential', 'list', '--id', $application.appId)
    $previousCredentials = @($credentials) | Where-Object displayName -like 'container-app-auth-*'
    if ($previousCredentials.Count -ge 2) {
        throw 'Two retained workbench credentials already exist. Remove a superseded credential after validating the last successful deployment before rotating again.'
    }

    $clientSecret = & az ad app credential reset `
        --id $application.appId `
        --append `
        --display-name $credentialDisplayName `
        --years 1 `
        --query password `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($clientSecret)) {
        throw 'Creating the workbench authentication credential failed.'
    }

    $deploymentParameters = Get-Content -LiteralPath $compiledParameterFile -Raw | ConvertFrom-Json -Depth 100
    Set-ArmParameter -Document $deploymentParameters -Name 'deployWorkbench' -Value $true
    Set-ArmParameter -Document $deploymentParameters -Name 'containerImage' -Value $image
    Set-ArmParameter -Document $deploymentParameters -Name 'foundryAgentEndpoint' -Value $FoundryAgentEndpoint
    Set-ArmParameter -Document $deploymentParameters -Name 'entraClientId' -Value $application.appId
    Set-ArmParameter -Document $deploymentParameters -Name 'entraClientSecret' -Value $clientSecret
    $deploymentParametersJson = $deploymentParameters | ConvertTo-Json -Depth 100
    Write-CurrentUserOnlyFile -Path $deploymentParameterFile -Content $deploymentParametersJson

    Write-Output 'Deploying the authenticated workbench revision...'
    [void](Invoke-AzJson -Arguments @(
        'deployment', 'group', 'create',
        '--name', $deploymentName,
        '--resource-group', $ResourceGroupName,
        '--template-file', $templateFile,
        '--parameters', $deploymentParameterFile
    ))

    Write-Output "Workbench deployed: $workbenchUrl"
    Write-Output 'Authentication: Microsoft Entra ID (single tenant).'
    Write-Output 'Foundry access: user-assigned managed identity with Foundry Agent Consumer at project scope.'
    if ($previousCredentials.Count -gt 0) {
        Write-Output 'Previous authentication credentials were retained. Remove them only after an authenticated browser smoke test succeeds.'
        foreach ($credential in $previousCredentials) {
            Write-Output "Retained credential: $($credential.displayName) (key ID $($credential.keyId))"
        }
    }
} finally {
    $clientSecret = $null
    $deploymentParameters = $null
    $deploymentParametersJson = $null
    Remove-Item -LiteralPath $compiledParameterFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $deploymentParameterFile -Force -ErrorAction SilentlyContinue
}