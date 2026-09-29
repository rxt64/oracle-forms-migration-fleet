#requires -Version 7.2

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Deploy-Forms6iSourceLab.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref] $tokens, [ref] $parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw "Deploy-Forms6iSourceLab.ps1 has $(@($parseErrors).Count) parse error(s)."
}

foreach ($functionName in @(
    'Set-UserOnlyAcl',
    'Initialize-UserOnlyDirectory',
        'Get-ExpectedNetworkSecurityRules',
        'Get-ExpectedOracleXeNetworkSecurityRules',
        'Get-NetworkRuleValues',
        'Assert-NetworkRuleValuesEqual',
        'Test-NetworkSecurityRuleContract')) {
    $functionAst = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
            }, $true))
    if ($functionAst.Count -ne 1) {
        throw "Expected exactly one '$functionName' function definition."
    }
    Invoke-Expression $functionAst[0].Extent.Text
}

function ConvertTo-SyntheticRules {
    return @(Get-ExpectedNetworkSecurityRules | ForEach-Object {
            [pscustomobject]@{
                name = $_.Name
                priority = $_.Priority
                access = $_.Access
                direction = $_.Direction
                protocol = $_.Protocol
                sourcePortRanges = @($_.SourcePorts)
                destinationPortRanges = @($_.DestinationPorts)
                sourceAddressPrefixes = @($_.SourceAddresses)
                destinationAddressPrefixes = @($_.DestinationAddresses)
            }
        })
}

function ConvertTo-SyntheticOracleXeRules {
    return @(Get-ExpectedOracleXeNetworkSecurityRules | ForEach-Object {
            [pscustomobject]@{
                name = $_.Name
                priority = $_.Priority
                access = $_.Access
                direction = $_.Direction
                protocol = $_.Protocol
                sourcePortRanges = @($_.SourcePorts)
                destinationPortRanges = @($_.DestinationPorts)
                sourceAddressPrefixes = @($_.SourceAddresses)
                destinationAddressPrefixes = @($_.DestinationAddresses)
            }
        })
}

function Assert-RejectedMutation {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [scriptblock] $Mutation
    )

    $rules = ConvertTo-SyntheticRules
    & $Mutation $rules
    try {
        Test-NetworkSecurityRuleContract -Rules $rules
    }
    catch {
        Write-Output "PASS rejected $Label"
        return
    }
    throw "Malformed network rules passed verification: $Label"
}

Test-NetworkSecurityRuleContract -Rules (ConvertTo-SyntheticRules)
Write-Output 'PASS accepted exact network rule contract'

Assert-RejectedMutation -Label 'public RDP source' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Bastion-Developer-RDP').sourceAddressPrefixes = @('*')
}
Assert-RejectedMutation -Label 'broader Bastion destination' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Bastion-Developer-RDP').destinationAddressPrefixes = @('10.246.0.0/24')
}
Assert-RejectedMutation -Label 'Bastion SSH access' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Bastion-Developer-RDP').destinationPortRanges = @('22', '3389')
}

$singularRules = ConvertTo-SyntheticRules
foreach ($rule in $singularRules) {
    foreach ($field in @(
            @{ Plural = 'sourcePortRanges'; Singular = 'sourcePortRange' },
            @{ Plural = 'destinationPortRanges'; Singular = 'destinationPortRange' },
            @{ Plural = 'sourceAddressPrefixes'; Singular = 'sourceAddressPrefix' },
            @{ Plural = 'destinationAddressPrefixes'; Singular = 'destinationAddressPrefix' })) {
        $values = @($rule.($field.Plural))
        if ($values.Count -eq 1) {
            $rule | Add-Member -NotePropertyName $field.Singular -NotePropertyValue $values[0]
            $rule.($field.Plural) = @()
        }
    }
}
Test-NetworkSecurityRuleContract -Rules $singularRules
Write-Output 'PASS accepted Azure CLI singular rule fields with empty plural fields'

Assert-RejectedMutation -Label 'wrong priority' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Deny-RFC1918-10').priority = 4095
}
Assert-RejectedMutation -Label 'wrong direction' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Deny-All-Inbound').direction = 'Outbound'
}
Assert-RejectedMutation -Label 'wrong allow-rule ports' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Azure-Guest-Agent').destinationPortRanges = @('443')
}
Assert-RejectedMutation -Label 'SQL*Net egress to the whole subnet' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-SqlNet-Outbound').destinationAddressPrefixes = @('10.246.0.32/27')
}
Assert-RejectedMutation -Label 'SQL*Net egress to the virtual network' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-SqlNet-Outbound').destinationAddressPrefixes = @('VirtualNetwork')
}
Assert-RejectedMutation -Label 'SQL*Net egress widened beyond 1521' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-SqlNet-Outbound').destinationPortRanges = @('1521', '22')
}
Assert-RejectedMutation -Label 'SQL*Net egress ordered after the virtual network deny' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-SqlNet-Outbound').priority = 310
}

function Assert-RejectedOracleXeMutation {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [scriptblock] $Mutation
    )

    $rules = ConvertTo-SyntheticOracleXeRules
    & $Mutation $rules
    try {
        Test-NetworkSecurityRuleContract -Rules $rules -ExpectedRules @(Get-ExpectedOracleXeNetworkSecurityRules) -ContractName 'Oracle XE'
    }
    catch {
        Write-Output "PASS rejected Oracle XE $Label"
        return
    }
    throw "Malformed Oracle XE network rules passed verification: $Label"
}

Assert-RejectedMutation -Label 'redirect egress to other hosts' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-Redirect-Outbound').destinationAddressPrefixes = @('VirtualNetwork')
}
Assert-RejectedMutation -Label 'redirect egress from other sources' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-Redirect-Outbound').sourceAddressPrefixes = @('*')
}
Assert-RejectedMutation -Label 'redirect port range widened' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Oracle9i-Redirect-Outbound').destinationPortRanges = @('*')
}
Assert-RejectedOracleXeMutation -Label 'redirect exposed to other hosts' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Forms6i-Redirect-Inbound').sourceAddressPrefixes = @('*')
}
Assert-RejectedOracleXeMutation -Label 'redirect destination broadened' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Forms6i-Redirect-Inbound').destinationAddressPrefixes = @('VirtualNetwork')
}

Test-NetworkSecurityRuleContract -Rules (ConvertTo-SyntheticOracleXeRules) `
    -ExpectedRules @(Get-ExpectedOracleXeNetworkSecurityRules) -ContractName 'Oracle XE'
Write-Output 'PASS accepted exact Oracle XE network rule contract'

Assert-RejectedOracleXeMutation -Label 'listener exposed to the whole virtual network' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Forms6i-SqlNet-Inbound-Oracle9i').sourceAddressPrefixes = @('VirtualNetwork')
}
Assert-RejectedOracleXeMutation -Label 'listener exposed to the internet' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Forms6i-SqlNet-Inbound-Oracle9i').sourceAddressPrefixes = @('*')
}
Assert-RejectedOracleXeMutation -Label 'inbound SSH reintroduced' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Forms6i-SqlNet-Inbound-Oracle9i').destinationPortRanges = @('22', '1521')
}
Assert-RejectedOracleXeMutation -Label 'inbound deny weakened to allow' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Deny-All-Inbound').access = 'Allow'
}
Assert-RejectedOracleXeMutation -Label 'registry egress widened to all of Azure' -Mutation {
    param($rules)
    ($rules | Where-Object name -eq 'Allow-Container-Registry-HTTPS').destinationAddressPrefixes = @('AzureCloud', 'Internet')
}

Write-Output 'PASS Forms6i source-lab deployment script regressions'

$installerScripts = @(
    Join-Path $PSScriptRoot 'Install-Forms6iSourceLab.ps1'
    Join-Path $PSScriptRoot 'Invoke-Forms6iGuestInstall.ps1'
    Join-Path $PSScriptRoot 'Retrieve-Forms6iGuestEvidence.ps1'
    Join-Path $PSScriptRoot 'Diagnose-Forms6iInstaller.ps1'
)
foreach ($installerScript in $installerScripts) {
    $installerTokens = $null
    $installerParseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($installerScript, [ref] $installerTokens, [ref] $installerParseErrors)
    if (@($installerParseErrors).Count -ne 0) {
        throw "$(Split-Path $installerScript -Leaf) has $(@($installerParseErrors).Count) parse error(s)."
    }
}

$guestScriptText = Get-Content -LiteralPath $installerScripts[1] -Raw
foreach ($requiredText in @(
    '5F84ED194BB6F84CEDC6AF38F4598733DBCF91E8F1008A9C34327090CDA456F4',
    '72A706D4E48D7E6F2B734ADE97AF859FED1CA91ADB8629415A785CEA76A406BE',
    'Mount-DiskImage -ImagePath $convertedImagePath -StorageType ISO -Access ReadOnly',
    'The three-attempt installer budget has been exhausted on this VM.',
    'installer-attempt-reservation-',
    "verificationMode = 'ReadOnlyInventory'",
    'FORMS6I_EVIDENCE_SHA256=',
    'WaitForExit($remainingMilliseconds)',
    "@('ifbld60.exe', 'ifcmp60.exe', 'ifrun60.exe')",
    'Get-SystemDllEvidence',
    'Compare-SystemDllEvidence')) {
    if (-not $guestScriptText.Contains($requiredText)) {
        throw "Guest installer safety contract is missing: $requiredText"
    }
}
if ($guestScriptText -match 'SendInput|mouse_event|keybd_event|SetCursorPos|PostMessage|SendMessage') {
    throw 'Guest installer script must not synthesize UI input.'
}
if ($guestScriptText -match '\[Threading\.Thread\]::Sleep|Start-Sleep') {
    throw 'Guest installer script must use event waits rather than sleep polling.'
}

$guestAst = [System.Management.Automation.Language.Parser]::ParseFile($installerScripts[1], [ref] $null, [ref] $null)
foreach ($functionName in @('Reserve-AttemptSlot', 'Complete-AttemptReservation')) {
    $functionAst = @($guestAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
            }, $true))
    if ($functionAst.Count -ne 1) { throw "Expected exactly one '$functionName' function definition." }
    Invoke-Expression $functionAst[0].Extent.Text
}
$budgetTestRoot = Join-Path $env:LOCALAPPDATA "OracleFormsMigrationFleet\forms6i-budget-test-$([Guid]::NewGuid().ToString('N'))"
try {
    [void](New-Item -ItemType Directory -Path $budgetTestRoot -Force)
    Set-Content -LiteralPath (Join-Path $budgetTestRoot 'probe-installer-1.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $budgetTestRoot 'attempt-install-2.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $budgetTestRoot 'installer-attempt-reservation-completed.json') -Value '{"status":"Completed"}'
    $evidenceRoot = $budgetTestRoot
    $Operation = 'AttemptInstall'
    $reservation = Reserve-AttemptSlot
    if ($reservation.AttemptNumber -ne 3) { throw 'Completed reservations were double-counted against legacy attempt evidence.' }
    try {
        [void](Reserve-AttemptSlot)
        throw 'A concurrent reservation incorrectly permitted a fourth installer launch.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'three-attempt installer budget') { throw }
    }
    Complete-AttemptReservation -Reservation $reservation
    $retainedReservation = Get-Content -LiteralPath $reservation.Path -Raw | ConvertFrom-Json
    if ($retainedReservation.status -ne 'Reserved') { throw 'Missing attempt evidence released a consumed installer slot.' }
    try {
        [void](Reserve-AttemptSlot)
        throw 'Failed evidence persistence incorrectly permitted a fourth installer launch.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'three-attempt installer budget') { throw }
    }
    @{ reservationId = $reservation.Id } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $budgetTestRoot 'attempt-install-3.json')
    Complete-AttemptReservation -Reservation $reservation
    $retainedReservation = Get-Content -LiteralPath $reservation.Path -Raw | ConvertFrom-Json
    if ($retainedReservation.status -ne 'Completed') { throw 'Saved attempt evidence did not complete its reservation.' }
    Write-Output 'PASS durable installer attempt budget reservation contract'
}
finally {
    Remove-Item -LiteralPath $budgetTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$hostScriptText = Get-Content -LiteralPath $installerScripts[0] -Raw
foreach ($requiredText in @(
    "'d4394e57-c076-4c92-a870-5de6bf44f255'",
    "'rg-oracle-forms-migration-fleet-dev-b9f0e875'",
    "'vm-ofm-forms6i-j6mrrerz'",
    "'eastus2'",
    "'RunPowerShellScript'",
    '$requestedVmId',
    '$verifiedFoundations[0].vmResourceId, $requestedVmId',
    'New-ProtectedScriptSnapshot',
    'Receive-GuestEvidence')) {
    if (-not $hostScriptText.Contains($requiredText)) {
        throw "Host installer target contract is missing: $requiredText"
    }
}
Write-Output 'PASS Forms6i installer host/guest safety contracts'

if ($null -eq [Diagnostics.Process].GetMethod('WaitForExit', [type[]]@([int]))) {
    throw 'The bounded process wait API is unavailable.'
}
$installerFunction = $guestAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-BoundedInstaller' }, $true)
$cleanupFinally = $installerFunction.FindAll({ param($node) $node -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally -and $node.Finally.Extent.Text.Contains('Stop-StartedProcessTree') }, $true)
if (@($cleanupFinally).Count -ne 1) { throw 'Installer termination must be guaranteed by finally.' }
Write-Output 'PASS supported bounded wait API and guaranteed cleanup structure'

$aclTestPath = Join-Path $env:LOCALAPPDATA "OracleFormsMigrationFleet\forms6i-source-lab-acl-test-$([Guid]::NewGuid().ToString('N'))"
try {
    Initialize-UserOnlyDirectory -Path $aclTestPath
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $accessSids = @((Get-Acl -LiteralPath $aclTestPath).Access | ForEach-Object {
            $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        } | Sort-Object -Unique)
    if ($accessSids.Count -ne 1 -or $accessSids[0] -ne $currentSid) {
        throw 'Protected directory ACL was not restricted to the current user.'
    }
    Write-Output 'PASS current-user-only temporary state ACL'
}
finally {
    if (Test-Path -LiteralPath $aclTestPath) {
        Remove-Item -LiteralPath $aclTestPath -Recurse -Force -ErrorAction Stop
    }
}