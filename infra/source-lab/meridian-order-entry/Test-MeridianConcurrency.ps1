[CmdletBinding()]
param(
    [string]$SubscriptionId = 'd4394e57-c076-4c92-a870-5de6bf44f255',

    [string]$ResourceGroupName = 'rg-oracle-forms-migration-fleet-dev-b9f0e875',

    [string]$ContainerAppName = 'ca-ofmfleet-db-dev-ykbpnrpd',

    [ValidateRange(3, 20)]
    [int]$BlockProofSeconds = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$authorizedSubscription = 'd4394e57-c076-4c92-a870-5de6bf44f255'
$authorizedResourceGroup = 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
$authorizedContainerApp = 'ca-ofmfleet-db-dev-ykbpnrpd'
if ($SubscriptionId -ne $authorizedSubscription -or
    $ResourceGroupName -ne $authorizedResourceGroup -or
    $ContainerAppName -ne $authorizedContainerApp) {
    throw 'Concurrency verification is restricted to the authorized disposable Meridian source lab.'
}

$invokeSqlPlus = Join-Path $PSScriptRoot 'Invoke-MeridianSqlPlus.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'ofm-meridian-concurrency-' + [guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot 'run-meridian-concurrency.sh'
$transcriptPath = Join-Path $temporaryRoot 'container-exec.log'

function Assert-Marker {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Marker
    )

    if ($Text -notmatch ('(?m)^' + [regex]::Escape($Marker) + '\s*$')) {
        throw "Required concurrency marker was not observed: $Marker"
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    foreach ($relativePath in @('source/db/schema.sql', 'source/db/seed.sql', 'source/db/package.sql')) {
        $sourcePath = Join-Path $PSScriptRoot $relativePath
        $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Output "OFM_CONCURRENCY|SOURCE_SHA256|$relativePath|$hash"
    }

    $harness = @'
set -euo pipefail
umask 077

work="$(mktemp -d /tmp/ofm-meridian-concurrency.XXXXXX)"
s1_pid=''
s2_pid=''
done_pid=''

cleanup_fixture() {
  set +e
  for pid in "$s1_pid" "$s2_pid" "$done_pid"; do
    if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi
  done
  sqlplus -s / as sysdba <<'SQL' >/dev/null 2>&1
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR CONTINUE
SET FEEDBACK OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;
DELETE FROM MRD_ORDER_ITEM
 WHERE ITM_ORD IN (SELECT ORD_NO FROM MRD_ORDER_HEAD WHERE ORD_CUST = 19001);
DELETE FROM MRD_ORDER_HEAD WHERE ORD_CUST = 19001;
DELETE FROM MRD_ARTICLE WHERE ART_NO = 29001;
DELETE FROM MRD_CUSTOMER WHERE CUST_NO = 19001;
COMMIT;
EXIT SUCCESS
SQL
  rm -rf "$work"
}
trap cleanup_fixture EXIT
cd "$work"

cat > setup.sql <<'SQL'
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
SET SERVEROUTPUT ON
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;
DECLARE
  v_customers PLS_INTEGER;
  v_articles PLS_INTEGER;
  v_heads PLS_INTEGER;
  v_items PLS_INTEGER;
  v_fixture PLS_INTEGER;
BEGIN
  SELECT COUNT(*) INTO v_customers FROM MRD_CUSTOMER;
  SELECT COUNT(*) INTO v_articles FROM MRD_ARTICLE;
  SELECT COUNT(*) INTO v_heads FROM MRD_ORDER_HEAD;
  SELECT COUNT(*) INTO v_items FROM MRD_ORDER_ITEM;
  SELECT (SELECT COUNT(*) FROM MRD_CUSTOMER WHERE CUST_NO = 19001) +
         (SELECT COUNT(*) FROM MRD_ARTICLE WHERE ART_NO = 29001)
    INTO v_fixture FROM DUAL;
  IF v_customers <> 5 OR v_articles <> 6 OR v_heads <> 2 OR v_items <> 3 OR v_fixture <> 0 THEN
    RAISE_APPLICATION_ERROR(-20020, 'Meridian is not at the exact seed baseline.');
  END IF;
END;
/
INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO)
VALUES (19001, 'OFM Concurrency Fixture', 'A', 'Disposable source-lab verification row.');
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV)
VALUES (29001, 'OFM Concurrency Fixture Article', 10.00, 3, 'A', 0);
COMMIT;
PROMPT OFM_CONCURRENCY|FIXTURE|STATE=READY|STOCK=3|REV=0
EXIT SUCCESS
SQL

cat > teardown.sql <<'SQL'
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
SET SERVEROUTPUT ON
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;
DELETE FROM MRD_ORDER_ITEM
 WHERE ITM_ORD IN (SELECT ORD_NO FROM MRD_ORDER_HEAD WHERE ORD_CUST = 19001);
DELETE FROM MRD_ORDER_HEAD WHERE ORD_CUST = 19001;
DELETE FROM MRD_ARTICLE WHERE ART_NO = 29001;
DELETE FROM MRD_CUSTOMER WHERE CUST_NO = 19001;
COMMIT;
DECLARE
  v_customers PLS_INTEGER;
  v_articles PLS_INTEGER;
  v_heads PLS_INTEGER;
  v_items PLS_INTEGER;
BEGIN
  SELECT COUNT(*) INTO v_customers FROM MRD_CUSTOMER;
  SELECT COUNT(*) INTO v_articles FROM MRD_ARTICLE;
  SELECT COUNT(*) INTO v_heads FROM MRD_ORDER_HEAD;
  SELECT COUNT(*) INTO v_items FROM MRD_ORDER_ITEM;
  IF v_customers <> 5 OR v_articles <> 6 OR v_heads <> 2 OR v_items <> 3 THEN
    RAISE_APPLICATION_ERROR(-20021, 'Meridian seed restoration failed.');
  END IF;
END;
/
SELECT 'OFM_CONCURRENCY|RESTORED|SEED=5|6|2|3' FROM DUAL;
EXIT SUCCESS
SQL

cat > final.sql <<'SQL'
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
SET SERVEROUTPUT ON
ALTER SESSION SET CONTAINER = FREEPDB1;
DECLARE
  v_compile PLS_INTEGER;
  v_customers PLS_INTEGER;
  v_articles PLS_INTEGER;
  v_heads PLS_INTEGER;
  v_items PLS_INTEGER;
  v_banking PLS_INTEGER;
  v_banking_invalid PLS_INTEGER;
BEGIN
  SELECT COUNT(*) INTO v_compile FROM DBA_ERRORS WHERE OWNER = 'MERIDIAN';
  SELECT COUNT(*) INTO v_customers FROM MERIDIAN.MRD_CUSTOMER;
  SELECT COUNT(*) INTO v_articles FROM MERIDIAN.MRD_ARTICLE;
  SELECT COUNT(*) INTO v_heads FROM MERIDIAN.MRD_ORDER_HEAD;
  SELECT COUNT(*) INTO v_items FROM MERIDIAN.MRD_ORDER_ITEM;
  SELECT COUNT(*) INTO v_banking FROM DBA_OBJECTS WHERE OWNER = 'BANKING';
  SELECT COUNT(*) INTO v_banking_invalid FROM DBA_OBJECTS
   WHERE OWNER = 'BANKING' AND STATUS <> 'VALID';
  IF v_compile <> 0 OR v_customers <> 5 OR v_articles <> 6 OR v_heads <> 2 OR v_items <> 3 OR
     v_banking <> 19 OR v_banking_invalid <> 0 THEN
    RAISE_APPLICATION_ERROR(-20022, 'Final source-lab invariants failed.');
  END IF;
END;
/
SELECT 'OFM_CONCURRENCY|FINAL|MERIDIAN_COMPILE_ERRORS=' || TO_CHAR(COUNT(*))
  FROM DBA_ERRORS WHERE OWNER = 'MERIDIAN';
SELECT 'OFM_CONCURRENCY|FINAL|SEED=' ||
       (SELECT COUNT(*) FROM MERIDIAN.MRD_CUSTOMER) || '|' ||
       (SELECT COUNT(*) FROM MERIDIAN.MRD_ARTICLE) || '|' ||
       (SELECT COUNT(*) FROM MERIDIAN.MRD_ORDER_HEAD) || '|' ||
       (SELECT COUNT(*) FROM MERIDIAN.MRD_ORDER_ITEM)
  FROM DUAL;
SELECT 'OFM_CONCURRENCY|FINAL|BANKING=' || COUNT(*) || '|' ||
       SUM(CASE WHEN STATUS <> 'VALID' THEN 1 ELSE 0 END)
  FROM DBA_OBJECTS WHERE OWNER = 'BANKING';
EXIT SUCCESS
SQL

run_scenario() {
  disposition="$1"
  expected_code="$2"
  label="$3"
  lower="$(printf '%s' "$label" | tr '[:upper:]' '[:lower:]')"

  sqlplus -s / as sysdba @setup.sql >"$lower-setup.log" 2>&1
  cat "$lower-setup.log"
  mkfifo "$lower-locked" "$lower-release" "$lower-started" "$lower-done"

  cat >"$lower-s1.sql" <<SQL
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET FEEDBACK OFF
SET VERIFY OFF
SET ECHO OFF
SET SERVEROUTPUT ON
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;
VARIABLE order_no NUMBER
BEGIN
  MRD_ORDER_ENTRY_API.CREATE_ORDER(19001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(29001, 2)), :order_no);
END;
/
HOST printf 'locked\n' > $lower-locked
HOST timeout 60s cat $lower-release > /dev/null
$disposition;
PROMPT OFM_CONCURRENCY|$label|S1|RELEASED=$disposition
EXIT SUCCESS
SQL

  cat >"$lower-s2.sql" <<SQL
WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
SET FEEDBACK OFF
SET HEADING OFF
SET PAGESIZE 0
SET VERIFY OFF
SET ECHO OFF
SET SERVEROUTPUT ON
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;
HOST printf 'started\n' > $lower-started
VARIABLE result_code NUMBER
VARIABLE wait_cs NUMBER
VARIABLE observed_stock NUMBER
VARIABLE observed_rev NUMBER
DECLARE
  v_started PLS_INTEGER := DBMS_UTILITY.GET_TIME;
  v_wait PLS_INTEGER;
  v_order_no NUMBER;
  v_code PLS_INTEGER := 0;
  v_stock NUMBER;
  v_rev NUMBER;
BEGIN
  BEGIN
    MRD_ORDER_ENTRY_API.CREATE_ORDER(19001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(29001, 2)), v_order_no);
  EXCEPTION WHEN OTHERS THEN v_code := SQLCODE;
  END;
  v_wait := DBMS_UTILITY.GET_TIME - v_started;
  SELECT ART_ON_HAND, ART_REV INTO v_stock, v_rev FROM MRD_ARTICLE WHERE ART_NO = 29001;
  :result_code := v_code;
  :wait_cs := v_wait;
  :observed_stock := v_stock;
  :observed_rev := v_rev;
  IF v_code <> $expected_code OR v_wait < (__BLOCK_PROOF_SECONDS__ * 100) THEN
    RAISE_APPLICATION_ERROR(-20023, 'Unexpected S2 result or blocking interval.');
  END IF;
END;
/
SELECT 'OFM_CONCURRENCY|$label|S2|CODE=' || TO_CHAR(:result_code) FROM DUAL;
SELECT 'OFM_CONCURRENCY|$label|S2|WAIT_CENTISECONDS=' || TO_CHAR(:wait_cs) FROM DUAL;
SELECT 'OFM_CONCURRENCY|$label|S2|STATE=' || TO_CHAR(:observed_stock) || '|' || TO_CHAR(:observed_rev) FROM DUAL;
HOST printf 'done\n' > $lower-done
ROLLBACK;
EXIT SUCCESS
SQL

  sqlplus -s / as sysdba @"$lower-s1.sql" >"$lower-s1.log" 2>&1 &
  s1_pid=$!
  timeout 30s cat "$lower-locked" >/dev/null
  sqlplus -s / as sysdba @"$lower-s2.sql" >"$lower-s2.log" 2>&1 &
  s2_pid=$!
  timeout 30s cat "$lower-started" >/dev/null

  set +e
  timeout __BLOCK_PROOF_SECONDS__s cat "$lower-done" >/dev/null
  early_status=$?
  set -e
  if [ "$early_status" -ne 124 ]; then
    echo "OFM_CONCURRENCY|$label|S2|BLOCKED=FALSE"
    exit 24
  fi
  echo "OFM_CONCURRENCY|$label|S2|BLOCKED=TRUE"

  cat "$lower-done" >/dev/null &
  done_pid=$!
  printf 'release\n' >"$lower-release"
  wait "$s1_pid"
  wait "$s2_pid"
  wait "$done_pid"
  s1_pid=''
  s2_pid=''
  done_pid=''

  cat "$lower-s1.log"
  cat "$lower-s2.log"
  grep -Fq "OFM_CONCURRENCY|$label|S1|RELEASED=$disposition" "$lower-s1.log"
  grep -Fq "OFM_CONCURRENCY|$label|S2|CODE=$expected_code" "$lower-s2.log"

  sqlplus -s / as sysdba @teardown.sql >"$lower-teardown.log" 2>&1
  cat "$lower-teardown.log"
  rm -f "$lower-locked" "$lower-release" "$lower-started" "$lower-done"
}

run_scenario COMMIT -20105 COMMIT
run_scenario ROLLBACK 0 ROLLBACK
sqlplus -s / as sysdba @final.sql
echo 'OFM_CONCURRENCY|STATE=PASSED'
exit 0
'@
    $harness = $harness.Replace('__BLOCK_PROOF_SECONDS__', [string]$BlockProofSeconds)
    Set-Content -LiteralPath $harnessPath -Value $harness -Encoding utf8NoBOM

    try {
      $output = (& $invokeSqlPlus `
        -SubscriptionId $SubscriptionId `
        -ResourceGroupName $ResourceGroupName `
        -ContainerAppName $ContainerAppName `
        -SqlPath $harnessPath `
        -InputMode Raw `
            -ExecCommand 'timeout 180s bash -s' `
        -TranscriptPath $transcriptPath `
        -TimeoutMinutes 4) -join "`n"
    }
    catch {
      if (Test-Path -LiteralPath $transcriptPath) {
        Write-Output (Get-Content -LiteralPath $transcriptPath -Raw)
      }
      throw
    }

    Write-Output $output
    if ($output -match '(?m)^(?:ORA|PLS|SP2)-\d+' -or
      $output -match '(?m)^OFM_CONCURRENCY\|(?:COMMIT|ROLLBACK)\|S2\|BLOCKED=FALSE\s*$') {
        throw 'The in-container concurrency harness reported a database or blocking failure.'
    }

    foreach ($marker in @(
        'OFM_CONCURRENCY|COMMIT|S2|BLOCKED=TRUE',
        'OFM_CONCURRENCY|COMMIT|S2|CODE=-20105',
        'OFM_CONCURRENCY|COMMIT|S2|STATE=1|1',
        'OFM_CONCURRENCY|ROLLBACK|S2|BLOCKED=TRUE',
        'OFM_CONCURRENCY|ROLLBACK|S2|CODE=0',
        'OFM_CONCURRENCY|ROLLBACK|S2|STATE=1|1',
        'OFM_CONCURRENCY|FINAL|MERIDIAN_COMPILE_ERRORS=0',
        'OFM_CONCURRENCY|FINAL|SEED=5|6|2|3',
        'OFM_CONCURRENCY|FINAL|BANKING=19|0',
        'OFM_CONCURRENCY|STATE=PASSED')) {
        Assert-Marker -Text $output -Marker $marker
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
