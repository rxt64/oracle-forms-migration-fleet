#requires -Version 7.2

<#
.SYNOPSIS
    Deploys the disposable demo legacy Oracle estate.

.DESCRIPTION
    Builds an Oracle Database Free image seeded with an independently designed implementation of the
    public banking case-study requirements, then deploys it to the existing Container Apps environment
    behind an internal-only TCP listener.

    The database is deliberately disposable. Container Apps replicas have ephemeral storage, so a
    restarted replica re-runs the seed scripts and returns to a known state. Nothing of value should
    ever be written to it.

.EXAMPLE
    .\Deploy-LegacyEstate.ps1 -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,

    [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,127}$')]
    [string] $ImageTag = (Get-Date -Format 'yyyyMMddHHmmss'),

    [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,127}$')]
    [string] $BaseImageTag = '23-slim-faststart'
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'
$oracleContext = Join-Path $PSScriptRoot 'oracle'
$compiledParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "legacy-estate-$([Guid]::NewGuid().ToString('N')).parameters.json"
$deploymentParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "legacy-estate-$([Guid]::NewGuid().ToString('N')).deployment.parameters.json"
$deploymentName = 'oracle-forms-legacy-estate'
$imageRepository = 'oracle-forms-legacy-db'
$baseRepository = 'oracle-free'
$upstreamImage = "docker.io/gvenzl/oracle-free:$BaseImageTag"
$oracleSysPassword = $null
$bankingSchemaPassword = $null
$deploymentParameters = $null

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $result = & az @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments[0..1] -join ' '): $($result -join [Environment]::NewLine)"
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
            throw "Failed to restrict permissions on $Path."
        }
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function New-DatabasePassword {
    # Letters and digits only. Oracle rejects some punctuation outright, and the rest would have to
    # survive SQL*Plus, a shell and an ARM parameter for no additional entropy worth having.
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $characters = foreach ($byte in $bytes) { $alphabet[$byte % $alphabet.Length] }

    # Oracle will not accept a password that starts with a digit.
    return 'Db' + (-join $characters)
}

try {
    Write-Output 'Compiling deployment parameters...'
    & az bicep build-params --file $parameterFile --outfile $compiledParameterFile --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Compiling the Bicep parameter file failed.'
    }

    $deploymentParameters = Get-Content -LiteralPath $compiledParameterFile -Raw | ConvertFrom-Json -Depth 100
    $registryName = $deploymentParameters.parameters.containerRegistryName.value
    if ([string]::IsNullOrWhiteSpace($registryName)) {
        throw "Parameter 'containerRegistryName' is missing from '$parameterFile'."
    }

    Write-Output 'Resolving the container registry...'
    $registry = Invoke-AzJson -Arguments @('acr', 'show', '--resource-group', $ResourceGroupName, '--name', $registryName)
    $image = "$($registry.loginServer)/$imageRepository`:$ImageTag"
    $baseImage = "$($registry.loginServer)/$baseRepository`:$BaseImageTag"
    $repositories = @(Invoke-AzJson -Arguments @('acr', 'repository', 'list', '--name', $registryName))

    $existingTags = @()
    if ($repositories -contains $baseRepository) {
        $existingTags = @(Invoke-AzJson -Arguments @(
            'acr', 'repository', 'show-tags',
            '--name', $registryName,
            '--repository', $baseRepository
        ))
    }

    if ($existingTags -notcontains $BaseImageTag) {
        # Imported rather than pulled at build time so the Oracle base is served from a registry this
        # subscription controls, and so a Docker Hub rate limit cannot break a deployment later.
        Write-Output "Importing $upstreamImage into $registryName..."
        & az acr import `
            --name $registryName `
            --source $upstreamImage `
            --image "$baseRepository`:$BaseImageTag" `
            --only-show-errors
        if ($LASTEXITCODE -ne 0) {
            throw "Importing the Oracle base image '$upstreamImage' failed."
        }
    }
    else {
        Write-Output "Base image $baseImage is already present."
    }

    $imageTags = @()
    if ($repositories -contains $imageRepository) {
        $imageTags = @(Invoke-AzJson -Arguments @(
            'acr', 'repository', 'show-tags',
            '--name', $registryName,
            '--repository', $imageRepository
        ))
    }
    if ($imageTags -contains $ImageTag) {
        throw "Image '$image' already exists. Use a new immutable tag so password rotation creates a fresh database revision."
    }

    Write-Output 'Building the seeded Oracle image in Azure Container Registry...'
    # Log streaming is disabled because the Azure CLI streamer writes through colorama on Windows and
    # dies on the first non-ASCII byte the build emits. --no-logs polls instead, but it also exits 0
    # for a failed run, so the status has to be asserted explicitly.
    $buildRun = Invoke-AzJson -Arguments @(
        'acr', 'build',
        '--registry', $registryName,
        '--image', "$imageRepository`:$ImageTag",
        '--file', (Join-Path $oracleContext 'Dockerfile'),
        '--build-arg', "BASE_IMAGE=$baseImage",
        $oracleContext,
        '--no-logs'
    )
    if ($buildRun.status -ne 'Succeeded') {
        throw "Azure Container Registry build failed with status '$($buildRun.status)'. Inspect the logs with: az acr task logs --registry $registryName --run-id $($buildRun.runId)"
    }

    $oracleSysPassword = New-DatabasePassword
    $bankingSchemaPassword = New-DatabasePassword

    Set-ArmParameter -Document $deploymentParameters -Name 'oracleImage' -Value $image
    Set-ArmParameter -Document $deploymentParameters -Name 'oracleSysPassword' -Value $oracleSysPassword
    Set-ArmParameter -Document $deploymentParameters -Name 'bankingSchemaPassword' -Value $bankingSchemaPassword
    Write-CurrentUserOnlyFile -Path $deploymentParameterFile -Content ($deploymentParameters | ConvertTo-Json -Depth 100)

    Write-Output 'Deploying the legacy estate...'
    # The container app and the AcrPull assignment that lets it pull the image are created by this
    # one deployment. ARM orders them correctly, but a brand new role assignment can take a minute
    # or two to become visible, and retrying is the documented way through that race.
    $deployment = $null
    $maxAttempts = 4
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $deployment = Invoke-AzJson -Arguments @(
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

            Write-Output "  Deployment attempt $attempt failed. Role assignments may still be propagating; retrying in 60 seconds..."
            Start-Sleep -Seconds 60
        }
    }

    $outputs = $deployment.properties.outputs
    Write-Output ''
    Write-Output 'Legacy estate deployed.'
    Write-Output "  Container app : $($outputs.containerAppName.value)"
    Write-Output "  Internal host : $($outputs.internalFqdn.value)"
    Write-Output "  Image         : $image"
    Write-Output ''
    Write-Output 'The listener is internal to the Container Apps environment and is not reachable from the internet.'
    Write-Output 'Passwords were generated here, handed straight to Azure, and never printed. Read them back with:'
    Write-Output "  az containerapp secret show -g $ResourceGroupName -n $($outputs.containerAppName.value) --secret-name oracle-sys-password --query value -o tsv"
    Write-Output ''
    Write-Output 'Open a SQL*Plus session inside the running container with:'
    Write-Output "  az containerapp exec -g $ResourceGroupName -n $($outputs.containerAppName.value) --command ""sqlplus BANKING/`$APP_USER_PASSWORD@localhost/FREEPDB1"""
    Write-Output ''
    Write-Output 'Oracle takes a few minutes to finish seeding on a new revision. Watch progress with:'
    Write-Output "  az containerapp logs show -g $ResourceGroupName -n $($outputs.containerAppName.value) --follow"
    Write-Output ''
    Write-Output 'This estate bills for a permanently running 2 vCPU replica. Stop it when you are not demonstrating:'
    Write-Output "  az containerapp update -g $ResourceGroupName -n $($outputs.containerAppName.value) --min-replicas 0 --max-replicas 1"
}
finally {
    $oracleSysPassword = $null
    $bankingSchemaPassword = $null
    $deploymentParameters = $null
    foreach ($file in @($compiledParameterFile, $deploymentParameterFile)) {
        if (Test-Path -LiteralPath $file) {
            try {
                Remove-Item -LiteralPath $file -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Could not remove restricted temporary parameter file '$file': $($_.Exception.Message)"
            }
        }
    }
}
