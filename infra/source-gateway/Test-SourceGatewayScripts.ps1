[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$common = Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1'
. $common

$script:mockExitCode = 0
$script:scCalls = [Collections.Generic.List[object]]::new()
function sc.exe {
    $script:scCalls.Add(@($args))
    $global:LASTEXITCODE = $script:mockExitCode
}
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

$expectedServiceArguments = @(
    'create',
    'OFM Source Gateway',
    'binPath=',
    '"C:\Program Files\OFM Source Gateway\OracleFormsMigrationFleet.SourceWorker.exe" --serve',
    'start=',
    'delayed-auto',
    'obj=',
    'NT AUTHORITY\LocalService'
)
Invoke-ScChecked -Arguments $expectedServiceArguments -FailureMessage 'unexpected service argument failure'
$actualServiceArguments = @($script:scCalls[$script:scCalls.Count - 1])
if ($actualServiceArguments.Count -ne $expectedServiceArguments.Count -or
    (Compare-Object -ReferenceObject $expectedServiceArguments -DifferenceObject $actualServiceArguments -SyncWindow 0)) {
    throw 'Invoke-ScChecked did not preserve the exact sc.exe service argument tokens.'
}

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

if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
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

    $odbcTestRoot = "HKCU:\Software\OFM-SourceGateway-Test-$([guid]::NewGuid().ToString('N'))"
    $driverStage = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-driver-$([guid]::NewGuid().ToString('N'))"
    try {
        $x86Source = Join-Path $env:SystemRoot 'SysWOW64\kernel32.dll'
        $x64Source = Join-Path $env:SystemRoot 'System32\kernel32.dll'
        if (-not (Test-Path -LiteralPath $x86Source -PathType Leaf) -or
            -not (Test-Path -LiteralPath $x64Source -PathType Leaf)) {
            throw 'The ODBC driver resolution test needs both a SysWOW64 and a System32 image on this host.'
        }
        New-Item -ItemType Directory -Path $driverStage | Out-Null
        $stagedX86 = Join-Path $driverStage 'ofmdrv32.dll'
        $stagedX64 = Join-Path $driverStage 'ofmdrv64.dll'
        $stagedText = Join-Path $driverStage 'ofmdrv-text.dll'
        Copy-Item -LiteralPath $x86Source -Destination $stagedX86
        Copy-Item -LiteralPath $x64Source -Destination $stagedX64
        Set-Content -LiteralPath $stagedText -Value 'not an image' -Encoding ascii

        if ((Get-SourceGatewayPortableExecutableMachine -Path $stagedX86) -ne 0x014C -or
            (Get-SourceGatewayPortableExecutableMachine -Path $stagedX64) -eq 0x014C) {
            throw 'The portable-executable machine reader did not distinguish x86 from x64 images.'
        }
        $notAnImage = try { Get-SourceGatewayPortableExecutableMachine -Path $stagedText; $null } catch { $_ }
        if ($null -eq $notAnImage -or $notAnImage.Exception.Message -notmatch 'not a Windows executable image') {
            throw 'The portable-executable machine reader accepted a file that is not an image.'
        }

        $redirected = Resolve-SourceGatewayWow64DriverPath -RawDriverPath (Join-Path $env:SystemRoot 'system32\kernel32.dll') `
            -SystemRoot $env:SystemRoot
        if ($redirected -cne $x86Source) {
            throw 'A 32-bit hive driver path under System32 must resolve to the physical SysWOW64 image.'
        }
        if ((Resolve-SourceGatewayWow64DriverPath -RawDriverPath $stagedX86 -SystemRoot $env:SystemRoot) -cne $stagedX86) {
            throw 'A driver path outside System32 must be returned unchanged.'
        }
        $defaultRoots = @(Get-SourceGatewayAllowedOdbcDriverRoots -SystemRoot $env:SystemRoot)
        if ($defaultRoots.Count -ne 2 -or $defaultRoots[0] -cne 'C:\orant\' -or
            $defaultRoots[1] -cne ((Join-Path $env:SystemRoot 'SysWOW64') + '\')) {
            throw 'The approved ODBC driver roots are not exactly the Forms Oracle home and the 32-bit system directory.'
        }

        $dataSourcesKey = Join-Path $odbcTestRoot 'ODBC\ODBC.INI\ODBC Data Sources'
        $installedKey = Join-Path $odbcTestRoot 'ODBC\ODBCINST.INI\ODBC Drivers'
        $driverKey = Join-Path $odbcTestRoot 'ODBC\ODBCINST.INI\Microsoft ODBC for Oracle'
        $legacyDriverKey = Join-Path $odbcTestRoot 'ODBC\ODBCINST.INI\OFM Legacy Oracle Test Driver'
        $legacyDsnKey = Join-Path $odbcTestRoot 'ODBC\ODBC.INI\OFM_LEGACY_TEST'
        New-Item -Path $dataSourcesKey -Force | Out-Null
        New-Item -Path $installedKey -Force | Out-Null
        New-Item -Path $driverKey -Force | Out-Null
        New-Item -Path $legacyDriverKey -Force | Out-Null
        New-Item -Path $legacyDsnKey -Force | Out-Null
        $odbcHive = Join-Path $odbcTestRoot 'ODBC'
        $stageRoots = @($driverStage + '\')
        $resolve = {
            param([string] $Dsn, [string] $ExpectedServer = 'OFM_ORCL9I')
            Resolve-SourceGatewayFormsOdbcDriver -Dsn $Dsn -OdbcRoot $odbcHive -SystemRoot $env:SystemRoot `
                -AllowedDriverRoots $stageRoots -ExpectedServer $ExpectedServer
        }

        $canonicalMicrosoftDriver = Get-SourceGatewayCanonicalOdbcDriverPath -DriverName 'Microsoft ODBC for Oracle' `
            -SystemRoot $env:SystemRoot
        if ($canonicalMicrosoftDriver -cne (Join-Path $env:SystemRoot 'SysWOW64\msorcl32.dll') -or
            $null -ne (Get-SourceGatewayCanonicalOdbcDriverPath -DriverName 'OFM Legacy Oracle Test Driver' `
                -SystemRoot $env:SystemRoot)) {
            throw 'The Microsoft ODBC for Oracle driver is not pinned to exactly its canonical 32-bit library.'
        }

        $missingDsn = try { & $resolve 'OFM_GATEWAY_ORACLE9I'; $null } catch { $_ }
        if ($null -eq $missingDsn -or $missingDsn.Exception.Message -notmatch 'is not registered on the Forms VM') {
            throw 'The ODBC preflight accepted an unregistered 32-bit System DSN.'
        }

        New-ItemProperty -Path $dataSourcesKey -Name 'OFM_GATEWAY_ORACLE9I' -Value 'Microsoft ODBC for Oracle' `
            -PropertyType String -Force | Out-Null
        $notInstalled = try { & $resolve 'OFM_GATEWAY_ORACLE9I'; $null } catch { $_ }
        if ($null -eq $notInstalled -or $notInstalled.Exception.Message -notmatch 'is not registered as installed') {
            throw 'The ODBC preflight accepted a DSN whose driver is not a registered installed driver.'
        }

        New-ItemProperty -Path $installedKey -Name 'Microsoft ODBC for Oracle' -Value 'Installed' `
            -PropertyType String -Force | Out-Null
        foreach ($rejectedMicrosoftDriver in @($x86Source, $stagedX86, $stagedX64)) {
            New-ItemProperty -Path $driverKey -Name 'Driver' -Value $rejectedMicrosoftDriver -PropertyType String -Force | Out-Null
            $offCanonical = try { & $resolve 'OFM_GATEWAY_ORACLE9I'; $null } catch { $_ }
            if ($null -eq $offCanonical -or $offCanonical.Exception.Message -notmatch 'canonical library') {
                throw 'The ODBC preflight accepted the Microsoft Oracle driver from a path other than its canonical library.'
            }
        }

        New-ItemProperty -Path $dataSourcesKey -Name 'OFM_LEGACY_TEST' -Value 'OFM Legacy Oracle Test Driver' `
            -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $installedKey -Name 'OFM Legacy Oracle Test Driver' -Value 'Installed' `
            -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $legacyDriverKey -Name 'Driver' -Value $x86Source -PropertyType String -Force | Out-Null
        $outsideAllowlist = try { & $resolve 'OFM_LEGACY_TEST'; $null } catch { $_ }
        if ($null -eq $outsideAllowlist -or $outsideAllowlist.Exception.Message -notmatch 'approved location') {
            throw 'The ODBC preflight accepted a driver outside the approved driver roots.'
        }

        New-ItemProperty -Path $legacyDriverKey -Name 'Driver' -Value $stagedX64 -PropertyType String -Force | Out-Null
        $wrongArchitecture = try { & $resolve 'OFM_LEGACY_TEST'; $null } catch { $_ }
        if ($null -eq $wrongArchitecture -or $wrongArchitecture.Exception.Message -notmatch 'is not an x86 image') {
            throw 'The ODBC preflight accepted a 64-bit driver image for a 32-bit System DSN.'
        }

        New-ItemProperty -Path $legacyDriverKey -Name 'Driver' -Value $stagedX86 -PropertyType String -Force | Out-Null
        $missingDsnDriverValue = try { & $resolve 'OFM_LEGACY_TEST'; $null } catch { $_ }
        if ($null -eq $missingDsnDriverValue) {
            throw 'The ODBC preflight accepted a DSN that records no driver library of its own.'
        }

        New-ItemProperty -Path $legacyDsnKey -Name 'Server' -Value 'OFM_ORCL9I' -PropertyType String -Force | Out-Null
        foreach ($rejectedDsnDriver in @($stagedX64, $x86Source, (Join-Path $driverStage 'absent.dll'))) {
            New-ItemProperty -Path $legacyDsnKey -Name 'Driver' -Value $rejectedDsnDriver -PropertyType String -Force | Out-Null
            $dsnDriverMismatch = try { & $resolve 'OFM_LEGACY_TEST'; $null } catch { $_ }
            if ($null -eq $dsnDriverMismatch -or
                $dsnDriverMismatch.Exception.Message -notmatch 'instead of the registered') {
                throw "The ODBC preflight trusted a good driver catalog entry while the DSN itself pointed at '$rejectedDsnDriver'."
            }
        }

        New-ItemProperty -Path $legacyDsnKey -Name 'Driver' -Value $stagedX86 -PropertyType String -Force | Out-Null
        foreach ($rejectedServer in @('XE', 'XEDED', 'OFM_ORCL9I_OLD', 'ofm_orcl9i', '')) {
            New-ItemProperty -Path $legacyDsnKey -Name 'Server' -Value $rejectedServer -PropertyType String -Force | Out-Null
            $serverMismatch = try { & $resolve 'OFM_LEGACY_TEST'; $null } catch { $_ }
            if ($null -eq $serverMismatch -or
                $serverMismatch.Exception.Message -notmatch 'approved tooling TNS alias') {
                throw "The ODBC preflight accepted a DSN bound to the stale or conflicting alias '$rejectedServer'."
            }
        }

        New-ItemProperty -Path $legacyDsnKey -Name 'Server' -Value 'OFM_ORCL9I' -PropertyType String -Force | Out-Null
        $unpinnedAlias = try { & $resolve 'OFM_LEGACY_TEST' ''; $null } catch { $_ }
        if ($null -eq $unpinnedAlias -or
            $unpinnedAlias.Exception.Message -notmatch 'approved tooling TNS alias to compare against') {
            throw 'The ODBC preflight resolved a DSN without an approved alias to compare it against.'
        }

        $resolved = & $resolve 'OFM_LEGACY_TEST'
        if ($resolved.DriverName -cne 'OFM Legacy Oracle Test Driver' -or $resolved.DriverPath -cne $stagedX86 -or
            $resolved.DriverMachine -cne 'x86' -or $resolved.Server -cne 'OFM_ORCL9I' -or
            @($resolved.PSObject.Properties).Count -ne 4) {
            throw 'The ODBC preflight did not report exactly the registered x86 driver and alias it verified.'
        }
    }
    finally {
        Remove-Item -LiteralPath $odbcTestRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $driverStage -Recurse -Force -ErrorAction SilentlyContinue
    }

    $bootstrapStage = Join-Path ([IO.Path]::GetTempPath()) "ofm-source-gateway-bootstrap-$([guid]::NewGuid().ToString('N'))"
    $bootstrapHost = $env:COMPUTERNAME
    try {
        New-Item -ItemType Directory -Path $bootstrapStage | Out-Null
        $mockCommonPath = Join-Path $bootstrapStage 'MockCommon.ps1'
        Set-Content -LiteralPath $mockCommonPath -Encoding ascii -Value @(
            ". '$($common.Replace("'", "''"))'",
            'function Get-SourceGatewayPortableExecutableMachine { param([string] $Path) return 0x014C }',
            'function Set-RestrictedPathAcl { param([string] $Path, [string[]] $Grants, [string] $TrustedOwnerSid) }'
        )

        $bootstrapText = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Initialize-SourceGatewayLabOracleConnection.ps1') -Raw) -replace "`r`n", "`n"
        $elevationGuard = @(
            'if (-not ([Security.Principal.WindowsPrincipal]::new(',
            '        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(',
            '        [Security.Principal.WindowsBuiltInRole]::Administrator)) {',
            "    throw 'This lab connection bootstrap must run elevated.'",
            '}'
        ) -join "`n"
        if ([regex]::Matches($bootstrapText, [regex]::Escape($elevationGuard)).Count -ne 1) {
            throw 'The lab connection bootstrap no longer carries exactly one verbatim elevation guard.'
        }
        $bootstrapScriptBlock = [scriptblock]::Create($bootstrapText.Replace($elevationGuard, ''))
        $canonicalDriverPath = Get-SourceGatewayCanonicalOdbcDriverPath -DriverName 'Microsoft ODBC for Oracle' `
            -SystemRoot $env:SystemRoot
        $bootstrapTnsNames = New-SourceGatewayTnsNamesContent -Alias 'OFM_ORCL9I' -OracleHost '10.246.0.37' `
            -OraclePort 1521 -OracleSid 'orcl'
        $matchingDsn = [pscustomobject]@{
            DriverName = 'Microsoft ODBC for Oracle'
            Attribute = @{ Server = 'OFM_ORCL9I' }
        }

        function New-MockedBootstrapState {
            param([hashtable] $Overrides = @{})
            $state = @{
                Calls = [Collections.Generic.List[string]]::new()
                DriverRegistration = 'Installed'
                DriverPath = $canonicalDriverPath
                SignatureStatus = 'Valid'
                SignerSubject = 'CN=Microsoft Windows, O=Microsoft Corporation, C=US'
                TnsNamesContent = $bootstrapTnsNames
                Environment = @('ASPNETCORE_ENVIRONMENT=Production')
                ServiceStatus = 'Running'
                StopFailures = 0
                StartFailures = 0
                StartNoOp = $false
                DsnCatalogDriver = 'Microsoft ODBC for Oracle'
                DsnAttributeDriver = $canonicalDriverPath
                DsnAttributeServer = 'OFM_ORCL9I'
                ExistingDsn = @()
                VerifiedDsn = @($matchingDsn)
                DsnReadCount = 0
                TnsNamesPath = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net\tnsnames.ora'
                TnsNamesPresent = $true
                RemoveDsnFailure = $false
                RemoveItemFailurePath = $null
                RemoveEnvFailure = $false
            }
            foreach ($key in $Overrides.Keys) { $state[$key] = $Overrides[$key] }
            return $state
        }

        function Invoke-MockedLabConnectionBootstrap {
            param([hashtable] $State, [switch] $PlanOnly)

            $mockTnsAdmin = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net'
            $mockTnsNames = $State.TnsNamesPath
            $mockServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\OFMSourceGateway'
            $mockDriversKey = 'HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI\ODBC Drivers'
            $mockDriverKey = 'HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI\Microsoft ODBC for Oracle'
            $mockDataSourcesKey = 'HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBC.INI\ODBC Data Sources'
            $mockDsnKey = 'HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBC.INI\OFM_GATEWAY_ORACLE9I'

            function Get-ItemProperty {
                param([string] $LiteralPath, [object] $ErrorAction)
                if ($LiteralPath -ceq $mockDriversKey) {
                    return [pscustomobject]@{ 'Microsoft ODBC for Oracle' = $State.DriverRegistration }
                }
                if ($LiteralPath -ceq $mockDataSourcesKey) {
                    return [pscustomobject]@{ 'OFM_GATEWAY_ORACLE9I' = $State.DsnCatalogDriver }
                }
                if ($LiteralPath -ceq $mockServiceKey) {
                    if ($null -eq $State.Environment) { return [pscustomobject]@{ ImagePath = 'mock' } }
                    return [pscustomobject]@{ Environment = $State.Environment }
                }
                throw "The bootstrap read an unexpected registry path '$LiteralPath'."
            }
            function Get-ItemPropertyValue {
                param([string] $LiteralPath, [string] $Name, [object] $ErrorAction)
                if ($LiteralPath -ceq $mockDsnKey) {
                    if ($Name -ceq 'Driver') { return $State.DsnAttributeDriver }
                    if ($Name -ceq 'Server') { return $State.DsnAttributeServer }
                    throw "The bootstrap read an unexpected DSN attribute '$Name'."
                }
                if ($LiteralPath -cne $mockDriverKey -or $Name -cne 'Driver') {
                    throw "The bootstrap read an unexpected driver registration '$LiteralPath'."
                }
                return $State.DriverPath
            }
            function Get-AuthenticodeSignature {
                param([string] $LiteralPath, [object] $ErrorAction)
                return [pscustomobject]@{
                    Status = $State.SignatureStatus
                    SignerCertificate = [pscustomobject]@{ Subject = $State.SignerSubject }
                }
            }
            # Redirects only the tnsnames.ora write, so an injected create/rollback stays off the real host path.
            function Join-Path {
                param([Parameter(Position = 0)] [string] $Path, [Parameter(Position = 1)] [string] $ChildPath)
                if ($Path -ceq $mockTnsAdmin -and $ChildPath -ceq 'tnsnames.ora') { return $mockTnsNames }
                return [IO.Path]::Combine($Path, $ChildPath)
            }
            function Test-Path {
                param([string] $LiteralPath, [object] $PathType, [object] $ErrorAction)
                if ($LiteralPath -ceq $mockTnsNames) {
                    return ($State.TnsNamesPresent -or [IO.File]::Exists($LiteralPath))
                }
                if ($LiteralPath -ceq 'C:\orant\BIN' -or $LiteralPath -ceq $State.DriverPath -or
                    $LiteralPath -ceq $mockTnsAdmin) {
                    return $true
                }
                throw "The bootstrap probed an unexpected path '$LiteralPath'."
            }
            function Get-Content {
                param([string] $LiteralPath, [switch] $Raw)
                if ($LiteralPath -cne $mockTnsNames) { throw "The bootstrap read an unexpected file '$LiteralPath'." }
                return $State.TnsNamesContent
            }
            function Get-OdbcDsn {
                param([string] $Name, [string] $DsnType, [string] $Platform, [object] $ErrorAction)
                $State.Calls.Add("Get-OdbcDsn:${Name}:${DsnType}:$Platform")
                if ($State.DsnReadCount -eq 0) {
                    $State.DsnReadCount = 1
                    return $State.ExistingDsn
                }
                $State.DsnReadCount = $State.DsnReadCount + 1
                return $State.VerifiedDsn
            }
            function Add-OdbcDsn {
                param([string] $Name, [string] $DriverName, [string] $DsnType, [string] $Platform,
                    [string[]] $SetPropertyValue, [object] $ErrorAction)
                $State.Calls.Add("Add-OdbcDsn:${Name}:${DriverName}:$($SetPropertyValue -join ',')")
            }
            function Remove-OdbcDsn {
                param([string] $Name, [string] $DsnType, [string] $Platform, [object] $ErrorAction)
                $State.Calls.Add("Remove-OdbcDsn:$Name")
                if ($State.RemoveDsnFailure) { throw 'mock DSN removal failure' }
            }
            function New-ItemProperty {
                param([string] $Path, [string] $Name, [string] $PropertyType, [object] $Value, [switch] $Force)
                $State.Calls.Add("New-ItemProperty:$Name=$(@($Value) -join '|')")
                $State.Environment = @($Value)
            }
            function Remove-ItemProperty {
                param([string] $Path, [string] $Name, [object] $ErrorAction)
                $State.Calls.Add("Remove-ItemProperty:$Name")
                if ($State.RemoveEnvFailure) { throw 'mock environment restore failure' }
                $State.Environment = $null
            }
            function New-Item {
                param([string] $ItemType, [string] $Path, [switch] $Force)
                $State.Calls.Add("New-Item:$Path")
            }
            function Remove-Item {
                param([string] $LiteralPath, [switch] $Recurse, [switch] $Force, [object] $ErrorAction)
                $State.Calls.Add("Remove-Item:$LiteralPath")
                if ($null -ne $State.RemoveItemFailurePath -and $LiteralPath -ceq $State.RemoveItemFailurePath) {
                    throw 'mock file removal failure'
                }
            }
            function Get-Service {
                param([string] $Name, [object] $ErrorAction)
                $State.Calls.Add("Get-Service:$Name")
                $controller = [pscustomobject]@{ Status = $State.ServiceStatus; MockState = $State }
                Add-Member -InputObject $controller -MemberType ScriptMethod -Name WaitForStatus -Value { param($Status, $Timeout) }
                Add-Member -InputObject $controller -MemberType ScriptMethod -Name Refresh -Value {
                    $this.Status = $this.MockState.ServiceStatus
                }
                Add-Member -InputObject $controller -MemberType ScriptMethod -Name Stop -Value {
                    $this.MockState.Calls.Add('ServiceController.Stop')
                    if ($this.MockState.StopFailures -gt 0) {
                        $this.MockState.StopFailures = $this.MockState.StopFailures - 1
                        throw 'mock service stop failure'
                    }
                    $this.MockState.ServiceStatus = 'Stopped'
                }
                Add-Member -InputObject $controller -MemberType ScriptMethod -Name Start -Value {
                    $this.MockState.Calls.Add('ServiceController.Start')
                    if ($this.MockState.StartFailures -gt 0) {
                        $this.MockState.StartFailures = $this.MockState.StartFailures - 1
                        throw 'mock service start failure'
                    }
                    if (-not $this.MockState.StartNoOp) { $this.MockState.ServiceStatus = 'Running' }
                }
                return $controller
            }

            $arguments = @{ CommonModulePath = $mockCommonPath }
            if ($PlanOnly) { $arguments['PlanOnly'] = $true }
            return & $bootstrapScriptBlock @arguments
        }

        function Get-MockedBootstrapMutations {
            param([hashtable] $State)
            return @($State.Calls | Where-Object {
                $_ -match '^(Add-OdbcDsn|Remove-OdbcDsn|New-ItemProperty|Remove-ItemProperty|New-Item|Remove-Item|ServiceController\.)'
            })
        }

        $env:COMPUTERNAME = 'OFMFORMS6I'

        $planState = New-MockedBootstrapState
        $plan = Invoke-MockedLabConnectionBootstrap -State $planState -PlanOnly | ConvertFrom-Json
        if ($plan.status -cne 'lab-connection-planned' -or $plan.dsnAction -cne 'create' -or
            $plan.tnsAction -cne 'reuse' -or $plan.serviceEnvironmentAction -cne 'add' -or
            $plan.serviceAction -cne 'restart' -or $plan.driverPath -cne $canonicalDriverPath -or
            @(Get-MockedBootstrapMutations -State $planState).Count -ne 0) {
            throw 'The lab connection plan either misreported the pending service restart or wrote to the host.'
        }

        $env:COMPUTERNAME = 'ofmforms6i'
        $lowercaseHostState = New-MockedBootstrapState
        $lowercasePlan = Invoke-MockedLabConnectionBootstrap -State $lowercaseHostState -PlanOnly | ConvertFrom-Json
        if ($lowercasePlan.status -cne 'lab-connection-planned' -or
            @(Get-MockedBootstrapMutations -State $lowercaseHostState).Count -ne 0) {
            throw 'The lab connection plan refused the Forms VM under the lowercase casing Windows actually reports.'
        }
        $env:COMPUTERNAME = 'OFMFORMS6I'

        $applyState = New-MockedBootstrapState
        $apply = Invoke-MockedLabConnectionBootstrap -State $applyState | ConvertFrom-Json
        $applyMutations = @(Get-MockedBootstrapMutations -State $applyState)
        if ($apply.status -cne 'lab-connection-ready' -or $apply.serviceAction -cne 'restart' -or
            @($applyMutations | Where-Object { $_ -ceq 'ServiceController.Stop' }).Count -ne 1 -or
            @($applyMutations | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 1 -or
            @($applyState.Calls | Where-Object { $_ -cmatch '^Restart-Service' }).Count -ne 0 -or
            @($applyMutations | Where-Object { $_ -cmatch '^Add-OdbcDsn:OFM_GATEWAY_ORACLE9I:Microsoft ODBC for Oracle:Server=OFM_ORCL9I$' }).Count -ne 1 -or
            @($applyMutations | Where-Object { $_ -cmatch '^New-ItemProperty:Environment=.*TNS_ADMIN=' }).Count -ne 1 -or
            $applyState.Calls.IndexOf('ServiceController.Stop') -lt
                $applyState.Calls.IndexOf('New-ItemProperty:Environment=ASPNETCORE_ENVIRONMENT=Production|TNS_ADMIN=C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net') -or
            $applyState.Calls.IndexOf('ServiceController.Start') -lt $applyState.Calls.IndexOf('ServiceController.Stop')) {
            throw 'The applied lab connection did not bind TNS_ADMIN and then restart the service through bounded stop/start control requests.'
        }

        $idempotentState = New-MockedBootstrapState @{
            Environment = @('ASPNETCORE_ENVIRONMENT=Production',
                'TNS_ADMIN=C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net')
            ExistingDsn = @($matchingDsn)
        }
        $idempotent = Invoke-MockedLabConnectionBootstrap -State $idempotentState | ConvertFrom-Json
        if ($idempotent.status -cne 'lab-connection-ready' -or $idempotent.dsnAction -cne 'reuse' -or
            $idempotent.serviceEnvironmentAction -cne 'reuse' -or $idempotent.serviceAction -cne 'none' -or
            @(Get-MockedBootstrapMutations -State $idempotentState).Count -ne 0) {
            throw 'An unchanged lab connection restarted the service or rewrote tooling state.'
        }

        $rollbackState = New-MockedBootstrapState @{
            VerifiedDsn = @([pscustomobject]@{
                DriverName = 'Microsoft ODBC for Oracle'
                Attribute = @{ Server = 'SOMETHING_ELSE' }
            })
        }
        $rollbackFailure = try { Invoke-MockedLabConnectionBootstrap -State $rollbackState; $null } catch { $_ }
        if ($null -eq $rollbackFailure -or $rollbackFailure.Exception.Message -notmatch 'did not verify after it was written' -or
            @($rollbackState.Calls | Where-Object { $_ -ceq 'Remove-OdbcDsn:OFM_GATEWAY_ORACLE9I' }).Count -ne 1 -or
            @($rollbackState.Calls | Where-Object { $_ -ceq 'ServiceController.Stop' }).Count -ne 2 -or
            @($rollbackState.Calls | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 2 -or
            $rollbackState.ServiceStatus -cne 'Running' -or
            (@($rollbackState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw ('A failed lab connection did not restore the prior service environment and running state. ' +
                "Error: $($rollbackFailure.Exception.Message); Status: $($rollbackState.ServiceStatus); " +
                "Env: $(@($rollbackState.Environment) -join '|'); Calls: $($rollbackState.Calls -join ',')")
        }

        # Every rollback step must aggregate its own failure and must not bypass the service recovery.
        $bootstrapTnsPath = [IO.Path]::Combine($bootstrapStage, 'tnsnames.ora')
        foreach ($injected in @(
                @{ Name = 'DSN removal'
                   Overrides = @{ RemoveDsnFailure = $true }
                   Pattern = "^Rollback could not remove the 32-bit System DSN 'OFM_GATEWAY_ORACLE9I' this run created \(mock DSN removal failure\)" },
                @{ Name = 'TNS configuration removal'
                   Overrides = @{ TnsNamesPresent = $false; TnsNamesPath = $bootstrapTnsPath
                       RemoveItemFailurePath = $bootstrapTnsPath }
                   Pattern = '^Rollback could not remove the tooling TNS configuration .+ this run created \(mock file removal failure\)' },
                @{ Name = 'service environment restore'
                   Overrides = @{ Environment = $null; RemoveEnvFailure = $true }
                   Pattern = '^Rollback could not restore the prior service environment \(mock environment restore failure\)' })) {
            $injectedOverrides = $injected.Overrides.Clone()
            $injectedOverrides['VerifiedDsn'] = @([pscustomobject]@{
                DriverName = 'Microsoft ODBC for Oracle'
                Attribute = @{ Server = 'SOMETHING_ELSE' }
            })
            $injectedState = New-MockedBootstrapState $injectedOverrides
            $injectedFailure = try { Invoke-MockedLabConnectionBootstrap -State $injectedState; $null } catch { $_ }
            if ($null -eq $injectedFailure -or $injectedFailure.Exception.Message -notmatch $injected.Pattern -or
                $injectedFailure.Exception.Message -notmatch "after 'The tooling-owned lab connection did not verify after it was written" -or
                @($injectedState.Calls | Where-Object { $_ -ceq 'ServiceController.Stop' }).Count -ne 2 -or
                @($injectedState.Calls | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 2 -or
                $injectedState.ServiceStatus -cne 'Running') {
                throw ("A failed $($injected.Name) rollback step did not aggregate with the primary failure or " +
                    "stopped the service recovery. Error: " +
                    "$(if ($null -ne $injectedFailure) { $injectedFailure.Exception.Message } else { 'no error' }); " +
                    "Status: $($injectedState.ServiceStatus); Calls: $($injectedState.Calls -join ',')")
            }
        }

        # The original defect: stop accepted, start refused, and the restart flag was only set afterwards,
        # so the rollback skipped the restart and left the gateway service down.
        $startFailsOnceState = New-MockedBootstrapState @{ StartFailures = 1 }
        $startFailsOnce = try { Invoke-MockedLabConnectionBootstrap -State $startFailsOnceState; $null } catch { $_ }
        if ($null -eq $startFailsOnce -or $startFailsOnce.Exception.Message -notmatch 'mock service start failure' -or
            $startFailsOnceState.ServiceStatus -cne 'Running' -or
            @($startFailsOnceState.Calls | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 2 -or
            @($startFailsOnceState.Calls | Where-Object { $_ -ceq 'Remove-OdbcDsn:OFM_GATEWAY_ORACLE9I' }).Count -ne 1 -or
            (@($startFailsOnceState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw 'A stop that succeeded and a start that failed did not rescue the service or restore the prior environment.'
        }

        $startAlwaysFailsState = New-MockedBootstrapState @{ StartFailures = 5 }
        $startAlwaysFails = try { Invoke-MockedLabConnectionBootstrap -State $startAlwaysFailsState; $null } catch { $_ }
        if ($null -eq $startAlwaysFails -or
            $startAlwaysFails.Exception.Message -notmatch "^Rollback could not return 'OFMSourceGateway' to its previous Running state" -or
            $startAlwaysFails.Exception.Message -notmatch 'mock service start failure' -or
            $startAlwaysFailsState.ServiceStatus -ceq 'Running' -or
            (@($startAlwaysFailsState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw 'An unrecoverable restart reported silent success instead of surfacing the cleanup failure with the primary failure.'
        }

        $stopFailsState = New-MockedBootstrapState @{ StopFailures = 5 }
        $stopFails = try { Invoke-MockedLabConnectionBootstrap -State $stopFailsState; $null } catch { $_ }
        if ($null -eq $stopFails -or $stopFails.Exception.Message -notmatch 'mock service stop failure' -or
            @($stopFailsState.Calls | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 0 -or
            $stopFailsState.ServiceStatus -cne 'Running' -or
            (@($stopFailsState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw 'A refused stop request did not restore the prior service environment while leaving the service running.'
        }

        # An accepted start control request that never reaches Running must end at the bounded wait, not hang.
        $noProgressState = New-MockedBootstrapState @{ StartNoOp = $true }
        $noProgress = try { Invoke-MockedLabConnectionBootstrap -State $noProgressState; $null } catch { $_ }
        if ($null -eq $noProgress -or
            $noProgress.Exception.Message -notmatch "did not reach Running within the bounded restart window" -or
            $noProgress.Exception.Message -notmatch "^Rollback could not return 'OFMSourceGateway'" -or
            @($noProgressState.Calls | Where-Object { $_ -ceq 'ServiceController.Start' }).Count -ne 2 -or
            (@($noProgressState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw 'A start request that never reached Running was not bounded and reported through the rollback.'
        }

        foreach ($rejectedBinding in @(
                @{ Overrides = @{ ExistingDsn = @($matchingDsn); DsnAttributeDriver = 'C:\orant\BIN\sqora32.dll' }
                   Pattern = 'instead of the registered' },
                @{ Overrides = @{ ExistingDsn = @($matchingDsn); DsnAttributeServer = 'XE' }
                   Pattern = 'approved tooling TNS alias' },
                @{ Overrides = @{ ExistingDsn = @($matchingDsn); DsnAttributeServer = 'OFM_ORCL9I_OLD' }
                   Pattern = 'approved tooling TNS alias' },
                @{ Overrides = @{ ExistingDsn = @($matchingDsn); DsnCatalogDriver = 'Oracle in OraHome92' }
                   Pattern = 'is not registered as installed' },
                @{ Overrides = @{ ExistingDsn = @([pscustomobject]@{
                        DriverName = 'Oracle in OraHome92'; Attribute = @{ Server = 'OFM_ORCL9I' } }) }
                   Pattern = 'Refusing to adopt the pre-existing' })) {
            $rejectedBindingState = New-MockedBootstrapState $rejectedBinding.Overrides
            $bindingRejection = try { Invoke-MockedLabConnectionBootstrap -State $rejectedBindingState; $null } catch { $_ }
            if ($null -eq $bindingRejection -or $bindingRejection.Exception.Message -notmatch $rejectedBinding.Pattern -or
                @(Get-MockedBootstrapMutations -State $rejectedBindingState).Count -ne 0) {
                throw ("The lab connection bootstrap adopted a DSN whose own registry binding was wrong " +
                    "('$($rejectedBinding.Pattern)'): $(if ($null -ne $bindingRejection) { $bindingRejection.Exception.Message } else { 'no error' })")
            }
        }

        # A DSN whose Server drifts after the write must fail verification and roll back, never be reported ready.
        $driftedServerState = New-MockedBootstrapState @{ DsnAttributeServer = 'XE' }
        $driftedServer = try { Invoke-MockedLabConnectionBootstrap -State $driftedServerState; $null } catch { $_ }
        if ($null -eq $driftedServer -or $driftedServer.Exception.Message -notmatch 'approved tooling TNS alias' -or
            @($driftedServerState.Calls | Where-Object { $_ -ceq 'Remove-OdbcDsn:OFM_GATEWAY_ORACLE9I' }).Count -ne 1 -or
            $driftedServerState.ServiceStatus -cne 'Running' -or
            (@($driftedServerState.Environment) -join '|') -cne 'ASPNETCORE_ENVIRONMENT=Production') {
            throw 'A written DSN bound to a stale alias was reported ready instead of verified and rolled back.'
        }

        $stoppedState = New-MockedBootstrapState @{ ServiceStatus = 'Stopped' }
        $stoppedFailure = try { Invoke-MockedLabConnectionBootstrap -State $stoppedState; $null } catch { $_ }
        $stoppedPlanFailure = try {
            Invoke-MockedLabConnectionBootstrap -State (New-MockedBootstrapState @{
                ServiceStatus = 'Stopped'
                Environment = @('ASPNETCORE_ENVIRONMENT=Production',
                    'TNS_ADMIN=C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net')
                ExistingDsn = @($matchingDsn)
            }) -PlanOnly
            $null
        } catch { $_ }
        if ($null -eq $stoppedFailure -or $stoppedFailure.Exception.Message -notmatch 'must be Running' -or
            $null -eq $stoppedPlanFailure -or $stoppedPlanFailure.Exception.Message -notmatch 'must be Running' -or
            @(Get-MockedBootstrapMutations -State $stoppedState).Count -ne 0) {
            throw 'The lab connection reported readiness while the source-gateway service was not running.'
        }

        foreach ($rejected in @(
                @{ Overrides = @{ ExistingDsn = @([pscustomobject]@{
                        DriverName = 'Microsoft ODBC for Oracle'; Attribute = @{ Server = 'OTHER_ALIAS' } }) }
                   Pattern = 'Refusing to adopt the pre-existing' },
                @{ Overrides = @{ TnsNamesContent = "OFM_ORCL9I = (DESCRIPTION = (ADDRESS = (HOST = 10.0.0.9)))`r`n" }
                   Pattern = 'Refusing to rewrite the existing tooling TNS configuration' },
                @{ Overrides = @{ DriverPath = 'C:\orant\BIN\sqora32.dll' }; Pattern = 'canonical library' },
                @{ Overrides = @{ DriverRegistration = 'Pending' }; Pattern = 'not registered as installed' },
                @{ Overrides = @{ SignatureStatus = 'NotSigned' }; Pattern = 'valid Authenticode signature' },
                @{ Overrides = @{ SignerSubject = 'CN=Contoso, O=Contoso Ltd' }; Pattern = 'not signed by Microsoft Corporation' })) {
            $rejectedState = New-MockedBootstrapState $rejected.Overrides
            $rejection = try { Invoke-MockedLabConnectionBootstrap -State $rejectedState; $null } catch { $_ }
            if ($null -eq $rejection -or $rejection.Exception.Message -notmatch $rejected.Pattern -or
                @(Get-MockedBootstrapMutations -State $rejectedState).Count -ne 0) {
                throw "The lab connection bootstrap did not reject '$($rejected.Pattern)' before writing to the host."
            }
        }

        $env:COMPUTERNAME = 'OFMWORKBENCH'
        $wrongHost = try { Invoke-MockedLabConnectionBootstrap -State (New-MockedBootstrapState); $null } catch { $_ }
        if ($null -eq $wrongHost -or $wrongHost.Exception.Message -notmatch 'unexpected host') {
            throw 'The lab connection bootstrap ran on a host other than the Forms VM.'
        }
        $env:COMPUTERNAME = 'OFMFORMS6I'

        foreach ($unpinned in @(
                @{ OdbcDsn = 'OFM_GATEWAY_OTHER' },
                @{ TnsAlias = 'OFM_OTHER' },
                @{ OracleHost = '10.246.0.38' },
                @{ OraclePort = 1522 },
                @{ OracleSid = 'xe' },
                @{ DriverName = 'Oracle in OraHome92' })) {
            $unpinnedArguments = $unpinned.Clone()
            $unpinnedArguments['CommonModulePath'] = $mockCommonPath
            $unpinnedArguments['PlanOnly'] = $true
            $unpinnedFailure = try { & $bootstrapScriptBlock @unpinnedArguments; $null } catch { $_ }
            if ($null -eq $unpinnedFailure -or
                $unpinnedFailure.Exception -isnot [Management.Automation.ParameterBindingException]) {
                throw "The lab connection bootstrap accepted the unpinned argument '$($unpinned.Keys)'."
            }
        }
    }
    finally {
        $env:COMPUTERNAME = $bootstrapHost
        Remove-Item -LiteralPath $bootstrapStage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$certificateStoreAbsent = & {
    function Test-Path { param([string] $LiteralPath, [object] $ErrorAction) return $false }
    function Get-ChildItem { throw 'An absent certificate store must not be enumerated.' }
    Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My'
}
if (@($certificateStoreAbsent).Count -ne 0) {
    throw 'An absent certificate store did not resolve to an empty transfer-certificate collection.'
}
$certificateStoreDenied = & {
    function Test-Path { param([string] $LiteralPath, [object] $ErrorAction) throw 'Access is denied' }
    try { Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My'; $null } catch { $_ }
}
if ($null -eq $certificateStoreDenied -or $certificateStoreDenied.Exception.Message -notmatch 'Access is denied') {
    throw 'A certificate store that could not be probed was treated as empty instead of failing closed.'
}
$certificateEnumerationDenied = & {
    function Test-Path { param([string] $LiteralPath, [object] $ErrorAction) return $true }
    function Get-ChildItem { param([string] $LiteralPath, [object] $ErrorAction) throw 'Enumeration failed' }
    try { Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My'; $null } catch { $_ }
}
if ($null -eq $certificateEnumerationDenied -or
    $certificateEnumerationDenied.Exception.Message -notmatch 'Enumeration failed') {
    throw 'A certificate store enumeration failure was swallowed instead of failing closed.'
}

$expectedTnsNames = "OFM_ORCL9I =`r`n  (DESCRIPTION =`r`n" +
    "    (ADDRESS = (PROTOCOL = TCP)(HOST = 10.246.0.37)(PORT = 1521))`r`n" +
    "    (CONNECT_DATA = (SID = orcl))`r`n  )`r`n"
$actualTnsNames = New-SourceGatewayTnsNamesContent -Alias 'OFM_ORCL9I' -OracleHost '10.246.0.37' `
    -OraclePort 1521 -OracleSid 'orcl'
if ($actualTnsNames -cne $expectedTnsNames) {
    throw 'The dedicated tooling TNS alias is not emitted as the exact reviewed descriptor.'
}
foreach ($rejectedTns in @(
        @{ Alias = 'ofm_orcl9i'; OracleHost = '10.246.0.37'; OraclePort = 1521; OracleSid = 'orcl' },
        @{ Alias = 'OFM_ORCL9I'; OracleHost = 'oracle.lab.internal'; OraclePort = 1521; OracleSid = 'orcl' },
        @{ Alias = 'OFM_ORCL9I'; OracleHost = '10.246.0.37'; OraclePort = 70000; OracleSid = 'orcl' },
        @{ Alias = 'OFM_ORCL9I'; OracleHost = '10.246.0.37'; OraclePort = 1521; OracleSid = 'orcl;XE' })) {
    $tnsFailure = try { New-SourceGatewayTnsNamesContent @rejectedTns; $null } catch { $_ }
    if ($null -eq $tnsFailure) {
        throw "The dedicated tooling TNS alias accepted invalid connection input '$($rejectedTns.Values -join '|')'."
    }
}

$baseEnvironment = @('ASPNETCORE_ENVIRONMENT=Production', 'OFM_GATEWAY_MAX_CONCURRENT_EXTRACTIONS=1')
$addedEnvironment = Merge-SourceGatewayServiceEnvironment -Existing $baseEnvironment -Name 'TNS_ADMIN' -Value 'C:\tns'
if (-not $addedEnvironment.Changed -or $addedEnvironment.Environment.Count -ne 3 -or
    $addedEnvironment.Environment[2] -cne 'TNS_ADMIN=C:\tns' -or
    (Compare-Object -ReferenceObject $baseEnvironment -DifferenceObject @($addedEnvironment.Environment[0..1]) -SyncWindow 0)) {
    throw 'The service-environment merge did not append exactly the isolated TNS directory.'
}
$reusedEnvironment = Merge-SourceGatewayServiceEnvironment -Existing @($addedEnvironment.Environment) `
    -Name 'TNS_ADMIN' -Value 'C:\tns'
if ($reusedEnvironment.Changed -or $reusedEnvironment.Environment.Count -ne 3) {
    throw 'The service-environment merge rewrote an already matching tooling-owned variable.'
}
$conflictEnvironment = try {
    Merge-SourceGatewayServiceEnvironment -Existing @('TNS_ADMIN=C:\orant\NET80\ADMIN') -Name 'TNS_ADMIN' -Value 'C:\tns'
    $null
} catch { $_ }
if ($null -eq $conflictEnvironment -or $conflictEnvironment.Exception.Message -notmatch 'different value') {
    throw 'The service-environment merge overwrote an unmanaged TNS_ADMIN binding.'
}
$duplicateEnvironment = try {
    Merge-SourceGatewayServiceEnvironment -Existing @('TNS_ADMIN=C:\tns', 'TNS_ADMIN=C:\tns') -Name 'TNS_ADMIN' -Value 'C:\tns'
    $null
} catch { $_ }
if ($null -eq $duplicateEnvironment -or $duplicateEnvironment.Exception.Message -notmatch 'more than once') {
    throw 'The service-environment merge accepted a duplicated variable declaration.'
}
$emptyEnvironment = Merge-SourceGatewayServiceEnvironment -Existing $null -Name 'TNS_ADMIN' -Value 'C:\tns'
if (-not $emptyEnvironment.Changed -or $emptyEnvironment.Environment.Count -ne 1) {
    throw 'The service-environment merge did not handle a service with no prior environment.'
}
$singleEnvironment = Merge-SourceGatewayServiceEnvironment -Existing @('ASPNETCORE_ENVIRONMENT=Production') `
    -Name 'TNS_ADMIN' -Value 'C:\tns'
if ($singleEnvironment.Environment.Count -ne 2 -or
    $singleEnvironment.Environment[0] -cne 'ASPNETCORE_ENVIRONMENT=Production' -or
    $singleEnvironment.Environment[1] -cne 'TNS_ADMIN=C:\tns') {
    throw 'The service-environment merge collapsed a single prior variable instead of appending to it.'
}

$tnsBindingAdmin = Join-Path ([IO.Path]::GetTempPath()) 'ofm-mocked-oracle-net'
$tnsBindingPath = Join-Path $tnsBindingAdmin 'tnsnames.ora'
function Invoke-MockedTnsBindingAssertion {
    param([object] $Environment, [bool] $FilePresent = $true, [string] $Content = $expectedTnsNames)

    return & {
        function Get-ItemProperty {
            param([string] $LiteralPath, [object] $ErrorAction)
            if ($LiteralPath -cne 'HKLM:\SYSTEM\CurrentControlSet\Services\OFMSourceGateway') {
                throw "The TNS binding check read an unexpected registry path '$LiteralPath'."
            }
            if ($null -eq $Environment) { return [pscustomobject]@{ ImagePath = 'mock' } }
            return [pscustomobject]@{ Environment = $Environment }
        }
        function Test-Path { param([string] $LiteralPath, [object] $PathType) return $FilePresent }
        function Get-Content { param([string] $LiteralPath, [switch] $Raw) return $Content }
        try {
            Assert-SourceGatewayLabTnsBinding -ServiceName 'OFMSourceGateway' -TnsAdmin $tnsBindingAdmin `
                -ExpectedTnsNames $expectedTnsNames
        } catch { $_ }
    }
}
if ((Invoke-MockedTnsBindingAssertion -Environment @('ASPNETCORE_ENVIRONMENT=Production', "TNS_ADMIN=$tnsBindingAdmin")) -cne $tnsBindingPath) {
    throw 'The approved tooling TNS binding was not accepted by the credential preflight.'
}
foreach ($rejectedBinding in @(
        @{ Environment = $null; Pattern = 'isolated tooling directory' },
        @{ Environment = @('ASPNETCORE_ENVIRONMENT=Production'); Pattern = 'isolated tooling directory' },
        @{ Environment = @('TNS_ADMIN=C:\orant\NET80\ADMIN'); Pattern = 'isolated tooling directory' },
        @{ Environment = @("tns_admin=$tnsBindingAdmin"); Pattern = 'isolated tooling directory' },
        @{ Environment = @("TNS_ADMIN=$tnsBindingAdmin"); FilePresent = $false; Pattern = 'is missing' },
        @{ Environment = @("TNS_ADMIN=$tnsBindingAdmin")
           Content = "OFM_ORCL9I =`r`n  (DESCRIPTION =`r`n    (ADDRESS = (PROTOCOL = TCP)(HOST = 10.246.0.36)(PORT = 1521))`r`n    (CONNECT_DATA = (SID = XE))`r`n  )`r`n"
           Pattern = 'approved Oracle listener endpoint' })) {
    $bindingArguments = @{ Environment = $rejectedBinding.Environment }
    if ($rejectedBinding.ContainsKey('FilePresent')) { $bindingArguments['FilePresent'] = $rejectedBinding.FilePresent }
    if ($rejectedBinding.ContainsKey('Content')) { $bindingArguments['Content'] = $rejectedBinding.Content }
    $bindingFailure = Invoke-MockedTnsBindingAssertion @bindingArguments
    if ($bindingFailure -isnot [Management.Automation.ErrorRecord] -or
        $bindingFailure.Exception.Message -notmatch $rejectedBinding.Pattern) {
        throw "The credential preflight accepted an unverified Oracle Net binding ('$($rejectedBinding.Pattern)')."
    }
}

$bootstrap = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Initialize-SourceGatewayLabOracleConnection.ps1') -Raw
foreach ($bootstrapGuard in @(
        "if (`$env:COMPUTERNAME -ine 'OFMFORMS6I')",
        'Refusing to adopt the pre-existing 32-bit System DSN',
        'Refusing to rewrite the existing tooling TNS configuration',
        'does not carry a valid Authenticode signature',
        'is not signed by Microsoft Corporation',
        'Restart-SourceGatewayServiceToRunning -Name $serviceName',
        '$controller.Stop()',
        '$controller.Start()',
        'Invoke-SourceGatewayCleanupStep',
        'ExpectedServer $TnsAlias',
        'must be Running so the new environment can be verified',
        "Add-OdbcDsn -Name `$OdbcDsn -DriverName `$DriverName -DsnType System -Platform '32-bit'",
        "Get-OdbcDsn -Name `$OdbcDsn -DsnType System -Platform '32-bit'")) {
    if ($bootstrap -cnotmatch [regex]::Escape($bootstrapGuard)) {
        throw "The lab connection bootstrap is missing '$bootstrapGuard'."
    }
}

$hostGuardPriorName = $env:COMPUTERNAME
try {
    $hostGuardSites = @()
    foreach ($hostGuardFile in @(
            (Join-Path $PSScriptRoot 'Invoke-SourceGatewayOracleCredentialProvision.ps1'),
            (Join-Path $PSScriptRoot 'Initialize-SourceGatewayLabOracleConnection.ps1'),
            (Join-Path (Split-Path -Parent $PSScriptRoot) 'source-lab\forms6i\New-SourceGatewayOracleCredential.ps1'))) {
        $hostGuardAst = [Management.Automation.Language.Parser]::ParseFile($hostGuardFile, [ref] $null, [ref] $null)
        $hostGuardSites += @($hostGuardAst.FindAll({
                param($node)
                $node -is [Management.Automation.Language.BinaryExpressionAst] -and
                    $node.Left.Extent.Text -ceq '$env:COMPUTERNAME'
            }, $true) | ForEach-Object {
                [pscustomobject]@{
                    Site = "$(Split-Path -Leaf $hostGuardFile):$($_.Extent.StartLineNumber)"
                    Expected = $_.Right.Extent.Text.Trim("'")
                    Rejects = [scriptblock]::Create(
                        "param([string] `$Name) `$env:COMPUTERNAME = `$Name; return [bool] ($($_.Extent.Text))")
                }
            })
    }
    if ($hostGuardSites.Count -ne 4) {
        throw "The Windows host identity is guarded at $($hostGuardSites.Count) sites rather than the four reviewed legs."
    }
    foreach ($hostGuardSite in $hostGuardSites) {
        $hostGuardExpected = $hostGuardSite.Expected
        $hostGuardMixed = -join (0..($hostGuardExpected.Length - 1) | ForEach-Object {
            if ($_ % 2) { ([string]$hostGuardExpected[$_]).ToLowerInvariant() } else { ([string]$hostGuardExpected[$_]).ToUpperInvariant() }
        })
        foreach ($hostGuardAccepted in @($hostGuardExpected.ToLowerInvariant(), $hostGuardExpected.ToUpperInvariant(), $hostGuardMixed)) {
            if (& $hostGuardSite.Rejects $hostGuardAccepted) {
                throw "The host guard at $($hostGuardSite.Site) refused '$hostGuardAccepted', which is the same Windows identity as '$hostGuardExpected'."
            }
        }
        foreach ($hostGuardRejected in @('OFMWORKBENCH', '', "X$hostGuardExpected", "${hostGuardExpected}X", " $hostGuardExpected", "$hostGuardExpected ")) {
            if (-not (& $hostGuardSite.Rejects $hostGuardRejected)) {
                throw "The host guard at $($hostGuardSite.Site) accepted '$hostGuardRejected', which is not '$hostGuardExpected'."
            }
        }
    }
}
finally {
    $env:COMPUTERNAME = $hostGuardPriorName
}
if ($bootstrap -match 'Uid=|Pwd=|Password' -or $bootstrap -match 'NET80\\ADMIN') {
    throw 'The lab connection bootstrap must hold no credential material and must not touch the Forms TNS configuration.'
}
$bootstrapWrite = $bootstrap.IndexOf('$createdDsn = $true')
$bootstrapRollback = $bootstrap.IndexOf('if (-not $completed) {')
$bootstrapVerify = $bootstrap.IndexOf('did not verify after it was written')
$bootstrapRestartFlag = $bootstrap.IndexOf('$serviceRestartAttempted = $true')
$bootstrapRestartCall = $bootstrap.IndexOf('Restart-SourceGatewayServiceToRunning -Name $serviceName')
if ($bootstrapWrite -lt 0 -or $bootstrapVerify -lt $bootstrapWrite -or $bootstrapRollback -lt $bootstrapVerify -or
    $bootstrapRestartFlag -lt 0 -or $bootstrapRestartCall -lt 0 -or $bootstrapRestartFlag -gt $bootstrapRestartCall -or
    $bootstrap -match 'Restart-Service -Name' -or
    $bootstrap -notmatch 'if \(\$PlanOnly\) \{' -or
    $bootstrap -notmatch '-Value \$previousEnvironment -Force') {
    throw 'The lab connection bootstrap must plan, verify after writing, roll back only what it created, and mark the restart attempt before issuing the control request.'
}
foreach ($guardedBranch in @('createdDsn', 'createdTnsNames', 'createdDirectory')) {
    if ($bootstrap -cnotmatch "(?s)if \(\`$$guardedBranch\) \{\s*Invoke-SourceGatewayCleanupStep") {
        throw "The lab connection rollback step for `$$guardedBranch does not run through the aggregating cleanup helper."
    }
}
if ($bootstrap.Substring($bootstrapRollback) -match 'SilentlyContinue' -or
    $bootstrap.IndexOf('if ($serviceRestartAttempted) {') -lt $bootstrapRollback) {
    throw 'The lab connection rollback must suppress no cleanup failure and must attempt the service recovery last.'
}

$provision = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-SourceGatewayOracleCredentialProvision.ps1') -Raw
if ($provision -cnotmatch [regex]::Escape("'Initialize-SourceGatewayLabOracleConnection.ps1',") -or
    $provision -cnotmatch [regex]::Escape("if (`$Operation -eq 'InitializeLabConnection')") -or
    ($provision | Select-String -Pattern 'Initialize-SourceGatewayLabOracleConnection\.ps1' -AllMatches).Matches.Count -ne 2) {
    throw 'The provisioning entry point must carry the bootstrap in its reviewed bundle and invoke it from one explicit stage.'
}
$validateBranch = $provision.IndexOf("if (`$Operation -eq 'ValidatePrerequisites')")
$validateBranchEnd = $provision.IndexOf("if (`$Operation -eq 'NewTransferKey')")
if ($validateBranch -lt 0 -or $validateBranchEnd -lt $validateBranch -or
    $provision.Substring($validateBranch, $validateBranchEnd - $validateBranch) -match
        'Add-OdbcDsn|New-Item|Set-Content|New-ItemProperty|WriteAllText') {
    throw 'The prerequisite validation stage must not write the lab connection.'
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
$serviceDefinitionCalls = @($installer | Select-String -Pattern "Invoke-ScChecked -Arguments @\('(create|config)'.+" -AllMatches).Matches.Value
if ($serviceDefinitionCalls.Count -ne 2 -or
    @($serviceDefinitionCalls | Where-Object { $_ -match '"(binPath=|obj=)\s' -or $_ -match "'start=\s" }).Count -ne 0 -or
    @($serviceDefinitionCalls | Where-Object { $_ -notmatch "'binPath=',\s*\`$binaryPath" -or $_ -notmatch "'start=',\s*'delayed-auto'" -or $_ -notmatch "'obj=',\s*\`$serviceAccount" }).Count -ne 0) {
    throw 'Service create/config calls must pass each sc.exe option name and value as separate argument tokens.'
}
$expectedBinaryPathConstruction = '$binaryPath = (''\"{0}\" --serve'' -f $workerDestination)'
if ($installer -notmatch [regex]::Escape($expectedBinaryPathConstruction)) {
    throw 'The service binary path must preserve its embedded executable quotes through Windows PowerShell 5.1 native argument marshalling.'
}
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
$restoreServiceConfiguration = $installer.IndexOf("'binPath=', `$previousServiceConfiguration.NativeBinaryPath")
$restoreRunningService = $installer.LastIndexOf('if ($serviceWasRunning) { Start-Service -Name $serviceName -ErrorAction Stop }')
if ($rollback -lt 0 -or $installer -notmatch 'Copy-Item -LiteralPath \$workerBackup -Destination \$workerDestination' -or
    $installer -notmatch 'NativeBinaryPath = \$previousBinaryPath\.Replace\(''"'', ''\\"''\)' -or
    $installer -notmatch 'previousServiceEnvironment' -or $restoreServiceConfiguration -lt $rollback -or
    $restoreRunningService -lt $restoreServiceConfiguration) {
    throw 'The installer must restore prior service content, SCM configuration, environment, and running state after a failed rerun.'
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

Import-Module (Join-Path $PSScriptRoot 'SourceGatewayCredentialTransfer.psm1') -Force
$mismatchedStoreCertificates = Get-SourceGatewayTransferCertificates -TransferId ('a' * 32) -Certificates @(
    [pscustomobject]@{ Subject = 'CN=Some Other Certificate'; FriendlyName = 'unrelated' },
    [pscustomobject]@{ Subject = "CN=ofm source gateway credential transfer $('a' * 32)"; FriendlyName = '' }
)
if (@($mismatchedStoreCertificates).Count -ne 0 -or
    @(Get-SourceGatewayTransferCertificates -TransferId ('a' * 32) -Certificates @()).Count -ne 0) {
    throw 'The transfer-certificate filter adopted certificates that are not the exact one-time binding.'
}

# Get-SourceGatewayCertificateStoreItems enumerates, so an empty store reaches the call site as $null and
# AllowEmptyCollection does not admit null. Both cleanup call sites must wrap the result in @(...).
$absentStoreComposition = & {
    function Test-Path { param([string] $LiteralPath, [object] $ErrorAction) return $false }
    function Get-ChildItem { throw 'An absent certificate store must not be enumerated.' }
    try {
        @(Get-SourceGatewayTransferCertificates -TransferId ('b' * 32) `
            -Certificates @(Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My'))
    } catch { $_ }
}
if ($absentStoreComposition -is [Management.Automation.ErrorRecord] -or @($absentStoreComposition).Count -ne 0) {
    throw 'Transfer-key cleanup against an absent certificate store did not resolve to no transfer certificates.'
}
$unwrappedStoreComposition = & {
    function Test-Path { param([string] $LiteralPath, [object] $ErrorAction) return $false }
    function Get-ChildItem { throw 'An absent certificate store must not be enumerated.' }
    try {
        Get-SourceGatewayTransferCertificates -TransferId ('b' * 32) `
            -Certificates (Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My')
        $null
    } catch { $_ }
}
if ($null -eq $unwrappedStoreComposition -or
    $unwrappedStoreComposition.Exception.Message -notmatch 'because it is null') {
    throw 'The unwrapped empty certificate store no longer reproduces the null-binding failure the call sites must avoid.'
}
$oracleBootstrapWrapSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-SourceGatewayOracleCredentialProvision.ps1') -Raw
if ([regex]::Matches($oracleBootstrapWrapSource, '-Certificates \(Get-SourceGatewayCertificateStoreItems').Count -ne 0 -or
    [regex]::Matches($oracleBootstrapWrapSource, '-Certificates @\(Get-SourceGatewayCertificateStoreItems').Count -ne 2) {
    throw 'Both transfer-material cleanup call sites must pass the enumerated certificate store as an explicit array.'
}
$testRsa = [Security.Cryptography.RSA]::Create(2048)
$testCertificate = $null
$testPublicCertificate = $null
$testPlaintext = [Text.Encoding]::UTF8.GetBytes('offline-transfer-round-trip')
$testCiphertext = $null
$testDecrypted = $null
try {
    $testRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=OFM Offline Transfer Test', $testRsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $testCertificate = $testRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddHours(1))
    $testPublicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $testCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $testCiphertext = Protect-SourceGatewayCredentialBytes -Certificate $testPublicCertificate -Plaintext $testPlaintext
    $testDecrypted = $testRsa.Decrypt($testCiphertext, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    if ($testCiphertext -isnot [byte[]] -or
        [Text.Encoding]::UTF8.GetString($testDecrypted) -cne 'offline-transfer-round-trip') {
        throw 'The shared credential transfer helper did not preserve an OAEP-SHA256 byte-array round trip.'
    }
}
finally {
    if ($null -ne $testDecrypted) { [Array]::Clear($testDecrypted, 0, $testDecrypted.Length) }
    if ($null -ne $testCiphertext) { [Array]::Clear($testCiphertext, 0, $testCiphertext.Length) }
    [Array]::Clear($testPlaintext, 0, $testPlaintext.Length)
    if ($null -ne $testPublicCertificate) { $testPublicCertificate.Dispose() }
    if ($null -ne $testCertificate) { $testCertificate.Dispose() }
    $testRsa.Dispose()
}

$passwords = @(1..500 | ForEach-Object { New-SourceGatewayOraclePassword })
if (@($passwords | Where-Object { $_ -cnotmatch '^[A-Za-z][A-Za-z0-9]{29}$' }).Count -ne 0 -or
    @($passwords | Sort-Object -Unique).Count -le 1) {
    throw 'The Oracle password generator violated the 30-character letter-first alphanumeric contract.'
}
$windowsPowerShell = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and $env:SystemRoot) {
    Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
}
if ($windowsPowerShell -and (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
    $modulePath = (Join-Path $PSScriptRoot 'SourceGatewayCredentialTransfer.psm1').Replace("'", "''")
    $windowsPassword = & $windowsPowerShell -NoProfile -NonInteractive -Command `
        "Import-Module '$modulePath' -Force; New-SourceGatewayOraclePassword" 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]$windowsPassword -cnotmatch '^[A-Za-z][A-Za-z0-9]{29}$') {
        throw 'The Oracle password generator did not execute under Windows PowerShell 5.1.'
    }
}

$knownPassword = 'Abc123456789012345678901234567'
$provisioningSql = New-SourceGatewayOracleProvisioningSql
$allProvisioningSql = $provisioningSql.BootstrapSql + "`n" + $provisioningSql.ReconciliationSql
$dictionaryViews = @(
    'DBA_OBJECTS', 'DBA_TABLES', 'DBA_TAB_COLUMNS', 'DBA_CONSTRAINTS', 'DBA_CONS_COLUMNS',
    'DBA_SEQUENCES', 'DBA_SOURCE', 'DBA_INDEXES', 'DBA_IND_COLUMNS', 'DBA_TAB_PRIVS',
    'DBA_COL_PRIVS', 'DBA_TRIGGERS', 'DBA_DEPENDENCIES'
)
foreach ($requiredSql in @(
        'create profile OFM_GATEWAY_TOOLING',
        'profile OFM_GATEWAY_TOOLING',
        'create user OFM_GATEWAY_RO identified externally',
        'account lock',
        "where grantee = 'OFM_GATEWAY_RO'",
        'create role OFM_GATEWAY_SOURCE_RO',
        'grant create session to OFM_GATEWAY_RO',
        'grant select on SYS.',
        "object_type = 'TABLE'",
        'grant select on MERIDIAN.',
        'grant OFM_GATEWAY_SOURCE_RO to OFM_GATEWAY_RO',
        'from dba_sys_privs',
        'from dba_role_privs',
        'from dba_tab_privs',
        'from dba_col_privs',
        'from dba_policies',
        'from dba_audit_policies',
        'from v$pwfile_users',
        'from proxy_users',
        'connect by prior granted_role = grantee',
        'DBA_DEPENDENCIES',
        'Final OFM gateway privileges do not exactly match the read-only contract')) {
    if ($allProvisioningSql -cnotmatch [regex]::Escape($requiredSql)) {
        throw "Oracle provisioning SQL is missing '$requiredSql'."
    }
}
foreach ($dictionaryView in $dictionaryViews) {
    if ($provisioningSql.ReconciliationSql -cnotmatch
        [regex]::Escape("grant select on SYS.$dictionaryView to OFM_GATEWAY_SOURCE_RO")) {
        throw "Oracle provisioning SQL does not directly grant the required SYS.$dictionaryView view."
    }
}
foreach ($adoptionGuard in @(
        "owner in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')",
        "username in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')",
        "proxy in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')",
        "client in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')",
        "profile = 'OFM_GATEWAY_TOOLING' and username <> 'OFM_GATEWAY_RO'",
        "(grantee = 'SYS' and admin_option = 'YES')")) {
    if ($provisioningSql.BootstrapSql -cnotmatch [regex]::Escape($adoptionGuard)) {
        throw "Oracle foreign-account adoption is missing '$adoptionGuard'."
    }
}
if ($provisioningSql.ReconciliationSql -cnotmatch
    [regex]::Escape("from dba_policies where object_owner = 'MERIDIAN'") -or
    $provisioningSql.ReconciliationSql -cnotmatch
    [regex]::Escape("from dba_audit_policies where object_schema = 'MERIDIAN'") -or
    $provisioningSql.ReconciliationSql -cnotmatch [regex]::Escape("object_type = 'TABLE'") -or
    $provisioningSql.ReconciliationSql -match [regex]::Escape("object_type = 'VIEW'")) {
    throw 'Oracle grants must fail on MERIDIAN VPD/FGA policies and cover MERIDIAN tables only.'
}
if ($provisioningSql.BootstrapSql -cnotmatch [regex]::Escape("(grantee = 'SYS' and admin_option = 'YES')") -or
    $provisioningSql.ReconciliationSql -cnotmatch [regex]::Escape("(grantee = 'SYS' and admin_option = 'YES')") -or
    $provisioningSql.ReconciliationSql -cnotmatch
        [regex]::Escape("where grantee = 'SYS' and granted_role = 'OFM_GATEWAY_SOURCE_RO' and admin_option = 'YES'") -or
    $provisioningSql.ReconciliationSql -match 'grantee\s*<>\s*''OFM_GATEWAY_RO''') {
    throw 'Oracle 9i role reconciliation must accept only the automatic SYS creator grant with ADMIN OPTION.'
}
if ($provisioningSql.ReconciliationSql.IndexOf('from dba_audit_policies') -gt
    $provisioningSql.ReconciliationSql.IndexOf("grant create session to OFM_GATEWAY_RO")) {
    throw 'FGA policy refusal must run before the first privilege grant.'
}
if ($provisioningSql.ReconciliationSql -match 'account unlock' -or
    $provisioningSql.UnlockSql -cnotmatch 'alter user OFM_GATEWAY_RO account unlock' -or
    $provisioningSql.LockSql -cnotmatch "account_status = 'LOCKED'") {
    throw 'The Oracle account must remain locked through reconciliation and use separate verified unlock/relock stages.'
}
foreach ($forbiddenSql in @(
        $knownPassword,
        'SELECT_CATALOG_ROLE',
        'SELECT ANY',
        'EXECUTE ANY',
        'GRANT DBA',
        'grant execute on MERIDIAN.',
        "object_type in ('TABLE', 'VIEW')",
        "object_type in ('TABLE', 'SEQUENCE')")) {
    if ($allProvisioningSql -match [regex]::Escape($forbiddenSql)) {
        throw "Oracle provisioning SQL contains forbidden broad privilege '$forbiddenSql'."
    }
}
$actualGrantContract = @(Get-SourceGatewayOracleGrantContract)
$expectedGrantContract = @(
    'CREATE SESSION',
    'OFM_GATEWAY_SOURCE_RO',
    'SELECT ON MERIDIAN TABLES',
    'SELECT ON 13 SYS DBA DICTIONARY VIEWS'
)
if (($actualGrantContract -join "`n") -cne ($expectedGrantContract -join "`n")) {
    throw 'The reported Oracle grant contract changed from the exact least-privilege allowlist.'
}

$script:capturedSqlPlusStartInfo = $null
$script:capturedSqlPlusLines = [Collections.Generic.List[string]]::new()
$script:mockSqlPlusOutputs = [Collections.Generic.Queue[string]]::new()
function New-MockSqlPlusProcess([string] $Output, [int] $ExitCode = 0) {
    $mockInput = [pscustomobject]@{}
    $mockInput | Add-Member ScriptMethod WriteLine { param($value) $script:capturedSqlPlusLines.Add([string]$value) }
    $mockInput | Add-Member ScriptMethod Close { }
    $outputReader = [pscustomobject]@{ Value = $Output }
    $outputReader | Add-Member ScriptMethod ReadToEndAsync { [Threading.Tasks.Task[string]]::FromResult([string]$this.Value) }
    $errorReader = [pscustomobject]@{ Value = '' }
    $errorReader | Add-Member ScriptMethod ReadToEndAsync { [Threading.Tasks.Task[string]]::FromResult([string]$this.Value) }
    $mockProcess = [pscustomobject]@{
        StandardInput = $mockInput
        StandardOutput = $outputReader
        StandardError = $errorReader
        ExitCode = $ExitCode
    }
    $mockProcess | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
    $mockProcess | Add-Member ScriptMethod Kill { }
    $mockProcess | Add-Member ScriptMethod Dispose { }
    return $mockProcess
}
@(
    'OFM_STAGE_BOOTSTRAP_OK',
    'OFM_STAGE_RELOCK_OK',
    'OFM_STAGE_PASSWORD_OK',
    'OFM_STAGE_RECONCILIATION_OK',
    'OFM_STAGE_UNLOCK_OK',
    "OFM_LOGIN_OK:OFM_GATEWAY_RO`nOFM_STAGE_LOGIN_OK"
) | ForEach-Object { $script:mockSqlPlusOutputs.Enqueue($_) }
Invoke-SourceGatewaySqlPlus -SqlPlusPath 'mock-sqlplus' -ProvisioningSql $provisioningSql `
    -Password $knownPassword -ProcessFactory {
    param($startInfo)
    $script:capturedSqlPlusStartInfo = $startInfo
    return New-MockSqlPlusProcess -Output $script:mockSqlPlusOutputs.Dequeue()
}
$passwordAnswerIndexes = @(for ($index = 0; $index -lt $script:capturedSqlPlusLines.Count; $index++) {
        if ($script:capturedSqlPlusLines[$index] -ceq $knownPassword) { $index }
    })
$nonAnswerInput = @($script:capturedSqlPlusLines | Where-Object { $_ -cne $knownPassword }) -join "`n"
if ($script:capturedSqlPlusStartInfo.Arguments -cne '-S /nolog' -or
    $script:capturedSqlPlusStartInfo.Arguments -match [regex]::Escape($knownPassword) -or
    $passwordAnswerIndexes.Count -ne 3 -or
    $passwordAnswerIndexes[1] -ne ($passwordAnswerIndexes[0] + 1) -or
    $nonAnswerInput -match [regex]::Escape($knownPassword) -or
    $script:capturedSqlPlusLines[$passwordAnswerIndexes[0] - 1] -cne 'password OFM_GATEWAY_RO' -or
    $script:capturedSqlPlusLines[$passwordAnswerIndexes[2] - 2] -cne 'connect' -or
    $script:capturedSqlPlusLines[$passwordAnswerIndexes[2] - 1] -cne 'OFM_GATEWAY_RO') {
    throw 'SQL*Plus provisioning did not keep the generated password exclusively in redirected prompt answers.'
}
if ($script:capturedSqlPlusLines.IndexOf([string]$provisioningSql.ReconciliationSql) -gt
    $script:capturedSqlPlusLines.IndexOf([string]$provisioningSql.UnlockSql)) {
    throw 'SQL*Plus unlocked the Oracle account before final privilege reconciliation passed.'
}
$bootstrapIndex = $script:capturedSqlPlusLines.IndexOf([string]$provisioningSql.BootstrapSql)
$preRotationLockIndex = $script:capturedSqlPlusLines.IndexOf([string]$provisioningSql.LockSql)
if ($bootstrapIndex -lt 0 -or $preRotationLockIndex -le $bootstrapIndex -or
    $preRotationLockIndex -ge $passwordAnswerIndexes[0]) {
    throw 'An adopted Oracle account was not locked and verified before password rotation.'
}

$failureCases = @(
    [pscustomobject]@{
        Prefix = @('OFM_STAGE_BOOTSTRAP_OK')
        Output = ''
        ExpectedCodes = @()
    },
    [pscustomobject]@{
        Prefix = @('OFM_STAGE_BOOTSTRAP_OK', 'OFM_STAGE_RELOCK_OK')
        Output = 'ORA-20006: sensitive text must not escape'
        ExpectedCodes = @('ORA-20006')
    },
    [pscustomobject]@{
        Prefix = @('OFM_STAGE_BOOTSTRAP_OK', 'OFM_STAGE_RELOCK_OK', 'OFM_STAGE_PASSWORD_OK',
            'OFM_STAGE_RECONCILIATION_OK', 'OFM_STAGE_UNLOCK_OK')
        Output = "ORA-01017: sensitive text must not escape`nSP2-0306: sensitive text must not escape"
        ExpectedCodes = @('ORA-01017', 'SP2-0306')
    }
)
foreach ($failureCase in $failureCases) {
    $script:capturedSqlPlusLines.Clear()
    $script:mockSqlPlusOutputs.Clear()
    foreach ($prefixOutput in $failureCase.Prefix) { $script:mockSqlPlusOutputs.Enqueue($prefixOutput) }
    $script:mockSqlPlusOutputs.Enqueue($failureCase.Output)
    $script:mockSqlPlusOutputs.Enqueue('OFM_STAGE_RELOCK_OK')
    $failure = try {
        Invoke-SourceGatewaySqlPlus -SqlPlusPath 'mock-sqlplus' -ProvisioningSql $provisioningSql `
            -Password $knownPassword -ProcessFactory {
            param($startInfo)
            return New-MockSqlPlusProcess -Output $script:mockSqlPlusOutputs.Dequeue()
        }
        $null
    }
    catch { $_ }
    if ($null -eq $failure -or
        $script:capturedSqlPlusLines -cnotcontains [string]$provisioningSql.LockSql -or
        $failure.Exception.Message -match 'sensitive text must not escape') {
        throw 'SQL*Plus marker/error refusal did not fail closed, sanitize output, and verify account relock.'
    }
    foreach ($expectedCode in $failureCase.ExpectedCodes) {
        if ($failure.Exception.Message -notmatch [regex]::Escape($expectedCode)) {
            throw "SQL*Plus refusal did not report observed error code '$expectedCode'."
        }
    }
}

foreach ($bootstrapRejection in @('ORA-20002: Existing OFM gateway principals are not tooling-owned.', '')) {
    $script:capturedSqlPlusLines.Clear()
    $script:mockSqlPlusOutputs.Clear()
    $script:mockSqlPlusOutputs.Enqueue($bootstrapRejection)
    $script:mockSqlPlusOutputs.Enqueue('OFM_STAGE_RELOCK_OK')
    $failure = try {
        Invoke-SourceGatewaySqlPlus -SqlPlusPath 'mock-sqlplus' -ProvisioningSql $provisioningSql `
            -Password $knownPassword -ProcessFactory {
            param($startInfo)
            return New-MockSqlPlusProcess -Output $script:mockSqlPlusOutputs.Dequeue()
        }
        $null
    }
    catch { $_ }
    if ($null -eq $failure -or
        $script:capturedSqlPlusLines -ccontains [string]$provisioningSql.LockSql -or
        $script:mockSqlPlusOutputs.Count -ne 1) {
        throw 'A bootstrap rejection mutated an Oracle account whose tooling ownership was not proven.'
    }
}
$credentialScript = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'infra/source-lab/forms6i/New-SourceGatewayOracleCredential.ps1') -Raw
if ($credentialScript -notmatch '\$accountProvisioned = \$true' -or
    $credentialScript -notmatch 'if \(-not \$completed -and \$accountProvisioned -and') {
    throw 'The credential host script must relock only after provisioning proved tooling ownership.'
}

$transferTestRoot = Join-Path ([IO.Path]::GetTempPath()) "ofm-transfer-cleanup-test-$([guid]::NewGuid().ToString('N'))"
try {
    [void](New-Item -ItemType Directory -Path $transferTestRoot)
    $transferId = '0123456789abcdef0123456789abcdef'
    if (@(Get-SourceGatewayTransferCertificates -TransferId $transferId -Certificates @()).Count -ne 0) {
        throw 'Transfer cleanup must accept an empty certificate store when no key was created.'
    }
    $corruptedMetadata = Join-Path $transferTestRoot "credential-transfer-$transferId.json"
    Set-Content -LiteralPath $corruptedMetadata -Value '{not-json' -Encoding utf8
    if ($null -ne (Read-SourceGatewayTransferMetadata -TransferId $transferId `
            -MetadataPath $corruptedMetadata).PrivateKeyName) {
        throw 'Corrupted transfer metadata unexpectedly supplied a private-key binding.'
    }
    Set-Content -LiteralPath $corruptedMetadata -Value '{"transferId":"0123456789abcdef0123456789abcdef"}' -Encoding utf8
    if ($null -ne (Read-SourceGatewayTransferMetadata -TransferId $transferId `
            -MetadataPath $corruptedMetadata).PrivateKeyName) {
        throw 'Partial transfer metadata unexpectedly supplied a private-key binding.'
    }
    $bound = [pscustomobject]@{ Subject = "CN=OFM Source Gateway Credential Transfer $transferId"; FriendlyName = ''; Name = 'bound' }
    $friendlyBound = [pscustomobject]@{ Subject = 'CN=unrelated'; FriendlyName = "OFM Source Gateway Credential Transfer $transferId"; Name = 'friendly' }
    $unrelated = [pscustomobject]@{ Subject = 'CN=unrelated'; FriendlyName = ''; Name = 'unrelated' }
    $resolved = @(Get-SourceGatewayTransferCertificates -TransferId $transferId `
        -Certificates @($bound, $friendlyBound, $unrelated))
    if (($resolved.Name -join ',') -cne 'bound,friendly') {
        throw 'Transfer cleanup did not discover certificates independently from the GUID binding.'
    }
}
finally {
    Remove-Item -LiteralPath $transferTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$result = New-SourceGatewayOracleCredentialResult -Dsn 'OFM_GATEWAY_ORACLE9I' `
    -CiphertextBase64 'AA==' -CiphertextSha256 ('a' * 64) -CertificateThumbprint ('B' * 40)
$resultJson = $result | ConvertTo-Json -Compress
$parsedResult = $resultJson | ConvertFrom-Json
$expectedResultKeys = @(
    'schemaVersion', 'status', 'username', 'roleName', 'schema', 'dsn', 'grants',
    'ciphertextBase64', 'ciphertextSha256', 'certificateThumbprint'
)
$actualResultKeys = @($parsedResult.PSObject.Properties.Name | Sort-Object)
$sortedExpectedResultKeys = @($expectedResultKeys | Sort-Object)
if (($actualResultKeys -join "`n") -cne ($sortedExpectedResultKeys -join "`n") -or
    $resultJson -match [regex]::Escape($knownPassword) -or $parsedResult.username -cne 'OFM_GATEWAY_RO') {
    throw 'The Oracle guest result does not match the exact nonsecret JSON allowlist.'
}

$oracleGuestPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'source-lab/forms6i/New-SourceGatewayOracleCredential.ps1'
$oracleGuestTokens = $null
$oracleGuestErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $oracleGuestPath, [ref]$oracleGuestTokens, [ref]$oracleGuestErrors)
if ($oracleGuestErrors.Count -ne 0) {
    throw "Oracle credential guest script parsing failed: $($oracleGuestErrors.Message -join '; ')"
}
$oracleGuest = Get-Content -LiteralPath $oracleGuestPath -Raw
if ($oracleGuest -match 'Start-Transcript|Write-Host|Write-Verbose' -or
    $oracleGuest -notmatch 'Invoke-SourceGatewaySqlPlus' -or
    $oracleGuest -notmatch '-ProvisioningSql \$sql -Password \$password' -or
    $oracleGuest -notmatch 'Protect-SourceGatewayCredentialBytes') {
    throw 'The Oracle guest script must use stdin-only SQL*Plus and the shared OAEP-SHA256 transfer helper without transcript output.'
}

$oracleBootstrapPath = Join-Path $PSScriptRoot 'Invoke-SourceGatewayOracleCredentialProvision.ps1'
$oracleBootstrapTokens = $null
$oracleBootstrapErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $oracleBootstrapPath, [ref]$oracleBootstrapTokens, [ref]$oracleBootstrapErrors)
if ($oracleBootstrapErrors.Count -ne 0) {
    throw "Oracle credential bootstrap parsing failed: $($oracleBootstrapErrors.Message -join '; ')"
}
$oracleBootstrap = Get-Content -LiteralPath $oracleBootstrapPath -Raw
foreach ($requiredBootstrapContract in @(
        "Resolve-SourceGatewayFormsOdbcDriver -Dsn `$Dsn -OdbcRoot 'HKLM:\SOFTWARE\WOW6432Node\ODBC'",
        'Get-SourceGatewayAllowedOdbcDriverRoots -SystemRoot $env:SystemRoot',
        'Complete-SourceGatewayCredentialTransfer.ps1',
        'OFM_GATEWAY_ORACLE_MERIDIAN_RO.dpapi',
        'oracleConnectionRegistered = $true',
        "if (`$Operation -eq 'CleanupTransferKey')",
        'privateKeyName',
        'Remove-TransferCertificateAndKey',
        '[Security.Cryptography.CngKey]::Open(',
        'Remove-BoundTransferMaterial -Id $TransferId',
        'catch {',
        'Transfer-key creation requires the runner-generated transfer identifier.',
        'CN=OFM Source Gateway Credential Transfer $TransferId',
        'Get-ChildItem -LiteralPath $transferRoot -Filter "credential-transfer-$TransferId.*"')) {
    if ($oracleBootstrap -cnotmatch [regex]::Escape($requiredBootstrapContract)) {
        throw "Oracle credential bootstrap is missing '$requiredBootstrapContract'."
    }
}
$newTransferKeyBranch = $oracleBootstrap.Substring($oracleBootstrap.IndexOf("if (`$Operation -eq 'NewTransferKey')"))
$createTransferCertificate = $newTransferKeyBranch.IndexOf('$certificate = New-SelfSignedCertificate')
$cleanupAfterCreationFailure = $newTransferKeyBranch.IndexOf('Remove-BoundTransferMaterial -Id $TransferId')
if ($createTransferCertificate -lt 0 -or $cleanupAfterCreationFailure -lt 0 -or
    $createTransferCertificate -gt $cleanupAfterCreationFailure) {
    throw 'Runner-bound transfer-key creation does not clean certificate and CNG material on failure.'
}

$oracleWorkflowPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.github/workflows/provision-source-gateway-oracle-credential.yml'
$oracleWorkflow = Get-Content -LiteralPath $oracleWorkflowPath -Raw
foreach ($requiredWorkflowContract in @(
        'id-token: write',
        'vm-ofm-forms6i-j6mrrerz',
        'vm-ofm-oracle9i-j6mrrerz',
        'az vm run-command create',
        'az vm run-command delete',
        'ValidatePrerequisites',
        'CleanupTransferKey',
        'PublicCertificateBase64=$PUBLIC_CERTIFICATE',
        'CiphertextBase64=$CIPHERTEXT',
        'transfer-key-removed',
        'Generate transfer identifier before key creation',
        'Remove transfer certificate and CNG key',
        'Delete managed Run Commands',
        'if: always()',
        'SELECT ON MERIDIAN TABLES',
        'SELECT ON 13 SYS DBA DICTIONARY VIEWS',
        'Compile every embedded Python block before mutation',
        "value['certificateThumbprint'].upper() != sys.argv[3].upper()",
        "value['oracleConnectionRegistered'] is not True")) {
    if ($oracleWorkflow -cnotmatch [regex]::Escape($requiredWorkflowContract)) {
        throw "Oracle credential workflow is missing '$requiredWorkflowContract'."
    }
}
if ($oracleWorkflow -match '(?i)(Password|Pwd)=\$') {
    throw 'The Oracle credential workflow passes plaintext credential material as a Run Command parameter.'
}
$oracleWorkflowDeleteStep = $oracleWorkflow.Substring($oracleWorkflow.IndexOf('Delete managed Run Commands'))
foreach ($ownedRunCommand in @('ofm-oracle-labconn-$GITHUB_RUN_ID', 'ofm-oracle-preflight-$GITHUB_RUN_ID',
        'ofm-oracle-key-$GITHUB_RUN_ID', 'ofm-oracle-create-$GITHUB_RUN_ID', 'ofm-oracle-complete-$GITHUB_RUN_ID',
        'ofm-oracle-key-cleanup-$GITHUB_RUN_ID')) {
    if ($oracleWorkflowDeleteStep -cnotmatch [regex]::Escape($ownedRunCommand)) {
        throw "The Oracle credential workflow leaves the owned Run Command '$ownedRunCommand' on the VM."
    }
}
foreach ($pinnedWorkflowInput in @("options: [OFM_GATEWAY_ORACLE9I]", "options: ['10.246.0.37']",
        "options: ['1521']", 'options: [orcl]', 'options: [OFM_ORCL9I]')) {
    if ($oracleWorkflow -cnotmatch [regex]::Escape($pinnedWorkflowInput)) {
        throw "The Oracle credential workflow does not pin the dispatch input '$pinnedWorkflowInput'."
    }
}
if ($oracleWorkflow -match 'SELECT_CATALOG_ROLE|SELECT ON MERIDIAN TABLES AND VIEWS' -or
    $oracleWorkflow -match 'CleanupTransferKey.+TransferCertificateThumbprint' -or
    $oracleWorkflow.IndexOf('uuid.uuid4().hex') -gt $oracleWorkflow.IndexOf('NewTransferKey')) {
    throw 'The Oracle credential workflow does not preserve the exact grant and pre-generated cleanup binding.'
}
if ($oracleWorkflow.IndexOf('Compile every embedded Python block before mutation') -gt
    $oracleWorkflow.IndexOf('invoke_guest "$ORACLE_VM"')) {
    throw 'Embedded workflow validation must complete before the Oracle mutation leg.'
}

$workflow = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.github/workflows/source-gateway-image.yml') -Raw
$dockerfile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Dockerfile.private-workbench') -Raw
$applicationTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'application.bicep') -Raw
if ($applicationTemplate -notmatch "param existingApplicationInsightsName string = 'appi-ofmfleet-web-dev-ykbpnrpd'" -or
    $applicationTemplate -notmatch "resource existingApplicationInsights 'Microsoft\.Insights/components@2020-02-02' existing" -or
    $applicationTemplate -notmatch "name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: existingApplicationInsights\.properties\.ConnectionString") {
    throw 'The private workbench must reuse the approved existing Application Insights component.'
}
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

# Everything above runs on the Forms VM under Windows PowerShell 5.1 and is proved on whichever host runs
# this harness. Preview-SourceGateway.ps1 and Configure-SourceGatewayEntra.ps1 execute only on the pwsh 7
# runner and use pwsh-only cmdlet parameters, so their checks below require that host.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    Write-Output 'SOURCE_GATEWAY_WINDOWS_POWERSHELL_51_TESTS_PASS'
    return
}

$previewPath = Join-Path $PSScriptRoot 'Preview-SourceGateway.ps1'
$previewTokens = $null
$previewErrors = $null
$previewAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $previewPath, [ref]$previewTokens, [ref]$previewErrors)
if ($previewErrors.Count -ne 0) {
    throw "Preview script parsing failed: $($previewErrors.Message -join '; ')"
}
$guidMemberAccess = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.MemberExpressionAst] -and
        $node.Member.Extent.Text -ceq 'Guid'
}, $true))
if ($guidMemberAccess.Count -ne 0) {
    throw 'The preview reads .Guid, which throws under strict mode for empty GUID arrays.'
}
$icaclsCommands = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq 'icacls.exe'
}, $true))
if ($icaclsCommands.Count -ne 2 -or
    @($icaclsCommands | Where-Object {
        $_.Extent.Text -cmatch '/setowner' -and $_.Extent.Text -cmatch '/inheritance|/grant'
    }).Count -ne 0) {
    throw 'The preview must set ownership and restrict the ACL in separate icacls calls.'
}
$parameterWrite = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq 'Set-Content' -and $node.Extent.Text -cmatch '\$parameterPath'
}, $true))
if ($parameterWrite.Count -ne 1 -or
    @($icaclsCommands | Where-Object { $_.Extent.StartOffset -gt $parameterWrite[0].Extent.StartOffset }).Count -ne 0) {
    throw 'The preview must restrict the secure parameter file before writing it.'
}
$guidConversions = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.PipelineAst] -and
        $node.Extent.Text -cmatch '^\$(Operator|Validation)\w+Ids \| ForEach-Object \{ \$_\.ToString\(\) \}'
}, $true))
if ($guidConversions.Count -ne 6) {
    throw "Expected six GUID array conversions in the preview; found $($guidConversions.Count)."
}
& {
    Set-StrictMode -Version Latest
    [guid[]] $OperatorPrincipalObjectIds = @([guid] 'dd84da40-177f-47b9-9c4d-4b657ee4de36')
    [guid[]] $ValidationPrincipalObjectIds = @()
    [guid[]] $ValidationClientApplicationIds = @()
    foreach ($conversion in $guidConversions) {
        $converted = @(& ([scriptblock]::Create($conversion.Extent.Text)))
        $expected = if ($conversion.Extent.Text.StartsWith('$Operator', [StringComparison]::Ordinal)) { 1 } else { 0 }
        if ($converted.Count -ne $expected -or ($expected -eq 1 -and $converted[0] -cne 'dd84da40-177f-47b9-9c4d-4b657ee4de36')) {
            throw "Preview GUID conversion produced an unexpected result: $($conversion.Extent.Text)"
        }
    }
}
$compilerFunctions = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -cin @('Resolve-BicepExecutable', 'Invoke-BicepCompilation')
}, $true))
if ($compilerFunctions.Count -ne 2) {
    throw 'The preview Bicep compiler functions could not be loaded for runtime verification.'
}
$compilerFunctionsText = $compilerFunctions.Extent.Text -join "`n"
& {
    function Test-Path {
        param([string] $LiteralPath, [object] $PathType)
        return $LiteralPath -ceq (Join-Path $env:AZURE_CONFIG_DIR 'bin\bicep.exe')
    }
    . ([scriptblock]::Create($compilerFunctionsText))
    $savedAzureConfigDirectory = $env:AZURE_CONFIG_DIR
    try {
        $env:AZURE_CONFIG_DIR = Join-Path ([IO.Path]::GetTempPath()) 'mock-azure-config'
        $expectedBicep = Join-Path $env:AZURE_CONFIG_DIR 'bin\bicep.exe'
        $resolvedBicep = Resolve-BicepExecutable
        if ($resolvedBicep -cne $expectedBicep) {
            throw 'The preview did not resolve Bicep from AZURE_CONFIG_DIR.'
        }
    }
    finally {
        $env:AZURE_CONFIG_DIR = $savedAzureConfigDirectory
    }
}
$missingBicep = & {
    function Test-Path { param([string] $LiteralPath, [object] $PathType); return $false }
    . ([scriptblock]::Create($compilerFunctionsText))
    try { Resolve-BicepExecutable; $null } catch { $_ }
}
if ($null -eq $missingBicep -or $missingBicep.Exception.Message -notmatch 'release workflow must install the pinned compiler') {
    throw 'The preview accepted a missing Bicep compiler.'
}
$failedCompilation = & {
    function mock-bicep { $global:LASTEXITCODE = 19 }
    . ([scriptblock]::Create($compilerFunctionsText))
    try {
        Invoke-BicepCompilation -BicepExecutable 'mock-bicep' -TemplatePath 'mock.bicep' -OutputPath 'mock.json'
        $null
    } catch { $_ }
}
if ($null -eq $failedCompilation -or $failedCompilation.Exception.Message -ne
    'Bicep compilation failed for mock.bicep with exit code 19.') {
    throw 'The preview accepted a nonzero Bicep compiler exit code.'
}
$successfulCompilation = & {
    function mock-bicep { $global:LASTEXITCODE = 0 }
    . ([scriptblock]::Create($compilerFunctionsText))
    try {
        Invoke-BicepCompilation -BicepExecutable 'mock-bicep' -TemplatePath 'mock.bicep' -OutputPath 'mock.json'
        return $true
    } catch { return $false }
}
if (-not $successfulCompilation) {
    throw 'The preview rejected a successful mocked Bicep compilation.'
}
$resourceScopeAssignments = @($previewAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -cin @('$actionableChanges', '$actualResourceIds', '$unexpectedChange', '$resourceIdDifferences')
}, $true) | Sort-Object { $_.Extent.StartOffset })
$resourceScopeGuard = $previewAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match '\$resourceIdDifferences\.Count'
}, $true)
if ($resourceScopeAssignments.Count -ne 4 -or $null -eq $resourceScopeGuard) {
    throw 'The preview resource-scope comparison could not be loaded for runtime verification.'
}
$resourceScopeCheck = [scriptblock]::Create(
    "$(($resourceScopeAssignments | ForEach-Object { $_.Extent.Text }) -join "`n")`n$($resourceScopeGuard.Extent.Text)")
$expectedResourceIds = @('/resource/a', '/resource/b')
function New-WhatIfChange([string] $Id, [string] $Type) { [pscustomobject]@{ resourceId = $Id; changeType = $Type } }
$resourceChanges = @(
    New-WhatIfChange '/resource/a' 'Create'
    New-WhatIfChange '/resource/b' 'Create'
    New-WhatIfChange '/resource/existing-vm' 'Ignore'
    New-WhatIfChange '/resource/existing-server' 'NoChange'
)
. $resourceScopeCheck
if ($actionableChanges.Count -ne 2) {
    throw 'The preview did not exclude Ignore and NoChange entries from the actionable change set.'
}
foreach ($rejectedCase in @(
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'Modify'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'Create'; New-WhatIfChange '/resource/existing-vm' 'Delete'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'Ignore'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'NoChange'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'Deploy'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/b' 'Unsupported'),
        @(New-WhatIfChange '/resource/a' 'Create'; New-WhatIfChange '/resource/c' 'Create'))) {
    $resourceChanges = $rejectedCase
    $scopeRejection = try { & $resourceScopeCheck; $null } catch { $_ }
    if ($null -eq $scopeRejection -or
        $scopeRejection.Exception.Message -notmatch 'exceeded the approved create-only scope') {
        throw "The preview resource-scope comparison accepted: $(($rejectedCase | ForEach-Object { "$($_.changeType) $($_.resourceId)" }) -join ', ')"
    }
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

Write-Output 'SOURCE_GATEWAY_POWERSHELL_TESTS_PASS'