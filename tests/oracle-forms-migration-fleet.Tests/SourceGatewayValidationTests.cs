// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The single point where a remote answer becomes a local fact.
///
/// Every test here is a way a compromised or confused source gateway could make this host keep bytes it
/// has no reason to trust. The positive case is last and is the only one that produces an artifact.
/// </summary>
public sealed class SourceGatewayValidationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private const string SourceEnvironmentId = "legacy-order-entry";
    private const string FormsRelease = "6i";
    private const string ProfileHash = "9f2b1c7d4e5a60718293a4b5c6d7e8f900112233445566778899aabbccddeeff";
    private const string ModuleAlias = "ORDERS.fmb";

    private static readonly SourceGatewayAuthorizationScope s_scope = new("tenant-a", "project-a");

    [Fact]
    public void An_extracted_module_is_admitted_with_the_bytes_the_host_hashed_itself()
    {
        byte[] module = Encoding.UTF8.GetBytes("FMB-bytes-as-uploaded");
        SourceGatewayFormsModuleRequest request = Request(module);
        byte[] artifact = Artifact(request);

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, Response(request, artifact), s_now);

        Assert.True(admission.Admitted, admission.Reason);
        Assert.Equal(SourceGatewayStatus.Extracted, admission.Reported);
        Assert.Equal("ORDERS", admission.ModuleIdentity);
        Assert.Equal(artifact, admission.Artifact);
    }

    [Fact]
    public void A_response_for_a_different_module_is_refused_even_when_its_own_digests_agree()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleRequest other = request with { ModuleAlias = "INVOICES.fmb" };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, Response(other, Artifact(other)), s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Contains("not correlated", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("release")]
    [InlineData("version")]
    [InlineData("hash")]
    public void A_response_bound_to_another_source_profile_is_refused(string drift)
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request));
        response = drift switch
        {
            "environment" => response with { SourceEnvironmentId = "other-environment" },
            "release" => response with { ExpectedFormsRelease = "12c" },
            "version" => response with { ProfileVersion = request.ProfileVersion + 1 },
            _ => response with { ProfileHash = new string('a', 64) },
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("not correlated", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_module_whose_observed_digest_differs_from_the_pinned_one_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request)) with
        {
            ObservedContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a different module"))),
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("same module", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_artifact_that_does_not_hash_to_its_declared_digest_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request));
        SourceGatewayArtifact tampered = response.IntermediateRepresentation! with
        {
            Base64 = Convert.ToBase64String(Artifact(request, moduleName: "TAMPERED")),
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, response with { IntermediateRepresentation = tampered }, s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("does not hash", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("normalized")]
    [InlineData("sourceRoot")]
    public void An_artifact_claiming_the_host_adjudication_fields_is_refused(string field)
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        byte[] artifact = Artifact(request, extra: field switch
        {
            "normalized" => "\"normalized\":true",
            _ => "\"sourceRoot\":\"forms\"",
        });

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, Response(request, artifact), s_now);

        Assert.False(admission.Admitted);
        Assert.Contains(field, admission.Reason, StringComparison.Ordinal);
        Assert.Contains("normalization phase", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_artifact_claiming_the_host_normalization_generator_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        byte[] artifact = Artifact(request, generator: FormsIntermediateReader.Generator);

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, Response(request, artifact), s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("native source worker", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stale_or_future_extraction_stamp_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse stale = Response(request, Artifact(request)) with
        {
            ExtractedUtc = s_now - TimeSpan.FromHours(3),
        };
        SourceGatewayFormsModuleResponse ahead = Response(request, Artifact(request)) with
        {
            ExtractedUtc = s_now + TimeSpan.FromHours(3),
        };

        Assert.Contains("clock-skew", SourceGatewayValidation.AdmitFormsModule(request, stale, s_now).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clock-skew", SourceGatewayValidation.AdmitFormsModule(request, ahead, s_now).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_blocked_prerequisite_is_reported_without_an_artifact_and_never_fabricated()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, artifact: null) with
        {
            Status = SourceGatewayStatus.BlockedPrerequisite,
            ModuleIdentity = null,
            ObservedContentSha256 = null,
            Findings = ["The Forms 6i open API libraries were not present on the worker host."],
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Equal(SourceGatewayStatus.BlockedPrerequisite, admission.Reported);
        Assert.Null(admission.Artifact);
        Assert.Contains("open API libraries", admission.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_that_still_ships_bytes_discredits_the_whole_response()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request)) with
        {
            Status = SourceGatewayStatus.ExtractionFailed,
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Equal(SourceGatewayStatus.Rejected, admission.Reported);
        Assert.Contains("still inlining an artifact", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_finding_that_looks_like_credential_material_refuses_the_response()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request)) with
        {
            Findings = ["The worker retried the connection with password=hunter2 and failed."],
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("credential material", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_schema_version_or_status_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));

        Assert.Contains(
            "schema version",
            SourceGatewayValidation.AdmitFormsModule(request, Response(request, Artifact(request)) with { SchemaVersion = 2 }, s_now).Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "does not recognize",
            SourceGatewayValidation.AdmitFormsModule(request, Response(request, Artifact(request)) with { Status = "Normalized" }, s_now).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_artifact_with_the_wrong_media_type_or_no_module_is_refused()
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request));

        SourceGatewayAdmission mediaType = SourceGatewayValidation.AdmitFormsModule(
            request,
            response with { IntermediateRepresentation = response.IntermediateRepresentation! with { MediaType = "application/json" } },
            s_now);
        SourceGatewayAdmission empty = SourceGatewayValidation.AdmitFormsModule(
            request, Response(request, Artifact(request, modules: "[]")), s_now);

        Assert.Contains("media type", mediaType.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no extracted module", empty.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_null_response_is_refused_rather_than_treated_as_an_empty_result()
    {
        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            Request(Encoding.UTF8.GetBytes("ORDERS")), null, s_now);

        Assert.False(admission.Admitted);
        Assert.Equal(SourceGatewayStatus.Rejected, admission.Reported);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("other-tenant")]
    [InlineData("other-project")]
    public void A_response_echoing_a_different_authorization_scope_is_refused(string drift)
    {
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS"));
        SourceGatewayFormsModuleResponse response = Response(request, Artifact(request));
        response = drift switch
        {
            "absent" => response with { Scope = null },
            "other-tenant" => response with { Scope = s_scope with { TenantId = "tenant-b" } },
            _ => response with { Scope = s_scope with { ProjectId = "project-b" } },
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Contains("authorization scope", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tenant a")]
    [InlineData("tenant/a")]
    public void A_request_this_host_could_not_scope_is_refused_before_any_digest_is_compared(string tenantId)
    {
        // The artifact is genuine and its digests all agree. It is still refused, because the thing that
        // would bind it to a tenant and a project was never derived, and matching bytes are not authority.
        SourceGatewayFormsModuleRequest request = Request(Encoding.UTF8.GetBytes("ORDERS")) with
        {
            Scope = new SourceGatewayAuthorizationScope(tenantId, "project-a"),
        };

        SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
            request, Response(request, Artifact(request)), s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Contains("authorization scope", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schema_response_echoing_a_different_authorization_scope_is_refused()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        SourceGatewayOracleSchemaResponse response = SchemaResponse(request, SchemaArtifact(request)) with
        {
            Scope = s_scope with { ProjectId = "project-b" },
        };

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Contains("authorization scope", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceGatewayFormsModuleRequest Request(byte[] moduleBytes) => new(
        SourceGatewayProtocol.SchemaVersion,
        SourceEnvironmentId,
        FormsRelease,
        4,
        ProfileHash,
        ModuleAlias,
        Convert.ToHexStringLower(SHA256.HashData(moduleBytes)),
        s_scope);

    private static SourceGatewayFormsModuleResponse Response(
        SourceGatewayFormsModuleRequest request,
        byte[]? artifact) => new(
        SourceGatewayProtocol.SchemaVersion,
        SourceGatewayStatus.Extracted,
        request.SourceEnvironmentId,
        request.ExpectedFormsRelease,
        request.ProfileVersion,
        request.ProfileHash,
        request.ModuleAlias,
        s_now - TimeSpan.FromSeconds(4),
        "ORDERS",
        request.ExpectedContentSha256,
        artifact is null
            ? null
            : new SourceGatewayArtifact(
                SourceGatewayProtocol.FormsIrMediaType,
                Convert.ToHexStringLower(SHA256.HashData(artifact)),
                Convert.ToBase64String(artifact)),
        new SourceGatewayNativeEvidence("forms.d2f", "6.0.8.22.1", "6i", new string('b', 64), new string('c', 64), ["d2ffmdld"], ["D2FP_TITLE"]),
        [],
        [],
        request.Scope);

    /// <summary>Builds an extraction artifact by hand, so a test can write a field the worker never writes.</summary>
    private static byte[] Artifact(
        SourceGatewayFormsModuleRequest request,
        string? generator = null,
        string moduleName = "ORDERS",
        string modules = "",
        string? extra = null)
    {
        string body = modules.Length > 0
            ? modules
            : $$"""[{"name":{{JsonSerializer.Serialize(moduleName)}},"title":null,"blocks":[],"triggers":[],"programUnits":[],"lovs":[]}]""";
        string json = $$"""
            {"generator":{{JsonSerializer.Serialize(generator ?? SourceGatewayProtocol.ExtractedIrGenerator)}},
            "schemaVersion":"{{SourceGatewayProtocol.ExtractedIrSchemaVersion}}",
            "sourceEnvironmentId":{{JsonSerializer.Serialize(request.SourceEnvironmentId)}},
            "profileVersion":{{request.ProfileVersion}},
            "profileHash":{{JsonSerializer.Serialize(request.ProfileHash)}},
            "moduleAlias":{{JsonSerializer.Serialize(request.ModuleAlias)}},
            "contentSha256":{{JsonSerializer.Serialize(request.ExpectedContentSha256)}},
            "formsFamily":"Forms6i","versionAuthority":"source-worker-native",{{(extra is null ? "" : extra + ",")}}
            "modules":{{body}}}
            """;
            return Encoding.UTF8.GetBytes(json);
            }

    // ---------------------------------------------------------------------------------------------
    // The Oracle schema half. Same shape of refusals, plus the two that are specific to it: the
    // response must cover exactly the schemas that were allowed, and the canonical DDL must separate
    // into the halves this host stores, or nothing is kept.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_extracted_schema_is_admitted_and_its_statements_are_split_where_the_worker_split_them()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.True(admission.Admitted, admission.Reason);
        Assert.Equal(artifact, admission.Artifact);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifact)), admission.SnapshotHash);

        SourceGatewaySchemaProjection projection = admission.Projection!;
        Assert.Equal(["LEGACY_LAB"], projection.Schemas);
        Assert.Contains("CREATE TABLE", projection.SchemaDdl, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE", projection.SchemaDdl, StringComparison.Ordinal);
        Assert.StartsWith(SourceGatewayProtocol.ProgramUnitSectionMarker, projection.ProgramUnitSql!, StringComparison.Ordinal);
        Assert.Equal(4, projection.Tables);
        Assert.Equal(14, projection.Columns);
        Assert.Equal(2, projection.ProgramUnits);

        // The PL/SQL carries a lone '/' line and a whole CREATE TABLE inside a literal and a comment. It
        // was read past rather than counted, which is the difference between a parser and a keyword scan.
        Assert.Contains("SHADOW", projection.ProgramUnitSql!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SchemaFlaw.InventoryOmitsATable, "does not add up")]
    [InlineData(SchemaFlaw.InventoryRepeatsAnObject, "lists the same object twice")]
    [InlineData(SchemaFlaw.DdlOmitsATable, "CREATE TABLE statements its own inventory")]
    [InlineData(SchemaFlaw.DdlRepeatsATable, "writes the same object twice")]
    [InlineData(SchemaFlaw.SpoofedTableInsideProgramBody, "CREATE TABLE statements its own inventory")]
    [InlineData(SchemaFlaw.CoverageOverCeiling, "ceiling")]
    [InlineData(SchemaFlaw.TruncatedDdl, "unterminated statement")]
    [InlineData(SchemaFlaw.GrantOnUnlistedObject, "grants a privilege on an object")]
    [InlineData(SchemaFlaw.IndexNotInInventory, "CREATE INDEX its own inventory does not name")]
    [InlineData(SchemaFlaw.ForeignOwner, "not one this build's own emitter writes")]
    public void A_schema_artifact_whose_statements_are_not_the_inventory_it_declares_is_refused(
        SchemaFlaw flaw,
        string expected)
    {
        // Every one of these hashes correctly, names the allowed schema, and carries a locatable program
        // unit boundary. Before the statements were reconciled against the inventory, all of them were
        // admitted and became a trusted prepared schema. The expected fragment is asserted so a refusal
        // cannot pass this test for an unrelated reason an earlier check happened to produce.
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request, flaw: flaw);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.False(admission.Admitted, admission.Reason);
        Assert.Null(admission.Artifact);
        Assert.Null(admission.Projection);
        Assert.Equal(SourceGatewayStatus.Rejected, admission.Reported);
        Assert.Contains(expected, admission.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_artifact_with_no_program_unit_is_admitted_with_no_program_unit_file()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request, programUnits: 0, includeProgramUnitSection: false);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.True(admission.Admitted, admission.Reason);
        Assert.Null(admission.Projection!.ProgramUnitSql);
        Assert.Equal(0, admission.Projection.ProgramUnits);
    }

    [Theory]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\")", true)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\") ON DELETE SET NULL DISABLE", true)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"UNAPPROVED\".\"CUSTOMERS\" (\"CUSTOMER_ID\")", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"UNLISTED\" (\"CUSTOMER_ID\")", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\"", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\") ON DELETE", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\") ON DELETE SET", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\", \"OTHER\")", false)]
    [InlineData("FOREIGN KEY (\"CUSTOMER_ID\" GARBAGE) REFERENCES \"LEGACY_LAB\".\"CUSTOMERS\" (\"CUSTOMER_ID\")", false)]
    [InlineData("PRIMARY KEY (\"CUSTOMER_ID\") DISABLE", true)]
    [InlineData("PRIMARY", false)]
    [InlineData("PRIMARY KEY (\"CUSTOMER_ID\") GARBAGE", false)]
    [InlineData("UNIQUE (\"CUSTOMER_ID\")", true)]
    [InlineData("UNIQUE ()", false)]
    [InlineData("CHECK (\"CUSTOMER_ID\" > 0)", true)]
    [InlineData("CHECK (\"STATUS\" <> q'[REFERENCES \"UNAPPROVED\".\"TABLE\"; ()]') DISABLE", true)]
    [InlineData("CHECK", false)]
    [InlineData("CHECK ()", false)]
    [InlineData("CHECK (/* no condition */)", false)]
    [InlineData("CHECK (\"CUSTOMER_ID\" > 0) GARBAGE", false)]
    public void Constraint_admission_requires_a_complete_definition_and_an_inventoried_reference(
        string definition, bool admitted)
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request, constraintDefinition: definition);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.True(admission.Admitted == admitted, admission.Reason);
        if (!admitted)
        {
            Assert.Equal(SourceGatewayStatus.Rejected, admission.Reported);
            Assert.Null(admission.Artifact);
            Assert.Null(admission.Projection);
        }
    }

    [Theory]
    [InlineData("program-units-without-a-section")]
    [InlineData("a-section-without-program-units")]
    public void A_schema_artifact_whose_coverage_does_not_describe_it_is_refused(string flaw)
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = flaw == "program-units-without-a-section"
            ? SchemaArtifact(request, programUnits: 2, includeProgramUnitSection: false)
            : SchemaArtifact(request, programUnits: 0, includeProgramUnitSection: true);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Null(admission.Projection);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("version")]
    [InlineData("hash")]
    public void A_schema_response_bound_to_another_source_profile_is_refused(string drift)
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        SourceGatewayOracleSchemaResponse response = SchemaResponse(request, SchemaArtifact(request));
        response = drift switch
        {
            "environment" => response with { SourceEnvironmentId = "another-source" },
            "version" => response with { ProfileVersion = response.ProfileVersion + 1 },
            _ => response with { ProfileHash = new string('a', 64) },
        };

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("not correlated", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schema_artifact_covering_schemas_the_request_did_not_allow_is_refused()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request, schemas: ["LEGACY_LAB", "SYS"]);

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("different set of schemas", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schema_artifact_that_claims_normalization_is_refused()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request, extra: "\"normalized\":true");

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(
            request, SchemaResponse(request, artifact), s_now);

        Assert.False(admission.Admitted);
        Assert.Contains("normalization phase", admission.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("declared-digest")]
    [InlineData("snapshot-digest")]
    [InlineData("media-type")]
    [InlineData("not-base64")]
    [InlineData("stale-clock")]
    [InlineData("unknown-status")]
    [InlineData("refusal-with-bytes")]
    [InlineData("credential-finding")]
    public void A_schema_response_this_host_cannot_verify_keeps_nothing(string flaw)
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        byte[] artifact = SchemaArtifact(request);
        SourceGatewayOracleSchemaResponse response = SchemaResponse(request, artifact);

        response = flaw switch
        {
            "declared-digest" => response with { SchemaArtifact = response.SchemaArtifact! with { Sha256 = new string('d', 64) } },
            "snapshot-digest" => response with { SnapshotHash = new string('e', 64) },
            "media-type" => response with { SchemaArtifact = response.SchemaArtifact! with { MediaType = "application/json" } },
            "not-base64" => response with { SchemaArtifact = response.SchemaArtifact! with { Base64 = "not base64 at all" } },
            "stale-clock" => response with { ExtractedUtc = s_now - TimeSpan.FromHours(2) },
            "unknown-status" => response with { Status = "Fine" },
            "refusal-with-bytes" => response with { Status = SourceGatewayStatus.BlockedPrerequisite },
            _ => response with { Findings = ["connected with password=hunter2"] },
        };

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Null(admission.Artifact);
        Assert.Null(admission.Projection);
    }

    [Fact]
    public void A_blocked_schema_answer_with_no_bytes_is_reported_as_blocked_rather_than_refused()
    {
        SourceGatewayOracleSchemaRequest request = SchemaRequest();
        SourceGatewayOracleSchemaResponse response = SchemaResponse(request, null) with
        {
            Status = SourceGatewayStatus.BlockedPrerequisite,
            SnapshotHash = null,
            Findings = ["This source environment registers no Oracle connection."],
        };

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(request, response, s_now);

        Assert.False(admission.Admitted);
        Assert.Equal(SourceGatewayStatus.BlockedPrerequisite, admission.Reported);
        Assert.Contains("no Oracle connection", admission.Reason, StringComparison.Ordinal);
    }

    private static SourceGatewayOracleSchemaRequest SchemaRequest() => new(
        SourceGatewayProtocol.SchemaVersion, SourceEnvironmentId, 4, ProfileHash, ["LEGACY_LAB"], s_scope);

    private static SourceGatewayOracleSchemaResponse SchemaResponse(
        SourceGatewayOracleSchemaRequest request,
        byte[]? artifact) => new(
        SourceGatewayProtocol.SchemaVersion,
        SourceGatewayStatus.Extracted,
        request.SourceEnvironmentId,
        request.ProfileVersion,
        request.ProfileHash,
        s_now - TimeSpan.FromSeconds(4),
        artifact is null ? null : Convert.ToHexStringLower(SHA256.HashData(artifact)),
        artifact is null
            ? null
            : new SourceGatewayArtifact(
                SourceGatewayProtocol.OracleSchemaMediaType,
                Convert.ToHexStringLower(SHA256.HashData(artifact)),
                Convert.ToBase64String(artifact)),
        [],
        [],
        request.Scope);

    /// <summary>Builds a schema artifact by hand, so a test can write a field the worker never writes.</summary>
    /// <summary>
    /// Builds a schema artifact the way the worker's emitter actually writes one, so a test can then
    /// break exactly one thing about it. The shape is the MERIDIAN estate: four tables, fourteen columns,
    /// ten written constraints over eleven catalog constraints (Oracle stores a NOT NULL column as a check
    /// the emitter refuses to repeat), five key-backed indexes that carry no CREATE INDEX of their own,
    /// one sequence, one grant and two program units.
    /// </summary>
    private static byte[] SchemaArtifact(
        SourceGatewayOracleSchemaRequest request,
        string[]? schemas = null,
        int programUnits = 2,
        bool includeProgramUnitSection = true,
        string? extra = null,
        SchemaFlaw flaw = SchemaFlaw.None,
        string? constraintDefinition = null)
    {
        string[] covered = schemas ?? [.. request.SchemaAllowlist];
        string owner = covered[0];

        (string Table, string[] Columns)[] tables =
        [
            ("CUSTOMERS", ["CUSTOMER_ID", "CUSTOMER_NAME", "CREDIT_LIMIT"]),
            ("ORDERS", ["ORDER_ID", "CUSTOMER_ID", "ORDER_DATE", "STATUS"]),
            ("ORDER_ITEMS", ["ORDER_ID", "PRODUCT_ID", "QUANTITY", "UNIT_PRICE"]),
            ("PRODUCTS", ["PRODUCT_ID", "PRODUCT_NAME", "UNIT_PRICE"]),
        ];
        string[] keyIndexes = ["PK_CUSTOMERS", "PK_ORDERS", "PK_ORDER_ITEMS", "PK_PRODUCTS", "UQ_PRODUCTS_NAME"];
        (string Kind, string Name)[] units = [("PROCEDURE", "PLACE_ORDER"), ("FUNCTION", "ORDER_TOTAL")];

        string ddl =
            $"CREATE SEQUENCE \"{owner}\".\"SEQ_ORDER_ID\" START WITH 1041 INCREMENT BY 1 " +
            "MINVALUE 1 MAXVALUE 999999 CACHE 20 NOCYCLE NOORDER;\n\n";

        foreach ((string table, string[] columns) in tables)
        {
            if (flaw is SchemaFlaw.DdlOmitsATable && table == "ORDERS")
            {
                continue;
            }

            string declared = flaw is SchemaFlaw.ForeignOwner && table == "PRODUCTS" ? "OTHER_LAB" : owner;
            string body = string.Join(",\n", columns.Select(column => $"    \"{column}\" NUMBER(10,0) NOT NULL"));
            string statement = $"CREATE TABLE \"{declared}\".\"{table}\" (\n{body}\n);\n\n";
            ddl += flaw is SchemaFlaw.DdlRepeatsATable && table == "ORDERS" ? statement + statement : statement;
        }

        // A check condition carrying a quoted semicolon and a newline is the canonical case a naive
        // splitter gets wrong, so the good fixture contains one rather than a test for it alone.
        string[] constraints =
        [
            $"\"CUSTOMERS\" ADD CONSTRAINT \"PK_CUSTOMERS\" PRIMARY KEY (\"CUSTOMER_ID\")",
            $"\"ORDERS\" ADD CONSTRAINT \"CK_ORDERS_STATUS\" CHECK (\"STATUS\" IN ('NEW', 'SHIPPED;\nDONE'))",
            $"\"ORDERS\" ADD CONSTRAINT \"FK_ORDERS_CUSTOMER\" FOREIGN KEY (\"CUSTOMER_ID\") REFERENCES \"{owner}\".\"CUSTOMERS\" (\"CUSTOMER_ID\")",
            $"\"ORDERS\" ADD CONSTRAINT \"PK_ORDERS\" PRIMARY KEY (\"ORDER_ID\")",
            $"\"ORDER_ITEMS\" ADD CONSTRAINT \"CK_ORDER_ITEMS_QTY\" CHECK (\"QUANTITY\" > 0)",
            $"\"ORDER_ITEMS\" ADD CONSTRAINT \"FK_ORDER_ITEMS_ORDER\" FOREIGN KEY (\"ORDER_ID\") REFERENCES \"{owner}\".\"ORDERS\" (\"ORDER_ID\") ON DELETE CASCADE",
            $"\"ORDER_ITEMS\" ADD CONSTRAINT \"FK_ORDER_ITEMS_PRODUCT\" FOREIGN KEY (\"PRODUCT_ID\") REFERENCES \"{owner}\".\"PRODUCTS\" (\"PRODUCT_ID\")",
            $"\"ORDER_ITEMS\" ADD CONSTRAINT \"PK_ORDER_ITEMS\" PRIMARY KEY (\"ORDER_ID\", \"PRODUCT_ID\")",
            $"\"PRODUCTS\" ADD CONSTRAINT \"PK_PRODUCTS\" PRIMARY KEY (\"PRODUCT_ID\")",
            $"\"PRODUCTS\" ADD CONSTRAINT \"UQ_PRODUCTS_NAME\" UNIQUE (\"PRODUCT_NAME\")",
        ];

        if (constraintDefinition is not null)
        {
            constraints[2] = $"\"ORDERS\" ADD CONSTRAINT \"FK_ORDERS_CUSTOMER\" {constraintDefinition}";
        }

        foreach (string constraint in constraints)
        {
            ddl += $"ALTER TABLE \"{owner}\".{constraint};\n\n";
        }

        if (flaw is SchemaFlaw.IndexNotInInventory)
        {
            ddl += $"CREATE INDEX \"{owner}\".\"IX_ORDERS_STATUS\" ON \"{owner}\".\"ORDERS\" (\"STATUS\");\n\n";
        }

        string grantTarget = flaw is SchemaFlaw.GrantOnUnlistedObject ? "SHADOW" : "ORDERS";
        List<string> grants = [$"GRANT SELECT ON \"{owner}\".\"{grantTarget}\" TO \"OFM_APP\""];
        if (programUnits > 0)
        {
            // A grant on a program unit only reconciles while that unit is in the inventory.
            grants.Add($"GRANT EXECUTE ON \"{owner}\".\"PLACE_ORDER\" TO PUBLIC");
        }

        foreach (string grant in grants)
        {
            ddl += $"{grant};\n\n";
        }

        if (flaw is SchemaFlaw.TruncatedDdl)
        {
            ddl += $"CREATE TABLE \"{owner}\".\"SHIPMENTS\" (\n    \"SHIPMENT_ID\" NUMBER(10,0) NOT NULL\n)\n\n";
        }

        if (includeProgramUnitSection)
        {
            // Both bodies hide a lone '/' line and a whole CREATE statement inside a literal or a comment.
            // The host must read past all of it and count neither.
            ddl +=
                $"{SourceGatewayProtocol.ProgramUnitSectionMarker}\"{owner}\";\n\n" +
                "CREATE OR REPLACE PROCEDURE PLACE_ORDER(p_order_id OUT NUMBER) IS\n" +
                $"  -- CREATE TABLE \"{owner}\".\"SHADOW\" (\"X\" NUMBER(10,0)); is a comment, not a statement\n" +
                "  v_note VARCHAR2(200) := 'END;\n" +
                "/\n" +
                $"CREATE TABLE \"{owner}\".\"SHADOW\" (\"X\" NUMBER(10,0));';\n" +
                "BEGIN\n" +
                "  SELECT SEQ_ORDER_ID.NEXTVAL INTO p_order_id FROM DUAL;\n" +
                "END PLACE_ORDER;\n/\n\n" +
                "CREATE OR REPLACE FUNCTION ORDER_TOTAL(p_order_id IN NUMBER) RETURN NUMBER IS\n" +
                $"  v_sql VARCHAR2(400) := q'{{GRANT SELECT ON \"{owner}\".\"SHADOW\" TO PUBLIC;\n" +
                "/\n" +
                "}';\n" +
                "BEGIN\n" +
                "  RETURN 0;\n" +
                "END ORDER_TOTAL;\n/\n\n";
        }

        List<object> objects =
        [
            .. tables.Where(entry => flaw is not SchemaFlaw.InventoryOmitsATable || entry.Table != "PRODUCTS")
                .Select(entry => new { schema = owner, kind = "TABLE", name = entry.Table }),
            .. keyIndexes.Select(name => new { schema = owner, kind = "INDEX", name }),
            new { schema = owner, kind = "SEQUENCE", name = "SEQ_ORDER_ID" },
            .. units.Take(programUnits).Select(entry => new { schema = owner, kind = entry.Kind, name = entry.Name }),
        ];

        int declaredTables = tables.Length;
        if (flaw is SchemaFlaw.InventoryRepeatsAnObject)
        {
            objects.Add(new { schema = owner, kind = "TABLE", name = "CUSTOMERS" });
            declaredTables++;
        }
        else if (flaw is SchemaFlaw.SpoofedTableInsideProgramBody)
        {
            objects.Add(new { schema = owner, kind = "TABLE", name = "SHADOW" });
            declaredTables++;
        }

        int columnCount = flaw is SchemaFlaw.CoverageOverCeiling
            ? SourceGatewayProtocol.MaxSchemaObjects + 1
            : tables.Sum(entry => entry.Columns.Length);

        string coverage = JsonSerializer.Serialize(new
        {
            objects = objects.Count,
            tables = declaredTables,
            columns = columnCount,
            constraints = constraints.Length + 1,
            sequences = 1,
            indexes = keyIndexes.Length,
            grants = grants.Count,
            programUnits,
        });

        string json = $$"""
            {"generator":{{JsonSerializer.Serialize(SourceGatewayProtocol.ExtractedSchemaGenerator)}},
            "schemaVersion":"{{SourceGatewayProtocol.ExtractedSchemaBodyVersion}}",
            "sourceEnvironmentId":{{JsonSerializer.Serialize(request.SourceEnvironmentId)}},
            "profileVersion":{{request.ProfileVersion}},
            "profileHash":{{JsonSerializer.Serialize(request.ProfileHash)}},
            "schemas":{{JsonSerializer.Serialize(covered)}},
            "provider":"odbc-oracle",
            "coverage":{{coverage}},
            "objects":{{JsonSerializer.Serialize(objects)}},{{(extra is null ? "" : extra + ",")}}
            "ddl":{{JsonSerializer.Serialize(ddl)}}}
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>One way a schema artifact can describe something other than the DDL it carries.</summary>
    public enum SchemaFlaw
    {
        None,
        InventoryOmitsATable,
        InventoryRepeatsAnObject,
        DdlOmitsATable,
        DdlRepeatsATable,
        SpoofedTableInsideProgramBody,
        CoverageOverCeiling,
        TruncatedDdl,
        GrantOnUnlistedObject,
        IndexNotInInventory,
        ForeignOwner,
    }
}
