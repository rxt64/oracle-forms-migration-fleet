#requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PrepareMedia', 'ProbeInstaller', 'AttemptInstall', 'VerifyInstallation')]
    [string] $Operation,

    [ValidatePattern('^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
    [string] $SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',

    [ValidatePattern('^[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
    [string] $TenantId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86',

    [string] $ResourceGroupName = 'rg-oracle-forms-migration-fleet-dev-b9f0e875',

    [string] $VmName = 'vm-ofm-forms6i-j6mrrerz',

    [string] $InstallerArguments = '',

    [ValidateRange(15, 1800)]
    [int] $InstallerTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$guestScriptPath = Join-Path $PSScriptRoot 'Invoke-Forms6iGuestInstall.ps1'
$retrievalScriptPath = Join-Path $PSScriptRoot 'Retrieve-Forms6iGuestEvidence.ps1'
$converterPath = Join-Path $PSScriptRoot 'Convert-RawCdImage.ps1'
$foundationScriptPath = Join-Path $PSScriptRoot 'Deploy-Forms6iSourceLab.ps1'
$localEvidenceRoot = Join-Path $env:LOCALAPPDATA 'OracleFormsMigrationFleet\forms6i-source-lab\evidence'

function Resolve-AzureCli {
    $command = Get-Command az -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $knownPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
    if (Test-Path -LiteralPath $knownPath) { return $knownPath }
    throw 'Azure CLI was not found.'
}

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $output = & $script:azureCli @Arguments --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed ($Label): $($output -join [Environment]::NewLine)"
    }
    return $output | ConvertFrom-Json -Depth 100
}

function Initialize-ProtectedEvidenceDirectory {
    [void](New-Item -ItemType Directory -Path $localEvidenceRoot -Force)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $localEvidenceRoot '/inheritance:r' '/grant:r' "*$($identity.Value):(OI)(CI)F" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to protect the local Forms 6i evidence directory.'
    }
}

function Write-LocalEvidence {
    param([Parameter(Mandatory)] [object] $Value)

    Initialize-ProtectedEvidenceDirectory
    $path = Join-Path $localEvidenceRoot "install-$($Operation.ToLowerInvariant())-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $path '/inheritance:r' '/grant:r' "*$($identity.Value):F" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to protect the local Forms 6i evidence file.'
    }
    return $path
}

function New-ProtectedScriptSnapshot {
    param([Parameter(Mandatory)] [string[]] $Paths)
    Initialize-ProtectedEvidenceDirectory
    $root = Join-Path $localEvidenceRoot "script-snapshot-$([Guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory -Path $root -Force)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $root '/inheritance:r' '/grant:r' "*$($identity.Value):(OI)(CI)F" *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to protect the installer script snapshot.' }
    $result = [ordered]@{}
    foreach ($path in $Paths) {
        $destination = Join-Path $root (Split-Path $path -Leaf)
        [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($path))
        $result[(Split-Path $path -Leaf)] = [pscustomobject]@{ path = $destination; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
    }
    return [pscustomobject]$result
}

function Receive-GuestEvidence {
    param([string] $GuestPath, [string] $ExpectedHash, [string] $RetrievalScript)
    $base = @('vm', 'run-command', 'invoke', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName, '--name', $VmName, '--command-id', 'RunPowerShellScript', '--scripts', "@$RetrievalScript", '--parameters')
    $metadataResult = Invoke-AzJson -Label 'read guest evidence metadata' -Arguments ($base + @('Operation=Metadata', "EvidencePath=$GuestPath"))
    $metadataText = @($metadataResult.value | ForEach-Object message) -join "`n"
    $match = [regex]::Match($metadataText, 'FORMS6I_EVIDENCE_METADATA=([^\r\n]+)')
    if (-not $match.Success) { throw 'Guest evidence metadata was not returned.' }
    $metadata = $match.Groups[1].Value | ConvertFrom-Json
    if (-not [string]::Equals($metadata.sha256, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Guest evidence changed before retrieval.' }
    $bytes = [byte[]]::new([int]$metadata.bytes)
    for ($offset = 0; $offset -lt $bytes.Length; $offset += 1800) {
        $length = [Math]::Min(1800, $bytes.Length - $offset)
        $chunkResult = Invoke-AzJson -Label "read guest evidence chunk $offset" -Arguments ($base + @('Operation=Chunk', "EvidencePath=$GuestPath", "Offset=$offset", "Length=$length"))
        $chunkText = @($chunkResult.value | ForEach-Object message) -join "`n"
        $chunkMatch = [regex]::Match($chunkText, 'FORMS6I_EVIDENCE_CHUNK=([^\r\n]+)')
        if (-not $chunkMatch.Success) { throw "Guest evidence chunk $offset was not returned." }
        $chunk = [Convert]::FromBase64String($chunkMatch.Groups[1].Value)
        [Array]::Copy($chunk, 0, $bytes, $offset, $chunk.Length)
    }
    $path = Join-Path $localEvidenceRoot (Split-Path $GuestPath -Leaf)
    [IO.File]::WriteAllBytes($path, $bytes)
    if (-not [string]::Equals((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Reassembled guest evidence failed SHA-256 verification.' }
    return $path
}

foreach ($path in @($guestScriptPath, $retrievalScriptPath, $converterPath, $foundationScriptPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required script is missing: $path"
    }
}
$snapshots = New-ProtectedScriptSnapshot -Paths @($guestScriptPath, $retrievalScriptPath, $converterPath)
$foundationHash = (Get-FileHash -LiteralPath $foundationScriptPath -Algorithm SHA256).Hash
$script:azureCli = Resolve-AzureCli
$account = Invoke-AzJson -Label 'read Azure account' -Arguments @('account', 'show')
if (-not [string]::Equals([string]$account.id, $SubscriptionId, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$account.tenantId, $TenantId, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The active Azure CLI account does not match the pinned subscription and tenant.'
}

if ((Get-FileHash -LiteralPath $foundationScriptPath -Algorithm SHA256).Hash -ne $foundationHash) { throw 'The foundation verification script changed before execution.' }
$foundationOutput = & $foundationScriptPath -Operation Verify -SubscriptionId $SubscriptionId -TenantId $TenantId -ResourceGroupName $ResourceGroupName 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Forms 6i foundation verification failed: $($foundationOutput -join [Environment]::NewLine)"
}

$vm = Invoke-AzJson -Label 'read exact Forms 6i VM' -Arguments @(
    'vm', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroupName,
    '--name', $VmName, '--show-details'
)
$requestedVmId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Compute/virtualMachines/$VmName"
$verifiedFoundations = @($foundationOutput | Where-Object { $_ -is [System.Collections.IDictionary] -and $_.Contains('vmResourceId') })
if ($verifiedFoundations.Count -ne 1 -or
    -not [string]::Equals([string]$verifiedFoundations[0].vmResourceId, $requestedVmId, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installation target differs from the VM whose foundation isolation was verified.'
}
if (-not [string]::Equals([string]$vm.id, $requestedVmId, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$vm.name, $VmName, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$vm.location, 'eastus2', [StringComparison]::OrdinalIgnoreCase) -or
    $null -ne $vm.identity) {
    throw 'The target VM name, location, or no-identity boundary does not match the authorized source lab.'
}

$converterBytes = [IO.File]::ReadAllBytes($snapshots.'Convert-RawCdImage.ps1'.path)
$converterSourceBase64 = [Convert]::ToBase64String($converterBytes)
$parameters = @(
    "Operation=$Operation",
    "ConverterSourceBase64=$converterSourceBase64",
    "InstallerTimeoutSeconds=$InstallerTimeoutSeconds"
)
if (-not [string]::IsNullOrWhiteSpace($InstallerArguments)) {
    $parameters += "InstallerArguments=$InstallerArguments"
}

$runCommandArguments = @(
    'vm', 'run-command', 'invoke',
    '--subscription', $SubscriptionId,
    '--resource-group', $ResourceGroupName,
    '--name', $VmName,
    '--command-id', 'RunPowerShellScript',
    '--scripts', "@$($snapshots.'Invoke-Forms6iGuestInstall.ps1'.path)",
    '--parameters'
) + $parameters
$runCommand = Invoke-AzJson -Label "run guest operation $Operation" -Arguments $runCommandArguments
$messages = @($runCommand.value | ForEach-Object { [string]$_.message })
$messageText = $messages -join "`n"
$pathMatch = [regex]::Match($messageText, 'FORMS6I_EVIDENCE_PATH=([^\r\n]+)')
$hashMatch = [regex]::Match($messageText, 'FORMS6I_EVIDENCE_SHA256=([0-9A-Fa-f]{64})')
if (-not $pathMatch.Success -or -not $hashMatch.Success) { throw 'Guest operation did not return retrievable evidence identity.' }
$retrievedGuestEvidencePath = Receive-GuestEvidence -GuestPath $pathMatch.Groups[1].Value -ExpectedHash $hashMatch.Groups[1].Value -RetrievalScript $snapshots.'Retrieve-Forms6iGuestEvidence.ps1'.path
$evidence = [ordered]@{
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    operation = $Operation
    subscriptionId = $SubscriptionId
    tenantId = $TenantId
    resourceGroup = $ResourceGroupName
    vmName = $VmName
    requestedVmId = $requestedVmId
    vmId = $vm.id
    vmLocation = $vm.location
    vmProvisioningState = $vm.provisioningState
    vmPowerState = $vm.powerState
    managedIdentity = $null
    guestScriptSha256 = $snapshots.'Invoke-Forms6iGuestInstall.ps1'.sha256
    retrievalScriptSha256 = $snapshots.'Retrieve-Forms6iGuestEvidence.ps1'.sha256
    converterSha256 = $snapshots.'Convert-RawCdImage.ps1'.sha256
    foundationScriptSha256 = $foundationHash
    installerArguments = $InstallerArguments
    installerTimeoutSeconds = $InstallerTimeoutSeconds
    foundationVerificationOutput = @($foundationOutput | ForEach-Object { [string]$_ })
    guestEvidencePath = $pathMatch.Groups[1].Value
    guestEvidenceSha256 = $hashMatch.Groups[1].Value
    retrievedGuestEvidencePath = $retrievedGuestEvidencePath
    runCommand = $runCommand
}
$evidencePath = Write-LocalEvidence -Value $evidence
Write-Output "Local evidence: $evidencePath"
$messages | ForEach-Object { Write-Output $_ }