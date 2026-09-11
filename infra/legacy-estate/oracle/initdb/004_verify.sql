-- Fails container initialization unless the full synthetic estate is ready.

WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = BANKING;

SET SERVEROUTPUT ON
SET FEEDBACK OFF

DECLARE
    v_requests       PLS_INTEGER;
    v_accounts       PLS_INTEGER;
    v_staff_users    PLS_INTEGER;
    v_transactions   PLS_INTEGER;
    v_invalid        PLS_INTEGER;
    v_balance        NUMBER;
    v_customer_login BOOLEAN;
    v_manager_login  BOOLEAN;
BEGIN
    SELECT COUNT(*) INTO v_requests FROM BANK_ACCOUNT_REQUEST;
    SELECT COUNT(*) INTO v_accounts FROM BANK_ACCOUNT;
    SELECT COUNT(*) INTO v_staff_users FROM BANK_STAFF_USER;
    SELECT COUNT(*) INTO v_transactions FROM BANK_TRANSACTION;

    SELECT COUNT(*)
      INTO v_invalid
      FROM DBA_OBJECTS
     WHERE OWNER = 'BANKING' AND STATUS <> 'VALID';

    v_balance := LEGACY_BANKING_API.CURRENT_BALANCE(500001);
    v_customer_login := LEGACY_BANKING_API.VALIDATE_CUSTOMER_LOGIN(500001, 'demo1234');
    v_manager_login := LEGACY_BANKING_API.VALIDATE_MANAGER_LOGIN('branch.manager', 'manager-demo-1');

    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: account requests    = ' || v_requests);
    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: registered accounts = ' || v_accounts);
    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: staff users         = ' || v_staff_users);
    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: transactions        = ' || v_transactions);
    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: invalid objects     = ' || v_invalid);
    DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: balance of 500001   = ' || v_balance);

    IF v_requests = 8
       AND v_accounts = 5
       AND v_staff_users = 2
       AND v_transactions = 12
       AND v_invalid = 0
       AND v_balance = 3300
       AND v_customer_login
       AND v_manager_login THEN
        DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: SEED OK');
    ELSE
        DBMS_OUTPUT.PUT_LINE('LEGACY ESTATE: SEED INCOMPLETE');
        RAISE_APPLICATION_ERROR(-20001, 'Legacy estate verification failed.');
    END IF;
END;
/