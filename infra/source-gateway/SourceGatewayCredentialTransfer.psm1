Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

Export-ModuleMember -Function @(
    'Protect-SourceGatewayCredentialBytes',
    'New-SourceGatewayOraclePassword',
    'Get-SourceGatewayOracleGrantContract',
    'Get-SourceGatewayTransferCertificates',
    'Read-SourceGatewayTransferMetadata',
    'New-SourceGatewayOracleProvisioningSql',
    'Invoke-SourceGatewaySqlPlus',
    'Set-SourceGatewayOracleAccountLocked',
    'New-SourceGatewayOracleCredentialResult'
)