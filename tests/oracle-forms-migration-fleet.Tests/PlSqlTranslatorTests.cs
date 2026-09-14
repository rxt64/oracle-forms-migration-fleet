// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class PlSqlTranslatorTests
{
    private const string Package = """
        CREATE OR REPLACE PACKAGE LEGACY_BANKING_API AS
            request_not_found EXCEPTION;
            FUNCTION CURRENT_BALANCE(p_account_id IN NUMBER) RETURN NUMBER;
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
                   AND ONLINE_PASSWORD_HASH = STANDARD_HASH(p_password, 'SHA256');

                RETURN v_count = 1;
            END VALIDATE_CUSTOMER_LOGIN;

            FUNCTION CURRENT_BALANCE(p_account_id IN NUMBER) RETURN NUMBER IS
                v_balance NUMBER;
            BEGIN
                SELECT NVL(SUM(AMOUNT), 0)
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
                    SELECT * INTO v_request FROM BANK_ACCOUNT_REQUEST
                     WHERE REQUEST_ID = p_request_id FOR UPDATE;
                EXCEPTION
                    WHEN NO_DATA_FOUND THEN
                        RAISE request_not_found;
                END;

                p_account_id := BANK_ACCOUNT_SEQ.NEXTVAL;

                UPDATE BANK_ACCOUNT_REQUEST
                   SET DECIDED_AT = SYSTIMESTAMP
                 WHERE REQUEST_ID = p_request_id;
            END APPROVE_REQUEST;
        END LEGACY_BANKING_API;
        /
        """;

    private const string Trigger = """
        CREATE OR REPLACE TRIGGER BANK_TRANSACTION_BI
            BEFORE INSERT ON BANK_TRANSACTION
            FOR EACH ROW
        BEGIN
            IF :NEW.TRANSACTION_ID IS NULL THEN
                :NEW.TRANSACTION_ID := BANK_TRANSACTION_SEQ.NEXTVAL;
            END IF;
        END;
        /
        """;

    private static PlSqlUnit Unit(PlSqlTranslation translation, string name) =>
        translation.Units.Single(unit => unit.Name == name);

    [Fact]
    public void A_package_function_becomes_a_plpgsql_function_named_for_its_package()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Package);

        PlSqlUnit login = Unit(translation, "legacy_banking_api_validate_customer_login");

        Assert.Equal(PlSqlUnitKind.Function, login.Kind);
        Assert.Contains("CREATE OR REPLACE FUNCTION legacy_banking_api_validate_customer_login(p_account_id numeric, p_password text)", login.Sql, StringComparison.Ordinal);
        Assert.Contains("RETURNS boolean", login.Sql, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE plpgsql", login.Sql, StringComparison.Ordinal);
        Assert.Contains("v_count integer;", login.Sql, StringComparison.Ordinal);

        // The stored column is bytea, so the digest has to stay binary here too or the comparison never matches.
        Assert.Contains("sha256(convert_to(p_password, 'UTF8'))", login.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("STANDARD_HASH", login.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle_only_expressions_inside_a_body_are_rewritten()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Package);

        Assert.Contains("COALESCE(SUM(AMOUNT), 0)", Unit(translation, "legacy_banking_api_current_balance").Sql, StringComparison.Ordinal);

        string approve = Unit(translation, "legacy_banking_api_approve_request").Sql;
        Assert.Contains("nextval('bank_account_seq')", approve, StringComparison.Ordinal);
        Assert.Contains("now()", approve, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTIMESTAMP", approve, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_out_parameter_keeps_its_direction()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Package);
        string approve = Unit(translation, "legacy_banking_api_approve_request").Sql;

        Assert.Contains("CREATE OR REPLACE PROCEDURE legacy_banking_api_approve_request(p_request_id numeric, OUT p_account_id numeric)", approve, StringComparison.Ordinal);
        Assert.Contains("v_request bank_account_request%rowtype;", approve, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_declared_exception_becomes_a_raise_with_the_name_as_its_message()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Package);

        // PostgreSQL has no user-declared exception type, so this cannot round-trip and must be reported.
        Assert.Contains("RAISE EXCEPTION 'request_not_found'", Unit(translation, "legacy_banking_api_approve_request").Sql, StringComparison.Ordinal);
        Assert.Contains(
            translation.Findings,
            finding => finding.Construct.Contains("request_not_found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_row_trigger_becomes_a_trigger_function_that_returns_the_row()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Trigger);
        string sql = Unit(translation, "bank_transaction_bi").Sql;

        Assert.Contains("CREATE OR REPLACE FUNCTION bank_transaction_bi_fn() RETURNS trigger", sql, StringComparison.Ordinal);
        Assert.Contains("NEW.TRANSACTION_ID := nextval('bank_transaction_seq');", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(":NEW", sql, StringComparison.Ordinal);

        // A BEFORE row trigger that returns nothing silently discards every insert.
        Assert.Contains("RETURN NEW;", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT ON bank_transaction", sql, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW EXECUTE FUNCTION bank_transaction_bi_fn();", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_is_translated_and_ordered_after_the_routines()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE VIEW BANK_ACCOUNT_STATEMENT AS
            SELECT a.ACCOUNT_ID, t.AMOUNT
              FROM BANK_ACCOUNT a
              JOIN BANK_TRANSACTION t ON t.ACCOUNT_ID = a.ACCOUNT_ID;
            """);

        PlSqlUnit view = Unit(translation, "bank_account_statement");

        Assert.Equal(PlSqlUnitKind.View, view.Kind);
        Assert.StartsWith("CREATE OR REPLACE VIEW bank_account_statement AS", view.Sql, StringComparison.Ordinal);
        Assert.EndsWith(";", view.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_does_not_swallow_whatever_source_file_follows_it()
    {
        // Sources are concatenated before translation, so an unterminated view would absorb the next file.
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE VIEW BANK_ACCOUNT_STATEMENT AS
            SELECT a.ACCOUNT_ID FROM BANK_ACCOUNT a;

            DECLARE
                v_accounts PLS_INTEGER;
            BEGIN
                DBMS_OUTPUT.PUT_LINE('verifying');
            END;
            """);

        string view = Unit(translation, "bank_account_statement").Sql;

        Assert.DoesNotContain("DBMS_OUTPUT", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v_accounts", view, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("FROM BANK_ACCOUNT a;", view, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_program_unit_is_reported_rather_than_half_translated()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE TYPE BODY SOME_TYPE AS
              MEMBER FUNCTION F RETURN NUMBER IS BEGIN RETURN 1; END;
            END;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Sql.Length > 0);
        Assert.Contains(translation.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    [Fact]
    public void An_owner_qualified_package_keeps_its_own_name()
    {
        // HRMS.PKG_AUDIT is the owner and the package. Taking the owner collapsed every package into one
        // prefix, so same-named routines from different packages overwrote each other.
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY HRMS.PKG_AUDIT AS
                FUNCTION GET_PARAM(p_code IN VARCHAR2) RETURN VARCHAR2 IS
                BEGIN
                    RETURN p_code;
                END GET_PARAM;
            END PKG_AUDIT;
            /
            """);

        PlSqlUnit unit = Assert.Single(translation.Units, candidate => candidate.Kind == PlSqlUnitKind.Function);

        Assert.Equal("pkg_audit_get_param", unit.Name);
        Assert.DoesNotContain("hrms_get_param", unit.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_unit_with_the_same_name_is_refused_rather_than_silently_replacing_the_first()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_ONE AS
                FUNCTION F RETURN NUMBER IS
                BEGIN
                    RETURN 1;
                END F;
                FUNCTION F RETURN NUMBER IS
                BEGIN
                    RETURN 2;
                END F;
            END PKG_ONE;
            /
            """);

        Assert.Single(translation.Units, unit => unit.Name == "pkg_one_f");
        Assert.Contains(
            translation.Findings,
            finding => finding.Reason.Contains("silently replaced the first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_package_ref_cursor_alias_becomes_a_postgres_refcursor()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE PKG_REPORTING AS
                TYPE t_report_cursor IS REF CURSOR;
            END PKG_REPORTING;
            /
            CREATE OR REPLACE PACKAGE BODY PKG_REPORTING AS
                PROCEDURE HEADCOUNT_REPORT(
                    p_cursor OUT t_report_cursor,
                    p_as_of_date IN DATE DEFAULT SYSDATE,
                    p_dept_id IN NUMBER DEFAULT NULL
                ) IS
                BEGIN
                    OPEN p_cursor FOR SELECT DEPT_ID FROM DEPARTMENTS;
                END HEADCOUNT_REPORT;
            END PKG_REPORTING;
            /
            """);

        string sql = Unit(translation, "pkg_reporting_headcount_report").Sql;

        Assert.Contains("OUT p_cursor refcursor", sql, StringComparison.Ordinal);
        Assert.Contains("p_as_of_date date DEFAULT CURRENT_DATE", sql, StringComparison.Ordinal);
        Assert.Contains("p_dept_id numeric DEFAULT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("t_report_cursor", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sys_refcursor_return_and_local_variable_become_postgres_refcursors()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_AUDIT AS
                FUNCTION GET_CHANGE_HISTORY(
                    p_table_name IN VARCHAR2,
                    p_user IN VARCHAR2 DEFAULT USER
                ) RETURN SYS_REFCURSOR IS
                    v_cursor SYS_REFCURSOR;
                BEGIN
                    OPEN v_cursor FOR SELECT AUDIT_ID FROM AUDIT_LOG WHERE TABLE_NAME = p_table_name;
                    RETURN v_cursor;
                END GET_CHANGE_HISTORY;
            END PKG_AUDIT;
            /
            """);

        string sql = Unit(translation, "pkg_audit_get_change_history").Sql;

        Assert.Contains("p_user text DEFAULT CURRENT_USER", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNS refcursor", sql, StringComparison.Ordinal);
        Assert.Contains("v_cursor refcursor;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_that_cannot_be_parsed_refuses_the_whole_routine()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT(p_values IN TABLE OF NUMBER) IS
                BEGIN
                    NULL;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(
            translation.Findings,
            finding => finding.Construct == "PKG_X.DO_IT"
                       && finding.Reason.Contains("routine was not emitted", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_keyword_inside_a_comment_does_not_delete_the_routine()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_PAYROLL AS
                PROCEDURE CALCULATE_PAYROLL IS
                BEGIN
                    -- This loop should use BULK COLLECT and FORALL in Oracle.
                    NULL;
                END CALCULATE_PAYROLL;
            END PKG_PAYROLL;
            /
            """);

        Assert.Contains(translation.Units, unit => unit.Name == "pkg_payroll_calculate_payroll");
    }

    [Fact]
    public void Raise_application_error_preserves_the_message_and_oracle_code_for_review()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_EMPLOYEE AS
                PROCEDURE VALIDATE_DEPT(p_dept_id IN NUMBER) IS
                BEGIN
                    RAISE_APPLICATION_ERROR(-20003, 'Invalid department: ' || p_dept_id);
                END VALIDATE_DEPT;
            END PKG_EMPLOYEE;
            /
            """);

        string sql = Unit(translation, "pkg_employee_validate_dept").Sql;

        Assert.Contains("RAISE EXCEPTION USING MESSAGE = 'Invalid department: ' || p_dept_id", sql, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P0001', DETAIL = 'Oracle error -20003'", sql, StringComparison.Ordinal);
        Assert.Contains(
            translation.Findings,
            finding => finding.Construct == "PKG_EMPLOYEE.VALIDATE_DEPT"
                       && finding.Severity == ConversionSeverity.ManualReview
                       && finding.Reason.Contains("numeric Oracle code", StringComparison.Ordinal));
    }

    [Fact]
    public void Dbms_output_becomes_a_notice_and_is_reported_as_a_caller_change()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_AUDIT AS
                PROCEDURE PURGE_OLD_RECORDS IS
                BEGIN
                    DBMS_OUTPUT.PUT_LINE('Purged records');
                END PURGE_OLD_RECORDS;
            END PKG_AUDIT;
            /
            """);

        string sql = Unit(translation, "pkg_audit_purge_old_records").Sql;

        Assert.Contains("RAISE NOTICE '%', 'Purged records';", sql, StringComparison.Ordinal);
        Assert.Contains(
            translation.Findings,
            finding => finding.Construct == "PKG_AUDIT.PURGE_OLD_RECORDS"
                       && finding.Severity == ConversionSeverity.ManualReview);
    }

    [Fact]
    public void Initialized_variables_row_count_and_dual_are_rewritten()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_COMMON AS
                FUNCTION NEXT_ID RETURN NUMBER IS
                    v_id NUMBER := 0;
                BEGIN
                    SELECT SEQ_EMPLOYEE.NEXTVAL INTO v_id FROM DUAL;
                    UPDATE EMPLOYEES SET ACTIVE_FLAG = 'Y';
                    v_id := SQL%ROWCOUNT;
                    RETURN v_id;
                END NEXT_ID;
            END PKG_COMMON;
            /
            """);

        string sql = Unit(translation, "pkg_common_next_id").Sql;

        Assert.Contains("v_id numeric := 0;", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT nextval('seq_employee') INTO v_id;", sql, StringComparison.Ordinal);
        Assert.Contains("GET DIAGNOSTICS v_id = ROW_COUNT;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DUAL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL%ROWCOUNT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OPEN p_cursor FOR v_sql;", "OPEN FOR dynamic SQL")]
    [InlineData("OPEN p_cursor FOR (v_sql);", "OPEN FOR dynamic SQL")]
    [InlineData("OPEN p_cursor FOR 'SELECT * FROM EMPLOYEES';", "OPEN FOR dynamic SQL")]
    [InlineData("OPEN p_cursor FOR q'[SELECT * FROM EMPLOYEES]';", "OPEN FOR dynamic SQL")]
    [InlineData("SELECT EMP_ID FROM EMPLOYEES CONNECT BY PRIOR EMP_ID = MANAGER_EMP_ID;", "CONNECT BY")]
    [InlineData("v_connection := UTL_SMTP.OPEN_CONNECTION('mail', 25);", "UTL_SMTP")]
    public void Unsafe_cursor_and_platform_specific_queries_are_refused(string body, string construct)
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate($"""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT(p_cursor OUT SYS_REFCURSOR) IS
                    v_sql VARCHAR2(100);
                BEGIN
                    {body}
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(
            translation.Findings,
            finding => finding.Construct == "PKG_X.DO_IT"
                       && finding.Reason.Contains(construct, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comment_markers_inside_a_string_do_not_hide_an_unsafe_call()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                    v_text VARCHAR2(20);
                BEGIN
                    v_text := '-- harmless'; UTL_SMTP.OPEN_CONNECTION('mail', 25);
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("UTL_SMTP", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("p_optional IN NUMBER DEFAULT 1, p_required IN NUMBER")]
    [InlineData("p_value OUT NUMBER DEFAULT 1")]
    public void A_signature_postgres_cannot_represent_is_refused(string parameters)
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate($"""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT({parameters}) IS
                BEGIN
                    NULL;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Construct == "PKG_X.DO_IT");
    }

    [Fact]
    public void Ref_cursor_aliases_are_scoped_to_their_package()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE PKG_A AS
                TYPE t_cursor IS REF CURSOR;
            END PKG_A;
            /
            CREATE OR REPLACE PACKAGE BODY PKG_B AS
                PROCEDURE DO_IT(p_cursor OUT t_cursor) IS
                BEGIN
                    OPEN p_cursor FOR SELECT 1;
                END DO_IT;
            END PKG_B;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_b_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("unresolved cursor type", StringComparison.Ordinal));
    }

    [Fact]
    public void New_rewrites_do_not_change_string_literals_or_comments()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION TEXT_VALUE RETURN VARCHAR2 IS
                BEGIN
                    -- DBMS_OUTPUT.PUT_LINE('not executable');
                    RETURN ' FROM DUAL; SQL%ROWCOUNT; DBMS_OUTPUT.PUT_LINE(''x'');';
                END TEXT_VALUE;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_text_value").Sql;

        Assert.Contains("-- DBMS_OUTPUT.PUT_LINE('not executable');", sql, StringComparison.Ordinal);
        Assert.Contains("' FROM DUAL; SQL%ROWCOUNT; DBMS_OUTPUT.PUT_LINE(''x'');'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rewrite_cannot_start_in_a_comment_and_consume_code_on_the_next_line()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                BEGIN
                    -- DBMS_OUTPUT.PUT_LINE(
                    NULL);
                END DO_IT;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_do_it").Sql;

        Assert.Contains("-- DBMS_OUTPUT.PUT_LINE(", sql, StringComparison.Ordinal);
        Assert.Contains("NULL);", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISE NOTICE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_local_cursor_declaration_refuses_the_whole_routine()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                    v_cursor t_missing_cursor;
                BEGIN
                    OPEN v_cursor FOR SELECT 1;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("routine was not emitted", StringComparison.Ordinal));
    }

    [Fact]
    public void A_comment_between_dual_and_the_terminator_refuses_the_routine()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION DO_IT RETURN NUMBER IS
                    v_value NUMBER;
                BEGIN
                    SELECT 1 INTO v_value FROM DUAL /* source compatibility */;
                    RETURN v_value;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("FROM DUAL", StringComparison.Ordinal));
    }

    [Fact]
    public void Assignment_defaults_and_commas_inside_literals_are_preserved()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION DO_IT(
                    p_label IN VARCHAR2 := 'a,b',
                    p_actor IN VARCHAR2 DEFAULT 'USER'
                ) RETURN VARCHAR2 IS
                BEGIN
                    RETURN p_label || p_actor;
                END DO_IT;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_do_it").Sql;

        Assert.Contains("p_label text DEFAULT 'a,b'", sql, StringComparison.Ordinal);
        Assert.Contains("p_actor text DEFAULT 'USER'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'CURRENT_USER'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_routine_local_cursor_alias_does_not_leak_to_another_routine()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DECLARES_ALIAS IS
                    TYPE t_local_cursor IS REF CURSOR;
                BEGIN
                    NULL;
                END DECLARES_ALIAS;
                PROCEDURE USES_ALIAS(p_cursor OUT t_local_cursor) IS
                BEGIN
                    OPEN p_cursor FOR SELECT 1;
                END USES_ALIAS;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_uses_alias");
    }

    [Fact]
    public void Oracle_alternative_quoted_text_is_not_scanned_as_executable_code()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION TEXT_VALUE RETURN VARCHAR2 IS
                BEGIN
                    RETURN q'[it's UTL_SMTP text]';
                END TEXT_VALUE;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_text_value").Sql;

        Assert.Contains("RETURN 'it''s UTL_SMTP text';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("q'[", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            translation.Findings,
            finding => finding.Construct == "PKG_X.TEXT_VALUE"
                       && finding.Reason.Contains("UTL_SMTP", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_call_messages_may_contain_a_closing_call_sequence()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                BEGIN
                    DBMS_OUTPUT.PUT_LINE('before ); after');
                    RAISE_APPLICATION_ERROR(-20001, 'bad ); value');
                END DO_IT;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_do_it").Sql;

        Assert.Contains("RAISE NOTICE '%', 'before ); after';", sql, StringComparison.Ordinal);
        Assert.Contains("MESSAGE = 'bad ); value'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DBMS_OUTPUT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RAISE_APPLICATION_ERROR", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_removable_dual_does_not_hide_an_aliased_dual()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                    v_one NUMBER;
                    v_two NUMBER;
                BEGIN
                    SELECT 1 INTO v_one FROM DUAL;
                    SELECT 2 INTO v_two FROM DUAL d;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("FROM DUAL", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_type_is_refused_without_relying_on_its_name()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT(p_value OUT t_result) IS
                BEGIN
                    NULL;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("unresolved cursor type", StringComparison.Ordinal));
    }

    [Fact]
    public void Sql_rowcount_outside_a_direct_assignment_is_refused()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                BEGIN
                    UPDATE EMPLOYEES SET ACTIVE_FLAG = 'Y';
                    IF SQL%ROWCOUNT = 0 THEN
                        NULL;
                    END IF;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("SQL%ROWCOUNT", StringComparison.Ordinal));
    }

    [Fact]
    public void A_computed_raise_application_error_code_is_refused()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                    v_code NUMBER := -20001;
                BEGIN
                    RAISE_APPLICATION_ERROR(v_code, 'bad');
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("literal Oracle error number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_multiline_initialized_declaration_is_translated_as_one_statement()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION DO_IT RETURN NUMBER IS
                    v_total NUMBER :=
                        0;
                BEGIN
                    RETURN v_total;
                END DO_IT;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_do_it").Sql;

        Assert.Contains("v_total numeric := 0;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_total NUMBER", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_q_quoted_default_with_an_apostrophe_and_comma_is_one_parameter()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION DO_IT(p_label IN VARCHAR2 DEFAULT q'[it's,a]') RETURN VARCHAR2 IS
                BEGIN
                    RETURN p_label;
                END DO_IT;
            END PKG_X;
            /
            """);

        string sql = Unit(translation, "pkg_x_do_it").Sql;

        Assert.Contains("p_label text DEFAULT 'it''s,a'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("q'[", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MY_RAISE_APPLICATION_ERROR(-20001, 'bad');")]
    [InlineData("MY_DBMS_OUTPUT.PUT_LINE('message');")]
    [InlineData("OTHER.DBMS_OUTPUT.PUT_LINE('message');")]
    public void Longer_or_qualified_builtin_names_are_not_partially_rewritten(string body)
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate($"""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                BEGIN
                    {body}
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    [Fact]
    public void A_qualified_sql_rowcount_target_is_refused_instead_of_partially_rewritten()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate("""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                    v_result EMPLOYEES%ROWTYPE;
                BEGIN
                    UPDATE EMPLOYEES SET ACTIVE_FLAG = 'Y';
                    v_result.row_count := SQL%ROWCOUNT;
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(translation.Findings, finding => finding.Reason.Contains("SQL%ROWCOUNT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RETURN OTHER.NVL(p_value, 0);")]
    [InlineData("RETURN a.USER;")]
    [InlineData("RETURN pkg.SYSDATE;")]
    [InlineData("RETURN pkg.SYSTIMESTAMP;")]
    [InlineData("RETURN HR.ACCOUNT_SEQ.NEXTVAL;")]
    public void Qualified_oracle_builtins_are_refused_instead_of_partially_rewritten(string body)
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate($"""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                FUNCTION DO_IT(p_value IN NUMBER) RETURN NUMBER IS
                BEGIN
                    {body}
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name == "pkg_x_do_it");
        Assert.Contains(
            translation.Findings,
            finding => finding.Reason.Contains("qualified Oracle built-in", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PRAGMA AUTONOMOUS_TRANSACTION; v NUMBER;")]
    [InlineData("EXECUTE IMMEDIATE 'select 1';")]
    [InlineData("EXCEPTION WHEN VALUE_ERROR THEN NULL;")]
    public void A_routine_postgres_cannot_take_is_refused_rather_than_emitted(string body)
    {
        // Emitting it does not degrade gracefully: the CREATE fails, and that failure blocks the data
        // load behind it. Refusing one routine costs a line on the remediation list.
        PlSqlTranslation translation = PlSqlTranslator.Translate($"""
            CREATE OR REPLACE PACKAGE BODY PKG_X AS
                PROCEDURE DO_IT IS
                BEGIN
                    {body}
                END DO_IT;
            END PKG_X;
            /
            """);

        Assert.DoesNotContain(translation.Units, unit => unit.Name.Contains("do_it", StringComparison.Ordinal));
        Assert.Contains(
            translation.Findings,
            finding => finding.Severity == ConversionSeverity.Unsupported
                       && finding.Reason.StartsWith("Not translated because of", StringComparison.Ordinal));
    }

    [Fact]
    public void Translated_units_are_marked_so_they_can_be_applied_apart_from_the_tables()
    {
        string rendered = PlSqlTranslator.Render(PlSqlTranslator.Translate(Package).Units);

        Assert.Contains(PlSqlTranslator.ProgramUnitsMarker, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Routines_are_rendered_before_the_triggers_and_views_that_may_use_them()
    {
        PlSqlTranslation translation = PlSqlTranslator.Translate(Package + "\n" + Trigger);
        string rendered = PlSqlTranslator.Render(translation.Units);

        int function = rendered.IndexOf("CREATE OR REPLACE FUNCTION legacy_banking_api_", StringComparison.Ordinal);
        int trigger = rendered.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);

        Assert.True(function >= 0 && trigger > function);
    }
}
