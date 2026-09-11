-- Independently implemented PL/SQL for the synthetic demo estate.
--
-- The package covers the public case-study workflows but is not a transcription or reverse
-- engineering of the logic embedded in the unlicensed Forms modules and PDF. Do not cite it as
-- upstream or customer code.

WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = BANKING;

SET DEFINE OFF

CREATE OR REPLACE PACKAGE LEGACY_BANKING_API AS
    invalid_credentials EXCEPTION;
    request_not_found   EXCEPTION;
    already_decided     EXCEPTION;

    FUNCTION VALIDATE_CUSTOMER_LOGIN(
        p_account_id IN NUMBER,
        p_password IN VARCHAR2
    ) RETURN BOOLEAN;

    FUNCTION VALIDATE_MANAGER_LOGIN(
        p_username IN VARCHAR2,
        p_password IN VARCHAR2
    ) RETURN BOOLEAN;

    FUNCTION CALCULATE_SIMPLE_INTEREST(
        p_principal IN NUMBER,
        p_rate IN NUMBER,
        p_years IN NUMBER
    ) RETURN NUMBER;

    FUNCTION CURRENT_BALANCE(p_account_id IN NUMBER) RETURN NUMBER;

    PROCEDURE APPROVE_REQUEST(
        p_request_id IN NUMBER,
        p_account_id OUT NUMBER
    );
END LEGACY_BANKING_API;
/

CREATE OR REPLACE PACKAGE BODY LEGACY_BANKING_API AS
    FUNCTION VALIDATE_CUSTOMER_LOGIN(
        p_account_id IN NUMBER,
        p_password IN VARCHAR2
    ) RETURN BOOLEAN IS
        v_count PLS_INTEGER;
    BEGIN
        SELECT COUNT(*)
          INTO v_count
          FROM BANK_ACCOUNT
         WHERE ACCOUNT_ID = p_account_id
           AND ONLINE_ENABLED = 'Y'
           AND ONLINE_PASSWORD_HASH = STANDARD_HASH(p_password, 'SHA256');

        RETURN v_count = 1;
    END VALIDATE_CUSTOMER_LOGIN;

    FUNCTION VALIDATE_MANAGER_LOGIN(
        p_username IN VARCHAR2,
        p_password IN VARCHAR2
    ) RETURN BOOLEAN IS
        v_count PLS_INTEGER;
    BEGIN
        SELECT COUNT(*)
          INTO v_count
          FROM BANK_STAFF_USER
         WHERE USERNAME = LOWER(TRIM(p_username))
           AND ROLE_CODE = 'MANAGER'
           AND ACTIVE_FLAG = 'Y'
           AND PASSWORD_HASH = STANDARD_HASH(p_password, 'SHA256');

        RETURN v_count = 1;
    END VALIDATE_MANAGER_LOGIN;

    FUNCTION CALCULATE_SIMPLE_INTEREST(
        p_principal IN NUMBER,
        p_rate IN NUMBER,
        p_years IN NUMBER
    ) RETURN NUMBER IS
    BEGIN
        IF p_principal IS NULL OR p_rate IS NULL OR p_years IS NULL THEN
            RETURN NULL;
        END IF;

        RETURN ROUND((p_principal * p_rate * p_years) / 100, 2);
    END CALCULATE_SIMPLE_INTEREST;

    FUNCTION CURRENT_BALANCE(p_account_id IN NUMBER) RETURN NUMBER IS
        v_balance NUMBER;
    BEGIN
        SELECT NVL(SUM(CASE DIRECTION_CODE WHEN 'CR' THEN AMOUNT WHEN 'DR' THEN -AMOUNT END), 0)
          INTO v_balance
          FROM BANK_TRANSACTION
         WHERE ACCOUNT_ID = p_account_id;

        RETURN v_balance;
    END CURRENT_BALANCE;

    PROCEDURE APPROVE_REQUEST(
        p_request_id IN NUMBER,
        p_account_id OUT NUMBER
    ) IS
        v_request BANK_ACCOUNT_REQUEST%ROWTYPE;
    BEGIN
        BEGIN
            SELECT *
              INTO v_request
              FROM BANK_ACCOUNT_REQUEST
             WHERE REQUEST_ID = p_request_id
               FOR UPDATE;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                RAISE request_not_found;
        END;

        IF v_request.REQUEST_STATUS <> 'SUBMITTED' THEN
            RAISE already_decided;
        END IF;

        p_account_id := BANK_ACCOUNT_SEQ.NEXTVAL;

        INSERT INTO BANK_ACCOUNT
            (ACCOUNT_ID, REQUEST_ID, BRANCH_CODE, ACCOUNT_KIND, OPENED_ON,
             ONLINE_ENABLED, ONLINE_PASSWORD_HASH)
        VALUES
            (p_account_id, v_request.REQUEST_ID, v_request.BRANCH_CODE, v_request.ACCOUNT_KIND,
             SYSDATE, 'N', NULL);

        UPDATE BANK_ACCOUNT_REQUEST
           SET REQUEST_STATUS = 'APPROVED',
               DECIDED_AT = SYSTIMESTAMP
         WHERE REQUEST_ID = p_request_id;
    END APPROVE_REQUEST;
END LEGACY_BANKING_API;
/

CREATE OR REPLACE TRIGGER BANK_TRANSACTION_BI
    BEFORE INSERT ON BANK_TRANSACTION
    FOR EACH ROW
BEGIN
    IF :NEW.TRANSACTION_ID IS NULL THEN
        :NEW.TRANSACTION_ID := BANK_TRANSACTION_SEQ.NEXTVAL;
    END IF;

    IF :NEW.TRANSACTION_TS IS NULL THEN
        :NEW.TRANSACTION_TS := SYSTIMESTAMP;
    END IF;
END;
/

CREATE OR REPLACE TRIGGER BANK_ACCOUNT_REQUEST_BI
    BEFORE INSERT ON BANK_ACCOUNT_REQUEST
    FOR EACH ROW
BEGIN
    IF :NEW.REQUEST_ID IS NULL THEN
        :NEW.REQUEST_ID := BANK_REQUEST_SEQ.NEXTVAL;
    END IF;
END;
/

CREATE OR REPLACE VIEW BANK_ACCOUNT_STATEMENT AS
SELECT a.ACCOUNT_ID,
       r.GIVEN_NAME || ' ' || r.FAMILY_NAME AS ACCOUNT_HOLDER,
       a.BRANCH_CODE,
       t.TRANSACTION_ID,
       t.TRANSACTION_TS,
       t.DIRECTION_CODE,
       t.AMOUNT,
       t.REFERENCE_CODE
  FROM BANK_ACCOUNT a
  JOIN BANK_ACCOUNT_REQUEST r
    ON r.REQUEST_ID = a.REQUEST_ID
  JOIN BANK_TRANSACTION t
    ON t.ACCOUNT_ID = a.ACCOUNT_ID;