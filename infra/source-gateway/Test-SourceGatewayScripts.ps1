[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$common = Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1'
. $common

$script:mockExitCode = 0
function sc.exe { $global:LASTEXITCODE = $script:mockExitCode }
function icacls.exe { $global:LASTEXITCODE = $script:mockExitCode }

$script:mockExitCode = 5
$threw = $false
try { Invoke-ScChecked -Arguments @('query', 'mock') -FailureMessage 'mock sc failure' } catch { $threw = $true }
if (-not $threw) { throw 'Invoke-ScChecked accepted a nonzero mocked exit code.' }

$threw = $false
try { Invoke-IcaclsChecked -Arguments @('mock') -FailureMessage 'mock ACL failure' } catch { $threw = $true }
if (-not $threw) { throw 'Invoke-IcaclsChecked accepted a nonzero mocked exit code.' }

$script:mockExitCode = 0
Invoke-ScChecked -Arguments @('query', 'mock') -FailureMessage 'unexpected mock sc failure'
Invoke-IcaclsChecked -Arguments @('mock') -FailureMessage 'unexpected mock ACL failure'

if ($IsWindows) {
    $aclTestRoot = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-acl-$([guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path $aclTestRoot | Out-Null
        $permissiveAcl = Get-Acl -LiteralPath $aclTestRoot
        $inheritance = [Security.AccessControl.InheritanceFlags]'ObjectInherit, ContainerInherit'
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $permissiveAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-1-0'),
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow))
        $permissiveAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),
            [Security.AccessControl.FileSystemRights]::Modify,
            $inheritance,
            $propagation,
            $allow))
        Set-Acl -LiteralPath $aclTestRoot -AclObject $permissiveAcl

        $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        Set-RestrictedPathAcl -Path $aclTestRoot -TrustedOwnerSid $currentSid -Grants @(
            "$currentSid`:(OI)(CI)(F)"
        )

        $restrictedAcl = Get-Acl -LiteralPath $aclTestRoot
        $explicitRules = @($restrictedAcl.Access | Where-Object { -not $_.IsInherited })
        $ownerSid = $restrictedAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $ruleSid = $explicitRules[0].IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $restrictedAcl.AreAccessRulesProtected -or
            $ownerSid -ne $currentSid -or
            $explicitRules.Count -ne 1 -or
            $ruleSid -ne $currentSid -or
            $explicitRules[0].AccessControlType -ne $allow -or
            $explicitRules[0].FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Set-RestrictedPathAcl did not replace a permissive reused directory with the exact owner-bound allowlist.'
        }
    }
    finally {
        Remove-Item -LiteralPath $aclTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$cleanupCalls = [Collections.Generic.List[object]]::new()
& {
    function Remove-Item {
        param([string] $LiteralPath, [switch] $DeleteKey, [switch] $Force, [object] $ErrorAction)
        $cleanupCalls.Add([pscustomobject]@{ Path = $LiteralPath; DeleteKey = $DeleteKey.IsPresent })
    }
    function Test-Path { param([string] $LiteralPath, [object] $PathType); return $false }
    Remove-TransferCertificateAndKey -CertificatePath 'Cert:\mock' -PrivateKeyPath 'C:\mock-key'
}
if ($cleanupCalls.Count -ne 1 -or $cleanupCalls[0].Path -cne 'Cert:\mock' -or -not $cleanupCalls[0].DeleteKey) {
    throw 'Transfer certificate cleanup must request private-key deletion from the certificate provider.'
}

$cleanupFailure = & {
    function Remove-Item { throw 'mock certificate deletion failure' }
    function Test-Path { param([string] $LiteralPath, [object] $PathType); return $false }
    try { Remove-TransferCertificateAndKey -CertificatePath 'Cert:\mock' -PrivateKeyPath 'C:\mock-key' } catch { $_ }
}
if ($null -eq $cleanupFailure -or $cleanupFailure.Exception.Message -notmatch 'not fully removed') {
    throw 'Transfer certificate cleanup suppressed a certificate-provider deletion failure.'
}

$residualKeyFailure = & {
    function Remove-Item { param([string] $LiteralPath, [switch] $DeleteKey, [switch] $Force, [object] $ErrorAction) }
    function Test-Path {
        param([string] $LiteralPath, [object] $PathType)
        return $LiteralPath -ceq 'C:\mock-key'
    }
    try { Remove-TransferCertificateAndKey -CertificatePath 'Cert:\mock' -PrivateKeyPath 'C:\mock-key' } catch { $_ }
}
if ($null -eq $residualKeyFailure -or $residualKeyFailure.Exception.Message -notmatch 'not fully removed') {
    throw 'Transfer certificate cleanup accepted a residual private-key file.'
}

$keyCreation = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-SourceGatewayCredentialTransferKey.ps1') -Raw
$createCertificate = $keyCreation.IndexOf('$certificate = New-SelfSignedCertificate')
$publishComplete = $keyCreation.IndexOf('$creationComplete = $true')
$failedCreationCleanup = $keyCreation.IndexOf('Remove-TransferCertificateAndKey -CertificatePath $certificatePath')
if ($createCertificate -lt 0 -or $publishComplete -lt 0 -or $failedCreationCleanup -lt 0 -or
    $createCertificate -gt $publishComplete -or $publishComplete -gt $failedCreationCleanup -or
    $keyCreation -notmatch 'if \(-not \$creationComplete -and \$null -ne \$certificatePath\)' -or
    $keyCreation -notmatch 'if \(\$null -ne \$operationError\) \{ throw \$operationError \}') {
    throw 'Failed transfer-key creation must remove and verify the newly created certificate and CNG key before returning the original error.'
}

$installerPath = Join-Path $PSScriptRoot 'Install-SourceGateway.ps1'
$installer = Get-Content -LiteralPath $installerPath -Raw
$stop = $installer.IndexOf('Stop-Service -Name $serviceName')
$replace = $installer.IndexOf('Move-Item -LiteralPath $stagedWorker -Destination $workerDestination')
if ($stop -lt 0 -or $replace -lt 0 -or $stop -gt $replace) {
    throw 'The service must stop before the installed worker is replaced.'
}
if (($installer | Select-String -Pattern 'New-SelfSignedCertificate' -AllMatches).Matches.Count -ne 2 -or
    $installer -notmatch 'certificateStatePath' -or
    $installer -notmatch 'Refusing to issue replacement trust automatically') {
    throw 'The certificate rerun binding is incomplete.'
}
foreach ($requiredJob in @('Native source worker contract', 'build-and-test', 'Container image builds', 'Guided UI browser checks')) {
    if ($installer -notmatch [regex]::Escape($requiredJob)) {
        throw "The installer does not require CI job '$requiredJob'."
    }
}
if ($installer -notmatch 'payload\.Count -ne 2' -or
    $installer -notmatch "payloadNames -cnotcontains 'manifest\.json'" -or
    $installer -notmatch 'payloadNames -cnotcontains \$workerName') {
    throw 'The installer must reject extra or renamed trusted artifact payload files.'
}
if ($installer -match 'SHA256\]::HashData|Convert\]::ToHexString|Path\]::GetRelativePath') {
    throw 'The installer uses an API unavailable to Windows PowerShell 5.1 on the approved host.'
}
if ($installer -notmatch '\$serviceAccount`:\(OI\)\(CI\)\(M\)' -or
    $installer -notmatch '\$serviceSid`:\(OI\)\(CI\)\(M\)') {
    throw 'The protected credential root must admit LocalService provisioning and the restricted service SID.'
}
$sidType = $installer.IndexOf("@('sidtype', `$serviceName, 'restricted')")
$firstServiceSidAcl = $installer.IndexOf('Set-RestrictedPathAcl -Path $ProgramDataRoot')
if ($sidType -lt 0 -or $firstServiceSidAcl -lt 0 -or $sidType -gt $firstServiceSidAcl) {
    throw 'The virtual service SID must exist and be restricted before it is used in filesystem ACLs.'
}
$rollback = $installer.IndexOf('catch {')
if ($rollback -lt 0 -or $installer -notmatch 'Copy-Item -LiteralPath \$workerBackup -Destination \$workerDestination' -or
    $installer -notmatch 'previousServiceEnvironment') {
    throw 'The installer must restore prior service content and environment after a failed rerun.'
}
$completion = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Complete-SourceGatewayCredentialTransfer.ps1') -Raw
if ($completion -notmatch "serviceAccount = 'NT AUTHORITY\\LocalService'" -or
    $completion -notmatch 'Copy-Item.+SourceGatewayInstaller\.Common\.ps1') {
    throw 'The scheduled LocalService credential task must receive its checked-command helper.'
}
if ($completion -notmatch 'certificateCleanupRequired = \$true' -or
    $completion -notmatch 'finally \{\s*try \{\s*Remove-TransferCertificateAndKey' -or
    $completion -notmatch 'if \(\$null -ne \$operationError\) \{ throw \$operationError \}') {
    throw 'The privileged scheduling leg must check certificate and private-key cleanup before reporting success.'
}
$workflow = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.github/workflows/source-gateway-image.yml') -Raw
$dockerfile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Dockerfile.private-workbench') -Raw
if ($workflow -notmatch '(?m)^permissions:\s*$' -or
    $workflow -notmatch '(?m)^  actions: read\s*$' -or
    $workflow -notmatch '(?m)^  contents: read\s*$' -or
    $workflow -notmatch '(?m)^  id-token: write\s*$') {
    throw 'The private image workflow must grant read-only Actions API access for trusted-CI verification.'
}
if ($workflow -notmatch 'openssl x509 -inform DER' -or $workflow -notmatch 'openssl verify -CAfile' -or
    $dockerfile -notmatch 'ofm-source-gateway-dev-root-2026\.pem') {
    throw 'The Linux trust overlay must convert and verify the reviewed DER root before installing PEM.'
}
if ($workflow -notmatch 'schemaVersion:1' -or $workflow -notmatch 'sourceCommit:\$sourceCommit' -or
    $workflow -notmatch 'image:\$image' -or $workflow -notmatch 'source-gateway-image-\$\{\{ inputs\.commit_sha \}\}') {
    throw 'The private image workflow must retain commit- and digest-bound provenance.'
}
if ($workflow -notmatch 'EXPECTED_SUBSCRIPTION_ID: d4394e57-c076-4c92-a870-5de6bf44f255' -or
    $workflow -notmatch 'RELEASE_IMAGE.+!=.+BASE_IMAGE' -or
    $workflow -notmatch 'RELEASE_LATEST.+!=.+RELEASE_READY' -or
    $workflow -notmatch 'baseRevision:\$baseRevision') {
    throw 'The private image base must be bound to the pinned healthy latest-ready public release.'
}
$ciWorkflow = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.github/workflows/ci.yml') -Raw
if ($ciWorkflow -notmatch 'checkov==3\.3\.19' -or
    $ciWorkflow -notmatch 'checkov -d infra/source-gateway --framework bicep --quiet') {
    throw 'CI must enforce the pinned source-gateway Checkov gate.'
}

$parseErrors = @()
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
    $parseErrors += $errors
}
if ($parseErrors.Count -gt 0) {
    throw "Source-gateway PowerShell parsing failed: $($parseErrors.Message -join '; ')"
}

Write-Output 'SOURCE_GATEWAY_POWERSHELL_TESTS_PASS'