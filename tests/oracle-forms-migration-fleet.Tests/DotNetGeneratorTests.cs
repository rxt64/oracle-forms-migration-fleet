using System.Text.Json;
using System.Text.Json.Nodes;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Pure tests for the declarative target-mapping reader and the .NET generator.
///
/// Two independently authored fixtures share no identifier. Wherever a test asserts that they produce the
/// same thing, that is the claim under test: the generator reads roles, not names.
/// </summary>
public sealed class DotNetGeneratorTests
{
    private static OracleSchema Meridian => OracleSchemaParser.Parse(DotNetPilotFixtures.MeridianSchema);

    private static OracleSchema Kestrel => OracleSchemaParser.Parse(DotNetPilotFixtures.KestrelSchema);

    private static OracleSchema UnboundHeader =>
        OracleSchemaParser.Parse(DotNetPilotFixtures.MeridianUnboundHeaderSchema);

    private static TargetMapping Accept(string manifest, OracleSchema schema, IReadOnlyList<FormsModule>? forms = null)
    {
        TargetMappingRead read = TargetMappingReader.Read(manifest, schema, forms);
        Assert.True(read.Mapping is not null, string.Join(" | ", read.Rejections));
        return read.Mapping!;
    }

    private static IReadOnlyList<string> Refuse(string manifest, OracleSchema schema, IReadOnlyList<FormsModule>? forms = null)
    {
        TargetMappingRead read = TargetMappingReader.Read(manifest, schema, forms);
        Assert.Null(read.Mapping);
        Assert.NotEmpty(read.Rejections);
        return read.Rejections;
    }

    /// <summary>Applies a structural mutation to a manifest so a negative test changes exactly one thing.</summary>
    private static string Mutate(string manifest, Action<JsonNode> mutate)
    {
        JsonNode root = JsonNode.Parse(manifest)!;
        mutate(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode Object(JsonNode root, string role) =>
        root["objects"]!.AsArray().First(entry => entry!["role"]!.GetValue<string>() == role)!;

    private static JsonNode Field(JsonNode root, string role, string fieldRole) =>
        Object(root, role)["fields"]!.AsArray().First(entry => entry!["role"]!.GetValue<string>() == fieldRole)!;

    private static IReadOnlyList<GeneratedFile> Generate(string manifest, OracleSchema schema, string application)
    {
        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            schema, Accept(manifest, schema), application, DatabaseTarget.PostgreSql);
        Assert.NotEmpty(conversion.Files);
        return conversion.Files;
    }

    private static string Contents(IReadOnlyList<GeneratedFile> files, string path) =>
        files.Single(file => file.Path == path).Contents;

    [Fact]
    public void Both_independently_authored_fixtures_are_accepted()
    {
        TargetMapping meridian = Accept(DotNetPilotFixtures.MeridianManifest, Meridian);
        TargetMapping kestrel = Accept(DotNetPilotFixtures.KestrelManifest, Kestrel);

        Assert.Equal(DotNetPilotFixtures.MeridianLabel, meridian.Declaration.FixtureLabel);
        Assert.Equal(DotNetPilotFixtures.KestrelLabel, kestrel.Declaration.FixtureLabel);
        Assert.Equal("MRD_ORDER_HEAD", meridian.Header.Table.Name);
        Assert.Equal("DISPATCH_CONSIGNMENT_HEADER", kestrel.Header.Table.Name);
    }

    [Fact]
    public void Both_fixtures_generate_the_same_set_of_files()
    {
        string[] meridian =
        [.. Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian").Select(file => file.Path).Order(StringComparer.Ordinal)];
        string[] kestrel =
        [.. Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel").Select(file => file.Path).Order(StringComparer.Ordinal)];

        Assert.Equal(meridian, kestrel);
        Assert.Contains("backend/Api/ApiHost.cs", meridian);
        Assert.Contains("backend/Api.Tests/AcceptanceTests.cs", meridian);
        Assert.Contains("frontend/src/App.tsx", meridian);
        Assert.Contains("database/schema.sql", meridian);
        Assert.Contains("database/routines.sql", meridian);
        Assert.Contains("mapping-manifest.json", meridian);
        Assert.Contains("deploy/containerapp.yaml", meridian);
    }

    [Fact]
    public void Neither_fixtures_identifiers_leak_into_the_other()
    {
        string meridian = string.Join('\n', Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian").Select(file => file.Contents));
        string kestrel = string.Join('\n', Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel").Select(file => file.Contents));

        foreach (string identifier in DotNetPilotFixtures.MeridianIdentifiers)
        {
            Assert.Contains(identifier.ToLowerInvariant(), meridian, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, kestrel, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string identifier in DotNetPilotFixtures.KestrelIdentifiers)
        {
            Assert.Contains(identifier.ToLowerInvariant(), kestrel, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, meridian, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Both_fixtures_produce_the_same_wire_contract_and_the_same_transform_decisions()
    {
        IReadOnlyList<GeneratedFile> meridian = Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian");
        IReadOnlyList<GeneratedFile> kestrel = Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel");

        // The HTTP surface, the request and response records, and the browser client are role-named, so two
        // estates with nothing in common still present the same API to anything that calls it.
        foreach (string path in new[] { "backend/Api/ApiHost.cs", "backend/Api/Contracts.cs", "backend/Api/Program.cs" })
        {
            Assert.Equal(Contents(meridian, path), Contents(kestrel, path));
        }

        string[] meridianDecisions =
        [.. DotNetApplicationEmitter.TransformDecisions(Accept(DotNetPilotFixtures.MeridianManifest, Meridian)).Select(decision => decision.Id)];
        string[] kestrelDecisions =
        [.. DotNetApplicationEmitter.TransformDecisions(Accept(DotNetPilotFixtures.KestrelManifest, Kestrel)).Select(decision => decision.Id)];

        Assert.Equal(meridianDecisions, kestrelDecisions);
    }

    [Fact]
    public void The_generated_schema_and_routine_carry_the_mapped_identifiers_and_the_declared_constants()
    {
        IReadOnlyList<GeneratedFile> files = Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian");
        string schema = Contents(files, "database/schema.sql");
        string routine = Contents(files, "database/routines.sql");

        Assert.Contains("CREATE TABLE \"mrd_customer\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"cust_no\" integer GENERATED BY DEFAULT AS IDENTITY", schema, StringComparison.Ordinal);
        Assert.Contains("\"art_price\" numeric(11,2)", schema, StringComparison.Ordinal);
        Assert.Contains("REFERENCES \"mrd_order_head\"", schema, StringComparison.Ordinal);

        // The lookup filters compare the literal the manifest declared. The generator invented neither.
        Assert.Contains("\"cust_state\" = 'A'", routine, StringComparison.Ordinal);
        Assert.Contains("'ENTERED'", routine, StringComparison.Ordinal);
        Assert.Contains("round(v_price * v_quantity, 2)", routine, StringComparison.Ordinal);

        // Lookup tables are created before the tables whose foreign keys reference them.
        Assert.True(schema.IndexOf("CREATE TABLE \"mrd_customer\"", StringComparison.Ordinal)
            < schema.IndexOf("CREATE TABLE \"mrd_order_head\"", StringComparison.Ordinal));
        Assert.True(schema.IndexOf("CREATE TABLE \"mrd_order_head\"", StringComparison.Ordinal)
            < schema.IndexOf("CREATE TABLE \"mrd_order_item\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_routine_is_optimistic_when_the_item_binds_a_version_column()
    {
        string routine = Contents(Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian"), "database/routines.sql");

        Assert.Contains("\"art_rev\" = v_version", routine, StringComparison.Ordinal);
        Assert.Contains("OFM05", routine, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", routine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_routine_locks_rows_when_the_item_binds_no_version_column()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
        {
            JsonArray fields = Object(root, "ItemLookup")["fields"]!.AsArray();
            JsonNode version = fields.First(entry => entry!["role"]!.GetValue<string>() == "ConcurrencyVersion")!;
            fields.Remove(version);
        });

        // ART_REV stays NOT NULL with no default, so the item table can no longer be inserted into at all.
        // That is the refusal under test first; the pessimistic path is exercised on a schema that allows it.
        OracleSchema schema = OracleSchemaParser.Parse(
            DotNetPilotFixtures.MeridianSchema.Replace("ART_REV      NUMBER(12)    NOT NULL,", string.Empty, StringComparison.Ordinal));

        string routine = Contents(Generate(manifest, schema, "Meridian"), "database/routines.sql");

        Assert.Contains("FOR UPDATE", routine, StringComparison.Ordinal);
        Assert.DoesNotContain("OFM05", routine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_not_null_column_no_role_is_bound_to_stops_generation()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
        {
            JsonArray fields = Object(root, "ItemLookup")["fields"]!.AsArray();
            JsonNode version = fields.First(entry => entry!["role"]!.GetValue<string>() == "ConcurrencyVersion")!;
            fields.Remove(version);
        });

        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            Meridian, Accept(manifest, Meridian), "Meridian", DatabaseTarget.PostgreSql);

        Assert.Empty(conversion.Files);
        Assert.Contains(conversion.Findings, finding =>
            finding.Severity == ConversionSeverity.Unsupported && finding.Construct == "MRD_ARTICLE.ART_REV");
    }

    /// <summary>
    /// Every fixture's table bodies have to be comma-separated lists a parser can read.
    ///
    /// The claim is structural rather than a search for one bad string: a column's provenance is allowed
    /// to be recorded, but never on the line that carries the separator, because a trailing <c>--</c>
    /// comment swallows the comma after it and merges two column definitions into one unparseable clause.
    /// </summary>
    [Theory]
    [InlineData("meridian")]
    [InlineData("kestrel")]
    [InlineData("unbound-header")]
    public void No_comment_in_the_generated_schema_swallows_a_clause_separator(string fixture)
    {
        string schema = Contents(GenerateFixture(fixture), "database/schema.sql");
        string[] lines = schema.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        bool inside = false;
        List<string> body = [];
        int tables = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (!inside)
            {
                inside = line.StartsWith("CREATE TABLE", StringComparison.Ordinal);
                body.Clear();
                continue;
            }

            if (line == ");")
            {
                inside = false;
                tables++;
                AssertBodyIsASeparatedList(body, fixture);
                continue;
            }

            body.Add(line);
        }

        Assert.False(inside, $"{fixture}: a CREATE TABLE body is never closed.");
        Assert.Equal(4, tables);
    }

    private static void AssertBodyIsASeparatedList(IReadOnlyList<string> body, string fixture)
    {
        string[] clauses = [.. body.Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))];
        Assert.NotEmpty(clauses);

        foreach (string line in body)
        {
            int comment = line.IndexOf("--", StringComparison.Ordinal);
            Assert.True(
                comment < 0 || comment == 0,
                $"{fixture}: '{line}' carries a comment after SQL, so anything the emitter appends to that line " +
                "is commented out along with it.");
        }

        for (int index = 0; index < clauses.Length - 1; index++)
        {
            Assert.EndsWith(",", clauses[index], StringComparison.Ordinal);
        }

        Assert.False(
            clauses[^1].EndsWith(',') || clauses[^1].EndsWith("--", StringComparison.Ordinal),
            $"{fixture}: the last clause '{clauses[^1]}' is not a terminating clause.");
    }

    /// <summary>
    /// The only two optional header roles the reader admits are left unbound, so the read-back query has no
    /// column to select for either. A null literal is not an identifier and must not be alias-qualified:
    /// <c>h.null::text</c> does not parse, and the failure surfaces only after a submission is committed.
    /// </summary>
    [Fact]
    public void A_header_binding_no_optional_column_reads_back_unqualified_null_literals()
    {
        string store = Contents(
            Generate(DotNetPilotFixtures.MeridianUnboundHeaderManifest, UnboundHeader, "Meridian"),
            "backend/Api/SubmissionStore.cs");

        Assert.Contains("null::text", store, StringComparison.Ordinal);
        Assert.Contains("null::bigint", store, StringComparison.Ordinal);
        Assert.DoesNotContain(".null", store, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NOT NULL column nothing is bound to is only satisfiable if the target carries its default, because
    /// every generated insert omits it. Dropping the default and generating anyway produces a schema whose
    /// first write fails.
    /// </summary>
    [Fact]
    public void A_required_unmapped_column_carries_its_literal_default_into_the_generated_schema()
    {
        string schema = Contents(
            Generate(DotNetPilotFixtures.MeridianUnboundHeaderManifest, UnboundHeader, "Meridian"),
            "database/schema.sql");

        Assert.Contains("\"ord_state\" varchar(10) DEFAULT 'ENTERED' NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("\"ord_rev\" bigint DEFAULT 0 NOT NULL", schema, StringComparison.Ordinal);
    }

    /// <summary>
    /// A default this generator cannot prove means the same thing to PostgreSQL is a refusal, not a
    /// rewrite. Carrying <c>SYSDATE</c> across as if it were a literal would be the generator deciding what
    /// the source meant.
    /// </summary>
    [Theory]
    [InlineData("SYSDATE")]
    [InlineData("SYS_GUID()")]
    [InlineData("MRD_ORD_SEQ.NEXTVAL")]
    [InlineData("NULL")]
    [InlineData("''")]
    public void A_required_unmapped_column_whose_default_is_not_a_literal_stops_generation(string expression)
    {
        string schemaText = DotNetPilotFixtures.MeridianUnboundHeaderSchema.Replace(
            "DEFAULT 'ENTERED'", $"DEFAULT {expression}", StringComparison.Ordinal);
        OracleSchema schema = OracleSchemaParser.Parse(schemaText);

        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            schema,
            Accept(DotNetPilotFixtures.MeridianUnboundHeaderManifest, schema),
            "Meridian",
            DatabaseTarget.PostgreSql);

        Assert.Empty(conversion.Files);
        Assert.Contains(conversion.Findings, finding =>
            finding.Severity == ConversionSeverity.Unsupported && finding.Construct == "MRD_ORDER_HEAD.ORD_STATE");
    }

    /// <summary>A literal of the wrong kind for the target type would not apply, so it is refused too.</summary>
    [Fact]
    public void A_required_unmapped_columns_default_must_match_the_target_type_it_is_attached_to()
    {
        string schemaText = DotNetPilotFixtures.MeridianUnboundHeaderSchema.Replace(
            "DEFAULT 0 NOT NULL", "DEFAULT 'ZERO' NOT NULL", StringComparison.Ordinal);
        OracleSchema schema = OracleSchemaParser.Parse(schemaText);

        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            schema,
            Accept(DotNetPilotFixtures.MeridianUnboundHeaderManifest, schema),
            "Meridian",
            DatabaseTarget.PostgreSql);

        Assert.Empty(conversion.Files);
        Assert.Contains(conversion.Findings, finding =>
            finding.Severity == ConversionSeverity.Unsupported && finding.Construct == "MRD_ORDER_HEAD.ORD_REV");
    }

    /// <summary>
    /// A default this generator cannot prove means the same thing to PostgreSQL is dropped and reported
    /// when nothing depends on it. Rewriting <c>SYS_GUID()</c> into something PostgreSQL accepts would be
    /// the generator deciding what the source meant.
    /// </summary>
    [Fact]
    public void An_untranslatable_default_on_a_nullable_column_is_dropped_and_reported()
    {
        string schemaText = DotNetPilotFixtures.MeridianSchema.Replace(
            "CUST_MEMO    VARCHAR2(200),", "CUST_MEMO    VARCHAR2(200) DEFAULT SYS_GUID(),", StringComparison.Ordinal);
        OracleSchema schema = OracleSchemaParser.Parse(schemaText);

        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            schema, Accept(DotNetPilotFixtures.MeridianManifest, schema), "Meridian", DatabaseTarget.PostgreSql);

        Assert.NotEmpty(conversion.Files);
        Assert.DoesNotContain(
            "\"cust_memo\" varchar(200) DEFAULT",
            Contents(conversion.Files, "database/schema.sql"),
            StringComparison.Ordinal);
        Assert.Contains(conversion.Findings, finding =>
            finding.Severity == ConversionSeverity.ManualReview && finding.Construct == "MRD_CUSTOMER.CUST_MEMO");
    }

    private static IReadOnlyList<GeneratedFile> GenerateFixture(string fixture) => fixture switch
    {
        "meridian" => Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian"),
        "kestrel" => Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel"),
        "unbound-header" => Generate(DotNetPilotFixtures.MeridianUnboundHeaderManifest, UnboundHeader, "Meridian"),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Unknown fixture."),
    };

    [Fact]
    public void The_resolved_manifest_records_every_binding_and_a_content_digest()
    {
        string manifest = Contents(Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel"), "mapping-manifest.json");
        JsonNode document = JsonNode.Parse(manifest)!;

        Assert.Equal(DotNetApplicationEmitter.ResolvedManifestSchemaVersion, document["schemaVersion"]!.GetValue<string>());
        Assert.Equal(DotNetPilotFixtures.KestrelLabel, document["fixtureLabel"]!.GetValue<string>());
        Assert.Equal("optimistic-version-column", document["concurrencyControl"]!.GetValue<string>());
        Assert.Equal(64, document["bindingDigest"]!.GetValue<string>().Length);
        Assert.False(document["verification"]!["executedByGenerator"]!.GetValue<bool>());

        string[] roles = [.. document["objects"]!.AsArray().Select(entry => entry!["role"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
        Assert.Equal(["DetailLine", "ItemLookup", "MasterHeader", "PartyLookup"], roles);

        JsonNode detail = document["objects"]!.AsArray()
            .First(entry => entry!["role"]!.GetValue<string>() == "DetailLine")!;
        Assert.Equal("DISPATCH_CONSIGNMENT_ALLOCATION", detail["sourceTable"]!.GetValue<string>());
        Assert.Contains(detail["fields"]!.AsArray(), field =>
            field!["role"]!.GetValue<string>() == "Quantity"
            && field["sourceColumn"]!.GetValue<string>() == "ALLOCATION_UNIT_COUNT"
            && field["targetType"]!.GetValue<string>() == "bigint");
    }

    [Fact]
    public void A_body_that_parses_is_refused_on_its_merits_and_only_an_unreadable_one_is_a_bad_request()
    {
        string host = Contents(
            Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian"), "backend/Api/ApiHost.cs");

        // Binding the request record put the framework's deserializer in front of the rule: a quantity of
        // 2.5 failed while being bound to a long, so the API answered 400 and never stated the refusal it
        // has a code for. The body is read here instead, and every whole number is read with TryGetInt64
        // so a fractional or out-of-range value is refused rather than rounded.
        Assert.DoesNotContain("SubmissionRequest request,", host, StringComparison.Ordinal);
        Assert.Contains("JsonDocument.ParseAsync(context.Request.Body", host, StringComparison.Ordinal);
        Assert.Contains("element.TryGetInt64(out value)", host, StringComparison.Ordinal);
        Assert.Contains("ReadSubmission(body.RootElement, out fault)", host, StringComparison.Ordinal);

        // One 400, and it is the body that could not be parsed. Everything the parser accepted is
        // answered as unprocessable, which is the only way a caller can tell the two apart.
        Assert.Equal(1, host.Split(", 400)", StringSplitOptions.None).Length - 1);
        Assert.Contains("The request body is not valid JSON", host, StringComparison.Ordinal);
        Assert.Contains("catch (JsonException malformed)", host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_acceptance_suite_separates_an_unreadable_body_from_a_refused_one()
    {
        string suite = Contents(
            Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel"), "backend/Api.Tests/AcceptanceTests.cs");

        Assert.Contains("A_fractional_quantity_changes_nothing", suite, StringComparison.Ordinal);
        Assert.Contains("A_quantity_that_is_not_a_whole_number_changes_nothing", suite, StringComparison.Ordinal);
        Assert.Contains("A_body_that_is_not_json_is_a_bad_request", suite, StringComparison.Ordinal);
        Assert.Contains("A_submission_missing_a_required_field_changes_nothing", suite, StringComparison.Ordinal);

        // The malformed case asserts the generated code's own code alongside the status, so a 400 the host
        // produced before any generated line ran cannot satisfy it.
        Assert.Contains("Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);", suite, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(\"OFM00\", await Code(response));", suite, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_screen_tests_unmount_between_cases()
    {
        IReadOnlyList<GeneratedFile> files = Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian");
        string suite = Contents(files, "frontend/src/App.test.tsx");

        // The runner enables no vitest globals, so Testing Library registers no automatic cleanup of its
        // own. Without an explicit unmount every render stayed in the document, the second case onwards
        // matched two of each data-testid, and three of the four cases failed on the duplicate rather than
        // on the behaviour they were written for.
        Assert.DoesNotContain("--globals", Contents(files, "frontend/package.json"), StringComparison.Ordinal);
        Assert.Contains("import { cleanup,", suite, StringComparison.Ordinal);
        Assert.Contains("cleanup();", suite, StringComparison.Ordinal);

        // More than one case shares the document, which is what makes the unmount load-bearing.
        Assert.True(
            suite.Split("render(<App />)", StringSplitOptions.None).Length - 1 > 1,
            "The generated screen suite renders once, so this regression no longer guards anything.");
    }

    [Fact]
    public void The_generated_frontend_uses_the_locked_dependency_tree()
    {
        IReadOnlyList<GeneratedFile> files = Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian");

        Assert.Contains("\"lockfileVersion\"", Contents(files, "frontend/package-lock.json"), StringComparison.Ordinal);
        Assert.Contains("\"vite\": \"6.1.0\"", Contents(files, "frontend/package.json"), StringComparison.Ordinal);
        Assert.Contains("npm ci", Contents(files, "BUILD.md"), StringComparison.Ordinal);
        Assert.Contains("Npgsql\" Version=\"9.0.3\"", Contents(files, "backend/Api/Api.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_generated_carries_a_credential_or_a_resolved_cloud_endpoint()
    {
        IReadOnlyList<GeneratedFile> files = Generate(DotNetPilotFixtures.KestrelManifest, Kestrel, "Kestrel");

        foreach (GeneratedFile file in files)
        {
            Assert.False(
                FleetGuardrails.ContainsPotentialSecret(file.Contents),
                $"{file.Path} looks like it carries credential material.");

            // Every infrastructure reference is a placeholder an operator has to fill in. A generator that
            // wrote a real registry, vault, or identity in would be choosing a tenant nobody named.
            foreach (string line in file.Contents.Split('\n').Where(line => line.Contains("azurecr.io", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.Contains("<registry>.azurecr.io", line, StringComparison.Ordinal);
            }
        }

        string deployment = Contents(files, "deploy/containerapp.yaml");
        Assert.Contains("secretRef: target-postgres-connection", deployment, StringComparison.Ordinal);
        Assert.Contains("keyVaultUrl: <key-vault-secret-uri>", deployment, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", deployment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_non_postgresql_target_generates_nothing()
    {
        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            Meridian, Accept(DotNetPilotFixtures.MeridianManifest, Meridian), "Meridian", DatabaseTarget.AzureSqlDatabase);

        Assert.Empty(conversion.Files);
        Assert.Contains(conversion.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    [Theory]
    [InlineData("fleet.target-mapping/2")]
    [InlineData("")]
    public void An_unknown_manifest_version_is_refused(string version)
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root => root["schemaVersion"] = version);

        Assert.Contains(Refuse(manifest, Meridian), reason => reason.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_asking_for_another_generator_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root => root["generator"] = "java-crud/9");

        Assert.Contains(Refuse(manifest, Meridian), reason => reason.Contains("java-crud/9", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_json_is_refused_without_throwing()
    {
        Assert.Contains(Refuse("{ not json", Meridian), reason => reason.Contains("valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_bound_to_a_table_the_schema_does_not_declare_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root => Object(root, "ItemLookup")["table"] = "MRD_MISSING");

        Assert.Contains(Refuse(manifest, Meridian), reason => reason.Contains("MRD_MISSING", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_bound_to_a_column_the_table_does_not_declare_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Field(root, "DetailLine", "Quantity")["column"] = "ITM_NOT_THERE");

        Assert.Contains(Refuse(manifest, Meridian), reason => reason.Contains("ITM_NOT_THERE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_quantity_bound_to_a_fractional_column_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Field(root, "DetailLine", "Quantity")["column"] = "ITM_PRICE");

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("fractional part", StringComparison.Ordinal));
    }

    [Fact]
    public void Money_bound_to_a_column_without_a_declared_scale_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Field(root, "ItemLookup", "UnitPrice")["column"] = "ART_ON_HAND");

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("scale", StringComparison.Ordinal));
    }

    [Fact]
    public void An_identifier_that_is_not_the_primary_key_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Field(root, "PartyLookup", "Identifier")["column"] = "CUST_NAME");

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("single-column primary key", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reference_the_schema_does_not_enforce_is_refused()
    {
        OracleSchema withoutForeignKey = OracleSchemaParser.Parse(DotNetPilotFixtures.MeridianSchema.Replace(
            "CONSTRAINT FK_MRD_ITEM_ART FOREIGN KEY (ITM_ART) REFERENCES MRD_ARTICLE (ART_NO)",
            "CONSTRAINT CK_MRD_ITEM_ART CHECK (ITM_ART > 0)",
            StringComparison.Ordinal));

        Assert.Contains(Refuse(DotNetPilotFixtures.MeridianManifest, withoutForeignKey), reason =>
            reason.Contains("no foreign key", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_required_object_role_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.KestrelManifest, root =>
        {
            JsonArray objects = root["objects"]!.AsArray();
            objects.Remove(Object(root, "ItemLookup"));
        });

        Assert.Contains(Refuse(manifest, Kestrel), reason =>
            reason.Contains("required object role ItemLookup", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_required_field_role_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.KestrelManifest, root =>
        {
            JsonArray fields = Object(root, "DetailLine")["fields"]!.AsArray();
            fields.Remove(Field(root, "DetailLine", "LineAmount"));
        });

        Assert.Contains(Refuse(manifest, Kestrel), reason =>
            reason.Contains("required field role LineAmount", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_object_role_is_refused_rather_than_resolved()
    {
        string manifest = Mutate(DotNetPilotFixtures.KestrelManifest, root =>
            root["objects"]!.AsArray().Add(Object(root, "PartyLookup").DeepClone()));

        Assert.Contains(Refuse(manifest, Kestrel), reason =>
            reason.Contains("declared more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrecognized_role_name_is_refused_rather_than_ignored()
    {
        string manifest = Mutate(DotNetPilotFixtures.KestrelManifest, root =>
            Field(root, "DetailLine", "Quantity")["role"] = "WeightInKilograms");

        Assert.Contains(Refuse(manifest, Kestrel), reason =>
            reason.Contains("WeightInKilograms", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_the_object_does_not_carry_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.KestrelManifest, root =>
            Field(root, "DetailLine", "Quantity")["role"] = "StockOnHand");

        Assert.Contains(Refuse(manifest, Kestrel), reason =>
            reason.Contains("does not carry", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_citing_an_undeclared_source_anchor_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Object(root, "MasterHeader")["sourceRefs"] = new JsonArray("sch-does-not-exist"));

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("sch-does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_citing_no_source_anchor_at_all_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Object(root, "MasterHeader")["sourceRefs"] = new JsonArray());

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("cites no source anchor", StringComparison.Ordinal));
    }

    [Fact]
    public void A_status_constant_outside_the_declared_domain_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Object(root, "MasterHeader")["constants"]!["Status"] = "PROVISIONAL");

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("PROVISIONAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_status_role_with_no_declared_constant_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
            Object(root, "MasterHeader")["constants"] = new JsonObject());

        Assert.Contains(Refuse(manifest, Meridian), reason =>
            reason.Contains("declares no constant", StringComparison.Ordinal));
    }

    [Fact]
    public void A_line_amount_that_would_round_the_unit_price_is_refused()
    {
        OracleSchema coarse = OracleSchemaParser.Parse(DotNetPilotFixtures.KestrelSchema.Replace(
            "ALLOCATION_EXTENDED      NUMBER(18,3)",
            "ALLOCATION_EXTENDED      NUMBER(18,2)",
            StringComparison.Ordinal));

        Assert.Contains(Refuse(DotNetPilotFixtures.KestrelManifest, coarse), reason =>
            reason.Contains("more decimal places", StringComparison.Ordinal));
    }

    [Fact]
    public void A_forms_anchor_is_confirmed_against_the_normalized_module_it_names()
    {
        FormsModule module = ModuleWithFacts("forms/ORDENT.xml", "digest-one", "{ns}FormModule[1]/{ns}Block[2]");
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
        {
            root["sources"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "forms-block",
                ["kind"] = "FormsSourceObject",
                ["path"] = "{ns}FormModule[1]/{ns}Block[2]",
                ["module"] = "forms/ORDENT.xml",
                ["textDigest"] = "digest-one",
            });
            Object(root, "MasterHeader")["sourceRefs"]!.AsArray().Add("forms-block");
        });

        Assert.NotNull(Accept(manifest, Meridian, [module]));
    }

    [Theory]
    [InlineData("digest-two", "{ns}FormModule[1]/{ns}Block[2]", "different export")]
    [InlineData("digest-one", "{ns}FormModule[1]/{ns}Block[9]", "does not retain")]
    public void A_forms_anchor_the_module_cannot_confirm_is_refused(string digest, string path, string expected)
    {
        FormsModule module = ModuleWithFacts("forms/ORDENT.xml", "digest-one", "{ns}FormModule[1]/{ns}Block[2]");
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
        {
            root["sources"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "forms-block",
                ["kind"] = "FormsSourceObject",
                ["path"] = path,
                ["module"] = "forms/ORDENT.xml",
                ["textDigest"] = digest,
            });
            Object(root, "MasterHeader")["sourceRefs"]!.AsArray().Add("forms-block");
        });

        Assert.Contains(Refuse(manifest, Meridian, [module]), reason =>
            reason.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_forms_anchor_naming_a_module_this_run_never_read_is_refused()
    {
        string manifest = Mutate(DotNetPilotFixtures.MeridianManifest, root =>
        {
            root["sources"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "forms-block",
                ["kind"] = "FormsSourceObject",
                ["path"] = "{ns}FormModule[1]",
                ["module"] = "forms/NEVER_READ.xml",
                ["textDigest"] = "digest-one",
            });
            Object(root, "MasterHeader")["sourceRefs"]!.AsArray().Add("forms-block");
        });

        Assert.Contains(Refuse(manifest, Meridian, []), reason =>
            reason.Contains("NEVER_READ.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void The_build_and_verification_phases_look_for_the_same_descriptor()
    {
        Assert.Equal("backend/pom.xml", GeneratedApplicationLayout.BackendDescriptor(BackEndStack.JavaSpringBoot));
        Assert.Equal("backend/GeneratedBackend.slnx", GeneratedApplicationLayout.BackendDescriptor(BackEndStack.AspNetCore));

        IReadOnlyList<GeneratedFile> files = Generate(DotNetPilotFixtures.MeridianManifest, Meridian, "Meridian");
        Assert.Contains(files, file => file.Path == GeneratedApplicationLayout.BackendDescriptor(BackEndStack.AspNetCore));
        Assert.Contains(files, file => file.Path == GeneratedApplicationLayout.FrontendDescriptor);
    }

    [Fact]
    public void The_java_stack_stays_the_default_so_an_old_request_still_decodes_as_one()
    {
        TargetStack stack = new() { Database = DatabaseTarget.PostgreSql };

        Assert.Equal(BackEndStack.JavaSpringBoot, stack.BackEnd);
        Assert.Equal(0, (int)BackEndStack.JavaSpringBoot);
    }

    private static FormsModule ModuleWithFacts(string sourcePath, string digest, string factId) =>
        new(
            "ORDENT",
            "Order entry",
            [],
            [],
            [],
            [],
            sourcePath,
            new FormsSourceFactSet(
                digest,
                null,
                [new FormsSourceFact(factId, 1, null, 0, "Block", "ns", "ORDERS", [], null, FormsSourceFactKind.Declared)]));
}
