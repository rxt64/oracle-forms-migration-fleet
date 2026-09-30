[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedMainCommitSha,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $TrustedCiRunId,

    [Parameter(Mandatory)]
    [guid] $GatewayApplicationClientId,

    [string] $Repository = 'rxt64/oracle-forms-migration-fleet',
    [string] $RegistrySourcePath = (Join-Path $PSScriptRoot 'source-registry.json'),
    [string] $InstallRoot = 'C:\Program Files\OracleFormsMigrationFleet\SourceGateway',
    [string] $ProgramDataRoot = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway',
    [string] $InputRoot = 'C:\OracleForms6iSourceLab\app\generated',
    [string] $OutputRoot = 'C:\OracleForms6iSourceLab\gateway\output',
    [string] $FormsHome = 'C:\orant',
    [string] $ListenerHost = 'gateway.ofm.source.internal',
    [string] $ListenerIp = '10.246.0.4',
    [string] $AllowedCallerClientId = 'f4492b02-ee64-4295-a1db-7c7677134e42',
    [string] $TenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'OFMSourceGateway'
$serviceSid = "NT SERVICE\$serviceName"
$serviceAccount = 'NT AUTHORITY\LocalService'
$expectedRepository = 'rxt64/oracle-forms-migration-fleet'
$expectedTenant = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
$expectedCaller = 'f4492b02-ee64-4295-a1db-7c7677134e42'
$workerName = 'OracleFormsMigrationFleet.SourceWorker.exe'
$artifactName = "source-worker-win-x86-$ExpectedMainCommitSha"
$registryDestination = Join-Path $InstallRoot 'source-registry.json'
$workerDestination = Join-Path $InstallRoot $workerName
$credentialRoot = Join-Path $ProgramDataRoot 'credentials'
$tempRoot = Join-Path $ProgramDataRoot 'temp'
$publicTrustRoot = Join-Path $ProgramDataRoot 'trust'
$certificateStatePath = Join-Path $publicTrustRoot 'certificate-state.json'
$rootCerPath = Join-Path $publicTrustRoot 'ofm-source-gateway-dev-root-2026.cer'
. (Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1')

if (-not [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent().IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this bootstrap from an elevated PowerShell session on the approved Forms VM.'
}
if ($Repository -cne $expectedRepository -or $TenantId -ne $expectedTenant -or $AllowedCallerClientId -ne $expectedCaller) {
    throw 'Repository, tenant, and caller identity are pinned to the delegated private-gateway scope.'
}
if (-not (Test-Path -LiteralPath $RegistrySourcePath -PathType Leaf)) {
    throw "Source registry not found: $RegistrySourcePath"
}

$registry = Get-Content -LiteralPath $RegistrySourcePath -Raw | ConvertFrom-Json
if ($registry.schemaVersion -ne 1 -or $registry.sources.Count -ne 1 -or
    $registry.sources[0].sourceEnvironmentId -cne 'meridian-native-6i' -or
    $registry.sources[0].authorizedTenantId -ne $expectedTenant -or
    $registry.sources[0].authorizedProjectIds.Count -ne 1 -or
    $registry.sources[0].authorizedProjectIds[0] -cne 'prj-d616e6e807e14b6bb5a468a33da3d744') {
    throw 'The source registry is not the approved single-source tenant/project binding.'
}

$gh = Get-Command gh -ErrorAction Stop
$run = & $gh.Source api "repos/$Repository/actions/runs/$TrustedCiRunId" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $run.name -cne 'CI' -or $run.path -cne '.github/workflows/ci.yml' -or
    $run.event -cne 'push' -or $run.head_branch -cne 'main' -or
    $run.head_sha -cne $ExpectedMainCommitSha -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
    throw 'The named GitHub Actions run is not a successful main-push CI run for the expected commit.'
}
$jobs = & $gh.Source api "repos/$Repository/actions/runs/$TrustedCiRunId/jobs?filter=latest&per_page=100" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $jobs.jobs) {
    throw 'The required job evidence for the named CI run could not be read.'
}
$requiredJobs = @('Native source worker contract', 'build-and-test', 'Container image builds', 'Guided UI browser checks')
foreach ($requiredJob in $requiredJobs) {
    $matches = @($jobs.jobs | Where-Object name -CEQ $requiredJob)
    if ($matches.Count -ne 1 -or $matches[0].status -cne 'completed' -or $matches[0].conclusion -cne 'success') {
        throw "Required CI job '$requiredJob' must appear exactly once and complete successfully."
    }
}

$downloadRoot = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-worker-$([guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    & $gh.Source run download $TrustedCiRunId --repo $Repository --name $artifactName --dir $downloadRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'The trusted worker artifact could not be downloaded.'
    }

    $manifestPath = Join-Path $downloadRoot 'manifest.json'
    $workerPath = Join-Path $downloadRoot $workerName
    $payload = @(Get-ChildItem -LiteralPath $downloadRoot -Force)
    $payloadNames = @($payload | ForEach-Object Name)
    if ($payload.Count -ne 2 -or @($payload | Where-Object { -not $_.PSIsContainer }).Count -ne 2 -or
        $payloadNames -cnotcontains 'manifest.json' -or $payloadNames -cnotcontains $workerName -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
        throw 'The artifact does not contain exactly the required worker and provenance manifest.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $worker = Get-Item -LiteralPath $workerPath
    $workerSha = (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.schemaVersion -ne 1 -or $manifest.repository -cne $expectedRepository -or
        $manifest.commit -cne $ExpectedMainCommitSha -or $manifest.ref -cne 'refs/heads/main' -or
        $manifest.event -cne 'push' -or $manifest.workflow -cne 'CI' -or
        [long]$manifest.runId -ne $TrustedCiRunId -or
        $manifest.runtimeIdentifier -cne 'win-x86' -or $manifest.fileName -cne $workerName -or
        [long]$manifest.length -ne $worker.Length -or $manifest.sha256 -cne $workerSha) {
        throw 'Worker provenance, runtime, length, or SHA-256 does not match the trusted main artifact.'
    }

    if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Install $serviceName from trusted main commit $ExpectedMainCommitSha")) {
        return
    }

    foreach ($directory in @($InstallRoot, $ProgramDataRoot, $credentialRoot, $tempRoot, $publicTrustRoot, $OutputRoot)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $rootSubject = 'CN=OFM Source Gateway Dev Root 2026'
    $leafSubject = "CN=$ListenerHost"
    if (Test-Path -LiteralPath $certificateStatePath -PathType Leaf) {
        $certificateState = Get-Content -LiteralPath $certificateStatePath -Raw | ConvertFrom-Json
        if ($certificateState.schemaVersion -ne 1 -or $certificateState.listenerHost -cne $ListenerHost -or
            $certificateState.rootThumbprint -notmatch '^[A-Fa-f0-9]{40,64}$' -or
            $certificateState.leafThumbprint -notmatch '^[A-Fa-f0-9]{40,64}$') {
            throw 'The persisted source-gateway certificate binding is malformed or belongs to another listener.'
        }
        $root = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($certificateState.rootThumbprint)" -ErrorAction SilentlyContinue
        $leaf = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($certificateState.leafThumbprint)" -ErrorAction SilentlyContinue
        if ($null -eq $root -or $null -eq $leaf -or -not $root.HasPrivateKey -or -not $leaf.HasPrivateKey -or
            $root.Subject -cne $rootSubject -or $leaf.Subject -cne $leafSubject -or $leaf.Issuer -cne $root.Subject -or
            $root.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow.AddDays(30) -or
            $leaf.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow.AddDays(30)) {
            throw 'The persisted source-gateway certificate binding is missing, expired, mismatched, or lacks its non-exportable private key. Explicit certificate rotation is required.'
        }
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $boundRootSha = -join ($sha256.ComputeHash($root.RawData) | ForEach-Object { $_.ToString('X2') })
        }
        finally {
            $sha256.Dispose()
        }
        if (-not (Test-Path -LiteralPath $rootCerPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $rootCerPath -Algorithm SHA256).Hash -cne $boundRootSha) {
            throw 'The persisted public root CER is missing or no longer matches the bound root certificate.'
        }
    } else {
        $unmanaged = Get-ChildItem -Path 'Cert:\LocalMachine\My' |
            Where-Object { $_.Subject -in @($rootSubject, $leafSubject) }
        if (@($unmanaged).Count -gt 0) {
            throw 'Matching source-gateway certificates already exist without a persisted binding. Refusing to issue replacement trust automatically.'
        }
        $root = New-SelfSignedCertificate -Type Custom -Subject $rootSubject `
            -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 `
            -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage CertSign, CrlSign `
            -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
        $leaf = New-SelfSignedCertificate -Type Custom -Subject $leafSubject -DnsName $ListenerHost `
            -Signer $root -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 `
            -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage DigitalSignature, KeyEncipherment `
            -NotAfter (Get-Date).AddDays(397) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        Export-Certificate -Cert $root -FilePath $rootCerPath -Type CERT | Out-Null
        [ordered]@{
            schemaVersion = 1
            listenerHost = $ListenerHost
            rootThumbprint = $root.Thumbprint
            leafThumbprint = $leaf.Thumbprint
        } | ConvertTo-Json | Set-Content -LiteralPath $certificateStatePath -Encoding utf8
    }

    if ($null -eq (Get-Item -LiteralPath "Cert:\LocalMachine\Root\$($root.Thumbprint)" -ErrorAction SilentlyContinue)) {
        Import-Certificate -FilePath $rootCerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceWasRunning = $null -ne $existingService -and $existingService.Status -ne 'Stopped'
    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $previousServiceEnvironment = if ($null -ne $existingService) {
        (Get-ItemProperty -Path $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue).Environment
    } else { $null }
    $workerBackup = Join-Path $InstallRoot "$workerName.previous-$([guid]::NewGuid().ToString('N'))"
    $registryBackup = Join-Path $InstallRoot "source-registry.previous-$([guid]::NewGuid().ToString('N')).json"
    $hadWorker = Test-Path -LiteralPath $workerDestination -PathType Leaf
    $hadRegistry = Test-Path -LiteralPath $registryDestination -PathType Leaf
    if ($hadWorker) { Copy-Item -LiteralPath $workerDestination -Destination $workerBackup }
    if ($hadRegistry) { Copy-Item -LiteralPath $registryDestination -Destination $registryBackup }
    $stagedWorker = Join-Path $InstallRoot "$workerName.new"
    Copy-Item -LiteralPath $workerPath -Destination $stagedWorker -Force
    if ((Get-FileHash -LiteralPath $stagedWorker -Algorithm SHA256).Hash.ToLowerInvariant() -cne $workerSha) {
        throw 'The staged worker changed after trusted artifact verification.'
    }
    $serviceCreated = $false
    $firewallCreated = $false
    try {
        if ($serviceWasRunning) {
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        Move-Item -LiteralPath $stagedWorker -Destination $workerDestination -Force
        Copy-Item -LiteralPath $RegistrySourcePath -Destination $registryDestination -Force

        $binaryPath = ('"{0}" --serve' -f $workerDestination)
        if ($null -eq $existingService) {
            Invoke-ScChecked -Arguments @('create', $serviceName, "binPath= $binaryPath", 'start= delayed-auto', "obj= $serviceAccount") `
                -FailureMessage "Service Control Manager could not create $serviceName"
            $serviceCreated = $true
        } else {
            Invoke-ScChecked -Arguments @('config', $serviceName, "binPath= $binaryPath", 'start= delayed-auto', "obj= $serviceAccount") `
                -FailureMessage "Service Control Manager could not configure $serviceName"
        }
        Invoke-ScChecked -Arguments @('sidtype', $serviceName, 'restricted') -FailureMessage "Could not restrict the service SID for $serviceName"
        Set-RestrictedPathAcl -Path $ProgramDataRoot -Grants @('SYSTEM:(OI)(CI)(F)', 'Administrators:(OI)(CI)(F)', "$serviceAccount`:(OI)(CI)(RX)", "$serviceSid`:(OI)(CI)(RX)")
        Set-RestrictedPathAcl -Path $credentialRoot -Grants @('SYSTEM:(OI)(CI)(F)', 'Administrators:(OI)(CI)(F)', "$serviceAccount`:(OI)(CI)(M)", "$serviceSid`:(OI)(CI)(M)")
        Set-RestrictedPathAcl -Path $tempRoot -Grants @('SYSTEM:(OI)(CI)(F)', 'Administrators:(OI)(CI)(F)', "$serviceAccount`:(OI)(CI)(M)", "$serviceSid`:(OI)(CI)(M)")
        Set-RestrictedPathAcl -Path $OutputRoot -Grants @('SYSTEM:(OI)(CI)(F)', 'Administrators:(OI)(CI)(F)', "$serviceAccount`:(OI)(CI)(M)", "$serviceSid`:(OI)(CI)(M)")
        Grant-PathAccess -Path $InstallRoot -Identity $serviceAccount -Rights '(OI)(CI)(RX)'
        Grant-PathAccess -Path $InstallRoot -Identity $serviceSid -Rights '(OI)(CI)(RX)'
        Grant-PathAccess -Path $InputRoot -Identity $serviceAccount -Rights '(OI)(CI)(RX)'
        Grant-PathAccess -Path $InputRoot -Identity $serviceSid -Rights '(OI)(CI)(RX)'
        Grant-PathAccess -Path $FormsHome -Identity $serviceAccount -Rights '(OI)(CI)(RX)'
        Grant-PathAccess -Path $FormsHome -Identity $serviceSid -Rights '(OI)(CI)(RX)'
        Invoke-ScChecked -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') `
            -FailureMessage "Could not configure recovery actions for $serviceName"
        Invoke-ScChecked -Arguments @('failureflag', $serviceName, '1') -FailureMessage "Could not enable recovery actions for $serviceName"

        $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($leaf)
        try {
            if ($privateKey -isnot [Security.Cryptography.RSACng]) {
                throw 'The gateway leaf private key is not in the expected Windows CNG key store.'
            }
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($privateKey.Key.UniqueName)"
            Grant-PathAccess -Path $keyPath -Identity $serviceAccount -Rights '(R)'
            Grant-PathAccess -Path $keyPath -Identity $serviceSid -Rights '(R)'
        }
        finally {
            $privateKey.Dispose()
        }

        $serviceEnvironment = @(
        "OFM_GATEWAY_URL=https://${ListenerHost}:443",
        "OFM_GATEWAY_TENANT_ID=$TenantId",
        "OFM_GATEWAY_AUDIENCE=api://$($GatewayApplicationClientId.Guid)",
        "OFM_GATEWAY_CALLER_APP_IDS=$AllowedCallerClientId",
        "OFM_GATEWAY_SOURCE_REGISTRY=$registryDestination",
        "OFM_GATEWAY_TLS_CERTIFICATE_THUMBPRINT=$($leaf.Thumbprint)",
        "OFM_GATEWAY_TLS_CERTIFICATE_SUBJECT=$ListenerHost",
        "OFM_GATEWAY_PROTECTED_CREDENTIAL_ROOT=$credentialRoot",
        'OFM_GATEWAY_MAX_CONCURRENT_EXTRACTIONS=1',
        'OFM_GATEWAY_EXTRACTION_TIMEOUT_SECONDS=300',
        'OFM_GATEWAY_MAX_WORKER_OUTPUT_BYTES=1048576',
        'OFM_GATEWAY_MAX_SCHEMA_OUTPUT_BYTES=25165824',
        "TEMP=$tempRoot",
        "TMP=$tempRoot",
        'ASPNETCORE_ENVIRONMENT=Production'
        )
        New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString `
            -Value $serviceEnvironment -Force | Out-Null

        Start-Service -Name $serviceName -ErrorAction Stop
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

        $firewallRule = Get-NetFirewallRule -DisplayName 'OFM Source Gateway HTTPS' -ErrorAction SilentlyContinue
        if ($null -eq $firewallRule) {
            New-NetFirewallRule -DisplayName 'OFM Source Gateway HTTPS' -Direction Inbound -Action Allow `
                -Protocol TCP -LocalAddress $ListenerIp -LocalPort 443 -RemoteAddress '10.246.0.64/27' `
                -Profile Domain, Private -EdgeTraversalPolicy Block -ErrorAction Stop | Out-Null
            $firewallCreated = $true
        } else {
            Set-NetFirewallRule -InputObject $firewallRule -Enabled True -Direction Inbound -Action Allow `
                -Profile Domain, Private -EdgeTraversalPolicy Block -ErrorAction Stop | Out-Null
            Get-NetFirewallPortFilter -AssociatedNetFirewallRule $firewallRule |
                Set-NetFirewallPortFilter -Protocol TCP -LocalPort 443 -RemotePort Any -ErrorAction Stop | Out-Null
            Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $firewallRule |
                Set-NetFirewallAddressFilter -LocalAddress $ListenerIp -RemoteAddress '10.246.0.64/27' -ErrorAction Stop | Out-Null
        }
    }
    catch {
        $installationError = $_
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        if ($hadWorker) { Copy-Item -LiteralPath $workerBackup -Destination $workerDestination -Force }
        else { Remove-Item -LiteralPath $workerDestination -Force -ErrorAction SilentlyContinue }
        if ($hadRegistry) { Copy-Item -LiteralPath $registryBackup -Destination $registryDestination -Force }
        else { Remove-Item -LiteralPath $registryDestination -Force -ErrorAction SilentlyContinue }
        if ($null -ne $existingService) {
            if ($null -eq $previousServiceEnvironment) {
                Remove-ItemProperty -Path $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
            } else {
                New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString `
                    -Value $previousServiceEnvironment -Force | Out-Null
            }
            if ($serviceWasRunning) { Start-Service -Name $serviceName -ErrorAction Stop }
        } elseif ($serviceCreated) {
            Invoke-ScChecked -Arguments @('delete', $serviceName) -FailureMessage "Could not remove failed new service $serviceName"
        }
        if ($firewallCreated) {
            Remove-NetFirewallRule -DisplayName 'OFM Source Gateway HTTPS' -ErrorAction SilentlyContinue
        }
        throw $installationError
    }
    finally {
        Remove-Item -LiteralPath $stagedWorker, $workerBackup, $registryBackup -Force -ErrorAction SilentlyContinue
    }

    $rootSha = (Get-FileHash -LiteralPath $rootCerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "Installed $serviceName from trusted commit $ExpectedMainCommitSha and CI run $TrustedCiRunId."
    Write-Output "Public root certificate: $rootCerPath"
    Write-Output "Public root certificate SHA-256: $rootSha"
    Write-Output 'The public root CER must be committed and installed by a later trusted workbench image build before the private app is released.'
    Write-Output 'No Oracle credential was requested, read, logged, or stored by this installer.'
}
finally {
    Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
}