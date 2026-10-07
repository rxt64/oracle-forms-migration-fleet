[CmdletBinding()]
param(
    [ValidateSet('OFM_GATEWAY_ORACLE9I')]
    [string] $OdbcDsn = 'OFM_GATEWAY_ORACLE9I',

    [ValidateSet('OFM_ORCL9I')]
    [string] $TnsAlias = 'OFM_ORCL9I',

    [ValidateSet('10.246.0.37')]
    [string] $OracleHost = '10.246.0.37',

    [ValidateSet(1521)]
    [int] $OraclePort = 1521,

    [ValidateSet('orcl')]
    [string] $OracleSid = 'orcl',

    [ValidateSet('Microsoft ODBC for Oracle')]
    [string] $DriverName = 'Microsoft ODBC for Oracle',

    [Parameter(Mandatory)]
    [string] $CommonModulePath,

    [switch] $PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This lab connection bootstrap must run elevated.'
}
if ($env:COMPUTERNAME -ine 'OFMFORMS6I') {
    throw 'The lab connection bootstrap reached an unexpected host.'
}
. $CommonModulePath

$odbcRoot = 'HKLM:\SOFTWARE\WOW6432Node\ODBC'
$systemRoot = $env:SystemRoot
$tnsAdmin = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net'
$tnsNamesPath = Join-Path $tnsAdmin 'tnsnames.ora'
$serviceName = 'OFMSourceGateway'
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"

function Restart-SourceGatewayServiceToRunning {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [int] $PhaseTimeoutSeconds = 60,
        [int] $TotalTimeoutSeconds = 120
    )

    # Restart-Service blocks inside the cmdlet before any WaitForStatus can apply a deadline. Initiating
    # Stop()/Start() on the controller returns as soon as the SCM accepts the control request, so every
    # wait below is the bounded one.
    $deadlineUtc = [DateTime]::UtcNow.AddSeconds($TotalTimeoutSeconds)
    $controller = Get-Service -Name $Name -ErrorAction Stop
    foreach ($phase in @('Stopped', 'Running')) {
        $remainingSeconds = ($deadlineUtc - [DateTime]::UtcNow).TotalSeconds
        if ($remainingSeconds -le 0) {
            throw "The source-gateway service '$Name' did not reach $phase within the bounded restart deadline."
        }
        $controller.Refresh()
        if ([string]$controller.Status -cne $phase) {
            if ($phase -ceq 'Stopped') { $controller.Stop() } else { $controller.Start() }
            $controller.WaitForStatus($phase,
                [TimeSpan]::FromSeconds([Math]::Min($PhaseTimeoutSeconds, $remainingSeconds)))
        }
        $controller.Refresh()
        if ([string]$controller.Status -cne $phase) {
            throw "The source-gateway service '$Name' did not reach $phase within the bounded restart window."
        }
    }
}

# Every rollback step must run even when an earlier one throws, so each failure is collected rather than raised.
function Invoke-SourceGatewayCleanupStep {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [scriptblock] $Action,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[string]] $Failures
    )

    try { & $Action }
    catch { $Failures.Add("could not $Description ($($_.Exception.Message))") }
}

if (-not (Test-Path -LiteralPath 'C:\orant\BIN' -PathType Container)) {
    throw 'The native Oracle Net8 client under C:\orant is not installed on this host.'
}

$installedDrivers = Get-ItemProperty -LiteralPath (Join-Path $odbcRoot 'ODBCINST.INI\ODBC Drivers') -ErrorAction Stop
$installedProperty = $installedDrivers.PSObject.Properties[$DriverName]
if ($null -eq $installedProperty -or [string]$installedProperty.Value -cne 'Installed') {
    throw "The 32-bit ODBC driver '$DriverName' is not registered as installed on this host."
}
$rawDriverPath = [string](Get-ItemPropertyValue -LiteralPath (Join-Path $odbcRoot "ODBCINST.INI\$DriverName") `
    -Name Driver -ErrorAction Stop)
$driverPath = Resolve-SourceGatewayWow64DriverPath -RawDriverPath $rawDriverPath -SystemRoot $systemRoot
$canonicalDriverPath = Get-SourceGatewayCanonicalOdbcDriverPath -DriverName $DriverName -SystemRoot $systemRoot
if ($null -eq $canonicalDriverPath) {
    throw "The lab connection bootstrap has no canonical library pinned for driver '$DriverName'."
}
if ($driverPath -ine $canonicalDriverPath -or -not (Test-Path -LiteralPath $driverPath -PathType Leaf)) {
    throw "The registered 32-bit driver '$DriverName' does not resolve to its canonical library $canonicalDriverPath."
}
$driverMachine = Get-SourceGatewayPortableExecutableMachine -Path $driverPath
if ($driverMachine -ne 0x014C) {
    throw "The registered driver '$DriverName' is not an x86 image (machine 0x$($driverMachine.ToString('X4')))."
}
$driverSignature = Get-AuthenticodeSignature -LiteralPath $driverPath -ErrorAction Stop
if ([string]$driverSignature.Status -cne 'Valid') {
    throw "The registered driver '$DriverName' does not carry a valid Authenticode signature ($($driverSignature.Status))."
}
$driverSubject = [string]$driverSignature.SignerCertificate.Subject
if ($driverSubject -notmatch 'O=Microsoft Corporation') {
    throw "The registered driver '$DriverName' is not signed by Microsoft Corporation."
}

$expectedTnsNames = New-SourceGatewayTnsNamesContent -Alias $TnsAlias -OracleHost $OracleHost `
    -OraclePort $OraclePort -OracleSid $OracleSid
$allowedDriverRoots = @(Get-SourceGatewayAllowedOdbcDriverRoots -SystemRoot $systemRoot)

$existingDsn = @(Get-OdbcDsn -Name $OdbcDsn -DsnType System -Platform '32-bit' -ErrorAction SilentlyContinue)
$dsnAction = 'create'
if ($existingDsn.Count -eq 1) {
    $serverProperty = $existingDsn[0].Attribute['Server']
    $registeredDsn = $null
    if ([string]$existingDsn[0].DriverName -ceq $DriverName -and [string]$serverProperty -ceq $TnsAlias) {
        $registeredDsn = Resolve-SourceGatewayFormsOdbcDriver -Dsn $OdbcDsn -OdbcRoot $odbcRoot `
            -SystemRoot $systemRoot -AllowedDriverRoots $allowedDriverRoots -ExpectedServer $TnsAlias
    }
    if ($null -eq $registeredDsn -or [string]$registeredDsn.DriverName -cne $DriverName -or
        $registeredDsn.DriverPath -ine $canonicalDriverPath -or [string]$registeredDsn.Server -cne $TnsAlias) {
        throw "Refusing to adopt the pre-existing 32-bit System DSN '$OdbcDsn'; it is bound to other connection settings."
    }
    $dsnAction = 'reuse'
}
elseif ($existingDsn.Count -gt 1) {
    throw "The 32-bit System DSN '$OdbcDsn' resolves to more than one registration."
}

$tnsAction = 'create'
if (Test-Path -LiteralPath $tnsNamesPath -PathType Leaf) {
    if ((Get-Content -LiteralPath $tnsNamesPath -Raw) -cne $expectedTnsNames) {
        throw "Refusing to rewrite the existing tooling TNS configuration at $tnsNamesPath."
    }
    $tnsAction = 'reuse'
}

$serviceProperties = Get-ItemProperty -LiteralPath $serviceRegistryPath -ErrorAction Stop
$previousEnvironment = if ($null -ne $serviceProperties.PSObject.Properties['Environment']) {
    @($serviceProperties.Environment)
} else {
    $null
}
$merged = Merge-SourceGatewayServiceEnvironment -Existing $previousEnvironment -Name 'TNS_ADMIN' -Value $tnsAdmin
$environmentAction = if ($merged.Changed) { 'add' } else { 'reuse' }
$serviceAction = if ($merged.Changed) { 'restart' } else { 'none' }

$previousServiceStatus = [string](Get-Service -Name $serviceName -ErrorAction Stop).Status
if ($previousServiceStatus -cne 'Running') {
    throw "Refusing to report the lab connection while '$serviceName' is $previousServiceStatus; the service must be Running so the new environment can be verified in a restarted process."
}

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        status = 'lab-connection-planned'
        dsn = $OdbcDsn
        dsnAction = $dsnAction
        driverName = $DriverName
        driverPath = $driverPath
        driverMachine = 'x86'
        tnsAlias = $TnsAlias
        tnsAdmin = $tnsAdmin
        tnsAction = $tnsAction
        serviceEnvironmentAction = $environmentAction
        serviceAction = $serviceAction
    } | ConvertTo-Json -Compress
    return
}

$createdDirectory = $false
$createdTnsNames = $false
$createdDsn = $false
$changedEnvironment = $false
$serviceRestartAttempted = $false
$completed = $false
$operationError = $null
try {
    if (-not (Test-Path -LiteralPath $tnsAdmin -PathType Container)) {
        New-Item -ItemType Directory -Path $tnsAdmin -Force | Out-Null
        $createdDirectory = $true
        Set-RestrictedPathAcl -Path $tnsAdmin -Grants @(
            'SYSTEM:(OI)(CI)(F)',
            'Administrators:(OI)(CI)(F)',
            'NT AUTHORITY\LocalService:(OI)(CI)(RX)',
            'NT SERVICE\OFMSourceGateway:(OI)(CI)(RX)'
        )
    }
    if ($tnsAction -eq 'create') {
        [IO.File]::WriteAllText($tnsNamesPath, $expectedTnsNames, [Text.UTF8Encoding]::new($false))
        $createdTnsNames = $true
    }
    if ($dsnAction -eq 'create') {
        Add-OdbcDsn -Name $OdbcDsn -DriverName $DriverName -DsnType System -Platform '32-bit' `
            -SetPropertyValue @("Server=$TnsAlias") -ErrorAction Stop
        $createdDsn = $true
    }
    if ($merged.Changed) {
        New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString `
            -Value $merged.Environment -Force | Out-Null
        $changedEnvironment = $true
        # Marked before the control request, because a stop that succeeds and a start that fails still
        # leaves the service down and still needs the rollback restart.
        $serviceRestartAttempted = $true
        Restart-SourceGatewayServiceToRunning -Name $serviceName
    }

    $verifiedDsn = @(Get-OdbcDsn -Name $OdbcDsn -DsnType System -Platform '32-bit' -ErrorAction Stop)
    $verifiedRegistration = Resolve-SourceGatewayFormsOdbcDriver -Dsn $OdbcDsn -OdbcRoot $odbcRoot `
        -SystemRoot $systemRoot -AllowedDriverRoots $allowedDriverRoots -ExpectedServer $TnsAlias
    $verifiedEnvironment = @((Get-ItemProperty -LiteralPath $serviceRegistryPath -ErrorAction Stop).Environment)
    if ($verifiedDsn.Count -ne 1 -or
        [string]$verifiedDsn[0].DriverName -cne $DriverName -or
        [string]$verifiedDsn[0].Attribute['Server'] -cne $TnsAlias -or
        [string]$verifiedRegistration.DriverName -cne $DriverName -or
        $verifiedRegistration.DriverPath -ine $canonicalDriverPath -or
        [string]$verifiedRegistration.Server -cne $TnsAlias -or
        $verifiedEnvironment -cnotcontains "TNS_ADMIN=$tnsAdmin" -or
        [string](Get-Service -Name $serviceName -ErrorAction Stop).Status -cne 'Running' -or
        (Get-Content -LiteralPath $tnsNamesPath -Raw) -cne $expectedTnsNames) {
        throw 'The tooling-owned lab connection did not verify after it was written.'
    }
    $completed = $true
}
catch {
    $operationError = $_
}
finally {
    if (-not $completed) {
        $cleanupFailures = [Collections.Generic.List[string]]::new()
        if ($changedEnvironment) {
            Invoke-SourceGatewayCleanupStep -Description 'restore the prior service environment' `
                -Failures $cleanupFailures -Action {
                if ($null -eq $previousEnvironment) {
                    Remove-ItemProperty -Path $serviceRegistryPath -Name Environment -ErrorAction Stop
                }
                else {
                    New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString `
                        -Value $previousEnvironment -Force | Out-Null
                }
            }
        }
        if ($createdDsn) {
            Invoke-SourceGatewayCleanupStep -Description "remove the 32-bit System DSN '$OdbcDsn' this run created" `
                -Failures $cleanupFailures -Action {
                Remove-OdbcDsn -Name $OdbcDsn -DsnType System -Platform '32-bit' -ErrorAction Stop
            }
        }
        if ($createdTnsNames) {
            Invoke-SourceGatewayCleanupStep `
                -Description "remove the tooling TNS configuration $tnsNamesPath this run created" `
                -Failures $cleanupFailures -Action {
                # Probed first so an already absent file is a no-op while a refused delete is still reported.
                if (Test-Path -LiteralPath $tnsNamesPath -PathType Leaf -ErrorAction Stop) {
                    Remove-Item -LiteralPath $tnsNamesPath -Force -ErrorAction Stop
                }
            }
        }
        if ($createdDirectory) {
            Invoke-SourceGatewayCleanupStep -Description "remove the tooling directory $tnsAdmin this run created" `
                -Failures $cleanupFailures -Action {
                if (Test-Path -LiteralPath $tnsAdmin -PathType Container -ErrorAction Stop) {
                    Remove-Item -LiteralPath $tnsAdmin -Recurse -Force -ErrorAction Stop
                }
            }
        }
        # Attempted last so the service is still rescued when an earlier cleanup step failed.
        if ($serviceRestartAttempted) {
            Invoke-SourceGatewayCleanupStep `
                -Description "return '$serviceName' to its previous $previousServiceStatus state" `
                -Failures $cleanupFailures -Action { Restart-SourceGatewayServiceToRunning -Name $serviceName }
        }
        if ($cleanupFailures.Count -ne 0) {
            $primaryFailure = if ($null -ne $operationError) {
                $operationError.Exception.Message
            } else {
                'the lab connection did not complete'
            }
            $operationError = [Management.Automation.ErrorRecord]::new(
                [InvalidOperationException]::new(
                    "Rollback $($cleanupFailures -join '; ') after '$primaryFailure'."),
                'SourceGatewayLabConnectionRollbackFailed',
                [Management.Automation.ErrorCategory]::InvalidOperation,
                $serviceName)
        }
    }
}
if ($null -ne $operationError) { throw $operationError }

[ordered]@{
    schemaVersion = 1
    status = 'lab-connection-ready'
    dsn = $OdbcDsn
    dsnAction = $dsnAction
    driverName = $DriverName
    driverPath = $driverPath
    driverMachine = 'x86'
    tnsAlias = $TnsAlias
    tnsAdmin = $tnsAdmin
    tnsAction = $tnsAction
    serviceEnvironmentAction = $environmentAction
    serviceAction = $serviceAction
} | ConvertTo-Json -Compress
