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

$trustedRun = [pscustomobject]@{
    id = 42
    name = 'CI'
    path = '.github/workflows/ci.yml'
    event = 'push'
    head_branch = 'main'
    head_sha = '0123456789abcdef0123456789abcdef01234567'
    status = 'completed'
    conclusion = 'success'
    repository = 'rxt64/oracle-forms-migration-fleet'
}
$trustedJobs = @(
    [pscustomobject]@{ name = 'Native source worker contract'; status = 'completed'; conclusion = 'success' },
    [pscustomobject]@{ name = 'build-and-test'; status = 'completed'; conclusion = 'success' },
    [pscustomobject]@{ name = 'Container image builds'; status = 'completed'; conclusion = 'success' },
    [pscustomobject]@{ name = 'Guided UI browser checks'; status = 'completed'; conclusion = 'success' }
)
Assert-SourceGatewayCiEvidence -Run $trustedRun -Jobs $trustedJobs `
    -ExpectedRepository 'rxt64/oracle-forms-migration-fleet' `
    -ExpectedCommitSha '0123456789abcdef0123456789abcdef01234567' -ExpectedRunId 42 -ExactRequiredJobs
$extraJobFailure = try {
    Assert-SourceGatewayCiEvidence -Run $trustedRun -Jobs @($trustedJobs + [pscustomobject]@{
            name = 'unexpected'; status = 'completed'; conclusion = 'success'
        }) -ExpectedRepository 'rxt64/oracle-forms-migration-fleet' `
        -ExpectedCommitSha '0123456789abcdef0123456789abcdef01234567' -ExpectedRunId 42 -ExactRequiredJobs
    $null
} catch { $_ }
if ($null -eq $extraJobFailure -or $extraJobFailure.Exception.Message -notmatch 'exactly the required job evidence') {
    throw 'Bundled CI evidence accepted a job outside the exact required allowlist.'
}

if ($IsWindows) {
    foreach ($guardScript in @('Install-SourceGateway.ps1', 'New-SourceGatewayCredentialTransferKey.ps1',
            'Complete-SourceGatewayCredentialTransfer.ps1')) {
        $guardTokens = $null
        $guardErrors = $null
        $guardAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot $guardScript), [ref]$guardTokens, [ref]$guardErrors)
        $adminGuard = $guardAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Clauses[0].Item1.Extent.Text -match 'WindowsPrincipal.*IsInRole'
        }, $true)
        if ($null -eq $adminGuard) { throw "The administrator guard is missing from $guardScript." }
        $guardResult = & ([scriptblock]::Create($adminGuard.Clauses[0].Item1.Extent.Text))
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        $expectedGuard = -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($guardResult -isnot [bool] -or $guardResult -ne $expectedGuard) {
            throw "The actual administrator guard returned an invalid decision in $guardScript."
        }
    }

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
    $commonText = Get-Content -LiteralPath $common -Raw
    if ($commonText -notmatch [regex]::Escape($requiredJob)) {
        throw "The shared installer evidence gate does not require CI job '$requiredJob'."
    }
}
if ($installer -notmatch 'payload\.Count -ne 2' -or
    $installer -notmatch "payloadNames -cnotcontains 'manifest\.json'" -or
    $installer -notmatch 'payloadNames -cnotcontains \$workerName') {
    throw 'The installer must reject extra or renamed trusted artifact payload files.'
}
if ($installer -notmatch "ParameterSetName = 'Offline'" -or
    $installer -notmatch 'Assert-SourceGatewayCiEvidence' -or
    $installer -notmatch '-ExpectedRunId \$TrustedCiRunId -ExactRequiredJobs' -or
    $installer -notmatch 'proof\.workerSha256 -cne \$workerSha' -or
    $installer -notmatch 'proof\.manifestSha256 -cne \$manifestSha') {
    throw 'The offline installer must reuse the exact CI gate and bind proof to both worker and manifest digests.'
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

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$deployWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/deploy-private-workbench.yml') -Raw
$ciExitGuardMatch = [regex]::Match(
    $deployWorkflow,
    '(?ms)^          if \(\$LASTEXITCODE -ne 0\) \{\r?\n              throw "Required CI verification failed with exit code \$LASTEXITCODE\."\r?\n          \}')
if (-not $ciExitGuardMatch.Success) {
    throw 'The private workbench workflow is missing the required-CI native exit guard.'
}
$ciExitGuard = [scriptblock]::Create($ciExitGuardMatch.Value.Trim())
$continuedAfterCiRejection = $false
$ciRejection = try {
    $PSNativeCommandUseErrorActionPreference = $false
    & (Get-Process -Id $PID).Path -NoProfile -Command 'exit 23'
    & $ciExitGuard
    $continuedAfterCiRejection = $true
    $null
} catch { $_ }
if ($null -eq $ciRejection -or $continuedAfterCiRejection -or
    $ciRejection.Exception.Message -ne 'Required CI verification failed with exit code 23.') {
    throw 'A nonzero required-CI verifier exit was allowed to continue to the next workflow command.'
}

$previewPath = Join-Path $PSScriptRoot 'Preview-SourceGateway.ps1'
$previewTokens = $null
$previewErrors = $null
$previewAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $previewPath, [ref]$previewTokens, [ref]$previewErrors)
if ($previewErrors.Count -ne 0) {
    throw "Preview script parsing failed: $($previewErrors.Message -join '; ')"
}
$resourceDifferenceAssignment = $previewAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$resourceIdDifferences'
}, $true)
$resourceScopeGuard = $previewAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match '\$resourceIdDifferences\.Count'
}, $true)
if ($null -eq $resourceDifferenceAssignment -or $null -eq $resourceScopeGuard) {
    throw 'The preview resource-scope comparison could not be loaded for runtime verification.'
}
$resourceScopeCheck = [scriptblock]::Create(
    "$($resourceDifferenceAssignment.Extent.Text)`n$($resourceScopeGuard.Extent.Text)")
$expectedResourceIds = @('/resource/a', '/resource/b')
$actualResourceIds = @('/resource/a', '/resource/b')
$resourceChanges = @(
    [pscustomobject]@{ resourceId = '/resource/a'; changeType = 'Create' }
    [pscustomobject]@{ resourceId = '/resource/b'; changeType = 'Create' }
)
$unexpectedChange = $null
& $resourceScopeCheck
$actualResourceIds = @('/resource/a', '/resource/c')
$scopeRejection = try { & $resourceScopeCheck; $null } catch { $_ }
if ($null -eq $scopeRejection -or
    $scopeRejection.Exception.Message -notmatch 'exceeded the approved create-only scope') {
    throw 'The preview resource-scope comparison accepted differing resource IDs.'
}

$installWorkflow = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.github/workflows/install-source-gateway.yml') -Raw
$bootstrap = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-SourceGatewayInstall.ps1') -Raw
$bootstrapTokens = $null
$bootstrapErrors = $null
$bootstrapAst = [System.Management.Automation.Language.Parser]::ParseInput(
    $bootstrap, [ref]$bootstrapTokens, [ref]$bootstrapErrors)
$lengthGuard = $bootstrapAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match 'ContentLength'
}, $true)
if ($null -eq $lengthGuard) { throw 'The guest download must check its Content-Length limit.' }
$lengthCheck = [scriptblock]::Create($lengthGuard.Extent.Text)
foreach ($testCase in @(
        @{ Length = $null; Refused = $false },
        @{ Length = [long]0; Refused = $false },
        @{ Length = [long]100; Refused = $false },
        @{ Length = [long]101; Refused = $true })) {
    $response = [pscustomobject]@{ Content = [pscustomobject]@{
        Headers = [pscustomobject]@{ ContentLength = $testCase.Length }
    } }
    $maximumBundleBytes = 100
    $lengthError = $null
    try { & $lengthCheck } catch { $lengthError = $_ }
    if (($null -ne $lengthError) -ne $testCase.Refused -or
        ($null -ne $lengthError -and $lengthError.Exception.Message -ne
            'The public release asset exceeds the transport size limit.')) {
        throw 'The guest Content-Length check mishandled an absent, unwrapped, or excessive header.'
    }
}
foreach ($binding in @(
        'd4394e57-c076-4c92-a870-5de6bf44f255',
        '1984d248-06ca-4d04-a3b8-4c0c1577ab86',
        'rg-oracle-forms-migration-fleet-dev-b9f0e875',
        'vm-ofm-forms6i-j6mrrerz',
        'b16b4127-9ef6-44a1-9f07-bbfe92053baf',
        'f4492b02-ee64-4295-a1db-7c7677134e42',
        '10.246.0.4')) {
    if ($installWorkflow -notmatch [regex]::Escape($binding)) {
        throw "The installation workflow is missing pinned scope binding '$binding'."
    }
}
if ($installWorkflow -notmatch '(?ms)^  publish:.*?permissions:\s*\n      actions: read\s*\n      contents: write' -or
    $installWorkflow -notmatch '(?ms)^  install:.*?permissions:\s*\n      actions: read\s*\n      contents: read\s*\n      id-token: write' -or
    $installWorkflow -notmatch 'gh release create' -or $installWorkflow -notmatch '--latest=false' -or
    $installWorkflow -notmatch 'env -u GH_TOKEN -u GITHUB_TOKEN curl' -or
    $installWorkflow -notmatch 'az vm run-command create' -or
    $installWorkflow -notmatch 'az vm run-command delete') {
    throw 'The installation workflow does not preserve its publish/install permission split and public managed-command transport.'
}
if ($bootstrap -match '\bgh\b|GITHUB_TOKEN|GH_TOKEN' -or
    $bootstrap -notmatch 'AllowAutoRedirect = \$false' -or
    $bootstrap -notmatch 'release-assets\.githubusercontent\.com' -or
    $bootstrap.IndexOf('Get-FileHash -LiteralPath $bundlePath') -gt $bootstrap.IndexOf('[IO.Compression.ZipFile]::OpenRead') -or
    $bootstrap -notmatch 'expectedNames = @\(' -or
    $bootstrap -notmatch 'Remove-Item -LiteralPath \$stagingRoot -Recurse -Force' -or
    $bootstrap -notmatch 'if \(Test-Path -LiteralPath \$stagingRoot\)' -or
    $bootstrap -notmatch '\$result\.status = ''failed''\s+\$result\.serviceStatus = \$null' -or
    $bootstrap -notmatch '\$exitCode = 1\s+}\s+Write-Output \(\$result \| ConvertTo-Json -Compress\)') {
    throw 'The guest bootstrap must use a credential-free, hash-before-extract, host-allowlisted, exact-entry transport with cleanup.'
}

$entraConfiguration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Configure-SourceGatewayEntra.ps1') -Raw
foreach ($requiredBinding in @(
        'd4394e57-c076-4c92-a870-5de6bf44f255',
        '1984d248-06ca-4d04-a3b8-4c0c1577ab86',
        'api-ofmfleet-source-gateway-dev',
        '595d3a27-a2a8-4ac4-b33d-98715cbcd684',
        'f4492b02-ee64-4295-a1db-7c7677134e42',
        'SourceGateway.Invoke',
        "allowedMemberTypes = @('Application')",
        '/appRoleAssignedTo',
        'artifacts/source-gateway-entra/identity.json')) {
    if ($entraConfiguration -notmatch [regex]::Escape($requiredBinding)) {
        throw "The Entra configurator is missing reviewed binding '$requiredBinding'."
    }
}
foreach ($forbiddenOperation in @(
        'az account set',
        'credential reset',
        'permission add',
        'admin-consent',
        'az role assignment',
        'password=')) {
    if ($entraConfiguration -match [regex]::Escape($forbiddenOperation)) {
        throw "The Entra configurator contains forbidden operation '$forbiddenOperation'."
    }
}
if ($entraConfiguration -notmatch '\& az @Arguments --subscription \$SubscriptionId' -or
    $entraConfiguration -notmatch 'az account get-access-token' -or
    $entraConfiguration -notmatch '--resource-type ms-graph' -or
    $entraConfiguration -notmatch 'Invoke-RestMethod @request' -or
    $entraConfiguration -notmatch 'AppRoleAssignment\.ReadWrite\.All' -or
    $entraConfiguration -notmatch 'Application\.ReadWrite\.All' -or
    $entraConfiguration -notmatch 'servicePrincipalType -ne ''ManagedIdentity''' -or
    $entraConfiguration -notmatch '\$null -ne \$caller\.appOwnerOrganizationId' -or
    $entraConfiguration -notmatch '\$caller\.appOwnerOrganizationId -ne \$expectedTenantId' -or
    $entraConfiguration -notmatch 'assignment\.principalId -eq \$callerPrincipalId' -or
    $entraConfiguration -notmatch 'assignment\.resourceId -eq \$resourceServicePrincipal\.id' -or
    $entraConfiguration -notmatch 'assignment\.appRoleId -eq \$role\.id' -or
    $entraConfiguration -notmatch '\$assignments\.Count -ne \$exactAssignments\.Count') {
    throw 'The Entra configurator does not enforce explicit subscription use, exact identity type, 403 guidance, and assignment tuple verification.'
}

function Invoke-MockedEntraConfiguration {
    param(
        [switch] $Apply,
        [bool] $InitialAppRoleAssignmentRequired = $false,
        [string] $ApplicationObjectId = '1841ae69-9889-48fc-aa56-f1cfdf2c7203'
    )

    $entraPath = Join-Path $PSScriptRoot 'Configure-SourceGatewayEntra.ps1'
    $evidencePath = Join-Path ([IO.Path]::GetTempPath()) "ofm-entra-evidence-$([guid]::NewGuid().ToString('N')).json"
    $requests = [Collections.Generic.List[object]]::new()
    $global:ofmEntraMockGuardEnabled = $InitialAppRoleAssignmentRequired
    try {
        $output = & {
            function az {
                param([Parameter(ValueFromRemainingArguments)] [object[]] $Arguments)

                $global:LASTEXITCODE = 0
                if ($Arguments -contains 'show') {
                    return '{"id":"d4394e57-c076-4c92-a870-5de6bf44f255","tenantId":"1984d248-06ca-4d04-a3b8-4c0c1577ab86"}'
                }
                if ($Arguments -contains 'get-access-token') {
                    return 'mock-graph-token'
                }
                throw "Unexpected mocked az arguments: $($Arguments -join ' ')"
            }

            function Invoke-RestMethod {
                param(
                    [string] $Method,
                    [string] $Uri,
                    [hashtable] $Headers,
                    [string] $ContentType,
                    [string] $Body,
                    [object] $ErrorAction
                )

                $requests.Add([pscustomobject]@{ Method = $Method; Uri = $Uri; Body = $Body })
                if ($Method -eq 'PATCH' -and $Uri -eq 'https://graph.microsoft.com/v1.0/servicePrincipals/c7f29be4-bffe-44e9-8108-1449571a031c') {
                    $patch = $Body | ConvertFrom-Json
                    if (@($patch.psobject.Properties).Count -ne 1 -or $patch.appRoleAssignmentRequired -ne $true) {
                        throw 'The mocked reconciliation PATCH changed more than appRoleAssignmentRequired=true.'
                    }
                    $global:ofmEntraMockGuardEnabled = $true
                    return $null
                }
                if ($Method -ne 'GET') {
                    throw "Unexpected mocked Graph mutation: $Method $Uri"
                }
                if ($Uri -like '*/servicePrincipals/595d3a27-a2a8-4ac4-b33d-98715cbcd684*') {
                    return [pscustomobject]@{
                        id = '595d3a27-a2a8-4ac4-b33d-98715cbcd684'
                        appId = 'f4492b02-ee64-4295-a1db-7c7677134e42'
                        servicePrincipalType = 'ManagedIdentity'
                        appOwnerOrganizationId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
                        alternativeNames = @('/subscriptions/d4394e57-c076-4c92-a870-5de6bf44f255/resourceGroups/mock/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mock')
                    }
                }
                if ($Uri.StartsWith('https://graph.microsoft.com/v1.0/applications?')) {
                    return [pscustomobject]@{ value = @([pscustomobject]@{
                                id = $ApplicationObjectId
                                appId = 'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
                                displayName = 'api-ofmfleet-source-gateway-dev'
                            }) }
                }
                if ($Uri -like '*/applications/1841ae69-9889-48fc-aa56-f1cfdf2c7203/owners*') {
                    return [pscustomobject]@{ value = @([pscustomobject]@{ id = 'mock-owner' }) }
                }
                if ($Uri -like '*/applications/1841ae69-9889-48fc-aa56-f1cfdf2c7203*') {
                    return [pscustomobject]@{
                        id = '1841ae69-9889-48fc-aa56-f1cfdf2c7203'
                        appId = 'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
                        displayName = 'api-ofmfleet-source-gateway-dev'
                        signInAudience = 'AzureADMyOrg'
                        identifierUris = @('api://b16b4127-9ef6-44a1-9f07-bbfe92053baf')
                        requiredResourceAccess = @()
                        passwordCredentials = @()
                        keyCredentials = @()
                        web = [pscustomobject]@{ redirectUris = @() }
                        spa = [pscustomobject]@{ redirectUris = @() }
                        publicClient = [pscustomobject]@{ redirectUris = @() }
                        api = [pscustomobject]@{
                            requestedAccessTokenVersion = 2
                            oauth2PermissionScopes = @()
                            preAuthorizedApplications = @()
                            knownClientApplications = @()
                        }
                        appRoles = @([pscustomobject]@{
                                allowedMemberTypes = @('Application')
                                description = 'Invoke the private Oracle Forms source gateway.'
                                displayName = 'SourceGateway.Invoke'
                                id = 'b03da987-2091-4443-86bc-5c3e9569a91a'
                                isEnabled = $true
                                value = 'SourceGateway.Invoke'
                            })
                    }
                }
                if ($Uri.StartsWith('https://graph.microsoft.com/v1.0/servicePrincipals?')) {
                    return [pscustomobject]@{ value = @([pscustomobject]@{
                                id = 'c7f29be4-bffe-44e9-8108-1449571a031c'
                                appId = 'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
                                servicePrincipalType = 'Application'
                                appOwnerOrganizationId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
                                appRoleAssignmentRequired = $global:ofmEntraMockGuardEnabled
                            }) }
                }
                if ($Uri -like '*/servicePrincipals/c7f29be4-bffe-44e9-8108-1449571a031c/appRoleAssignedTo') {
                    return [pscustomobject]@{ value = @([pscustomobject]@{
                                id = 'mock-assignment'
                                principalId = '595d3a27-a2a8-4ac4-b33d-98715cbcd684'
                                resourceId = 'c7f29be4-bffe-44e9-8108-1449571a031c'
                                appRoleId = 'b03da987-2091-4443-86bc-5c3e9569a91a'
                            }) }
                }
                if ($Uri -like '*/servicePrincipals/c7f29be4-bffe-44e9-8108-1449571a031c*') {
                    return [pscustomobject]@{
                        id = 'c7f29be4-bffe-44e9-8108-1449571a031c'
                        appId = 'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
                        servicePrincipalType = 'Application'
                        appOwnerOrganizationId = '1984d248-06ca-4d04-a3b8-4c0c1577ab86'
                        appRoleAssignmentRequired = $global:ofmEntraMockGuardEnabled
                        appRoles = @([pscustomobject]@{
                                allowedMemberTypes = @('Application')
                                displayName = 'SourceGateway.Invoke'
                                id = 'b03da987-2091-4443-86bc-5c3e9569a91a'
                                isEnabled = $true
                                value = 'SourceGateway.Invoke'
                            })
                    }
                }
                throw "Unexpected mocked Graph read: $Uri"
            }

            $arguments = @{ OutputPath = $evidencePath }
            if ($Apply) { $arguments.Apply = $true }
            & $entraPath @arguments
        }
        return [pscustomobject]@{
            Output = @($output)
            Error = $null
            Requests = @($requests)
            Evidence = if (Test-Path -LiteralPath $evidencePath) {
                Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
            } else { $null }
        }
    } catch {
        return [pscustomobject]@{ Output = @(); Error = $_; Stack = $_.ScriptStackTrace; Requests = @($requests); Evidence = $null }
    } finally {
        Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue
        Remove-Variable -Name ofmEntraMockGuardEnabled -Scope Global -ErrorAction SilentlyContinue
    }
}

$guardPreflight = Invoke-MockedEntraConfiguration
if ($null -eq $guardPreflight.Error -or $guardPreflight.Error.Exception.Message -notmatch 'does not require app-role assignment' -or
    @($guardPreflight.Requests | Where-Object Method -eq 'PATCH').Count -ne 0) {
    $detail = if ($null -ne $guardPreflight.Error) { $guardPreflight.Error.Exception.Message } else { 'no error' }
    $patchCount = @($guardPreflight.Requests | Where-Object Method -eq 'PATCH').Count
    throw "Entra verify-only mode must reject appRoleAssignmentRequired=false without mutation. Error: $detail; Stack: $($guardPreflight.Stack); PATCH count: $patchCount"
}

$guardApply = Invoke-MockedEntraConfiguration -Apply
$guardPatches = @($guardApply.Requests | Where-Object Method -eq 'PATCH')
if ($null -ne $guardApply.Error -or $guardPatches.Count -ne 1 -or
    $guardPatches[0].Uri -cne 'https://graph.microsoft.com/v1.0/servicePrincipals/c7f29be4-bffe-44e9-8108-1449571a031c' -or
    $null -eq $guardApply.Evidence -or $guardApply.Evidence.appRoleAssignmentRequired -ne $true -or
    $guardApply.Evidence.appRoleAssignmentRequiredChanged -ne $true -or
    $guardApply.Evidence.assignmentTupleVerified -ne $true) {
    $detail = if ($null -ne $guardApply.Error) { $guardApply.Error.Exception.Message } else { 'mock output did not match' }
    throw "Entra apply mode did not reconcile and evidence the exact service-principal guard: $detail"
}

$wrongApplication = Invoke-MockedEntraConfiguration -Apply -ApplicationObjectId '00000000-0000-0000-0000-000000000001'
if ($null -eq $wrongApplication.Error -or $wrongApplication.Error.Exception.Message -notmatch 'does not match the authorized gateway application' -or
    @($wrongApplication.Requests | Where-Object Method -eq 'PATCH').Count -ne 0) {
    throw 'The Entra configurator must reject a same-name but non-authorized application before mutation.'
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