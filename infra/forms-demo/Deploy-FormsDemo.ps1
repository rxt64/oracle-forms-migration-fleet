#requires -Version 7.2

<#
.SYNOPSIS
    Builds and deploys the browser-executable Oracle Forms workflow replica.

.DESCRIPTION
    Builds the workflow replica image in Azure Container Registry and deploys it to the existing
    Container Apps environment. The server connects to the internal disposable Oracle estate; its
    credential is retrieved into memory and passed to Azure through a restricted temporary file.

.EXAMPLE
    .\Deploy-FormsDemo.ps1 -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$')]
    [string] $ImageTag = (Get-Date -Format 'yyyyMMddHHmmss')
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'
$applicationContext = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\oracle-forms-demo'))
$dockerfile = Join-Path $applicationContext 'Dockerfile'
$compiledParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "forms-demo-$([Guid]::NewGuid().ToString('N')).parameters.json"
$deploymentParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "forms-demo-$([Guid]::NewGuid().ToString('N')).deployment.parameters.json"
$deploymentName = 'oracle-forms-demo'
$imageRepository = 'oracle-forms-demo'
$databaseSecretName = 'banking-schema-password'
$deploymentParameters = $null
$databasePassword = $null
$oracleConnectionString = $null
$secretCommandOutput = $null

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)] [string] $CommandLabel,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $result = & az @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed ($CommandLabel): $($result -join [Environment]::NewLine)"
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
        $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $acl = [System.Security.AccessControl.FileSecurity]::new()
        $acl.SetOwner($currentIdentity.User)
        $acl.SetAccessRuleProtection($true, $false)
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $currentIdentity.User,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void] $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    else {
        & chmod 600 $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restrict permissions on '$Path'."
        }
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

try {
    if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
        throw "Dockerfile was not found at '$dockerfile'."
    }

    $compileResult = & az bicep build-params `
        --file $parameterFile `
        --outfile $compiledParameterFile `
        --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Compiling the Bicep parameter file failed: $($compileResult -join [Environment]::NewLine)"
    }

    $deploymentParameters = Get-Content -LiteralPath $compiledParameterFile -Raw | ConvertFrom-Json -Depth 100
    $registryName = $deploymentParameters.parameters.containerRegistryName.value
    $environmentName = $deploymentParameters.parameters.containerAppEnvironmentName.value
    $databaseAppName = $deploymentParameters.parameters.databaseContainerAppName.value
    foreach ($configuredName in @{
            containerRegistryName = $registryName
            containerAppEnvironmentName = $environmentName
            databaseContainerAppName = $databaseAppName
        }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string] $configuredName.Value)) {
            throw "Parameter '$($configuredName.Key)' is missing from '$parameterFile'."
        }
    }

    $registry = Invoke-AzJson -CommandLabel 'resolve container registry' -Arguments @(
        'acr', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $registryName
    )
    $environment = Invoke-AzJson -CommandLabel 'resolve Container Apps environment' -Arguments @(
        'containerapp', 'env', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $environmentName
    )
    $databaseApp = Invoke-AzJson -CommandLabel 'resolve internal Oracle container app' -Arguments @(
        'containerapp', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $databaseAppName
    )

    if ([string]::IsNullOrWhiteSpace([string] $registry.loginServer)) {
        throw 'The configured container registry has no login server.'
    }
    if ([string]::IsNullOrWhiteSpace([string] $environment.id)) {
        throw 'The configured Container Apps environment has no resource ID.'
    }
    if (-not [string]::Equals(
            [string] $databaseApp.properties.managedEnvironmentId,
            [string] $environment.id,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Oracle database app is not attached to the configured Container Apps environment.'
    }

    $databaseIngress = $databaseApp.properties.configuration.ingress
    if ($null -eq $databaseIngress -or $databaseIngress.external -ne $false) {
        throw 'The Oracle database app must use internal-only ingress.'
    }
    if (-not [string]::Equals([string] $databaseIngress.transport, 'tcp', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Oracle database app must use TCP ingress.'
    }
    if ([int] $databaseIngress.targetPort -ne 1521 -or [int] $databaseIngress.exposedPort -ne 1521) {
        throw 'The Oracle database app must expose TCP port 1521 to target port 1521.'
    }
    $databaseFqdn = [string] $databaseIngress.fqdn
    if ([string]::IsNullOrWhiteSpace($databaseFqdn)) {
        throw 'The Oracle database app has no internal FQDN.'
    }

    # In this Consumption environment, the long internal FQDN resolves to the environment ingress
    # VIP but does not route app-to-app TCP. The Container App name resolves directly to the
    # in-environment service and is the validated endpoint for port 1521.
    $databaseHost = $databaseAppName

    $image = "$($registry.loginServer)/$imageRepository`:$ImageTag"
    $repositories = @(Invoke-AzJson -CommandLabel 'list container registry repositories' -Arguments @(
        'acr', 'repository', 'list',
        '--name', $registryName
    ))
    $imageTags = @()
    if ($repositories -contains $imageRepository) {
        $imageTags = @(Invoke-AzJson -CommandLabel 'list workflow replica image tags' -Arguments @(
            'acr', 'repository', 'show-tags',
            '--name', $registryName,
            '--repository', $imageRepository
        ))
    }
    if ($imageTags -contains $ImageTag) {
        throw "Image '$image' already exists. Supply a new immutable tag."
    }

    $buildRun = Invoke-AzJson -CommandLabel 'build workflow replica image' -Arguments @(
        'acr', 'build',
        '--registry', $registryName,
        '--image', "$imageRepository`:$ImageTag",
        '--file', $dockerfile,
        $applicationContext,
        '--no-logs'
    )
    if ($buildRun.status -ne 'Succeeded') {
        throw "Azure Container Registry build failed with status '$($buildRun.status)'. Inspect run '$($buildRun.runId)' in registry '$registryName'."
    }

    $secretCommandOutput = & az containerapp secret show `
        --resource-group $ResourceGroupName `
        --name $databaseAppName `
        --secret-name $databaseSecretName `
        --query value `
        --output tsv `
        --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        $secretCommandOutput = $null
        throw 'Could not retrieve the internal Oracle application credential.'
    }
    $databasePassword = ([string] ($secretCommandOutput -join '')).Trim()
    $secretCommandOutput = $null
    if ([string]::IsNullOrWhiteSpace($databasePassword) -or $databasePassword -notmatch '^[A-Za-z0-9]+$') {
        throw 'The internal Oracle application credential is missing or has an unexpected format.'
    }

    $oracleConnectionString = "User Id=BANKING;Password=$databasePassword;Data Source=$databaseHost`:1521/FREEPDB1;Connection Timeout=15;"
    Set-ArmParameter -Document $deploymentParameters -Name 'containerImage' -Value $image
    Set-ArmParameter -Document $deploymentParameters -Name 'oracleConnectionString' -Value $oracleConnectionString
    Write-CurrentUserOnlyFile `
        -Path $deploymentParameterFile `
        -Content ($deploymentParameters | ConvertTo-Json -Depth 100)

    $deployment = $null
    $maxAttempts = 4
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $deployment = Invoke-AzJson -CommandLabel "deploy workflow replica (attempt $attempt)" -Arguments @(
                'deployment', 'group', 'create',
                '--name', $deploymentName,
                '--resource-group', $ResourceGroupName,
                '--template-file', $templateFile,
                '--parameters', $deploymentParameterFile
            )
            break
        }
        catch {
            if ($attempt -eq $maxAttempts) {
                throw
            }

            Write-Verbose "Deployment attempt $attempt failed; waiting for AcrPull propagation before retrying."
            Start-Sleep -Seconds 60
        }
    }

    $outputs = $deployment.properties.outputs
    Write-Output "Container app : $($outputs.containerAppName.value)"
    Write-Output "HTTPS URL     : $($outputs.url.value)"
    Write-Output "Image         : $image"
    Write-Output "Database app  : $databaseAppName"
}
finally {
    $secretCommandOutput = $null
    $databasePassword = $null
    $oracleConnectionString = $null
    $deploymentParameters = $null
    foreach ($file in @($compiledParameterFile, $deploymentParameterFile)) {
        if (Test-Path -LiteralPath $file) {
            try {
                Remove-Item -LiteralPath $file -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Could not remove temporary parameter file '$file': $($_.Exception.Message)"
            }
        }
    }
}
