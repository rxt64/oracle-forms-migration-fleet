Set-StrictMode -Version Latest

function Assert-SourceGatewayCiEvidence(
    [object] $Run,
    [object[]] $Jobs,
    [string] $ExpectedRepository,
    [string] $ExpectedCommitSha,
    [long] $ExpectedRunId,
    [switch] $ExactRequiredJobs
) {
    if ($null -eq $Run -or $null -eq $Jobs) {
        throw 'The required CI run and job evidence is missing.'
    }

    $headRepositoryProperty = $Run.PSObject.Properties['head_repository']
    $repositoryProperty = $Run.PSObject.Properties['repository']
    $headRepository = if ($null -ne $headRepositoryProperty -and $null -ne $headRepositoryProperty.Value) {
        $headRepositoryProperty.Value.full_name
    } elseif ($null -ne $repositoryProperty) {
        $repositoryProperty.Value
    } else {
        $null
    }
    if ([long]$Run.id -ne $ExpectedRunId -or $Run.name -cne 'CI' -or
        $Run.path -cne '.github/workflows/ci.yml' -or $Run.event -cne 'push' -or
        $Run.head_branch -cne 'main' -or $Run.head_sha -cne $ExpectedCommitSha -or
        $headRepository -cne $ExpectedRepository -or $Run.status -cne 'completed' -or
        $Run.conclusion -cne 'success') {
        throw 'The named GitHub Actions run is not a successful main-push CI run for the expected repository and commit.'
    }

    $requiredJobs = @('Native source worker contract', 'build-and-test', 'Container image builds', 'Guided UI browser checks')
    if ($ExactRequiredJobs -and $Jobs.Count -ne $requiredJobs.Count) {
        throw 'Bundled CI proof must contain exactly the required job evidence.'
    }
    foreach ($requiredJob in $requiredJobs) {
        $matches = @($Jobs | Where-Object name -CEQ $requiredJob)
        if ($matches.Count -ne 1 -or $matches[0].status -cne 'completed' -or $matches[0].conclusion -cne 'success') {
            throw "Required CI job '$requiredJob' must appear exactly once and complete successfully."
        }
    }
}

function Invoke-ScChecked([string[]] $Arguments, [string] $FailureMessage) {
    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (sc.exe exit code $LASTEXITCODE)."
    }
}

function Invoke-IcaclsChecked([string[]] $Arguments, [string] $FailureMessage) {
    & icacls.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (icacls.exe exit code $LASTEXITCODE)."
    }
}

function Set-RestrictedPathAcl(
    [string] $Path,
    [string[]] $Grants,
    [string] $TrustedOwnerSid = 'S-1-5-32-544'
) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($TrustedOwnerSid))
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($grant in $Grants) {
        if ($grant -notmatch '^(?<identity>.+):\((?<inheritance>OI)\)\((?<container>CI)\)\((?<rights>F|M|RX)\)$') {
            throw "Unsupported restricted-directory grant '$grant'."
        }
        $identityText = $Matches.identity
        $rightsText = $Matches.rights
        $rights = switch ($rightsText) {
            'F' { [Security.AccessControl.FileSystemRights]::FullControl }
            'M' { [Security.AccessControl.FileSystemRights]::Modify }
            'RX' { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        }
        $identity = if ($identityText -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($identityText)
        } else {
            [Security.Principal.NTAccount]::new($identityText)
        }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            $rights,
            [Security.AccessControl.InheritanceFlags]'ObjectInherit, ContainerInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }

    try {
        Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
    }
    catch {
        throw "Could not establish the exact restricted ACL on $Path. $($_.Exception.Message)"
    }
}

function Set-RestrictedFileAcl(
    [string] $Path,
    [string[]] $Grants,
    [string] $TrustedOwnerSid = 'S-1-5-32-544'
) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($TrustedOwnerSid))
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($grant in $Grants) {
        if ($grant -notmatch '^(?<identity>.+):\((?<rights>F|RX|R,D)\)$') {
            throw "Unsupported restricted-file grant '$grant'."
        }
        $identityText = $Matches.identity
        $rightsText = $Matches.rights
        $rights = switch ($rightsText) {
            'F' { [Security.AccessControl.FileSystemRights]::FullControl }
            'RX' { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
            'R,D' { [Security.AccessControl.FileSystemRights]::Read -bor [Security.AccessControl.FileSystemRights]::Delete }
        }
        $identity = if ($identityText -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($identityText)
        } else {
            [Security.Principal.NTAccount]::new($identityText)
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            $rights,
            [Security.AccessControl.AccessControlType]::Allow))
    }

    try {
        Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
    }
    catch {
        throw "Could not establish the exact restricted ACL on $Path. $($_.Exception.Message)"
    }
}

function Remove-TransferCertificateAndKey(
    [string] $CertificatePath,
    [AllowNull()]
    [string] $PrivateKeyPath
) {
    $removeError = $null
    try {
        Remove-Item -LiteralPath $CertificatePath -DeleteKey -Force -ErrorAction Stop
    }
    catch {
        $removeError = $_
    }

    $certificateRemains = Test-Path -LiteralPath $CertificatePath
    $privateKeyUnverifiable = [string]::IsNullOrWhiteSpace($PrivateKeyPath)
    $privateKeyRemains = -not $privateKeyUnverifiable -and
        (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)
    if ($null -ne $removeError -or $certificateRemains -or $privateKeyUnverifiable -or $privateKeyRemains) {
        $reason = if ($null -ne $removeError) { $removeError.Exception.Message }
            elseif ($privateKeyUnverifiable) { 'the private-key path was unavailable for deletion verification' }
            else { 'residual material was detected' }
        throw "The one-time transfer certificate and private key were not fully removed: $reason."
    }
}

function Grant-PathAccess([string] $Path, [string] $Identity, [string] $Rights) {
    Invoke-IcaclsChecked -Arguments @($Path, '/grant:r', "${Identity}:$Rights") `
        -FailureMessage "Could not grant $Identity access to $Path"
}

function Get-SourceGatewayPortableExecutableMachine([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
                throw "The file '$Path' is not a Windows executable image."
            }
            $stream.Position = 0x3C
            $headerOffset = $reader.ReadInt32()
            if ($headerOffset -lt 64 -or ($headerOffset + 6) -gt $stream.Length) {
                throw "The file '$Path' has an unreadable PE header offset."
            }
            $stream.Position = $headerOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "The file '$Path' carries no PE signature."
            }
            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-SourceGatewayAllowedOdbcDriverRoots([string] $SystemRoot) {
    return @('C:\orant\', ((Join-Path $SystemRoot 'SysWOW64') + '\'))
}

# The Microsoft driver ships at one fixed library; only legacy Oracle-home drivers may float within an approved root.
function Get-SourceGatewayCanonicalOdbcDriverPath([string] $DriverName, [string] $SystemRoot) {
    if ($DriverName -ceq 'Microsoft ODBC for Oracle') {
        return (Join-Path $SystemRoot 'SysWOW64\msorcl32.dll')
    }
    return $null
}

function Get-SourceGatewayCertificateStoreItems([string] $StorePath) {
    if (-not (Test-Path -LiteralPath $StorePath -ErrorAction Stop)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $StorePath -ErrorAction Stop)
}

function Resolve-SourceGatewayWow64DriverPath([string] $RawDriverPath, [string] $SystemRoot) {
    $path = [Environment]::ExpandEnvironmentVariables($RawDriverPath)
    $nativeSystem = (Join-Path $SystemRoot 'System32') + '\'
    if ($path.StartsWith($nativeSystem, [StringComparison]::OrdinalIgnoreCase)) {
        $redirected = Join-Path (Join-Path $SystemRoot 'SysWOW64') $path.Substring($nativeSystem.Length)
        if (Test-Path -LiteralPath $redirected -PathType Leaf) {
            return $redirected
        }
    }
    return $path
}

function Resolve-SourceGatewayFormsOdbcDriver(
    [string] $Dsn,
    [string] $OdbcRoot,
    [string] $SystemRoot,
    [string[]] $AllowedDriverRoots,
    [string] $ExpectedServer
) {
    if ([string]::IsNullOrWhiteSpace($ExpectedServer)) {
        throw 'The 32-bit System DSN preflight requires the approved tooling TNS alias to compare against.'
    }
    $dataSources = Get-ItemProperty -LiteralPath (Join-Path $OdbcRoot 'ODBC.INI\ODBC Data Sources') -ErrorAction Stop
    $driverProperty = $dataSources.PSObject.Properties[$Dsn]
    $driverName = if ($null -eq $driverProperty) { $null } else { [string]$driverProperty.Value }
    if ([string]::IsNullOrWhiteSpace($driverName)) {
        throw "The required 32-bit System DSN '$Dsn' is not registered on the Forms VM."
    }

    $installedDrivers = Get-ItemProperty -LiteralPath (Join-Path $OdbcRoot 'ODBCINST.INI\ODBC Drivers') -ErrorAction Stop
    $installedProperty = $installedDrivers.PSObject.Properties[$driverName]
    if ($null -eq $installedProperty -or [string]$installedProperty.Value -cne 'Installed') {
        throw "The 32-bit ODBC driver '$driverName' behind DSN '$Dsn' is not registered as installed."
    }

    $rawDriver = [string](Get-ItemPropertyValue -LiteralPath (Join-Path $OdbcRoot "ODBCINST.INI\$driverName") `
        -Name Driver -ErrorAction Stop)
    if ([string]::IsNullOrWhiteSpace($rawDriver)) {
        throw "The 32-bit ODBC driver '$driverName' records no driver library path."
    }

    $driverPath = Resolve-SourceGatewayWow64DriverPath -RawDriverPath $rawDriver -SystemRoot $SystemRoot
    $canonicalDriverPath = Get-SourceGatewayCanonicalOdbcDriverPath -DriverName $driverName -SystemRoot $SystemRoot
    if ($null -ne $canonicalDriverPath) {
        if ($driverPath -ine $canonicalDriverPath) {
            throw "The 32-bit ODBC driver '$driverName' behind DSN '$Dsn' does not resolve to its canonical library $canonicalDriverPath."
        }
    }
    else {
        $allowed = $false
        foreach ($root in $AllowedDriverRoots) {
            $normalized = if ($root.EndsWith('\')) { $root } else { $root + '\' }
            if ($driverPath.StartsWith($normalized, [StringComparison]::OrdinalIgnoreCase)) {
                $allowed = $true
                break
            }
        }
        if (-not $allowed) {
            throw "The 32-bit System DSN '$Dsn' does not resolve to an installed driver in an approved location."
        }
    }
    if (-not (Test-Path -LiteralPath $driverPath -PathType Leaf)) {
        throw "The 32-bit System DSN '$Dsn' does not resolve to an installed driver in an approved location."
    }

    $machine = Get-SourceGatewayPortableExecutableMachine -Path $driverPath
    if ($machine -ne 0x014C) {
        throw "The driver behind 32-bit System DSN '$Dsn' is not an x86 image (machine 0x$($machine.ToString('X4')))."
    }

    # The catalog entry only says what the driver name maps to; the DSN carries its own Driver value and
    # its own Server binding, and either can point somewhere else entirely. Read exactly those two named
    # values - never the whole attribute set, which may carry Uid/Pwd on an operator-authored DSN.
    $dsnKey = Join-Path $OdbcRoot "ODBC.INI\$Dsn"
    $rawDsnDriver = [string](Get-ItemPropertyValue -LiteralPath $dsnKey -Name Driver -ErrorAction Stop)
    if ([string]::IsNullOrWhiteSpace($rawDsnDriver)) {
        throw "The 32-bit System DSN '$Dsn' records no driver library path of its own."
    }
    $dsnDriverPath = Resolve-SourceGatewayWow64DriverPath -RawDriverPath $rawDsnDriver -SystemRoot $SystemRoot
    if ($dsnDriverPath -ine $driverPath) {
        throw "The 32-bit System DSN '$Dsn' resolves to driver library $dsnDriverPath instead of the registered '$driverName' library $driverPath."
    }
    $dsnServer = [string](Get-ItemPropertyValue -LiteralPath $dsnKey -Name Server -ErrorAction Stop)
    if ($dsnServer -cne $ExpectedServer) {
        throw "The 32-bit System DSN '$Dsn' is not bound to the approved tooling TNS alias '$ExpectedServer'."
    }

    return [pscustomobject]@{
        DriverName = $driverName
        DriverPath = $driverPath
        DriverMachine = 'x86'
        Server = $dsnServer
    }
}

# Credentials must never be issued against a DSN whose alias resolves somewhere other than the approved
# listener, so the service's own TNS_ADMIN and that directory's tnsnames.ora are both verified verbatim.
function Assert-SourceGatewayLabTnsBinding(
    [string] $ServiceName,
    [string] $TnsAdmin,
    [string] $ExpectedTnsNames
) {
    $serviceProperties = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -ErrorAction Stop
    $environmentProperty = $serviceProperties.PSObject.Properties['Environment']
    $environment = if ($null -eq $environmentProperty) { @() } else { @($environmentProperty.Value) }
    if ($environment -cnotcontains "TNS_ADMIN=$TnsAdmin") {
        throw "The source-gateway service '$ServiceName' does not resolve Oracle Net through the isolated tooling directory $TnsAdmin."
    }
    $tnsNamesPath = Join-Path $TnsAdmin 'tnsnames.ora'
    if (-not (Test-Path -LiteralPath $tnsNamesPath -PathType Leaf)) {
        throw "The isolated tooling TNS configuration $tnsNamesPath is missing."
    }
    if ((Get-Content -LiteralPath $tnsNamesPath -Raw) -cne $ExpectedTnsNames) {
        throw "The isolated tooling TNS configuration $tnsNamesPath does not bind the approved Oracle listener endpoint."
    }
    return $tnsNamesPath
}

function New-SourceGatewayTnsNamesContent(
    [string] $Alias,
    [string] $OracleHost,
    [int] $OraclePort,
    [string] $OracleSid
) {
    if ($Alias -cnotmatch '^[A-Z][A-Z0-9_]{2,31}$') {
        throw "The dedicated tooling TNS alias '$Alias' is not an uppercase bounded identifier."
    }
    $address = [Net.IPAddress]::None
    if (-not [Net.IPAddress]::TryParse($OracleHost, [ref] $address) -or
        $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'The Oracle listener host must be an explicit IPv4 address.'
    }
    if ($OraclePort -lt 1 -or $OraclePort -gt 65535) {
        throw 'The Oracle listener port is outside the TCP port range.'
    }
    if ($OracleSid -cnotmatch '^[A-Za-z][A-Za-z0-9_]{0,15}$') {
        throw 'The Oracle SID is not a bounded identifier.'
    }

    $lines = @(
        "$Alias =",
        '  (DESCRIPTION =',
        "    (ADDRESS = (PROTOCOL = TCP)(HOST = $OracleHost)(PORT = $OraclePort))",
        "    (CONNECT_DATA = (SID = $OracleSid))",
        '  )'
    )
    return (($lines -join "`r`n") + "`r`n")
}

function Merge-SourceGatewayServiceEnvironment(
    [AllowNull()]
    [string[]] $Existing,
    [string] $Name,
    [string] $Value
) {
    $current = @()
    if ($null -ne $Existing) { $current = @($Existing) }
    $prefix = "$Name="
    $matching = @($current | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($matching.Count -gt 1) {
        throw "The source-gateway service environment declares '$Name' more than once."
    }
    if ($matching.Count -eq 1) {
        if ($matching[0] -cne "$prefix$Value") {
            throw "The source-gateway service environment already binds '$Name' to a different value."
        }
        return [pscustomobject]@{ Environment = $current; Changed = $false }
    }
    return [pscustomobject]@{ Environment = @($current + "$prefix$Value"); Changed = $true }
}