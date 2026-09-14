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

    [Theory]
    [InlineData("RAISE_APPLICATION_ERROR(-20001, 'bad');")]
    [InlineData("PRAGMA AUTONOMOUS_TRANSACTION; v NUMBER;")]
    [InlineData("DBMS_OUTPUT.PUT_LINE('x');")]
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
