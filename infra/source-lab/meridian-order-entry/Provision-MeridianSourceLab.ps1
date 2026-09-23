[CmdletBinding()]
param(
    [ValidateSet('Probe', 'Install')]
    [string]$Operation = 'Probe',

    [string]$SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',

    [string]$ResourceGroupName = 'rg-oracle-forms-migration-fleet-dev-b9f0e875',

    [string]$ContainerAppName = 'ca-ofmfleet-db-dev-ykbpnrpd',

    [string]$ExpectedImage = 'acrofmfleedevykbpnrpd.azurecr.io/oracle-forms-legacy-db:v2'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = $PSScriptRoot
$invokeSqlPlus = Join-Path $scriptRoot 'Invoke-MeridianSqlPlus.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ofm-meridian-source-lab-' + [guid]::NewGuid().ToString('N'))
$azCommand = Get-Command az -ErrorAction Stop
$azExecutable = $azCommand.Source
$azPrefix = @()
if ([System.IO.Path]::GetExtension($azExecutable) -eq '.cmd') {
    $azCliRoot = Split-Path (Split-Path $azExecutable -Parent) -Parent
    $azPython = Join-Path $azCliRoot 'python.exe'
    if (Test-Path -LiteralPath $azPython -PathType Leaf) {
        $azExecutable = $azPython
        $azPrefix = @('-IBm', 'azure.cli')
    }
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $stderrPath = Join-Path $temporaryRoot ('az-stderr-' + [guid]::NewGuid().ToString('N') + '.log')
    try {
        $raw = & $azExecutable @azPrefix @Arguments -o json 2>$stderrPath
        if ($LASTEXITCODE -ne 0) {
            throw (Get-Content -LiteralPath $stderrPath -Raw)
        }

        return ($raw | Out-String | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SqlPlusScript {
    param([Parameter(Mandatory)][string]$Path)

    $transcriptPath = Join-Path $temporaryRoot ('sqlplus-' + [guid]::NewGuid().ToString('N') + '.log')
    try {
        $rawOutput = @(& $invokeSqlPlus `
            -SubscriptionId $SubscriptionId `
            -ResourceGroupName $ResourceGroupName `
            -ContainerAppName $ContainerAppName `
            -SqlPath $Path `
            -TranscriptPath $transcriptPath)
    }
    catch {
        $transcript = if (Test-Path -LiteralPath $transcriptPath) {
            Get-Content -LiteralPath $transcriptPath -Raw
        }
        else {
            ''
        }
        $diagnostics = @($transcript -split "`r?`n" | Where-Object {
            $_ -match '^(?:ORA|PLS|SP2)-\d+' -or $_ -match 'MERIDIAN ORDER LAB:'
        }) -join [Environment]::NewLine
        throw "SQL*Plus execution failed. $diagnostics"
    }

    $rawText = $rawOutput -join "`n"
    $outputLines = @($rawText -split "`r?`n")
    $oracleDiagnostics = @($outputLines | Where-Object {
        $_ -match '^(?:ORA|PLS|SP2)-\d+'
    }) -join [Environment]::NewLine
    if (-not [string]::IsNullOrWhiteSpace($oracleDiagnostics)) {
        throw "SQL*Plus reported an Oracle error. $oracleDiagnostics"
    }

    $evidence = @($outputLines | Where-Object {
        $_ -match '^OFM_(?:PROBE|INSTALL)\|' -or
        $_ -match '^MERIDIAN ORDER LAB: (?:PASS|FAIL|NOT VERIFIED|SEED OK|SEED INCOMPLETE)' -or
        $_ -match '^(?:ORA|PLS|SP2)-\d+'
    })

    return [pscustomobject]@{
        Raw = $rawOutput
        Evidence = $evidence
    }
}

function Get-RequiredMarkerInt {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $match = [regex]::Match($Text, "OFM_PROBE\|$([regex]::Escape($Name))=(\d+)")
    if (-not $match.Success) {
        throw "Oracle inventory probe did not return marker '$Name'."
    }

    return [int]$match.Groups[1].Value
}

function Get-MeridianCheckpoint {
    $inventoryPath = Join-Path $temporaryRoot ('inventory-' + [guid]::NewGuid().ToString('N') + '.sql')
    @'
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SET SERVEROUTPUT ON
DECLARE
    v_users              PLS_INTEGER;
    v_schema_objects     PLS_INTEGER;
    v_program_objects    PLS_INTEGER;
    v_unexpected_objects PLS_INTEGER;
    v_constraints        PLS_INTEGER;
    v_bad_constraints    PLS_INTEGER;
    v_compile_errors     PLS_INTEGER;
    v_customers          PLS_INTEGER := 0;
    v_articles           PLS_INTEGER := 0;
    v_order_heads        PLS_INTEGER := 0;
    v_order_items        PLS_INTEGER := 0;

    PROCEDURE read_count(p_table IN VARCHAR2, p_count OUT PLS_INTEGER) IS
        v_exists PLS_INTEGER;
    BEGIN
        p_count := 0;
        SELECT COUNT(*) INTO v_exists
          FROM DBA_TABLES
         WHERE OWNER = 'MERIDIAN' AND TABLE_NAME = p_table;
        IF v_exists = 1 THEN
            EXECUTE IMMEDIATE 'SELECT COUNT(*) FROM MERIDIAN.' || DBMS_ASSERT.SIMPLE_SQL_NAME(p_table)
                INTO p_count;
        END IF;
    END read_count;
BEGIN
    SELECT COUNT(*) INTO v_users FROM DBA_USERS WHERE USERNAME = 'MERIDIAN';

    SELECT COUNT(*) INTO v_schema_objects
      FROM DBA_OBJECTS
     WHERE OWNER = 'MERIDIAN'
       AND (OBJECT_TYPE, OBJECT_NAME) IN (
            ('TABLE', 'MRD_CUSTOMER'), ('TABLE', 'MRD_ARTICLE'),
            ('TABLE', 'MRD_ORDER_HEAD'), ('TABLE', 'MRD_ORDER_ITEM'),
            ('SEQUENCE', 'MRD_ORDER_SEQ'), ('SEQUENCE', 'MRD_ORDER_ITEM_SEQ'),
            ('INDEX', 'PK_MRD_CUSTOMER'), ('INDEX', 'PK_MRD_ARTICLE'),
            ('INDEX', 'PK_MRD_ORDER_HEAD'), ('INDEX', 'PK_MRD_ORDER_ITEM'),
            ('INDEX', 'IX_MRD_ORDER_HEAD_CUST'), ('INDEX', 'IX_MRD_ORDER_ITEM_ORD'),
            ('INDEX', 'IX_MRD_ORDER_ITEM_ART'));

    SELECT COUNT(*) INTO v_program_objects
      FROM DBA_OBJECTS
     WHERE OWNER = 'MERIDIAN'
       AND (OBJECT_TYPE, OBJECT_NAME) IN (
            ('TYPE', 'MRD_ORDER_LINE_T'), ('TYPE', 'MRD_ORDER_LINE_TAB'),
            ('PACKAGE', 'MRD_ORDER_ENTRY_API'), ('PACKAGE BODY', 'MRD_ORDER_ENTRY_API'));

    SELECT COUNT(*) INTO v_unexpected_objects
      FROM DBA_OBJECTS
     WHERE OWNER = 'MERIDIAN'
       AND (OBJECT_TYPE, OBJECT_NAME) NOT IN (
            ('TABLE', 'MRD_CUSTOMER'), ('TABLE', 'MRD_ARTICLE'),
            ('TABLE', 'MRD_ORDER_HEAD'), ('TABLE', 'MRD_ORDER_ITEM'),
            ('SEQUENCE', 'MRD_ORDER_SEQ'), ('SEQUENCE', 'MRD_ORDER_ITEM_SEQ'),
            ('INDEX', 'PK_MRD_CUSTOMER'), ('INDEX', 'PK_MRD_ARTICLE'),
            ('INDEX', 'PK_MRD_ORDER_HEAD'), ('INDEX', 'PK_MRD_ORDER_ITEM'),
            ('INDEX', 'IX_MRD_ORDER_HEAD_CUST'), ('INDEX', 'IX_MRD_ORDER_ITEM_ORD'),
            ('INDEX', 'IX_MRD_ORDER_ITEM_ART'),
            ('TYPE', 'MRD_ORDER_LINE_T'), ('TYPE', 'MRD_ORDER_LINE_TAB'),
            ('PACKAGE', 'MRD_ORDER_ENTRY_API'), ('PACKAGE BODY', 'MRD_ORDER_ENTRY_API'));

    SELECT COUNT(*), NVL(SUM(CASE WHEN STATUS <> 'ENABLED' OR VALIDATED <> 'VALIDATED' THEN 1 ELSE 0 END), 0)
      INTO v_constraints, v_bad_constraints
      FROM DBA_CONSTRAINTS
         WHERE OWNER = 'MERIDIAN'
             AND CONSTRAINT_NAME IN (
                        'PK_MRD_CUSTOMER', 'CK_MRD_CUST_STATE',
                        'PK_MRD_ARTICLE', 'CK_MRD_ART_STATE',
                        'PK_MRD_ORDER_HEAD', 'FK_MRD_ORDER_CUST', 'CK_MRD_ORD_STATE',
                        'PK_MRD_ORDER_ITEM', 'FK_MRD_ITEM_ORD', 'FK_MRD_ITEM_ART');

    SELECT COUNT(*) INTO v_compile_errors FROM DBA_ERRORS WHERE OWNER = 'MERIDIAN';
    read_count('MRD_CUSTOMER', v_customers);
    read_count('MRD_ARTICLE', v_articles);
    read_count('MRD_ORDER_HEAD', v_order_heads);
    read_count('MRD_ORDER_ITEM', v_order_items);

    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|SELECT_ONE=1');
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_USERS=' || v_users);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_SCHEMA_OBJECTS=' || v_schema_objects);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_PROGRAM_OBJECTS=' || v_program_objects);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_UNEXPECTED_OBJECTS=' || v_unexpected_objects);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_CONSTRAINTS=' || v_constraints);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_BAD_CONSTRAINTS=' || v_bad_constraints);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_COMPILE_ERRORS=' || v_compile_errors);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_CUSTOMERS=' || v_customers);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_ARTICLES=' || v_articles);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_ORDER_HEADS=' || v_order_heads);
    DBMS_OUTPUT.PUT_LINE('OFM_PROBE|MERIDIAN_ORDER_ITEMS=' || v_order_items);
END;
/
SELECT 'OFM_PROBE|BANKING_OBJECTS=' || TO_CHAR(COUNT(*)) FROM DBA_OBJECTS WHERE OWNER = 'BANKING';
SELECT 'OFM_PROBE|BANKING_INVALID=' || TO_CHAR(COUNT(*)) FROM DBA_OBJECTS WHERE OWNER = 'BANKING' AND STATUS <> 'VALID';
EXIT SUCCESS
'@ | Set-Content -LiteralPath $inventoryPath -Encoding utf8NoBOM

    $run = Invoke-SqlPlusScript -Path $inventoryPath
    $text = $run.Raw -join "`n"

    $state = [ordered]@{
        Users = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_USERS'
        SchemaObjects = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_SCHEMA_OBJECTS'
        ProgramObjects = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_PROGRAM_OBJECTS'
        UnexpectedObjects = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_UNEXPECTED_OBJECTS'
        Constraints = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_CONSTRAINTS'
        BadConstraints = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_BAD_CONSTRAINTS'
        CompileErrors = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_COMPILE_ERRORS'
        Customers = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_CUSTOMERS'
        Articles = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_ARTICLES'
        OrderHeads = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_ORDER_HEADS'
        OrderItems = Get-RequiredMarkerInt -Text $text -Name 'MERIDIAN_ORDER_ITEMS'
        BankingObjects = Get-RequiredMarkerInt -Text $text -Name 'BANKING_OBJECTS'
        BankingInvalid = Get-RequiredMarkerInt -Text $text -Name 'BANKING_INVALID'
    }

    $rowsEmpty = $state.Customers -eq 0 -and $state.Articles -eq 0 -and
        $state.OrderHeads -eq 0 -and $state.OrderItems -eq 0
    $seedComplete = $state.Customers -eq 5 -and $state.Articles -eq 6 -and
        $state.OrderHeads -eq 2 -and $state.OrderItems -eq 3
    $schemaComplete = $state.SchemaObjects -eq 13 -and $state.Constraints -eq 10 -and
        $state.BadConstraints -eq 0

    $checkpoint = if ($state.Users -eq 0 -and $state.SchemaObjects -eq 0 -and
        $state.ProgramObjects -eq 0 -and $state.UnexpectedObjects -eq 0) {
        'Absent'
    }
    elseif ($state.Users -eq 1 -and $state.UnexpectedObjects -eq 0 -and
        $schemaComplete -and $state.ProgramObjects -eq 0 -and $rowsEmpty) {
        'Schema'
    }
    elseif ($state.Users -eq 1 -and $state.UnexpectedObjects -eq 0 -and
        $schemaComplete -and $state.ProgramObjects -eq 0 -and $seedComplete) {
        'Seed'
    }
    elseif ($state.Users -eq 1 -and $state.UnexpectedObjects -eq 0 -and
        $schemaComplete -and $state.ProgramObjects -eq 4 -and $state.CompileErrors -eq 0 -and
        $seedComplete) {
        'Ready'
    }
    else {
        'PartialUnsupported'
    }

    return [pscustomobject]@{
        Checkpoint = $checkpoint
        State = [pscustomobject]$state
    }
}

function Invoke-InstallStage {
    param(
        [Parameter(Mandatory)][string]$ScriptName,
        [Parameter(Mandatory)][string]$ExpectedCheckpoint
    )

    Write-Host "OFM_INSTALL|SCRIPT=$ScriptName|STATE=STARTED"
    $scriptRun = Invoke-SqlPlusScript -Path (Join-Path $scriptRoot "initdb/$ScriptName")
    $scriptRun.Evidence | ForEach-Object { Write-Host $_ }
    if (($scriptRun.Raw -join "`n") -match '(?m)^(?:ORA|PLS|SP2)-\d+') {
        throw "Oracle reported an error while executing $ScriptName."
    }

    $checkpoint = Get-MeridianCheckpoint
    if ($checkpoint.Checkpoint -ne $ExpectedCheckpoint) {
        throw "After $ScriptName, MERIDIAN checkpoint is '$($checkpoint.Checkpoint)', not '$ExpectedCheckpoint'."
    }
    Write-Host "OFM_INSTALL|SCRIPT=$ScriptName|STATE=PASSED"
    return $checkpoint
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    $account = Invoke-AzJson -Arguments @('account', 'show')
    if ($account.id -ne $SubscriptionId) {
        throw "Azure CLI is using subscription '$($account.id)', not authorized subscription '$SubscriptionId'."
    }

    $app = Invoke-AzJson -Arguments @(
        'containerapp', 'show',
        '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroupName,
        '--name', $ContainerAppName)

    $container = @($app.properties.template.containers) | Where-Object name -EQ 'oracle-free'
    if ($container.Count -ne 1) {
        throw "Expected exactly one container named 'oracle-free'; found $($container.Count)."
    }
    if ($container[0].image -ne $ExpectedImage) {
        throw "Container image '$($container[0].image)' does not match authorized image '$ExpectedImage'."
    }
    if ($container[0].resources.memory -ne '4Gi') {
        throw "Container memory '$($container[0].resources.memory)' does not match the authorized 4Gi profile."
    }
    if ($app.properties.configuration.ingress.external -ne $false -or
        $app.properties.configuration.ingress.targetPort -ne 1521 -or
        $app.properties.configuration.ingress.exposedPort -ne 1521) {
        throw 'Container App ingress is not the authorized internal TCP 1521 configuration.'
    }
    if ($app.properties.runningStatus -ne 'Running' -or $app.properties.provisioningState -ne 'Succeeded') {
        throw "Container App is not ready: provisioning=$($app.properties.provisioningState), running=$($app.properties.runningStatus)."
    }

    Write-Output "OFM_INVENTORY|SUBSCRIPTION=$SubscriptionId"
    Write-Output "OFM_INVENTORY|RESOURCE_GROUP=$ResourceGroupName"
    Write-Output "OFM_INVENTORY|CONTAINER_APP=$ContainerAppName"
    Write-Output "OFM_INVENTORY|IMAGE=$($container[0].image)"
    Write-Output "OFM_INVENTORY|INGRESS=internal-tcp-1521"
    Write-Output "OFM_INVENTORY|REVISION=$($app.properties.latestReadyRevisionName)"

    $checkpoint = Get-MeridianCheckpoint
    Write-Output "OFM_PROBE|MERIDIAN_USERS=$($checkpoint.State.Users)"
    Write-Output "OFM_PROBE|MERIDIAN_SCHEMA_OBJECTS=$($checkpoint.State.SchemaObjects)"
    Write-Output "OFM_PROBE|MERIDIAN_PROGRAM_OBJECTS=$($checkpoint.State.ProgramObjects)"
    Write-Output "OFM_PROBE|MERIDIAN_UNEXPECTED_OBJECTS=$($checkpoint.State.UnexpectedObjects)"
    Write-Output "OFM_PROBE|MERIDIAN_CONSTRAINTS=$($checkpoint.State.Constraints)"
    Write-Output "OFM_PROBE|MERIDIAN_BAD_CONSTRAINTS=$($checkpoint.State.BadConstraints)"
    Write-Output "OFM_PROBE|MERIDIAN_COMPILE_ERRORS=$($checkpoint.State.CompileErrors)"
    Write-Output "OFM_PROBE|MERIDIAN_ROWS=$($checkpoint.State.Customers),$($checkpoint.State.Articles),$($checkpoint.State.OrderHeads),$($checkpoint.State.OrderItems)"
    Write-Output "OFM_PROBE|BANKING_OBJECTS=$($checkpoint.State.BankingObjects)"
    Write-Output "OFM_PROBE|BANKING_INVALID=$($checkpoint.State.BankingInvalid)"
    Write-Output "OFM_PROBE|MERIDIAN_CHECKPOINT=$($checkpoint.Checkpoint)"

    if ($Operation -eq 'Probe') {
        return
    }

    if ($checkpoint.Checkpoint -eq 'PartialUnsupported') {
        throw 'MERIDIAN is partial or owns objects outside the exact fixture contract. Install refuses to drop, reset, or modify it.'
    }

    $bankingObjectsBefore = $checkpoint.State.BankingObjects
    $bankingInvalidBefore = $checkpoint.State.BankingInvalid
    if ($checkpoint.Checkpoint -eq 'Absent') {
        $checkpoint = Invoke-InstallStage -ScriptName '001_meridian_schema.sql' -ExpectedCheckpoint 'Schema'
    }
    if ($checkpoint.Checkpoint -eq 'Schema') {
        $checkpoint = Invoke-InstallStage -ScriptName '002_meridian_seed.sql' -ExpectedCheckpoint 'Seed'
    }
    if ($checkpoint.Checkpoint -eq 'Seed') {
        $checkpoint = Invoke-InstallStage -ScriptName '003_meridian_plsql.sql' -ExpectedCheckpoint 'Ready'
    }
    if ($checkpoint.Checkpoint -ne 'Ready') {
        throw "MERIDIAN installation did not reach the Ready checkpoint; current checkpoint is '$($checkpoint.Checkpoint)'."
    }

    $compilePath = Join-Path $temporaryRoot 'compile-errors.sql'
    @'
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT 'OFM_INSTALL|COMPILE_ERROR|' || TYPE || '|' || NAME || '|' || TO_CHAR(LINE) || '|' || TO_CHAR(POSITION) || '|' || REPLACE(TEXT, CHR(10), ' ')
  FROM DBA_ERRORS
 WHERE OWNER = 'MERIDIAN'
 ORDER BY TYPE, NAME, SEQUENCE;
SELECT 'OFM_INSTALL|COMPILE_ERROR_ROWS=' || TO_CHAR(COUNT(*)) FROM DBA_ERRORS WHERE OWNER = 'MERIDIAN';
EXIT SUCCESS
'@ | Set-Content -LiteralPath $compilePath -Encoding utf8NoBOM
    $compileRun = Invoke-SqlPlusScript -Path $compilePath
    $compileRun.Evidence | ForEach-Object { Write-Output $_ }
    if (($compileRun.Raw -join "`n") -notmatch 'OFM_INSTALL\|COMPILE_ERROR_ROWS=0') {
        throw 'MERIDIAN has Oracle compilation errors.'
    }

    Write-Output 'OFM_INSTALL|SCRIPT=004_meridian_verify.sql|STATE=STARTED'
    $verifyRun = Invoke-SqlPlusScript -Path (Join-Path $scriptRoot 'initdb/004_meridian_verify.sql')
    $verifyRun.Evidence | ForEach-Object { Write-Output $_ }
    $verifyText = $verifyRun.Raw -join "`n"
    if ($verifyText -notmatch '(?m)^MERIDIAN ORDER LAB: SEED OK\s*$' -or
        $verifyText -match '(?m)^MERIDIAN ORDER LAB: FAIL') {
        throw 'Meridian verifier did not report a clean seed result.'
    }
    Write-Output 'OFM_INSTALL|SCRIPT=004_meridian_verify.sql|STATE=PASSED'

    $post = Get-MeridianCheckpoint
    if ($post.Checkpoint -ne 'Ready' -or
        $post.State.BankingObjects -ne $bankingObjectsBefore -or
        $post.State.BankingInvalid -ne $bankingInvalidBefore) {
        throw 'Post-install inventory did not preserve the required Meridian and banking invariants.'
    }

    Write-Output 'OFM_INSTALL|STATE=PASSED'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}