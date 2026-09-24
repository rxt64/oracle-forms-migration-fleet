-- Meridian Order Entry source lab: schema owner, tables, sequences, indexes.
--
-- This installs a SECOND, independent estate alongside the existing synthetic banking estate. It
-- creates its own schema owner, MERIDIAN, and touches no BANKING object. The banking init scripts
-- in infra/legacy-estate/oracle/initdb are not modified, moved, or re-ordered by this lab.
--
-- The four CREATE TABLE statements below are reproduced verbatim from
-- ../source/db/schema.sql, which is byte-identical to the Meridian mapping fixture this
-- repository's tests already declare. Keeping one text means the lab and the fixture cannot drift
-- into two different estates. The check that enforces it lives in
-- tests/oracle-forms-migration-fleet.Tests/MeridianSourceLabFixtureTests.cs.
--
-- MERIDIAN is a schema-only account: NO AUTHENTICATION means nothing can log in as it, so this
-- script introduces no credential and no secret. SYS creates the objects into it via CURRENT_SCHEMA.
-- Its storage is bounded rather than UNLIMITED TABLESPACE: this lab holds four small tables and a
-- few dozen seeded rows, so a quota on the pre-existing USERS tablespace is the least privilege
-- that still installs, and it cannot fill a shared tablespace if a run goes wrong.

WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;

SET SERVEROUTPUT ON
SET DEFINE OFF

-- USERS is the default permanent tablespace an Oracle Free FREEPDB1 ships with. Fail here, loudly,
-- rather than later with a quota error that reads like a defect in the lab's own DDL.
DECLARE
    v_users PLS_INTEGER;
BEGIN
    SELECT COUNT(*)
      INTO v_users
      FROM DBA_TABLESPACES
     WHERE TABLESPACE_NAME = 'USERS'
       AND CONTENTS = 'PERMANENT';

    IF v_users = 0 THEN
        RAISE_APPLICATION_ERROR(
            -20001,
            'MERIDIAN ORDER LAB: no permanent tablespace named USERS in this PDB. '
            || 'Point the CREATE USER below at the PDB''s default permanent tablespace instead.');
    END IF;
END;
/

CREATE USER MERIDIAN NO AUTHENTICATION
    DEFAULT TABLESPACE USERS
    QUOTA 64M ON USERS;

ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;

-- BEGIN source/db/schema.sql
CREATE TABLE MRD_CUSTOMER (
  CUST_NO      NUMBER(8)     NOT NULL,
  CUST_NAME    VARCHAR2(80)  NOT NULL,
  CUST_STATE   CHAR(1)       NOT NULL,
  CUST_MEMO    VARCHAR2(200),
  CONSTRAINT PK_MRD_CUSTOMER PRIMARY KEY (CUST_NO),
  CONSTRAINT CK_MRD_CUST_STATE CHECK (CUST_STATE IN ('A','I'))
);

CREATE TABLE MRD_ARTICLE (
  ART_NO       NUMBER(8)     NOT NULL,
  ART_DESC     VARCHAR2(80)  NOT NULL,
  ART_PRICE    NUMBER(11,2)  NOT NULL,
  ART_ON_HAND  NUMBER(9)     NOT NULL,
  ART_STATE    CHAR(1)       NOT NULL,
  ART_REV      NUMBER(12)    NOT NULL,
  CONSTRAINT PK_MRD_ARTICLE PRIMARY KEY (ART_NO),
  CONSTRAINT CK_MRD_ART_STATE CHECK (ART_STATE IN ('A','I'))
);

CREATE TABLE MRD_ORDER_HEAD (
  ORD_NO       NUMBER(10)    NOT NULL,
  ORD_CUST     NUMBER(8)     NOT NULL,
  ORD_VALUE    NUMBER(14,2)  NOT NULL,
  ORD_RAISED   DATE          NOT NULL,
  ORD_STATE    VARCHAR2(10)  NOT NULL,
  ORD_REV      NUMBER(12)    NOT NULL,
  CONSTRAINT PK_MRD_ORDER_HEAD PRIMARY KEY (ORD_NO),
  CONSTRAINT FK_MRD_ORDER_CUST FOREIGN KEY (ORD_CUST) REFERENCES MRD_CUSTOMER (CUST_NO),
  CONSTRAINT CK_MRD_ORD_STATE CHECK (ORD_STATE IN ('ENTERED','SHIPPED'))
);

CREATE TABLE MRD_ORDER_ITEM (
  ITM_NO       NUMBER(12)    NOT NULL,
  ITM_ORD      NUMBER(10)    NOT NULL,
  ITM_ART      NUMBER(8)     NOT NULL,
  ITM_QTY      NUMBER(9)     NOT NULL,
  ITM_PRICE    NUMBER(11,2)  NOT NULL,
  ITM_VALUE    NUMBER(14,2)  NOT NULL,
  CONSTRAINT PK_MRD_ORDER_ITEM PRIMARY KEY (ITM_NO),
  CONSTRAINT FK_MRD_ITEM_ORD FOREIGN KEY (ITM_ORD) REFERENCES MRD_ORDER_HEAD (ORD_NO),
  CONSTRAINT FK_MRD_ITEM_ART FOREIGN KEY (ITM_ART) REFERENCES MRD_ARTICLE (ART_NO)
);
-- END source/db/schema.sql

CREATE SEQUENCE MRD_ORDER_SEQ START WITH 9003 INCREMENT BY 1 NOCACHE ORDER NOCYCLE;

CREATE SEQUENCE MRD_ORDER_ITEM_SEQ START WITH 7004 INCREMENT BY 1 NOCACHE ORDER NOCYCLE;

CREATE INDEX IX_MRD_ORDER_HEAD_CUST ON MRD_ORDER_HEAD (ORD_CUST);

CREATE INDEX IX_MRD_ORDER_ITEM_ORD ON MRD_ORDER_ITEM (ITM_ORD);

CREATE INDEX IX_MRD_ORDER_ITEM_ART ON MRD_ORDER_ITEM (ITM_ART);
