// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Representative excerpts of the demo legacy estate's Oracle scripts, embedded as constants so the tests
/// stay self-contained instead of reading across project boundaries into infra/.
/// </summary>
internal static class OracleSamples
{
    /// <summary>A Forms export in the shape frmf2xml produces, declaring whichever release a test needs.</summary>
    public static string FormsXml(string? version, string moduleName = "ORDER_ENTRY") =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <Module xmlns="http://xmlns.oracle.com/Forms"{(version is null ? string.Empty : $" version=\"{version}\" FormsVersion=\"{version}\"")}>
           <FormModule Name="{moduleName}" Title="Order entry">
             <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN NULL; END;"/>
             <Block Name="ORDER_BLOCK" QueryDataSourceName="BANK_ACCOUNT" RecordsDisplayCount="10">
               <Item Name="ACCOUNT_ID" ItemType="Text Item" DataType="Number" ColumnName="ACCOUNT_ID" Prompt="Account" Required="true"/>
             </Block>
           </FormModule>
         </Module>
         """;

    /// <summary>
    /// An independently authored master/detail export with a lookup, written for these tests rather than
    /// taken from any Oracle sample application. It carries the constructs the interpreting parser does not
    /// read — radio buttons, a relation join condition, a program unit body, a record group query, a
    /// formula, canvas text, a menu and library reference, and a site-specific property — so an omission in
    /// what is retained can be detected rather than assumed.
    ///
    /// <paramref name="wrapperVersion"/> defaults to the internal build number an Oracle export writes on
    /// the Module wrapper. The release catalog does not interpret that form, so a test that has to reach
    /// the release-adjudicating phases supplies a release instead.
    /// </summary>
    public static string MasterDetailExport(string wrapperVersion = "122010400") =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="{wrapperVersion}" FormsVersion="12.2.1.4">
          <FormModule Name="WAREHOUSE_PICKING" Title="Warehouse picking" MenuModule="WAREHOUSE_MENU" FirstNavigationBlock="PICK_HEADER" ContosoAuditFlag="Y">
            <AttachedLibrary Name="WAREHOUSE_COMMON" LibrarySource="File"/>
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN :GLOBAL.PICK_SESSION := 'OPEN'; END;"/>
            <ProgramUnit Name="RECALCULATE_TOTALS" ProgramUnitType="Procedure" ProgramUnitText="PROCEDURE RECALCULATE_TOTALS IS&amp;#10;BEGIN&amp;#10;  NULL;&amp;#10;END;"/>
            <RecordGroup Name="BIN_RG" RecordGroupType="Query" RecordGroupQuery="SELECT BIN_CODE, BIN_LABEL FROM WAREHOUSE.BIN ORDER BY BIN_CODE"/>
            <LOV Name="BIN_LOV" RecordGroupName="BIN_RG" Title="Pick a bin">
              <LOVColumnMapping Name="BIN_CODE" ReturnItem="PICK_LINE.BIN_CODE" DisplayWidth="60"/>
              <LOVColumnMapping Name="BIN_LABEL" DisplayWidth="180"/>
            </LOV>
            <Canvas Name="MAIN_CANVAS" CanvasType="Content" Width="800" Height="600">
              <Graphics Name="HEADER_TEXT" GraphicsType="Text" GraphicsFontName="Tahoma">
                <CompoundText>
                  <TextSegment TextSegmentString="Warehouse picking" TextSegmentFontSize="1000"/>
                </CompoundText>
              </Graphics>
            </Canvas>
            <Block Name="PICK_HEADER" QueryDataSourceName="WAREHOUSE.PICK_HEADER" DMLDataTargetName="WAREHOUSE.PICK_HEADER" RecordsDisplayed="1">
              <Item Name="PICK_ID" ItemType="Text Item" DataType="Number" ColumnName="PICK_ID" Prompt="Pick:" Required="Yes" Enabled="Yes" UpdateAllowed="No" InsertAllowed="No" FormatMask="9999999" DatabaseItem="Yes" PrimaryKey="Yes"/>
              <Item Name="REQUESTED_ON" ItemType="Text Item" DataType="Date" ColumnName="REQUESTED_ON" Prompt="Requested:" FormatMask="DD-MON-YYYY" Required="No" Enabled="Yes" UpdateAllowed="Yes"/>
              <Item Name="PRIORITY" ItemType="Radio Group" DataType="Char" ColumnName="PRIORITY" Prompt="Priority:" InitialValue="N">
                <RadioButton Name="PRIORITY_NORMAL" RadioButtonValue="N" Label="Normal" AccessKey="N"/>
                <RadioButton Name="PRIORITY_URGENT" RadioButtonValue="U" Label="Urgent" AccessKey="U"/>
              </Item>
              <Item Name="LINE_TOTAL" ItemType="Display Item" DataType="Number" CalculationMode="Formula" Formula=":PICK_LINE.QUANTITY * :PICK_LINE.UNIT_COST" DatabaseItem="No" Prompt="Total:"/>
              <Trigger Name="WHEN-VALIDATE-RECORD" TriggerStyle="PL/SQL">
                <TriggerText>BEGIN RECALCULATE_TOTALS; END;</TriggerText>
              </Trigger>
            </Block>
            <Block Name="PICK_LINE" QueryDataSourceName="WAREHOUSE.PICK_LINE" DMLDataTargetName="WAREHOUSE.PICK_LINE" RecordsDisplayed="10">
              <Item Name="PICK_ID" ItemType="Text Item" DataType="Number" ColumnName="PICK_ID" Visible="No" Required="Yes"/>
              <Item Name="BIN_CODE" ItemType="Text Item" DataType="Char" ColumnName="BIN_CODE" Prompt="Bin:" LOVName="BIN_LOV" ValidateFromList="Yes" Required="Yes" Enabled="Yes"/>
              <Item Name="QUANTITY" ItemType="Text Item" DataType="Number" ColumnName="QUANTITY" Prompt="Qty:" MaximumLength="6" Required="Yes" FormatMask="999G999"/>
              <Item Name="UNIT_COST" ItemType="Text Item" DataType="Number" ColumnName="UNIT_COST" Prompt="Unit cost:" FormatMask="999G999D99" UpdateAllowed="No"/>
            </Block>
            <Relation Name="PICK_HEADER_LINE" DetailBlock="PICK_LINE" JoinCondition="PICK_HEADER.PICK_ID = PICK_LINE.PICK_ID" DeleteRecordBehavior="Cascading" PreventMasterlessOperation="Yes"/>
          </FormModule>
        </Module>
        """;

    /// <summary>
    /// A second independently authored export under a different module name, using the FormModule root form
    /// and carrying a site-specific element in a namespace of its own.
    /// </summary>
    public const string LookupExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FormModule xmlns="http://xmlns.oracle.com/Forms" xmlns:ext="urn:contoso:forms-annotations" Name="STOCK_LOOKUP" Title="Stock lookup" MenuModule="DEFAULT">
          <ext:Annotation Origin="site-standard" Reviewed="2019-04-02"/>
          <Block Name="STOCK" QueryDataSourceName="WAREHOUSE.STOCK" RecordsDisplayed="15">
            <Item Name="SKU" ItemType="Text Item" DataType="Char" ColumnName="SKU" Prompt="SKU:" Required="Yes" Enabled="Yes" CaseRestriction="Upper"/>
            <Item Name="ON_HAND" ItemType="Text Item" DataType="Number" ColumnName="ON_HAND" Prompt="On hand:" UpdateAllowed="No" FormatMask="999G999"/>
          </Block>
        </FormModule>
        """;

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

    /// <summary>
    /// The whole retail banking workflow schema: four tables, every column the workflows touch, and the
    /// sequences behind their identifiers. <see cref="Schema"/> above is deliberately a subset of this, so a
    /// test that passes against one and not the other proves recognition is structural.
    /// </summary>
    public const string BankingSchema = """
        CREATE TABLE BANK_ACCOUNT_REQUEST
        (
            REQUEST_ID      NUMBER(10) PRIMARY KEY,
            BRANCH_CODE     VARCHAR2(12) NOT NULL,
            ACCOUNT_KIND    VARCHAR2(12) NOT NULL,
            HONORIFIC       VARCHAR2(8),
            GIVEN_NAME      VARCHAR2(40) NOT NULL,
            FAMILY_NAME     VARCHAR2(40) NOT NULL,
            DATE_OF_BIRTH   DATE NOT NULL,
            WORK_PHONE      VARCHAR2(20),
            HOME_PHONE      VARCHAR2(20),
            STREET_ADDRESS  VARCHAR2(120) NOT NULL,
            REGION_CODE     VARCHAR2(20) NOT NULL,
            POSTAL_CODE     VARCHAR2(12) NOT NULL,
            EMAIL_ADDRESS   VARCHAR2(120) NOT NULL,
            REQUEST_STATUS  VARCHAR2(12) DEFAULT 'SUBMITTED' NOT NULL,
            SUBMITTED_AT    TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL,
            DECIDED_AT      TIMESTAMP,
            CONSTRAINT BANK_REQ_KIND_CK CHECK (ACCOUNT_KIND IN ('SAVINGS', 'CHECKING')),
            CONSTRAINT BANK_REQ_STATUS_CK CHECK (REQUEST_STATUS IN ('SUBMITTED', 'APPROVED', 'REJECTED'))
        );

        CREATE TABLE BANK_ACCOUNT
        (
            ACCOUNT_ID            NUMBER(10) PRIMARY KEY,
            REQUEST_ID            NUMBER(10) NOT NULL,
            BRANCH_CODE           VARCHAR2(12) NOT NULL,
            ACCOUNT_KIND          VARCHAR2(12) NOT NULL,
            OPENED_ON             DATE DEFAULT SYSDATE NOT NULL,
            ONLINE_ENABLED        CHAR(1) DEFAULT 'N' NOT NULL,
            ONLINE_PASSWORD_HASH  RAW(32),
            CONSTRAINT BANK_ACCT_REQUEST_UQ UNIQUE (REQUEST_ID),
            CONSTRAINT BANK_ACCT_REQUEST_FK FOREIGN KEY (REQUEST_ID)
                REFERENCES BANK_ACCOUNT_REQUEST (REQUEST_ID),
            CONSTRAINT BANK_ACCT_ONLINE_CK CHECK (ONLINE_ENABLED IN ('Y', 'N'))
        );

        CREATE TABLE BANK_STAFF_USER
        (
            STAFF_ID       NUMBER(10) PRIMARY KEY,
            USERNAME       VARCHAR2(40) NOT NULL,
            PASSWORD_HASH  RAW(32) NOT NULL,
            ROLE_CODE      VARCHAR2(12) NOT NULL,
            ACTIVE_FLAG    CHAR(1) DEFAULT 'Y' NOT NULL,
            CONSTRAINT BANK_STAFF_USERNAME_UQ UNIQUE (USERNAME),
            CONSTRAINT BANK_STAFF_ROLE_CK CHECK (ROLE_CODE IN ('MANAGER', 'AUDITOR'))
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
            CONSTRAINT BANK_TXN_DIRECTION_CK CHECK (DIRECTION_CODE IN ('CR', 'DR'))
        );

        CREATE SEQUENCE BANK_REQUEST_SEQ START WITH 1001 INCREMENT BY 1 NOCACHE;
        CREATE SEQUENCE BANK_ACCOUNT_SEQ START WITH 500001 INCREMENT BY 1 NOCACHE;
        CREATE SEQUENCE BANK_TRANSACTION_SEQ START WITH 9001 INCREMENT BY 1 NOCACHE;
        """;
}
