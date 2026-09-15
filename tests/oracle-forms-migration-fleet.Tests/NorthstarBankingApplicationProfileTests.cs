// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The point of the profile is that the fleet, not a hand-edited fixture, produces a working application.
/// These tests hold that line: recognition only fires on complete evidence, every source route and module
/// survives generation, anything less falls back to the generic path, and the output never claims the
/// behaviour came from a Forms export.
/// </summary>
public class NorthstarBankingApplicationProfileTests
{
    private static OracleSchema Banking() => OracleSchemaParser.Parse(OracleSamples.BankingSchema);

    private static ApplicationConversion Convert(OracleSchema schema) =>
        ApplicationCodeEmitter.Convert(schema, "Northstar Online Banking", DatabaseTarget.PostgreSql);

    private static string File(ApplicationConversion conversion, string path) =>
        conversion.Files.Single(file => file.Path == path).Contents;

    private static OracleSchema Without(OracleSchema schema, string table) =>
        schema with
        {
            Tables = [.. schema.Tables.Where(candidate => !string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase))],
        };

    private static OracleSchema WithoutColumn(OracleSchema schema, string table, string column) =>
        schema with
        {
            Tables =
            [
                .. schema.Tables.Select(candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase)
                    ? candidate with { Columns = [.. candidate.Columns.Where(item => !string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase))] }
                    : candidate),
            ],
        };

    /// <summary>Replaces one column in one table, leaving everything else exactly as parsed.</summary>
    private static OracleSchema WithColumn(OracleSchema schema, string table, string column, Func<OracleColumn, OracleColumn> change) =>
        schema with
        {
            Tables =
            [
                .. schema.Tables.Select(candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase)
                    ? candidate with
                    {
                        Columns =
                        [
                            .. candidate.Columns.Select(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase)
                                ? change(item)
                                : item),
                        ],
                    }
                    : candidate),
            ],
        };

    private static OracleSchema WithoutConstraint(OracleSchema schema, string table, Func<OracleConstraint, bool> drop) =>
        schema with
        {
            Tables =
            [
                .. schema.Tables.Select(candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase)
                    ? candidate with { Constraints = [.. candidate.Constraints.Where(item => !drop(item))] }
                    : candidate),
            ],
        };

    private static OracleSchema WithSequence(OracleSchema schema, string name, Func<OracleSequence, OracleSequence?> change) =>
        schema with
        {
            Sequences =
            [
                .. schema.Sequences
                    .Select(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                        ? change(candidate)
                        : candidate)
                    .OfType<OracleSequence>(),
            ],
        };

    [Fact]
    public void The_complete_workflow_schema_selects_the_profile() =>
        Assert.True(NorthstarBankingApplicationProfile.Matches(Banking()));

    [Fact]
    public void The_demo_estate_script_the_fleet_actually_reads_selects_the_profile() =>
        Assert.True(NorthstarBankingApplicationProfile.Matches(
            OracleSchemaParser.Parse(System.IO.File.ReadAllText(DemoEstateScript("001_schema.sql")))));

    [Theory]
    [InlineData("BANK_STAFF_USER")]
    [InlineData("BANK_TRANSACTION")]
    [InlineData("BANK_ACCOUNT")]
    [InlineData("BANK_ACCOUNT_REQUEST")]
    public void A_missing_workflow_table_does_not_select_the_profile(string table) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(Without(Banking(), table)));

    [Theory]
    [InlineData("BANK_ACCOUNT", "ONLINE_PASSWORD_HASH")]
    [InlineData("BANK_ACCOUNT_REQUEST", "EMAIL_ADDRESS")]
    [InlineData("BANK_TRANSACTION", "DIRECTION_CODE")]
    [InlineData("BANK_STAFF_USER", "ROLE_CODE")]
    public void A_missing_workflow_column_does_not_select_the_profile(string table, string column) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(WithoutColumn(Banking(), table, column)));

    [Theory]
    [InlineData("BANK_TRANSACTION", "AMOUNT", "VARCHAR2")]
    [InlineData("BANK_ACCOUNT", "ONLINE_PASSWORD_HASH", "VARCHAR2")]
    [InlineData("BANK_STAFF_USER", "PASSWORD_HASH", "BLOB")]
    [InlineData("BANK_ACCOUNT_REQUEST", "SUBMITTED_AT", "DATE")]
    public void A_column_of_the_wrong_type_does_not_select_the_profile(string table, string column, string baseType) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithColumn(Banking(), table, column, item => item with { BaseType = baseType })));

    [Fact]
    public void A_money_column_that_lost_its_scale_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithColumn(Banking(), "BANK_TRANSACTION", "AMOUNT", item => item with { Scale = null })));

    [Fact]
    public void A_flag_column_wider_than_the_code_it_holds_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithColumn(Banking(), "BANK_TRANSACTION", "DIRECTION_CODE", item => item with { Precision = 4 })));

    [Theory]
    [InlineData("BANK_ACCOUNT_REQUEST", "EMAIL_ADDRESS")]
    [InlineData("BANK_ACCOUNT", "ONLINE_ENABLED")]
    [InlineData("BANK_STAFF_USER", "PASSWORD_HASH")]
    public void A_required_column_that_became_nullable_does_not_select_the_profile(string table, string column) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithColumn(Banking(), table, column, item => item with { NotNull = false })));

    [Fact]
    public void An_optional_column_that_became_required_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithColumn(Banking(), "BANK_ACCOUNT", "ONLINE_PASSWORD_HASH", item => item with { NotNull = true })));

    [Fact]
    public void A_table_without_its_primary_key_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(WithoutConstraint(
            Banking(), "BANK_ACCOUNT", constraint => constraint.Kind == OracleConstraintKind.PrimaryKey)));

    [Fact]
    public void An_account_that_is_no_longer_unique_per_request_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(WithoutConstraint(
            Banking(), "BANK_ACCOUNT", constraint => constraint.Kind == OracleConstraintKind.Unique)));

    [Fact]
    public void A_transaction_without_its_account_foreign_key_does_not_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(WithoutConstraint(
            Banking(), "BANK_TRANSACTION", constraint => constraint.Kind == OracleConstraintKind.ForeignKey)));

    [Fact]
    public void A_foreign_key_pointing_somewhere_else_does_not_select_the_profile()
    {
        OracleSchema schema = Banking();
        OracleSchema redirected = schema with
        {
            Tables =
            [
                .. schema.Tables.Select(table => table.Name == "BANK_TRANSACTION"
                    ? table with
                    {
                        Constraints =
                        [
                            .. table.Constraints.Select(constraint => constraint.Kind == OracleConstraintKind.ForeignKey
                                ? constraint with { ReferencedTable = "BANK_ACCOUNT_REQUEST" }
                                : constraint),
                        ],
                    }
                    : table),
            ],
        };

        Assert.False(NorthstarBankingApplicationProfile.Matches(redirected));
    }

    [Theory]
    [InlineData("BANK_TRANSACTION", "DIRECTION_CODE")]
    [InlineData("BANK_ACCOUNT_REQUEST", "REQUEST_STATUS")]
    [InlineData("BANK_ACCOUNT_REQUEST", "ACCOUNT_KIND")]
    [InlineData("BANK_STAFF_USER", "ROLE_CODE")]
    [InlineData("BANK_ACCOUNT", "ONLINE_ENABLED")]
    public void A_missing_domain_check_constraint_does_not_select_the_profile(string table, string column) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(WithoutConstraint(
            Banking(),
            table,
            constraint => constraint.Kind == OracleConstraintKind.Check
                          && constraint.CheckExpression?.Contains(column, StringComparison.OrdinalIgnoreCase) == true)));

    [Fact]
    public void A_check_constraint_over_a_different_domain_does_not_select_the_profile()
    {
        OracleSchema schema = Banking();
        OracleSchema widened = schema with
        {
            Tables =
            [
                .. schema.Tables.Select(table => table.Name == "BANK_TRANSACTION"
                    ? table with
                    {
                        Constraints =
                        [
                            .. table.Constraints.Select(constraint =>
                                constraint.Kind == OracleConstraintKind.Check
                                    ? constraint with { CheckExpression = "DIRECTION_CODE IN ('CR', 'DR', 'RV')" }
                                    : constraint),
                        ],
                    }
                    : table),
            ],
        };

        Assert.False(NorthstarBankingApplicationProfile.Matches(widened));
    }

    [Fact]
    public void A_check_constraint_spelled_differently_still_selects_the_profile()
    {
        OracleSchema schema = Banking();
        OracleSchema respaced = schema with
        {
            Tables =
            [
                .. schema.Tables.Select(table => table.Name == "BANK_TRANSACTION"
                    ? table with
                    {
                        Constraints =
                        [
                            .. table.Constraints.Select(constraint =>
                                constraint.Kind == OracleConstraintKind.Check
                                    ? constraint with { CheckExpression = "direction_code   in\n    ('CR','DR')" }
                                    : constraint),
                        ],
                    }
                    : table),
            ],
        };

        Assert.True(NorthstarBankingApplicationProfile.Matches(respaced));
    }

    [Theory]
    [InlineData("BANK_REQUEST_SEQ")]
    [InlineData("BANK_ACCOUNT_SEQ")]
    [InlineData("BANK_TRANSACTION_SEQ")]
    public void A_missing_identifier_sequence_does_not_select_the_profile(string sequence) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithSequence(Banking(), sequence, _ => null)));

    [Theory]
    [InlineData("BANK_REQUEST_SEQ", 2L)]
    [InlineData("BANK_ACCOUNT_SEQ", 10L)]
    [InlineData("BANK_TRANSACTION_SEQ", -1L)]
    public void A_sequence_that_does_not_step_by_one_does_not_select_the_profile(string sequence, long increment) =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(
            WithSequence(Banking(), sequence, item => item with { IncrementBy = increment })));

    [Fact]
    public void An_undeclared_increment_is_the_oracle_default_of_one() =>
        Assert.True(NorthstarBankingApplicationProfile.Matches(
            WithSequence(Banking(), "BANK_ACCOUNT_SEQ", item => item with { IncrementBy = null })));

    [Fact]
    public void The_schema_the_other_tests_use_is_not_enough_to_select_the_profile() =>
        Assert.False(NorthstarBankingApplicationProfile.Matches(OracleSchemaParser.Parse(OracleSamples.Schema)));

    [Fact]
    public void The_application_name_alone_never_selects_the_profile()
    {
        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            OracleSchemaParser.Parse(OracleSamples.Schema), "Northstar Online Banking", DatabaseTarget.PostgreSql);

        Assert.DoesNotContain(conversion.Files, file => file.Path.Contains("/banking/", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_source_route_is_generated()
    {
        string controller = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingController.java");

        foreach (string route in NorthstarBankingApplicationProfile.Routes)
        {
            string path = route.Split(' ')[1];
            Assert.Contains($"\"{path}\"", controller, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("@Transactional")]
    [InlineData("FOR UPDATE")]
    [InlineData("MessageDigest.isEqual")]
    [InlineData("SELECT nextval('")]
    [InlineData("REQUEST_SEQUENCE = \"bank_request_seq\"")]
    [InlineData("ACCOUNT_SEQUENCE = \"bank_account_seq\"")]
    [InlineData("TRANSACTION_SEQUENCE = \"bank_transaction_seq\"")]
    public void The_workflow_service_carries_the_behaviour_the_source_relied_on(string expected)
    {
        ApplicationConversion conversion = Convert(Banking());
        string backend = string.Join(
            "\n",
            conversion.Files.Where(file => file.Path.Contains("/banking/", StringComparison.Ordinal)).Select(file => file.Contents));

        Assert.Contains(expected, backend, StringComparison.Ordinal);
    }

    [Fact]
    public void Identifiers_are_advanced_past_the_migrated_rows_before_the_first_insert()
    {
        string sequences = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingSequences.java");

        // Serialised across replicas, and released when the transaction ends however it ends.
        Assert.Contains("@Transactional", sequences, StringComparison.Ordinal);
        Assert.Contains("SELECT pg_advisory_xact_lock(?)", sequences, StringComparison.Ordinal);

        // Monotonic: the sequence's own last value is one of the candidates, so alignment cannot lower it.
        Assert.Contains("GREATEST(", sequences, StringComparison.Ordinal);
        Assert.Contains("SELECT s.last_value", sequences, StringComparison.Ordinal);
        Assert.Contains("FROM pg_sequences s", sequences, StringComparison.Ordinal);
        Assert.Contains("WHERE c.oid = '%s'::regclass), 0)", sequences, StringComparison.Ordinal);
        Assert.Contains("COALESCE((SELECT MAX(%s) FROM %s), 0)", sequences, StringComparison.Ordinal);
        Assert.Contains("true)", sequences, StringComparison.Ordinal);

        // The declared START WITH, minus one, so a freshly created sequence hands out the source's first value.
        Assert.Contains("\"request_id\", 1000L", sequences, StringComparison.Ordinal);
        Assert.Contains("\"account_id\", 500000L", sequences, StringComparison.Ordinal);
        Assert.Contains("\"transaction_id\", 9000L", sequences, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sequence_that_cannot_be_aligned_fails_startup_rather_than_being_logged()
    {
        string sequences = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingSequences.java");

        // A caught failure here would leave the process serving traffic with a sequence behind the loaded
        // rows, so every insert would fail on the primary key and nothing would have said so at startup.
        Assert.DoesNotContain("catch", sequences, StringComparison.Ordinal);
        Assert.DoesNotContain("DataAccessException", sequences, StringComparison.Ordinal);
        Assert.Contains("throw new IllegalStateException", sequences, StringComparison.Ordinal);
    }

    [Fact]
    public void Alignment_never_lowers_a_sequence_another_replica_already_advanced()
    {
        string sequences = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingSequences.java");

        int greatest = sequences.IndexOf("GREATEST(", StringComparison.Ordinal);
        int lastValue = sequences.IndexOf("s.last_value", greatest, StringComparison.Ordinal);
        int closing = sequences.IndexOf("true)", greatest, StringComparison.Ordinal);

        Assert.InRange(lastValue, greatest, closing);
        Assert.DoesNotContain("LEAST(", sequences, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_client_carries_every_source_module()
    {
        string shell = File(Convert(Banking()), "frontend/index.html");

        foreach (string module in NorthstarBankingApplicationProfile.Modules)
        {
            Assert.Contains(module, shell, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_browser_client_calls_every_generated_route()
    {
        string script = File(Convert(Banking()), "frontend/public/app.js");

        foreach (string route in NorthstarBankingApplicationProfile.Routes)
        {
            // /healthz exists for the platform's liveness probe, not for the page, so the client never calls it.
            if (route == "GET /healthz")
            {
                continue;
            }

            // The approve route is built from a request id at run time, so only its stable prefix can be matched.
            string path = route.Split(' ')[1].Replace("/{requestId}/approve", string.Empty, StringComparison.Ordinal);
            Assert.Contains(path, script, StringComparison.Ordinal);
        }

        Assert.Contains("/approve", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frontend/index.html")]
    [InlineData("frontend/public/app.js")]
    [InlineData("frontend/public/styles.css")]
    [InlineData("frontend/package.json")]
    [InlineData("frontend/vite.config.ts")]
    public void The_generated_frontend_contains_every_build_input(string path) =>
        Assert.Contains(Convert(Banking()).Files, file => file.Path == path);

    [Fact]
    public void The_generated_client_needs_no_framework()
    {
        string package = File(Convert(Banking()), "frontend/package.json");

        Assert.DoesNotContain("react", package, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"build\": \"vite build\"", package, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_asks_for_the_assets_vite_publishes()
    {
        string shell = File(Convert(Banking()), "frontend/index.html");

        Assert.Contains("href=\"/styles.css\"", shell, StringComparison.Ordinal);
        Assert.Contains("src=\"/app.js\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_says_it_is_the_migrated_application_rather_than_the_replica()
    {
        ApplicationConversion conversion = Convert(Banking());
        string shell = File(conversion, "frontend/index.html");
        string script = File(conversion, "frontend/public/app.js");

        Assert.Contains("Migrated to Azure Database for PostgreSQL", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Oracle Forms workflow replica", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("browser replica", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_never_claims_forms_xml_produced_the_generated_behaviour()
    {
        ApplicationConversion conversion = Convert(Banking());
        string readme = File(conversion, "README.md");

        Assert.Contains("Nothing here was recovered from a", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(
            conversion.Findings,
            finding => finding.Reason.Contains("follow the blocks, item order, and prompts in the Forms XML", StringComparison.Ordinal));
        Assert.Contains(
            conversion.Findings,
            finding => finding.Category == "User interface"
                       && finding.Reason.Contains("not extracted from a Forms module", StringComparison.Ordinal));
    }

    [Fact]
    public void The_generated_readme_bounds_the_demonstrated_version_path()
    {
        string readme = File(Convert(Banking()), "README.md");

        Assert.Contains("12.2.1.4", readme, StringComparison.Ordinal);
        Assert.Contains("Oracle Database Free 23", readme, StringComparison.Ordinal);
        Assert.Contains("Azure Database for PostgreSQL 16", readme, StringComparison.Ordinal);
        Assert.Contains("not a general compatibility claim", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_forms_export_does_not_turn_the_generated_screens_into_a_forms_claim()
    {
        OracleSchema schema = Banking();
        FormsModule module = new(
            "BANK_ACCOUNT_REQUEST_FORM", "Requests",
            [new FormsBlock("REQUEST_BLOCK", "BANK_ACCOUNT_REQUEST", 10,
                [new FormsItem("REQUEST_ID", "Text Item", null, "REQUEST_ID", "Request", true, true, null)], [])],
            [], [], []);

        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            schema, "Northstar Online Banking", DatabaseTarget.PostgreSql, [module]);

        Assert.DoesNotContain(conversion.Files, file => file.Path == "frontend/src/App.tsx");
        Assert.DoesNotContain(
            conversion.Findings,
            finding => finding.Reason.Contains("Forms XML export", StringComparison.Ordinal));
    }

    [Fact]
    public void Covered_program_units_are_reported_as_implemented_rather_than_outstanding()
    {
        OracleSchema schema = OracleSchemaParser.Parse(OracleSamples.BankingSchema + "\n" + OracleSamples.PlSql);
        ApplicationConversion conversion = Convert(schema);

        ConversionFinding finding = Assert.Single(
            conversion.Findings,
            candidate => candidate.Category == "Server-side logic"
                         && candidate.Construct.Contains("LEGACY_BANKING_API", StringComparison.Ordinal));

        Assert.Equal(ConversionSeverity.Note, finding.Severity);
        Assert.Contains("implemented by the generated workflow service", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_package_whose_name_contains_a_covered_one_is_not_claimed_as_implemented()
    {
        // The old substring test would have called this covered. It is a different package with different
        // behaviour, and nothing in the generated service implements it.
        const string lookalike = """
            CREATE OR REPLACE PACKAGE BODY LEGACY_BANKING_API_V2 AS
                PROCEDURE SWEEP IS
                BEGIN
                    NULL;
                END SWEEP;
            END LEGACY_BANKING_API_V2;
            /
            """;

        ApplicationConversion conversion = Convert(
            OracleSchemaParser.Parse(OracleSamples.BankingSchema + "\n" + lookalike));

        ConversionFinding finding = Assert.Single(
            conversion.Findings,
            candidate => candidate.Category == "Server-side logic"
                         && candidate.Construct.Contains("LEGACY_BANKING_API_V2", StringComparison.Ordinal));

        Assert.Equal(ConversionSeverity.ManualReview, finding.Severity);
    }

    [Fact]
    public void A_covered_name_used_for_a_different_kind_of_object_is_not_claimed_as_implemented()
    {
        // A procedure called LEGACY_BANKING_API is not the package the workflow service reimplements.
        const string differentKind = """
            CREATE OR REPLACE PROCEDURE LEGACY_BANKING_API IS
            BEGIN
                NULL;
            END LEGACY_BANKING_API;
            /
            """;

        ApplicationConversion conversion = Convert(
            OracleSchemaParser.Parse(OracleSamples.BankingSchema + "\n" + differentKind));

        ConversionFinding finding = Assert.Single(
            conversion.Findings,
            candidate => candidate.Category == "Server-side logic"
                         && candidate.Construct.Contains("LEGACY_BANKING_API", StringComparison.Ordinal));

        Assert.Equal(ConversionSeverity.ManualReview, finding.Severity);
    }

    [Fact]
    public void A_program_unit_that_merely_mentions_a_covered_object_is_not_claimed_as_implemented()
    {
        const string mentions = """
            CREATE OR REPLACE PACKAGE BODY BRANCH_REPORTING AS
                PROCEDURE NIGHTLY IS
                BEGIN
                    LEGACY_BANKING_API.APPROVE_REQUEST(1);
                END NIGHTLY;
            END BRANCH_REPORTING;
            /
            """;

        ApplicationConversion conversion = Convert(
            OracleSchemaParser.Parse(OracleSamples.BankingSchema + "\n" + mentions));

        ConversionFinding finding = Assert.Single(
            conversion.Findings,
            candidate => candidate.Category == "Server-side logic"
                         && candidate.Construct.Contains("BRANCH_REPORTING", StringComparison.Ordinal));

        Assert.Equal(ConversionSeverity.ManualReview, finding.Severity);
    }

    [Fact]
    public void Behaviour_outside_the_profile_is_still_reported_as_outstanding()
    {
        const string unrelated = """
            CREATE OR REPLACE PACKAGE BODY BRANCH_REPORTING AS
                PROCEDURE NIGHTLY IS
                BEGIN
                    NULL;
                END NIGHTLY;
            END BRANCH_REPORTING;
            /
            """;

        ApplicationConversion conversion = Convert(
            OracleSchemaParser.Parse(OracleSamples.BankingSchema + "\n" + unrelated));

        Assert.Contains(
            conversion.Findings,
            finding => finding.Category == "Server-side logic"
                       && finding.Severity == ConversionSeverity.ManualReview
                       && finding.Construct.Contains("BRANCH_REPORTING", StringComparison.Ordinal));
    }

    [Fact]
    public void Entities_and_repositories_are_still_generated_for_every_table()
    {
        ApplicationConversion conversion = Convert(Banking());

        Assert.Equal(4, conversion.Files.Count(file => file.Path.Contains("/domain/", StringComparison.Ordinal)));
        Assert.Equal(4, conversion.Files.Count(file => file.Path.Contains("/repository/", StringComparison.Ordinal)));
    }

    [Fact]
    public void No_unauthorized_per_table_controller_is_generated()
    {
        ApplicationConversion conversion = Convert(Banking());

        // These controllers would have published every column of every table without a session or role check,
        // including the migrated password hashes, and accepted unvalidated writes on the same paths.
        Assert.DoesNotContain(conversion.Files, file => file.Path.Contains("/api/", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Files, file => file.Path.EndsWith("Controller.java", StringComparison.Ordinal)
                                                       && !file.Path.Contains("/banking/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/bank-account")]
    [InlineData("/api/bank-account-request")]
    [InlineData("/api/bank-staff-user")]
    [InlineData("/api/bank-transaction")]
    public void No_generated_source_maps_a_per_table_route(string route)
    {
        ApplicationConversion conversion = Convert(Banking());

        foreach (GeneratedFile file in conversion.Files.Where(file => file.Path.EndsWith(".java", StringComparison.Ordinal)))
        {
            // The generated test names these routes to assert they are absent; nothing else may map one.
            if (file.Path.Contains("/src/test/", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain($"\"{route}\"", file.Contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Removing_the_crud_controllers_is_reported_rather_than_left_to_be_noticed()
    {
        ConversionFinding finding = Assert.Single(
            Convert(Banking()).Findings, candidate => candidate.Category == "Authorization");

        Assert.Equal(ConversionSeverity.Note, finding.Severity);
        Assert.Contains("No per-table CRUD controller was generated", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_liveness_endpoint_is_generated_and_never_touches_the_database()
    {
        string controller = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingController.java");

        Assert.Contains("@GetMapping(\"/healthz\")", controller, StringComparison.Ordinal);
        Assert.Contains("GET /healthz", NorthstarBankingApplicationProfile.Routes);

        int liveness = controller.IndexOf("public ResponseEntity<HealthResponse> liveness()", StringComparison.Ordinal);
        int readiness = controller.IndexOf("public ResponseEntity<HealthResponse> health()", StringComparison.Ordinal);
        Assert.InRange(liveness, 0, readiness);
        Assert.DoesNotContain("databaseAvailable", controller[liveness..readiness], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_api_path_answers_the_same_json_error_shape_as_every_other_failure()
    {
        string fallback = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingApiFallbackController.java");

        Assert.Contains("@RestController", fallback, StringComparison.Ordinal);
        Assert.Contains("@RequestMapping({\"/api\", \"/api/**\"})", fallback, StringComparison.Ordinal);
        Assert.Contains("HttpStatus.NOT_FOUND", fallback, StringComparison.Ordinal);
        Assert.Contains("new ErrorResponse(", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_fallback_serves_browser_routes_without_ever_intercepting_the_api()
    {
        string spa = File(Convert(Banking()), "backend/src/main/java/com/northstar/migrated/banking/BankingSpaController.java");

        Assert.Contains("@Controller", spa, StringComparison.Ordinal);
        Assert.DoesNotContain("@RestController", spa, StringComparison.Ordinal);
        Assert.Contains("forward:/index.html", spa, StringComparison.Ordinal);

        // The exclusion is in the mapping, not in a runtime check that a later edit could drop.
        Assert.Contains("(?!api$|healthz$)", spa, StringComparison.Ordinal);

        // A segment containing a dot is a file, so the static handler keeps serving app.js and styles.css.
        Assert.Contains("[^.]*", spa, StringComparison.Ordinal);
        Assert.DoesNotContain("@GetMapping(\"/**\")", spa, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backend/src/test/java/com/northstar/migrated/banking/BankingControllerTest.java")]
    [InlineData("backend/src/test/java/com/northstar/migrated/banking/GeneratedSurfaceTest.java")]
    public void Runnable_backend_tests_are_generated(string path) =>
        Assert.Contains(Convert(Banking()).Files, file => file.Path == path);

    [Fact]
    public void The_generated_tests_exercise_the_dispatcher_rather_than_the_source_text()
    {
        string test = File(Convert(Banking()), "backend/src/test/java/com/northstar/migrated/banking/BankingControllerTest.java");

        Assert.Contains("@WebMvcTest", test, StringComparison.Ordinal);
        Assert.Contains("@MockBean", test, StringComparison.Ordinal);
        Assert.Contains("private BankingRepository repository;", test, StringComparison.Ordinal);
        Assert.Contains("private BankingSessionStore sessions;", test, StringComparison.Ordinal);
        Assert.Contains("mvc.perform(get(\"/healthz\"))", test, StringComparison.Ordinal);
        Assert.Contains("forwardedUrl(\"/index.html\")", test, StringComparison.Ordinal);
        Assert.Contains("status().isNotFound()", test, StringComparison.Ordinal);
        Assert.Contains("status().isUnauthorized()", test, StringComparison.Ordinal);
        Assert.Contains("status().isBadRequest()", test, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_tests_name_every_controller_that_must_not_exist()
    {
        string test = File(Convert(Banking()), "backend/src/test/java/com/northstar/migrated/banking/GeneratedSurfaceTest.java");

        foreach (string table in (string[])["BankAccount", "BankAccountRequest", "BankStaffUser", "BankTransaction"])
        {
            Assert.Contains($"com.northstar.migrated.api.{table}Controller", test, StringComparison.Ordinal);
        }

        Assert.Contains("ClassNotFoundException.class", test, StringComparison.Ordinal);
        Assert.Contains("RequestMappingHandlerMapping", test, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_build_can_run_those_tests()
    {
        string pom = File(Convert(Banking()), "backend/pom.xml");

        Assert.Contains("<artifactId>spring-boot-starter-test</artifactId>", pom, StringComparison.Ordinal);
        Assert.Contains("<scope>test</scope>", pom, StringComparison.Ordinal);
    }

    [Fact]
    public void The_demo_estate_program_units_the_service_reimplements_are_all_recognised()
    {
        OracleSchema schema = OracleSchemaParser.Parse(
            System.IO.File.ReadAllText(DemoEstateScript("003_plsql.sql")));

        IEnumerable<string> covered = schema.ProgramUnits
            .Where(NorthstarBankingApplicationProfile.Covers)
            .Select(unit => $"{unit.Kind} {unit.Name}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "PackageBody LEGACY_BANKING_API",
                "PackageSpecification LEGACY_BANKING_API",
                "Trigger BANK_ACCOUNT_REQUEST_BI",
                "Trigger BANK_TRANSACTION_BI",
            ],
            covered);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        ApplicationConversion first = Convert(Banking());
        ApplicationConversion second = Convert(Banking());

        Assert.Equal(
            first.Files.Select(file => (file.Path, file.Contents)),
            second.Files.Select(file => (file.Path, file.Contents)));
    }

    /// <summary>
    /// The demo estate's own scripts, not the excerpts the other tests use. The fingerprint and the coverage
    /// list have to hold against what the fleet is actually pointed at, or the demo silently changes shape.
    /// </summary>
    private static string DemoEstateScript(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "infra", "legacy-estate", "oracle", "initdb", fileName);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"The demo estate script '{fileName}' was not found above the test assembly.");
    }
}
