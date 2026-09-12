// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Representative excerpts of the demo legacy estate's Oracle scripts, embedded as constants so the tests
/// stay self-contained instead of reading across project boundaries into infra/.
/// </summary>
internal static class OracleSamples
{
    public const string Schema = """
        -- Independently designed schema for the demo legacy estate.

        WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

        ALTER SESSION SET CURRENT_SCHEMA = BANKING;

        SET DEFINE OFF

        CREATE TABLE BANK_ACCOUNT_REQUEST
        (
            REQUEST_ID      NUMBER(10) PRIMARY KEY,
            BRANCH_CODE     VARCHAR2(12) NOT NULL,
            ACCOUNT_KIND    VARCHAR2(12) NOT NULL,
            GIVEN_NAME      VARCHAR2(40) NOT NULL,
            DATE_OF_BIRTH   DATE NOT NULL,
            REQUEST_STATUS  VARCHAR2(12) DEFAULT 'SUBMITTED' NOT NULL,
            SUBMITTED_AT    TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL,
            DECIDED_AT      TIMESTAMP,
            CONSTRAINT BANK_REQ_KIND_CK CHECK (ACCOUNT_KIND IN ('SAVINGS', 'CHECKING')),
            CONSTRAINT BANK_REQ_STATUS_CK CHECK (REQUEST_STATUS IN ('SUBMITTED', 'APPROVED', 'REJECTED'))
        );

        create table BANK_ACCOUNT
        (
            ACCOUNT_ID            NUMBER(10) PRIMARY KEY,
            REQUEST_ID            NUMBER(10) NOT NULL,
            OPENED_ON             DATE DEFAULT SYSDATE NOT NULL,
            ONLINE_ENABLED        CHAR(1) DEFAULT 'N' NOT NULL,
            ONLINE_PASSWORD_HASH  RAW(32),
            CONSTRAINT BANK_ACCT_REQUEST_UQ UNIQUE (REQUEST_ID),
            CONSTRAINT BANK_ACCT_REQUEST_FK FOREIGN KEY (REQUEST_ID)
                REFERENCES BANK_ACCOUNT_REQUEST (REQUEST_ID),
            CONSTRAINT BANK_ACCT_ONLINE_CK CHECK (ONLINE_ENABLED IN ('Y', 'N'))
        );

        CREATE TABLE BANK_TRANSACTION
        (
            TRANSACTION_ID  NUMBER(10) PRIMARY KEY,
            ACCOUNT_ID      NUMBER(10) NOT NULL,
            TRANSACTION_TS  TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL,
            AMOUNT          NUMBER(12, 2) NOT NULL,
            REFERENCE_CODE  VARCHAR2(30) NOT NULL,
            DIRECTION_CODE  CHAR(2) NOT NULL,
            CONSTRAINT BANK_TXN_ACCOUNT_FK FOREIGN KEY (ACCOUNT_ID)
                REFERENCES BANK_ACCOUNT (ACCOUNT_ID),
            CONSTRAINT BANK_TXN_AMOUNT_CK CHECK (AMOUNT > 0)
        );

        CREATE SEQUENCE BANK_REQUEST_SEQ START WITH 1001 INCREMENT BY 1 NOCACHE;
        CREATE SEQUENCE BANK_ACCOUNT_SEQ START WITH 500001 INCREMENT BY 1 NOCACHE;

        CREATE INDEX BANK_TXN_ACCOUNT_IX ON BANK_TRANSACTION (ACCOUNT_ID, TRANSACTION_TS);
        """;

    public const string PlSql = """
        CREATE OR REPLACE PACKAGE BODY LEGACY_BANKING_API AS
            FUNCTION VALIDATE_CUSTOMER_LOGIN(
                p_account_id IN NUMBER,
                p_credential IN VARCHAR2
            ) RETURN BOOLEAN IS
                v_count PLS_INTEGER;
            BEGIN
                SELECT COUNT(*)
                  INTO v_count
                  FROM BANK_ACCOUNT
                 WHERE ACCOUNT_ID = p_account_id
                   AND ONLINE_PASSWORD_HASH = STANDARD_HASH(p_credential, 'SHA256');

                RETURN v_count = 1;
            END VALIDATE_CUSTOMER_LOGIN;

            PROCEDURE APPROVE_REQUEST(p_request_id IN NUMBER) IS
                v_request BANK_ACCOUNT_REQUEST%ROWTYPE;
                v_now     DATE;
            BEGIN
                SELECT SYSDATE INTO v_now FROM DUAL;

                SELECT *
                  INTO v_request
                  FROM BANK_ACCOUNT_REQUEST
                 WHERE REQUEST_ID = p_request_id
                   AND ROWNUM = 1;
            END APPROVE_REQUEST;
        END LEGACY_BANKING_API;
        /
        """;
}
