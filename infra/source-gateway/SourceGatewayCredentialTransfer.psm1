Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SourceGatewayPinnedStageRoot = 'C:\ProgramData\OracleFormsMigrationFleet'

function Protect-SourceGatewayCredentialBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,

        [Parameter(Mandatory)]
        [byte[]] $Plaintext
    )

    if ($Certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw 'The one-time public certificate has expired.'
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    try {
        if ($null -eq $rsa) {
            throw 'The one-time public certificate has no RSA public key.'
        }

        # RSAOpenSsl exposes KeySize as write-only to PowerShell on Linux; the modulus length is portable.
        $keyBytes = $rsa.ExportParameters($false).Modulus.Length
        $maximumPlaintextBytes = $keyBytes - (2 * 32) - 2
        if ($Plaintext.Length -le 0 -or $Plaintext.Length -gt $maximumPlaintextBytes) {
            throw "The credential must contain at most $maximumPlaintextBytes UTF-8 bytes."
        }

        return ,$rsa.Encrypt($Plaintext, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    }
    finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }
}

function New-SourceGatewayOraclePassword {
    [CmdletBinding()]
    param()

    $letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'
    $alphanumeric = $letters + '0123456789'
    $characters = [char[]]::new(30)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = [byte[]]::new(4)
    try {
        $characters[0] = $letters[(Get-SourceGatewaySecureRandomIndex -Random $random -Bytes $bytes -UpperBound $letters.Length)]
        for ($index = 1; $index -lt $characters.Length; $index++) {
            $characters[$index] = $alphanumeric[(Get-SourceGatewaySecureRandomIndex -Random $random -Bytes $bytes -UpperBound $alphanumeric.Length)]
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $random.Dispose()
    }
    return [string]::new($characters)
}

function Get-SourceGatewaySecureRandomIndex {
    param(
        [Parameter(Mandatory)] [Security.Cryptography.RandomNumberGenerator] $Random,
        [Parameter(Mandatory)] [byte[]] $Bytes,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $UpperBound
    )

    $range = [uint64]([uint64][uint32]::MaxValue + 1)
    $limit = $range - ($range % [uint64]$UpperBound)
    do {
        $Random.GetBytes($Bytes)
        $value = [uint64][BitConverter]::ToUInt32($Bytes, 0)
    } while ($value -ge $limit)
    return [int]($value % [uint64]$UpperBound)
}

function Get-SourceGatewayOracleGrantContract {
    [CmdletBinding()]
    param()

    return @(
        'CREATE SESSION',
        'OFM_GATEWAY_SOURCE_RO',
        'SELECT ON MERIDIAN TABLES',
        'SELECT ON 13 SYS DBA DICTIONARY VIEWS'
    )
}

function Get-SourceGatewayTransferCertificates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{32}$')] [string] $TransferId,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Certificates
    )

    $binding = "OFM Source Gateway Credential Transfer $TransferId"
    return @($Certificates | Where-Object {
        $_.Subject -ceq "CN=$binding" -or $_.FriendlyName -ceq $binding
    })
}

function Read-SourceGatewayTransferMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{32}$')] [string] $TransferId,
        [Parameter(Mandatory)] [string] $MetadataPath
    )

    $privateKeyName = $null
    if (Test-Path -LiteralPath $MetadataPath -PathType Leaf) {
        try {
            $metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
            if ([string]$metadata.transferId -ceq $TransferId -and
                -not [string]::IsNullOrWhiteSpace([string]$metadata.privateKeyName) -and
                [IO.Path]::GetFileName([string]$metadata.privateKeyName) -ceq [string]$metadata.privateKeyName) {
                $privateKeyName = [string]$metadata.privateKeyName
            }
        }
        catch {
            $privateKeyName = $null
        }
    }
    return [pscustomobject]@{ PrivateKeyName = $privateKeyName }
}

function New-SourceGatewayOracleProvisioningSql {
    [CmdletBinding()]
    param()

    $bootstrapSql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
set echo off feedback off heading off pagesize 0 verify off termout off
connect / as sysdba
declare
    user_count number;
    role_count number;
    profile_count number;
    mismatch_count number;
begin
    select count(*) into user_count from dba_users where username = 'OFM_GATEWAY_RO';
    select count(*) into role_count from dba_roles where role = 'OFM_GATEWAY_SOURCE_RO';
    select count(*) into profile_count from dba_profiles where profile = 'OFM_GATEWAY_TOOLING';
    if user_count = 0 and role_count = 0 then
        if profile_count <> 0 then
            raise_application_error(-20001, 'Refusing to adopt pre-existing OFM gateway principals.');
        end if;
        execute immediate 'create profile OFM_GATEWAY_TOOLING limit sessions_per_user unlimited';
        execute immediate 'create role OFM_GATEWAY_SOURCE_RO';
        execute immediate 'create user OFM_GATEWAY_RO identified externally profile OFM_GATEWAY_TOOLING account lock';
    else
        if user_count <> 1 or role_count <> 1 or profile_count = 0 then
            raise_application_error(-20002, 'Existing OFM gateway principals are not tooling-owned.');
        end if;
        select count(*) into mismatch_count from dba_users
        where username = 'OFM_GATEWAY_RO' and profile = 'OFM_GATEWAY_TOOLING';
        select count(*) into profile_count from dba_users
        where profile = 'OFM_GATEWAY_TOOLING' and username <> 'OFM_GATEWAY_RO';
        if mismatch_count <> 1 or profile_count <> 0 then
            raise_application_error(-20002, 'Existing OFM gateway principals are not tooling-owned.');
        end if;

        select count(*) into mismatch_count from dba_sys_privs
        where grantee = 'OFM_GATEWAY_RO'
          and (privilege <> 'CREATE SESSION' or admin_option <> 'NO');
        select count(*) + mismatch_count into mismatch_count from dba_role_privs
        where grantee = 'OFM_GATEWAY_RO' and
          (granted_role <> 'OFM_GATEWAY_SOURCE_RO' or admin_option <> 'NO' or default_role <> 'YES');
        select count(*) + mismatch_count into mismatch_count from dba_tab_privs
        where grantee = 'OFM_GATEWAY_RO';
        select count(*) + mismatch_count into mismatch_count from dba_col_privs
        where grantee in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        select count(*) + mismatch_count into mismatch_count from dba_sys_privs
        where grantee = 'OFM_GATEWAY_SOURCE_RO';
        select count(*) + mismatch_count into mismatch_count from dba_role_privs
        where grantee = 'OFM_GATEWAY_SOURCE_RO';
        select count(*) + mismatch_count into mismatch_count from dba_tab_privs privileges
        where privileges.grantee = 'OFM_GATEWAY_SOURCE_RO'
          and (privileges.privilege <> 'SELECT' or privileges.grantable <> 'NO' or not (
            (privileges.owner = 'MERIDIAN' and exists (select 1 from dba_objects objects
              where objects.owner = privileges.owner and objects.object_name = privileges.table_name
                and objects.object_type = 'TABLE')) or
            (privileges.owner = 'SYS' and privileges.table_name in
              ('DBA_OBJECTS','DBA_TABLES','DBA_TAB_COLUMNS','DBA_CONSTRAINTS','DBA_CONS_COLUMNS',
               'DBA_SEQUENCES','DBA_SOURCE','DBA_INDEXES','DBA_IND_COLUMNS','DBA_TAB_PRIVS',
               'DBA_COL_PRIVS','DBA_TRIGGERS','DBA_DEPENDENCIES'))));
        select count(*) + mismatch_count into mismatch_count from (
            select grantee, granted_role, admin_option from dba_role_privs
            start with grantee = 'OFM_GATEWAY_RO'
            connect by prior granted_role = grantee
        ) where granted_role <> 'OFM_GATEWAY_SOURCE_RO' or admin_option <> 'NO';
        select count(*) + mismatch_count into mismatch_count from dba_role_privs
        where granted_role = 'OFM_GATEWAY_SOURCE_RO' and not (
            (grantee = 'OFM_GATEWAY_RO' and admin_option = 'NO') or
            (grantee = 'SYS' and admin_option = 'YES'));
        select count(*) + mismatch_count into mismatch_count from dba_objects
        where owner in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        select count(*) + mismatch_count into mismatch_count from v`$pwfile_users
        where username in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        select count(*) + mismatch_count into mismatch_count from proxy_users
        where proxy in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')
           or client in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        if mismatch_count <> 0 then
            raise_application_error(-20003, 'Existing OFM gateway privileges exceed the read-only contract.');
        end if;
    end if;
end;
/
prompt OFM_STAGE_BOOTSTRAP_OK
"@

    $reconciliationSql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
set echo off feedback off heading off pagesize 0 verify off termout off
connect / as sysdba
declare
    mismatch_count number;
    policy_count number;
begin
    select count(*) into policy_count from dba_policies where object_owner = 'MERIDIAN';
    if policy_count <> 0 then
        raise_application_error(-20004, 'MERIDIAN objects have VPD policies; refusing read-role grants.');
    end if;
    select count(*) into policy_count from dba_audit_policies where object_schema = 'MERIDIAN';
    if policy_count <> 0 then
        raise_application_error(-20006, 'MERIDIAN objects have FGA policies; refusing read-role grants.');
    end if;
    execute immediate 'grant create session to OFM_GATEWAY_RO';

    execute immediate 'grant select on SYS.DBA_OBJECTS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_TABLES to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_TAB_COLUMNS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_CONSTRAINTS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_CONS_COLUMNS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_SEQUENCES to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_SOURCE to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_INDEXES to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_IND_COLUMNS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_TAB_PRIVS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_COL_PRIVS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_TRIGGERS to OFM_GATEWAY_SOURCE_RO';
    execute immediate 'grant select on SYS.DBA_DEPENDENCIES to OFM_GATEWAY_SOURCE_RO';

    for item in (
        select object_name from dba_objects
        where owner = 'MERIDIAN' and object_type = 'TABLE'
        order by object_name
    ) loop
        execute immediate 'grant select on MERIDIAN."' || replace(item.object_name, '"', '""') ||
            '" to OFM_GATEWAY_SOURCE_RO';
    end loop;
    execute immediate 'grant OFM_GATEWAY_SOURCE_RO to OFM_GATEWAY_RO';

    select count(*) into mismatch_count from dba_sys_privs
    where grantee = 'OFM_GATEWAY_RO' and (privilege <> 'CREATE SESSION' or admin_option <> 'NO');
    select count(*) + mismatch_count into mismatch_count from dba_sys_privs
    where grantee = 'OFM_GATEWAY_SOURCE_RO';
    select count(*) + mismatch_count into mismatch_count from dba_role_privs
    where grantee = 'OFM_GATEWAY_RO'
            and (granted_role <> 'OFM_GATEWAY_SOURCE_RO' or admin_option <> 'NO' or default_role <> 'YES');
    select count(*) + mismatch_count into mismatch_count from dba_role_privs
    where grantee = 'OFM_GATEWAY_SOURCE_RO';
    select count(*) + mismatch_count into mismatch_count from dba_tab_privs
    where grantee = 'OFM_GATEWAY_RO';
    select count(*) + mismatch_count into mismatch_count from dba_col_privs
    where grantee in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
    select count(*) + mismatch_count into mismatch_count from dba_tab_privs privileges
    where privileges.grantee = 'OFM_GATEWAY_SOURCE_RO'
            and (privileges.privilege <> 'SELECT' or privileges.grantable <> 'NO' or not (
                (privileges.owner = 'MERIDIAN' and exists (select 1 from dba_objects objects
                    where objects.owner = privileges.owner and objects.object_name = privileges.table_name
                        and objects.object_type = 'TABLE')) or
                (privileges.owner = 'SYS' and privileges.table_name in
                    ('DBA_OBJECTS','DBA_TABLES','DBA_TAB_COLUMNS','DBA_CONSTRAINTS','DBA_CONS_COLUMNS',
                     'DBA_SEQUENCES','DBA_SOURCE','DBA_INDEXES','DBA_IND_COLUMNS','DBA_TAB_PRIVS',
                     'DBA_COL_PRIVS','DBA_TRIGGERS','DBA_DEPENDENCIES'))));
    select count(*) + mismatch_count into mismatch_count from dba_objects objects
        where objects.owner = 'MERIDIAN' and objects.object_type = 'TABLE'
      and not exists (select 1 from dba_tab_privs privileges
        where privileges.grantee = 'OFM_GATEWAY_SOURCE_RO' and privileges.owner = objects.owner
          and privileges.table_name = objects.object_name and privileges.privilege = 'SELECT');
        select count(*) + mismatch_count into mismatch_count from (
                select grantee, granted_role, admin_option from dba_role_privs
                start with grantee = 'OFM_GATEWAY_RO'
                connect by prior granted_role = grantee
        ) where granted_role <> 'OFM_GATEWAY_SOURCE_RO' or admin_option <> 'NO';
    select count(*) + mismatch_count into mismatch_count from dba_role_privs
    where granted_role = 'OFM_GATEWAY_SOURCE_RO' and not (
        (grantee = 'OFM_GATEWAY_RO' and admin_option = 'NO') or
        (grantee = 'SYS' and admin_option = 'YES'));
        select count(*) + mismatch_count into mismatch_count from dba_objects
        where owner in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        select count(*) + mismatch_count into mismatch_count from v`$pwfile_users
        where username in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
        select count(*) + mismatch_count into mismatch_count from proxy_users
        where proxy in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO')
             or client in ('OFM_GATEWAY_RO', 'OFM_GATEWAY_SOURCE_RO');
    select count(*) + mismatch_count into mismatch_count from (
        select count(*) granted_count from dba_sys_privs
        where grantee = 'OFM_GATEWAY_RO' and privilege = 'CREATE SESSION'
        having count(*) <> 1
    );
    select count(*) + mismatch_count into mismatch_count from (
        select count(*) granted_count from dba_role_privs
                where grantee = 'OFM_GATEWAY_RO' and granted_role = 'OFM_GATEWAY_SOURCE_RO'
                having count(*) <> 1
        );
        select count(*) + mismatch_count into mismatch_count from (
            select count(*) granted_count from dba_role_privs
            where grantee = 'SYS' and granted_role = 'OFM_GATEWAY_SOURCE_RO' and admin_option = 'YES'
            having count(*) <> 1
        );
        select count(*) + mismatch_count into mismatch_count from (
                select count(*) granted_count from dba_tab_privs
                where grantee = 'OFM_GATEWAY_SOURCE_RO' and owner = 'SYS' and privilege = 'SELECT'
                    and table_name in
                        ('DBA_OBJECTS','DBA_TABLES','DBA_TAB_COLUMNS','DBA_CONSTRAINTS','DBA_CONS_COLUMNS',
                         'DBA_SEQUENCES','DBA_SOURCE','DBA_INDEXES','DBA_IND_COLUMNS','DBA_TAB_PRIVS',
                         'DBA_COL_PRIVS','DBA_TRIGGERS','DBA_DEPENDENCIES')
                having count(*) <> 13
    );
    if mismatch_count <> 0 then
                raise_application_error(-20005, 'Final OFM gateway privileges do not exactly match the read-only contract.');
    end if;
end;
/
prompt OFM_STAGE_RECONCILIATION_OK
"@

    $unlockSql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
set echo off feedback off heading off pagesize 0 verify off termout off
connect / as sysdba
alter user OFM_GATEWAY_RO account unlock;
prompt OFM_STAGE_UNLOCK_OK
"@

    $lockSql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
set echo off feedback off heading off pagesize 0 verify off termout off
connect / as sysdba
alter user OFM_GATEWAY_RO account lock;
declare
    locked_count number;
begin
    select count(*) into locked_count from dba_users
    where username = 'OFM_GATEWAY_RO' and account_status = 'LOCKED';
    if locked_count <> 1 then
        raise_application_error(-20007, 'OFM gateway account lock could not be verified.');
    end if;
end;
/
prompt OFM_STAGE_RELOCK_OK
"@

        return [pscustomobject]@{
                BootstrapSql = $bootstrapSql
                ReconciliationSql = $reconciliationSql
                UnlockSql = $unlockSql
                LockSql = $lockSql
        }
}

function Invoke-SourceGatewaySqlPlusStage {
    param(
        [Parameter(Mandatory)] [string] $SqlPlusPath,
        [Parameter(Mandatory)] [string[]] $InputLines,
        [Parameter(Mandatory)] [string[]] $RequiredMarkers,
        [Parameter(Mandatory)] [string] $StageName,
        [Parameter(Mandatory)] [scriptblock] $ProcessFactory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $SqlPlusPath
    $startInfo.Arguments = '-S /nolog'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = & $ProcessFactory $startInfo
    if ($null -eq $process) {
        throw 'Oracle credential provisioning could not start SQL*Plus.'
    }
    try {
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        foreach ($line in $InputLines) { $process.StandardInput.WriteLine($line) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Oracle credential provisioning stage '$StageName' timed out."
        }
        $combinedOutput = ([string]$standardOutput.Result) + "`n" + ([string]$standardError.Result)
        $observedMarkers = @($combinedOutput -split "`r?`n" | ForEach-Object { $_.Trim() } |
            Where-Object { $_ -cmatch '^OFM_(?:STAGE_[A-Z_]+_OK|LOGIN_OK:OFM_GATEWAY_RO)$' } |
            Sort-Object -Unique)
        $errorCodes = @([regex]::Matches($combinedOutput, '(?m)\b(?:ORA-\d{5}|SP2-\d{4})\b') |
            ForEach-Object Value | Sort-Object -Unique)
        $missingMarkers = @($RequiredMarkers | Where-Object { $observedMarkers -cnotcontains $_ })
        if ($process.ExitCode -ne 0 -or $errorCodes.Count -ne 0 -or $missingMarkers.Count -ne 0) {
            $markerSummary = if ($observedMarkers.Count -eq 0) { 'none' } else { $observedMarkers -join ',' }
            $codeSummary = if ($errorCodes.Count -eq 0) { 'none' } else { $errorCodes -join ',' }
            throw "Oracle credential provisioning stage '$StageName' failed; markers=$markerSummary; errorCodes=$codeSummary; exitCode=$($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Set-SourceGatewayOracleAccountLocked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SqlPlusPath,
        [Parameter(Mandatory)] [psobject] $ProvisioningSql,
        [scriptblock] $ProcessFactory = { param($startInfo) [Diagnostics.Process]::Start($startInfo) }
    )

    Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
        -StageName 'relock' -RequiredMarkers @('OFM_STAGE_RELOCK_OK') `
        -InputLines @([string]$ProvisioningSql.LockSql, 'exit')
}

function Invoke-SourceGatewaySqlPlus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SqlPlusPath,
        [Parameter(Mandatory)] [psobject] $ProvisioningSql,
        [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z][A-Za-z0-9]{29}$')] [string] $Password,
        [scriptblock] $ProcessFactory = { param($startInfo) [Diagnostics.Process]::Start($startInfo) }
    )

    $ownershipProven = $false
    try {
        Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
            -StageName 'bootstrap' -RequiredMarkers @('OFM_STAGE_BOOTSTRAP_OK') `
            -InputLines @([string]$ProvisioningSql.BootstrapSql, 'exit')
        # Bootstrap raises ORA-20002 for principals this tooling does not own, so only success proves ownership.
        $ownershipProven = $true
        # An adopted account may be OPEN from a prior run; it stays locked until final verification.
        Set-SourceGatewayOracleAccountLocked -SqlPlusPath $SqlPlusPath `
            -ProvisioningSql $ProvisioningSql -ProcessFactory $ProcessFactory
        Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
            -StageName 'password' -RequiredMarkers @('OFM_STAGE_PASSWORD_OK') `
            -InputLines @('whenever sqlerror exit failure rollback', 'whenever oserror exit failure rollback',
                'set echo off feedback off heading off pagesize 0 verify off termout off', 'connect / as sysdba',
                'password OFM_GATEWAY_RO', $Password, $Password, 'prompt OFM_STAGE_PASSWORD_OK', 'exit')
        Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
            -StageName 'reconciliation' -RequiredMarkers @('OFM_STAGE_RECONCILIATION_OK') `
            -InputLines @([string]$ProvisioningSql.ReconciliationSql, 'exit')
        Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
            -StageName 'unlock' -RequiredMarkers @('OFM_STAGE_UNLOCK_OK') `
            -InputLines @([string]$ProvisioningSql.UnlockSql, 'exit')
        Invoke-SourceGatewaySqlPlusStage -SqlPlusPath $SqlPlusPath -ProcessFactory $ProcessFactory `
            -StageName 'login' -RequiredMarkers @('OFM_LOGIN_OK:OFM_GATEWAY_RO', 'OFM_STAGE_LOGIN_OK') `
            -InputLines @('whenever sqlerror exit failure rollback', 'whenever oserror exit failure rollback',
                'set echo off feedback off heading off pagesize 0 verify off termout off', 'connect',
                'OFM_GATEWAY_RO', $Password, "select 'OFM_LOGIN_OK:' || user from dual;",
                'prompt OFM_STAGE_LOGIN_OK', 'exit')
    }
    catch {
        $provisioningFailure = $_
        if (-not $ownershipProven) {
            throw $provisioningFailure
        }
        try {
            Set-SourceGatewayOracleAccountLocked -SqlPlusPath $SqlPlusPath `
                -ProvisioningSql $ProvisioningSql -ProcessFactory $ProcessFactory
        }
        catch {
            throw "Oracle credential provisioning failed and account relock verification also failed. $($_.Exception.Message)"
        }
        throw $provisioningFailure
    }
}

function New-SourceGatewayOracleCredentialResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Dsn,
        [Parameter(Mandatory)] [string] $CiphertextBase64,
        [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{64}$')] [string] $CiphertextSha256,
        [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{40,64}$')] [string] $CertificateThumbprint
    )

    return [ordered]@{
        schemaVersion = 1
        status = 'encrypted'
        username = 'OFM_GATEWAY_RO'
        roleName = 'OFM_GATEWAY_SOURCE_RO'
        schema = 'MERIDIAN'
        dsn = $Dsn
        grants = @(Get-SourceGatewayOracleGrantContract)
        ciphertextBase64 = $CiphertextBase64
        ciphertextSha256 = $CiphertextSha256
        certificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
    }
}

function Get-SourceGatewayInteractiveDesktopSession {
    [CmdletBinding()]
    param()

    return @(Get-CimInstance -ClassName Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid
        if ($null -ne $owner -and $owner.ReturnValue -eq 0) {
            [pscustomobject]@{
                UserSid = [string]$owner.Sid
                SessionId = [int]$_.SessionId
            }
        }
    })
}

function New-SourceGatewayScheduledTaskAdapter {
    [CmdletBinding()]
    param()

    # Every scriptblock below is invoked from the caller's scope, so it may only use its own parameters.
    return @{
        Exists = { param($Name) $null -ne (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue) }
        Register = {
            param($Name, $Executable, $Arguments, $UserId, $ExecutionTimeLimitSeconds)
            $action = New-ScheduledTaskAction -Execute $Executable -Argument $Arguments
            $principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet `
                -ExecutionTimeLimit (New-TimeSpan -Seconds ([int]$ExecutionTimeLimitSeconds)) `
                -MultipleInstances IgnoreNew
            $registered = Register-ScheduledTask -TaskName $Name -Action $action `
                -Principal $principal -Settings $settings
            $registeredAction = @($registered.Actions)[0]
            return [pscustomobject]@{
                UserId = [string]$registered.Principal.UserId
                LogonType = [string]$registered.Principal.LogonType
                RunLevel = [string]$registered.Principal.RunLevel
                Execute = [string]$registeredAction.Execute
                Arguments = [string]$registeredAction.Arguments
            }
        }
        Start = { param($Name) Start-ScheduledTask -TaskName $Name }
        Stop = { param($Name) Stop-ScheduledTask -TaskName $Name -ErrorAction Stop }
        Query = {
            param($Name)
            $info = Get-ScheduledTaskInfo -TaskName $Name
            return [pscustomobject]@{
                State = [string](Get-ScheduledTask -TaskName $Name).State
                LastTaskResult = [int]$info.LastTaskResult
            }
        }
        Unregister = { param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction Stop }
    }
}

function Get-SourceGatewayTrustedDirectorySids {
    [CmdletBinding()]
    param()

    return @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    )
}

function Get-SourceGatewayDirectorySecurity {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Path = $Path; Exists = $false; IsReparsePoint = $false; OwnerSid = ''; Access = @() }
    }
    $item = Get-Item -LiteralPath $Path -Force
    $acl = Get-Acl -LiteralPath $Path
    $owner = ''
    try { $owner = [string]$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { $owner = '' }
    $access = @($acl.Access | ForEach-Object {
        $sid = ''
        try {
            $sid = if ($_.IdentityReference -is [Security.Principal.SecurityIdentifier]) {
                [string]$_.IdentityReference.Value
            } else {
                [string]$_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            }
        }
        catch { $sid = '' }
        [pscustomobject]@{
            IdentitySid = $sid
            Rights = [Security.AccessControl.FileSystemRights]$_.FileSystemRights
            Type = [string]$_.AccessControlType
            InheritanceFlags = [Security.AccessControl.InheritanceFlags]$_.InheritanceFlags
            PropagationFlags = [Security.AccessControl.PropagationFlags]$_.PropagationFlags
        }
    })
    return [pscustomobject]@{
        Path = [string]$item.FullName
        Exists = $true
        IsReparsePoint = (([int]$item.Attributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0)
        OwnerSid = $owner
        Access = $access
    }
}

function Get-SourceGatewayDirectoryAncestry {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $chain = @()
    $cursor = [IO.Path]::GetFullPath($Path)
    if ($cursor.Length -gt 3) { $cursor = $cursor.TrimEnd('\') }
    while (-not [string]::IsNullOrEmpty($cursor)) {
        $chain = @($cursor) + $chain
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrEmpty($parent) -or $parent -ceq $cursor) { break }
        $cursor = if ($parent.Length -gt 3) { $parent.TrimEnd('\') } else { $parent }
    }
    return @($chain)
}

function Assert-SourceGatewaySecureStageAncestry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $RequireExists,
        [AllowEmptyString()] [ValidatePattern('^(?:|S-1-[0-9-]+)$')] [string] $ApprovedStageSid = '',
        [scriptblock] $DirectorySecurityProbe = { param($Probed) Get-SourceGatewayDirectorySecurity -Path $Probed }
    )

    $trusted = @(Get-SourceGatewayTrustedDirectorySids)
    $dangerous = [int]([Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership, WriteData, CreateDirectories')
    # Windows stores Synchronize on every Allow ACE, so the exact approved-leaf allowance is Modify plus it.
    $approvedLeafMask = [int]([Security.AccessControl.FileSystemRights]'Modify, Synchronize')
    $ancestors = @(Get-SourceGatewayDirectoryAncestry -Path $Path)
    if ($ancestors.Count -eq 0) {
        throw 'The staging path could not be resolved to a canonical ancestry chain.'
    }
    # Only the leaf this call is validating is this run's own folder; every ancestor above it stays
    # trusted-principals-only, so an untrusted parent can never earn delete-child rights over it.
    $ownedStage = [string]$ancestors[$ancestors.Count - 1]
    foreach ($ancestor in $ancestors) {
        $security = $null
        try { $security = & $DirectorySecurityProbe $ancestor }
        catch { throw 'The staging path ancestry could not be inspected; refusing to stage.' }
        if ($null -eq $security) {
            throw 'The staging path ancestry could not be inspected; refusing to stage.'
        }
        if (-not [bool]$security.Exists) {
            if ($RequireExists) {
                throw 'The staging path ancestry disappeared between validation and use.'
            }
            continue
        }
        if ([bool]$security.IsReparsePoint) {
            throw 'The staging path ancestry crosses a reparse point; refusing to stage.'
        }
        if ($trusted -cnotcontains [string]$security.OwnerSid) {
            throw 'The staging path ancestry is not owned by SYSTEM or Administrators; refusing to stage.'
        }
        # The pinned root and the run folder are the only places this tooling writes, so anything above them
        # may only be writable or deletable by trusted principals.
        $isPinnedOrBelow = $ancestor -ieq $script:SourceGatewayPinnedStageRoot -or
            $ancestor.StartsWith($script:SourceGatewayPinnedStageRoot + '\', [StringComparison]::OrdinalIgnoreCase)
        $isOwnedStageOrBelow = $ancestor -ieq $ownedStage -or
            $ancestor.StartsWith($ownedStage + '\', [StringComparison]::OrdinalIgnoreCase)
        foreach ($rule in @($security.Access)) {
            if ([string]$rule.Type -cne 'Allow') { continue }
            if ($trusted -ccontains [string]$rule.IdentitySid) { continue }
            $granted = [int]$rule.Rights -band $dangerous
            if ($granted -eq 0) { continue }
            $propagationProperty = $rule.PSObject.Properties['PropagationFlags']
            $isInheritOnly = $null -ne $propagationProperty -and
                (([Security.AccessControl.PropagationFlags]$propagationProperty.Value -band
                    [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)
            if (-not $isPinnedOrBelow -and $isInheritOnly) { continue }
            if ($isOwnedStageOrBelow -and -not [string]::IsNullOrEmpty($ApprovedStageSid) -and
                [string]$rule.IdentitySid -ceq $ApprovedStageSid) {
                # This run creates the leaf and deliberately grants the resolved interactive account Modify
                # so the scheduled task can write its own outputs. Nothing beyond Modify is tolerated even
                # here, and no other identity reaches this exemption.
                if (([int]$rule.Rights -band -bnot $approvedLeafMask) -ne 0) {
                    throw 'The staging folder grants the approved interactive account more than Modify; refusing to stage.'
                }
                continue
            }
            if (-not $isPinnedOrBelow -and
                ($granted -band [int]([Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership')) -eq 0) {
                # Stock Windows lets standard users add subdirectories under C:\ProgramData; that alone cannot
                # redirect or remove an already-created, trusted-owned root.
                continue
            }
            throw 'The staging path ancestry grants untrusted write or delete access; refusing to stage.'
        }
    }
}

function Get-SourceGatewaySafeErrorCodes {
    [CmdletBinding()]
    param([AllowNull()] [AllowEmptyString()] [string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return @() }
    return @([regex]::Matches($Text, '\b(?:ORA-\d{5}|SP2-\d{4})\b') | ForEach-Object Value | Sort-Object -Unique)
}

function Resolve-SourceGatewayPrincipalSid {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Identity)

    if ($Identity -cmatch '^S-1-[0-9-]+$') { return $Identity }
    $account = if ($Identity.Contains('\')) {
        [Security.Principal.NTAccount]::new($Identity)
    } else {
        [Security.Principal.NTAccount]::new($env:COMPUTERNAME, $Identity)
    }
    try {
        return $account.Translate([Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "The approved interactive account '$Identity' could not be resolved on this host."
    }
}

function New-SourceGatewayInteractiveStageWrapper {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $StagingPath,
        [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{32}$')] [string] $RunId
    )

    $template = @'
$ErrorActionPreference = 'Stop'
$stage = '__STAGE__'
$status = [ordered]@{ runId = '__RUNID__'; stage = 'start'; status = 'failed'; errorType = ''; errorCodes = @() }
try {
    # Kill-on-close containment must exist before any Oracle action so that stopping this process also
    # terminates SQL*Plus and every other descendant it ever creates.
    Add-Type -Namespace OfmSourceGateway -Name Containment -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr CreateJobObjectW(System.IntPtr attributes, string name);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetInformationJobObject(System.IntPtr job, int infoClass, System.IntPtr info, uint length);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool AssignProcessToJobObject(System.IntPtr job, System.IntPtr process);
"@
    $script:ofmContainmentJob = [OfmSourceGateway.Containment]::CreateJobObjectW([IntPtr]::Zero, $null)
    if ($script:ofmContainmentJob -eq [IntPtr]::Zero) {
        throw 'The staged Oracle run could not create its containment job.'
    }
    $limitBytes = if ([IntPtr]::Size -eq 8) { 144 } else { 112 }
    $limitBuffer = [Runtime.InteropServices.Marshal]::AllocHGlobal($limitBytes)
    try {
        for ($offset = 0; $offset -lt $limitBytes; $offset++) {
            [Runtime.InteropServices.Marshal]::WriteByte($limitBuffer, $offset, 0)
        }
        # LimitFlags sits at offset 16 of JOBOBJECT_BASIC_LIMIT_INFORMATION on both x86 and x64.
        [Runtime.InteropServices.Marshal]::WriteInt32($limitBuffer, 16, 0x00002000)
        if (-not [OfmSourceGateway.Containment]::SetInformationJobObject(
                $script:ofmContainmentJob, 9, $limitBuffer, [uint32]$limitBytes)) {
            throw 'The staged Oracle run could not arm kill-on-close containment.'
        }
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($limitBuffer)
    }
    $self = Get-Process -Id $PID
    if (-not [OfmSourceGateway.Containment]::AssignProcessToJobObject($script:ofmContainmentJob, $self.Handle)) {
        throw 'The staged Oracle run could not join its containment job.'
    }
    Set-Content -LiteralPath (Join-Path $stage 'process.json') -Encoding ascii -NoNewline -Value (([ordered]@{
        runId = '__RUNID__'
        processId = [int]$self.Id
        startTimeUtcTicks = [string]$self.StartTime.ToUniversalTime().Ticks
    }) | ConvertTo-Json -Compress)
    $request = Get-Content -LiteralPath (Join-Path $stage 'input.json') -Raw | ConvertFrom-Json
    if ([string]$request.runId -cne '__RUNID__') { throw 'The staged request does not belong to this run.' }
    $status.stage = 'provision'
    $output = & ([string]$request.credentialScriptPath) `
        -PublicCertificateBase64 ([string]$request.publicCertificateBase64) `
        -TransferModulePath ([string]$request.transferModulePath) `
        -OdbcDsn ([string]$request.odbcDsn)
    $status.stage = 'result'
    Set-Content -LiteralPath (Join-Path $stage 'result.json') `
        -Value (([ordered]@{ runId = '__RUNID__'; result = [string](@($output) | Select-Object -Last 1) } |
            ConvertTo-Json -Compress)) -Encoding ascii -NoNewline
    $status.stage = 'complete'
    $status.status = 'succeeded'
}
catch {
    $status.errorType = $_.Exception.GetType().Name
    $status.errorCodes = @([regex]::Matches([string]$_.Exception.Message, '\b(?:ORA-\d{5}|SP2-\d{4})\b') |
        ForEach-Object Value | Sort-Object -Unique)
}
finally {
    Set-Content -LiteralPath (Join-Path $stage 'status.json') -Value ($status | ConvertTo-Json -Compress) -Encoding ascii
}
if ($status.status -cne 'succeeded') { exit 3 }
'@

    return $template.Replace('__STAGE__', $StagingPath).Replace('__RUNID__', $RunId)
}

function Wait-SourceGatewayStagedProcessExit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ProcessRecordPath,
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [ValidateRange(1, 600)] [int] $TimeoutSeconds,
        [Parameter(Mandatory)] [scriptblock] $Wait,
        [Parameter(Mandatory)] [scriptblock] $ProcessProbe
    )

    # Returns 'Exited', 'Running', 'NoRecord' or 'Unknown'. Only 'Exited' is proof; an absent record is
    # not, because a task that has only just started has not written the record yet.
    if (-not (Test-Path -LiteralPath $ProcessRecordPath -PathType Leaf)) {
        return 'NoRecord'
    }
    # Every read, shape, type and range check happens here so a missing property, an unexpected property or
    # an out-of-range number can never escape as an exception or be mistaken for proof of exit.
    $processId = 0
    $startTicks = [long]0
    try {
        $recordText = [string](Get-Content -LiteralPath $ProcessRecordPath -Raw)
        if ($recordText.Length -eq 0 -or $recordText.Length -gt 4096) { throw 'unbounded record' }
        if (-not $recordText.TrimStart().StartsWith('{', [StringComparison]::Ordinal)) { throw 'record shape' }
        $record = $recordText | ConvertFrom-Json
        if ($null -eq $record -or $record -isnot [psobject] -or $record -is [Array]) { throw 'record shape' }
        if ((@($record.PSObject.Properties.Name | Sort-Object) -join "`n") -cne "processId`nrunId`nstartTimeUtcTicks") {
            throw 'record fields'
        }
        if ([string]$record.runId -cne $RunId) { throw 'record run identifier' }
        $recordedPid = 0
        $recordedTicks = [long]0
        if (-not [int]::TryParse([string]$record.processId, [ref]$recordedPid) -or $recordedPid -le 0) {
            throw 'record process identifier'
        }
        if (-not [long]::TryParse([string]$record.startTimeUtcTicks, [ref]$recordedTicks) -or $recordedTicks -le 0) {
            throw 'record start time'
        }
        $processId = $recordedPid
        $startTicks = $recordedTicks
    }
    catch { return 'Unknown' }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $observed = 'Unknown'
        $live = $null
        $probeFailed = $false
        try { $live = & $ProcessProbe $processId } catch { $probeFailed = $true }
        if (-not $probeFailed) {
            if ($null -eq $live) { return 'Exited' }
            $liveTicks = $null
            try { $liveTicks = [long]$live.StartTime.ToUniversalTime().Ticks } catch { $liveTicks = $null }
            if ($null -ne $liveTicks) {
                if ($liveTicks -ne $startTicks) { return 'Exited' }
                $observed = 'Running'
            }
        }
        if ([DateTime]::UtcNow -ge $deadline) { return $observed }
        & $Wait 250
    }
}

function Confirm-SourceGatewayStageContainment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $TaskName,
        [Parameter(Mandatory)] [hashtable] $TaskAdapter,
        [Parameter(Mandatory)] [string] $ProcessRecordPath,
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [ValidateRange(1, 600)] [int] $TimeoutSeconds,
        [Parameter(Mandatory)] [scriptblock] $Wait,
        [Parameter(Mandatory)] [scriptblock] $ProcessProbe
    )

    $failures = @()
    try { & $TaskAdapter.Stop $TaskName }
    catch { $failures += 'stop request failed' }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    # The wrapper joins a kill-on-close job before it touches Oracle, so proving the recorded wrapper
    # identity is gone also proves SQL*Plus and every other descendant is gone.
    $exit = Wait-SourceGatewayStagedProcessExit -ProcessRecordPath $ProcessRecordPath -RunId $RunId `
        -TimeoutSeconds $TimeoutSeconds -Wait $Wait -ProcessProbe $ProcessProbe
    if ($exit -ceq 'Running') { $failures += 'staged process still running' }
    elseif ($exit -ceq 'Unknown') { $failures += 'the staged process exit could not be confirmed' }
    elseif ($exit -ceq 'NoRecord') {
        # No record only means "nothing ran" once the task itself is observed out of Running.
        $settled = $false
        while ($true) {
            $state = $null
            try { $state = & $TaskAdapter.Query $TaskName } catch { $state = $null }
            if ($null -ne $state -and [string]$state.State -cne 'Running') { $settled = $true; break }
            if ([DateTime]::UtcNow -ge $deadline) { break }
            & $Wait 250
        }
        if (-not $settled) { $failures += 'the staged process could not be identified' }
    }
    return @($failures)
}

function Invoke-SourceGatewayInteractiveOracleStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $CredentialScriptPath,
        [Parameter(Mandatory)] [string] $TransferModulePath,
        [Parameter(Mandatory)] [string] $PublicCertificateBase64,
        [Parameter(Mandatory)] [ValidateSet('OFM_GATEWAY_ORACLE9I')] [string] $OdbcDsn,
        [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{32}$')] [string] $RunId,
        [string] $StagingRoot = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway',
        [ValidatePattern('^(?:S-1-[0-9-]+|[A-Za-z][A-Za-z0-9_.-]{2,31})$')] [string] $InteractiveAccountName = 'ofmlabadmin',
        [ValidateRange(5, 1800)] [int] $TimeoutSeconds = 600,
        [ValidateRange(5, 600)] [int] $ContainmentTimeoutSeconds = 60,
        [scriptblock] $InteractiveSessionProbe = { Get-SourceGatewayInteractiveDesktopSession },
        [hashtable] $TaskAdapter,
        [scriptblock] $Wait = { param($Milliseconds) [Threading.Thread]::Sleep($Milliseconds) },
        [scriptblock] $ProcessProbe = { param($ProcessId) Get-Process -Id $ProcessId -ErrorAction SilentlyContinue },
        [scriptblock] $DirectorySecurityProbe = { param($Probed) Get-SourceGatewayDirectorySecurity -Path $Probed }
    )

    $adapterInjected = $PSBoundParameters.ContainsKey('TaskAdapter') -and $null -ne $TaskAdapter
    $canonicalStagingRoot = [IO.Path]::GetFullPath($StagingRoot).TrimEnd('\')
    if (-not $adapterInjected -and -not (
            $canonicalStagingRoot -ieq $script:SourceGatewayPinnedStageRoot -or
            $canonicalStagingRoot.StartsWith($script:SourceGatewayPinnedStageRoot + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The interactive Oracle credential stage only runs under the pinned ProgramData root.'
    }

    foreach ($path in @($CredentialScriptPath, $TransferModulePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'The reviewed Oracle credential inputs were not staged.'
        }
    }
    $certificate = $null
    $expectedThumbprint = $null
    try {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [Convert]::FromBase64String($PublicCertificateBase64))
        $expectedThumbprint = [string]$certificate.Thumbprint
    }
    catch {
        throw 'The one-time public certificate supplied to the interactive Oracle stage is not a valid certificate.'
    }
    finally {
        if ($null -ne $certificate) { $certificate.Dispose() }
    }

    $expectedSid = Resolve-SourceGatewayPrincipalSid -Identity $InteractiveAccountName
    $sessions = @(& $InteractiveSessionProbe)
    $interactive = @($sessions | Where-Object { [string]$_.UserSid -ceq $expectedSid -and [int]$_.SessionId -gt 0 })
    if ($interactive.Count -eq 0) {
        throw ("Oracle credential provisioning requires an existing interactive desktop session for " +
            "$InteractiveAccountName on $env:COMPUTERNAME; sign that account in and rerun. No session was created.")
    }

    if (-not $adapterInjected) {
        $TaskAdapter = New-SourceGatewayScheduledTaskAdapter
    }

    $stagingPath = Join-Path $canonicalStagingRoot "oracle-credential-$RunId"
    if (Test-Path -LiteralPath $stagingPath) {
        throw 'The staging folder for this run identifier already exists; refusing to reuse it.'
    }
    # Ancestry must be proven safe before any task file is written and before any task name is claimed.
    Assert-SourceGatewaySecureStageAncestry -Path $stagingPath -DirectorySecurityProbe $DirectorySecurityProbe

    $taskName = "OFM-SourceGatewayOracleCredential-$RunId"
    if (& $TaskAdapter.Exists $taskName) {
        throw 'A scheduled task already owns this run identifier; refusing to reuse an existing task.'
    }

    $registrationAttempted = $false
    $startRequested = $false
    $exitConfirmed = $false
    $preserveStaging = $false
    $primaryFailure = $null
    $cleanupFailures = @()
    $stageResult = $null
    $processPath = Join-Path $stagingPath 'process.json'
    try {
        [void](New-Item -ItemType Directory -Path $stagingPath -Force)
        $acl = Get-Acl -LiteralPath $stagingPath
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($existing in @($acl.Access)) { [void]$acl.RemoveAccessRule($existing) }
        foreach ($grant in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @($expectedSid, 'Modify'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($grant[0]),
                [Security.AccessControl.FileSystemRights]$grant[1],
                [Security.AccessControl.InheritanceFlags]'ObjectInherit, ContainerInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $stagingPath -AclObject $acl -ErrorAction Stop
        Assert-SourceGatewaySecureStageAncestry -Path $stagingPath -RequireExists `
            -ApprovedStageSid $expectedSid -DirectorySecurityProbe $DirectorySecurityProbe

        $wrapperPath = Join-Path $stagingPath 'Invoke-OracleCredentialStage.ps1'
        $resultPath = Join-Path $stagingPath 'result.json'
        $statusPath = Join-Path $stagingPath 'status.json'
        Set-Content -LiteralPath (Join-Path $stagingPath 'input.json') -Encoding ascii -Value (([ordered]@{
            runId = $RunId
            credentialScriptPath = $CredentialScriptPath
            transferModulePath = $TransferModulePath
            publicCertificateBase64 = $PublicCertificateBase64
            odbcDsn = $OdbcDsn
        }) | ConvertTo-Json -Compress)
        Set-Content -LiteralPath $wrapperPath -Encoding ascii `
            -Value (New-SourceGatewayInteractiveStageWrapper -StagingPath $stagingPath -RunId $RunId)

        $taskUserId = if ($InteractiveAccountName -cmatch '^S-1-') {
            $InteractiveAccountName
        } else {
            $env:COMPUTERNAME + '\' + $InteractiveAccountName
        }
        $expectedExecutable = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
        $expectedArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $wrapperPath + '"'
        # The name is a fresh GUID proven Absent above, so anything under it from here on is this run's to remove,
        # even when registration only half succeeds.
        $registrationAttempted = $true
        $principal = & $TaskAdapter.Register $taskName $expectedExecutable $expectedArguments $taskUserId $TimeoutSeconds
        if ((Resolve-SourceGatewayPrincipalSid -Identity ([string]$principal.UserId)) -cne $expectedSid -or
            [string]$principal.LogonType -cne 'Interactive' -or [string]$principal.RunLevel -cne 'Highest') {
            throw 'The one-shot Oracle credential task principal is not the approved interactive account.'
        }
        if ([string]$principal.Execute -cne $expectedExecutable -or [string]$principal.Arguments -cne $expectedArguments) {
            throw 'The one-shot Oracle credential task does not run the staged wrapper that was reviewed.'
        }

        $startRequested = $true
        & $TaskAdapter.Start $taskName
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $completed = $false
        $lastTaskResult = $null
        while ([DateTime]::UtcNow -lt $deadline) {
            $state = & $TaskAdapter.Query $taskName
            $lastTaskResult = [int]$state.LastTaskResult
            if ([string]$state.State -cne 'Running' -and $lastTaskResult -ne 267009 -and $lastTaskResult -ne 267011) {
                $completed = $true
                break
            }
            & $Wait 1000
        }
        if (-not $completed) {
            # Containment is proven once, in the finally block, for every post-start failure.
            throw "The one-shot Oracle credential task did not finish within $TimeoutSeconds seconds."
        }
        # The task was observed out of Running by a Query that did not throw, so an absent process record
        # here really does mean the wrapper never started.
        $observedExit = Wait-SourceGatewayStagedProcessExit -ProcessRecordPath $processPath -RunId $RunId `
            -TimeoutSeconds $ContainmentTimeoutSeconds -Wait $Wait -ProcessProbe $ProcessProbe
        $exitConfirmed = $observedExit -ceq 'Exited' -or $observedExit -ceq 'NoRecord'

        if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
            throw "The one-shot Oracle credential task produced no status; taskResult=$lastTaskResult."
        }
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        if ([string]$status.runId -cne $RunId) {
            throw 'The staged Oracle credential status does not belong to this run.'
        }
        if ([string]$status.status -cne 'succeeded') {
            $codes = @(Get-SourceGatewaySafeErrorCodes -Text ((@($status.errorCodes) -join ',')))
            $codeSummary = if ($codes.Count -eq 0) { 'none' } else { $codes -join ',' }
            throw ("The interactive Oracle credential stage failed; stage=$([string]$status.stage); " +
                "errorType=$([string]$status.errorType); errorCodes=$codeSummary; taskResult=$lastTaskResult.")
        }
        if ($lastTaskResult -ne 0) {
            throw "The one-shot Oracle credential task reported taskResult=$lastTaskResult."
        }

        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw 'The interactive Oracle credential stage produced no result document.'
        }
        $resultText = (Get-Content -LiteralPath $resultPath -Raw)
        if ([string]::IsNullOrWhiteSpace($resultText)) {
            throw 'The interactive Oracle credential stage produced an empty result document.'
        }
        $stageResult = Assert-SourceGatewayOracleCredentialResult -ResultJson $resultText -Dsn $OdbcDsn `
            -ExpectedCertificateThumbprint $expectedThumbprint -ExpectedRunId $RunId -IncludeRunId
    }
    catch {
        $primaryFailure = $_
    }
    finally {
        # The Task Scheduler execution limit is only a backup: nothing may be unregistered or deleted until
        # this run has proven the wrapper and every descendant it contains are gone.
        if ($startRequested -and -not $exitConfirmed) {
            # A fault inside containment verification is itself an unverified containment, so it joins the
            # same bounded, nonsensitive cleanup aggregation instead of displacing the primary failure.
            $containmentFailures = @()
            try {
                $containmentFailures = @(Confirm-SourceGatewayStageContainment -TaskName $taskName `
                    -TaskAdapter $TaskAdapter -ProcessRecordPath $processPath -RunId $RunId `
                    -TimeoutSeconds $ContainmentTimeoutSeconds -Wait $Wait -ProcessProbe $ProcessProbe)
            }
            catch { $containmentFailures = @('the containment check could not be completed') }
            if ($containmentFailures.Count -ne 0) {
                $preserveStaging = $true
                $cleanupFailures += ("containment could not be verified (" +
                    ($containmentFailures -join '; ') + ')')
            }
        }
        if ($registrationAttempted) {
            try { & $TaskAdapter.Unregister $taskName }
            catch { $cleanupFailures += 'the one-shot task could not be removed' }
            try {
                if (& $TaskAdapter.Exists $taskName) { $cleanupFailures += 'the one-shot task is still registered' }
            }
            catch { $cleanupFailures += 'the one-shot task removal could not be verified' }
        }
        if ($preserveStaging) {
            $cleanupFailures += "the staging folder was preserved at $stagingPath for recovery"
        }
        else {
            try { Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction Stop }
            catch {
                if (Test-Path -LiteralPath $stagingPath) { $cleanupFailures += 'the staging folder could not be removed' }
            }
        }
    }

    if ($null -ne $primaryFailure) {
        if ($cleanupFailures.Count -eq 0) { throw $primaryFailure }
        throw ([string]$primaryFailure.Exception.Message + ' Cleanup also failed: ' + ($cleanupFailures -join '; ') + '.')
    }
    if ($cleanupFailures.Count -ne 0) {
        throw ('The interactive Oracle credential stage completed but cleanup failed: ' +
            ($cleanupFailures -join '; ') + '.')
    }
    return $stageResult
}

function Assert-SourceGatewayOracleCredentialResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ResultJson,
        [Parameter(Mandatory)] [ValidateSet('OFM_GATEWAY_ORACLE9I')] [string] $Dsn,
        [ValidatePattern('^[A-Fa-f0-9]{40,64}$')] [string] $ExpectedCertificateThumbprint,
        [ValidatePattern('^[a-f0-9]{32}$')] [string] $ExpectedRunId,
        [switch] $IncludeRunId
    )

    # Bound the document before parsing so an oversized payload can never be buffered, echoed, or scanned.
    if ($ResultJson.Length -gt 8192) {
        throw 'The interactive Oracle credential stage returned an oversized result document.'
    }
    $parsed = $null
    try { $parsed = $ResultJson | ConvertFrom-Json }
    catch { throw 'The interactive Oracle credential stage returned a malformed result document.' }
    if ($null -eq $parsed -or $parsed -isnot [psobject]) {
        throw 'The interactive Oracle credential stage returned a malformed result document.'
    }

    if ($IncludeRunId) {
        if ([string]::IsNullOrEmpty($ExpectedRunId)) {
            throw 'The interactive Oracle credential result cannot be bound without an expected run identifier.'
        }
        $envelopeKeys = @($parsed.PSObject.Properties.Name | Sort-Object)
        if (($envelopeKeys -join "`n") -cne "result`nrunId") {
            throw 'The interactive Oracle credential envelope does not match the run-binding allowlist.'
        }
        if ($parsed.runId -isnot [string] -or [string]$parsed.runId -cne $ExpectedRunId) {
            throw 'The interactive Oracle credential envelope does not belong to this run.'
        }
        if ($parsed.result -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$parsed.result)) {
            throw 'The interactive Oracle credential envelope does not carry a result document.'
        }
        # The envelope is a local transport detail; the returned document stays on the public allowlist.
        return (Assert-SourceGatewayOracleCredentialResult -ResultJson ([string]$parsed.result) -Dsn $Dsn `
            -ExpectedCertificateThumbprint $ExpectedCertificateThumbprint)
    }

    $contract = New-SourceGatewayOracleCredentialResult -Dsn $Dsn -CiphertextBase64 'AA==' `
        -CiphertextSha256 ('0' * 64) -CertificateThumbprint ('A' * 40)
    $expected = @($contract.Keys)
    $actual = @($parsed.PSObject.Properties.Name)
    if ((@($actual | Sort-Object) -join "`n") -cne (@($expected | Sort-Object) -join "`n")) {
        throw 'The interactive Oracle credential result does not match the nonsecret allowlist.'
    }

    foreach ($stringField in @('status', 'username', 'roleName', 'schema', 'dsn',
            'ciphertextBase64', 'ciphertextSha256', 'certificateThumbprint')) {
        if ($parsed.$stringField -isnot [string]) {
            throw 'The interactive Oracle credential result failed the nonsecret value contract.'
        }
    }
    # ConvertFrom-Json widens integers to Int64 on PowerShell 7, so accept either width but never a string.
    if (($parsed.schemaVersion -isnot [int] -and $parsed.schemaVersion -isnot [long]) -or
        [long]$parsed.schemaVersion -ne 1 -or
        [string]$parsed.status -cne 'encrypted' -or
        [string]$parsed.username -cne 'OFM_GATEWAY_RO' -or
        [string]$parsed.roleName -cne 'OFM_GATEWAY_SOURCE_RO' -or
        [string]$parsed.schema -cne 'MERIDIAN' -or
        [string]$parsed.dsn -cne $Dsn) {
        throw 'The interactive Oracle credential result failed the nonsecret value contract.'
    }

    $expectedGrants = @(Get-SourceGatewayOracleGrantContract)
    $actualGrants = @($parsed.grants)
    if ($actualGrants.Count -ne $expectedGrants.Count) {
        throw 'The interactive Oracle credential result failed the nonsecret grant contract.'
    }
    for ($index = 0; $index -lt $expectedGrants.Count; $index++) {
        if ($actualGrants[$index] -isnot [string] -or [string]$actualGrants[$index] -cne $expectedGrants[$index]) {
            throw 'The interactive Oracle credential result failed the nonsecret grant contract.'
        }
    }

    $ciphertextBase64 = [string]$parsed.ciphertextBase64
    if ($ciphertextBase64.Length -lt 344 -or $ciphertextBase64.Length -gt 1368 -or
        $ciphertextBase64 -cnotmatch '^[A-Za-z0-9+/]+={0,2}$') {
        throw 'The interactive Oracle credential result failed the ciphertext bounds contract.'
    }
    $ciphertext = $null
    try { $ciphertext = [Convert]::FromBase64String($ciphertextBase64) }
    catch { throw 'The interactive Oracle credential result failed the ciphertext bounds contract.' }
    # RSA-OAEP output is exactly one modulus block, so 2048-8192 bit keys land on these bounds.
    if ($ciphertext.Length -lt 256 -or $ciphertext.Length -gt 1024 -or ($ciphertext.Length % 8) -ne 0) {
        throw 'The interactive Oracle credential result failed the ciphertext bounds contract.'
    }

    if ([string]$parsed.ciphertextSha256 -cnotmatch '^[a-f0-9]{64}$') {
        throw 'The interactive Oracle credential result failed the ciphertext digest contract.'
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $actualDigest = ([BitConverter]::ToString($sha256.ComputeHash($ciphertext))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    if ([string]$parsed.ciphertextSha256 -cne $actualDigest) {
        throw 'The interactive Oracle credential result failed the ciphertext digest contract.'
    }

    $thumbprint = [string]$parsed.certificateThumbprint
    if ($thumbprint -cnotmatch '^[A-F0-9]{40,64}$' -or ($thumbprint.Length % 2) -ne 0) {
        throw 'The interactive Oracle credential result failed the certificate binding contract.'
    }
    if (-not [string]::IsNullOrEmpty($ExpectedCertificateThumbprint) -and
        $thumbprint -cne $ExpectedCertificateThumbprint.ToUpperInvariant()) {
        throw 'The interactive Oracle credential result failed the certificate binding contract.'
    }

    return ($parsed | ConvertTo-Json -Compress)
}

Export-ModuleMember -Function @(
    'Protect-SourceGatewayCredentialBytes',
    'New-SourceGatewayOraclePassword',
    'Get-SourceGatewayOracleGrantContract',
    'Get-SourceGatewayTransferCertificates',
    'Read-SourceGatewayTransferMetadata',
    'New-SourceGatewayOracleProvisioningSql',
    'Invoke-SourceGatewaySqlPlus',
    'Set-SourceGatewayOracleAccountLocked',
    'New-SourceGatewayOracleCredentialResult',
    'Get-SourceGatewayInteractiveDesktopSession',
    'Get-SourceGatewaySafeErrorCodes',
    'New-SourceGatewayScheduledTaskAdapter',
    'Get-SourceGatewayTrustedDirectorySids',
    'Get-SourceGatewayDirectorySecurity',
    'Get-SourceGatewayDirectoryAncestry',
    'Assert-SourceGatewaySecureStageAncestry',
    'Wait-SourceGatewayStagedProcessExit',
    'Confirm-SourceGatewayStageContainment',
    'New-SourceGatewayInteractiveStageWrapper',
    'Assert-SourceGatewayOracleCredentialResult',
    'Invoke-SourceGatewayInteractiveOracleStage'
)