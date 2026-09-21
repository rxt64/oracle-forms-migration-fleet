// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class SourceEnvironmentProbeTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 21, 18, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-source-profile-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Probe_without_forms_tooling_reports_a_machine_readable_blocked_prerequisite()
    {
        SourceEnvironmentProfile profile = Profile();
        UnavailableSourceEnvironmentProbe probe = new(() => s_now);

        SourceEnvironmentProbeResult first = await probe.ProbeAsync(profile, CancellationToken.None);
        SourceEnvironmentProbeResult second = await probe.ProbeAsync(profile, CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.BlockedPrerequisite, first.Status);
        Assert.Contains(SourcePrerequisite.OracleFormsInstallation, first.BlockedPrerequisites);
        Assert.Contains(SourcePrerequisite.WorkerHostArchitecture, first.BlockedPrerequisites);
        SourceCapabilityResult forms = Assert.Single(first.Capabilities, capability => capability.Id == "forms.module.extract");
        Assert.Equal("6.0.8.22.1", forms.RequiredRelease);
        Assert.Equal("x86", forms.RequiredArchitecture);
        Assert.Equal("WindowsWorker", forms.RequiredHost);
        SourceCapabilityResult architecture = Assert.Single(first.Capabilities, capability => capability.Id == "forms.worker.architecture");
        Assert.Equal(SourcePrerequisite.WorkerHostArchitecture, architecture.Prerequisite);
        Assert.Equal("x86", architecture.RequiredArchitecture);
        Assert.Equal("WindowsWorker", architecture.RequiredHost);
        Assert.Null(first.Observed.Forms);
        Assert.Null(first.Observed.Database);
        Assert.Null(profile.LastVerifiedUtc);
        Assert.Equal(Serialize(first), Serialize(second));
    }

    [Theory]
    [InlineData("C:\\forms")]
    [InlineData("../forms")]
    [InlineData("//server/share")]
    [InlineData("forms/source")]
    public void Path_alias_rejects_a_rooted_or_traversing_value(string pathAlias)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Profile(pathAlias: pathAlias));

        Assert.Contains("server-safe alias", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secret_reference_stores_a_name_and_never_a_value()
    {
        SourceEnvironmentProfile valid = Profile(secretReferences: ["oracle-source-credential"]);
        Assert.Contains("oracle-source-credential", valid.SecretReferences);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            Profile(secretReferences: ["password=hunter2"]));

        Assert.Contains("credential material", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_hash_is_stable_for_reordered_sets_and_changes_for_meaningful_configuration()
    {
        SourceEnvironmentProfile first = Profile(
            schemaAllowlist: ["PRODUCTS", "LEGACY_LAB"],
            secretReferences: ["oracle-source-credential", "forms-media-reference"]);
        SourceEnvironmentProfile reordered = Profile(
            schemaAllowlist: ["LEGACY_LAB", "PRODUCTS"],
            secretReferences: ["forms-media-reference", "oracle-source-credential"]);
        SourceEnvironmentProfile changed = Profile(pathAlias: "legacy-order-entry-v2");

        Assert.Equal(first.CanonicalHash, reordered.CanonicalHash);
        Assert.NotEqual(first.CanonicalHash, changed.CanonicalHash);
        SourceEnvironmentProfile duplicates = Profile(
            schemaAllowlist: ["LEGACY_LAB", "LEGACY_LAB"],
            secretReferences: ["oracle-source-credential", "oracle-source-credential"]);
        Assert.Equal(Profile().DeclarationHash, duplicates.DeclarationHash);
    }

    [Fact]
    public async Task Unchanged_declaration_reuses_version_and_probe_records_a_new_immutable_version()
    {
        FilePlatformStateStore store = new(Path.Combine(_root, "platform.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = new(store, sandbox: null, () => s_now);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            "tenant-a", "11111111-1111-1111-1111-111111111111", [WorkbenchRoles.MigrationOperator]);
        PlatformProject project = (await service.CreateProjectAsync(actor, "Legacy pilot", CancellationToken.None)).Value!;
        SourceEnvironmentDeclaration declaration = Declaration();

        SourceEnvironmentProfile first = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, declaration, CancellationToken.None)).Value!;
        SourceEnvironmentProfile same = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, declaration, CancellationToken.None)).Value!;
        PlatformResult<SourceEnvironmentProbeResult> probe = await service.ProbeSourceEnvironmentAsync(
            actor,
            project.ProjectId,
            declaration.SourceEnvironmentId,
            new UnavailableSourceEnvironmentProbe(() => s_now),
            CancellationToken.None);
        SourceEnvironmentProfile observed = (await store.GetSourceEnvironmentProfileAsync(
            actor.TenantId, project.ProjectId, declaration.SourceEnvironmentId, version: null, CancellationToken.None))!;

        Assert.Equal(1, first.Version);
        Assert.Equal(first.CanonicalHash, same.CanonicalHash);
        Assert.True(probe.Succeeded);
        Assert.Equal(2, observed.Version);
        Assert.Equal(SourceEnvironmentReadiness.BlockedPrerequisite, observed.Readiness);
        Assert.Null(observed.LastVerifiedUtc);
        Assert.NotEqual(first.CanonicalHash, observed.CanonicalHash);
        Assert.Equal(2, probe.Value!.ProfileVersion);
        Assert.Equal(observed.CanonicalHash, probe.Value.ProfileHash);

        PlatformResult<SourceEnvironmentProbeResult> repeated = await service.ProbeSourceEnvironmentAsync(
            actor,
            project.ProjectId,
            declaration.SourceEnvironmentId,
            new UnavailableSourceEnvironmentProbe(() => s_now),
            CancellationToken.None);
        Assert.True(repeated.Succeeded);
        Assert.Equal(2, (await store.GetSourceEnvironmentProfileAsync(
            actor.TenantId, project.ProjectId, declaration.SourceEnvironmentId, version: null, CancellationToken.None))!.Version);
    }

    [Fact]
    public async Task Meaningful_declaration_change_creates_one_new_version_and_then_stabilizes()
    {
        FilePlatformStateStore store = new(Path.Combine(_root, "platform.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = new(store, sandbox: null, () => s_now);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            "tenant-a", "11111111-1111-1111-1111-111111111111", [WorkbenchRoles.MigrationOperator]);
        PlatformProject project = (await service.CreateProjectAsync(actor, "Legacy pilot", CancellationToken.None)).Value!;

        SourceEnvironmentProfile first = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, Declaration(), CancellationToken.None)).Value!;
        SourceEnvironmentDeclaration changed = Declaration() with { PathAlias = "legacy-order-entry-v2" };
        SourceEnvironmentProfile second = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, changed, CancellationToken.None)).Value!;
        SourceEnvironmentProfile stable = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, changed, CancellationToken.None)).Value!;

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(second.CanonicalHash, stable.CanonicalHash);
    }

    [Fact]
    public void Probe_verified_without_observations_or_with_blockers_is_rejected()
    {
        SourceEnvironmentProfile profile = Profile();
        SourceEnvironmentProbeResult result = ProbeResult(profile) with
        {
            Status = SourceEnvironmentProbeStatus.Verified,
            Observed = new("6i", "9i"),
            BlockedPrerequisites = [SourcePrerequisite.OracleFormsInstallation],
        };

        Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.RecordProbe(profile, result, s_now));
    }

    [Theory]
    [InlineData("password=hunter2", "9i")]
    [InlineData("banana", "9i")]
    public void Probe_observations_must_be_non_secret_catalogued_versions(string forms, string database)
    {
        SourceEnvironmentProfile profile = Profile();
        SourceEnvironmentProbeResult result = ProbeResult(profile) with
        {
            Observed = new(forms, database),
        };

        Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.RecordProbe(profile, result, s_now));
    }

    [Fact]
    public void Rejected_probe_stays_rejected_and_observation_changes_full_identity_only()
    {
        SourceEnvironmentProfile profile = Profile();
        SourceEnvironmentProfile rejected = SourceEnvironmentProfiles.RecordProbe(
            profile,
            ProbeResult(profile) with
            {
                Status = SourceEnvironmentProbeStatus.Rejected,
                BlockedPrerequisites = [],
                Contradictions = ["The worker reported an unrecognized native build."],
                Capabilities = [new("forms.module.extract", SourceEnvironmentProbeStatus.Rejected,
                    SourcePrerequisite.OracleFormsInstallation, "6.0.8.22.1", "x86", "WindowsWorker", "operator.install.forms.6i.worker")],
            },
            s_now);

        Assert.Equal(SourceEnvironmentReadiness.Rejected, rejected.Readiness);
        Assert.Equal(profile.DeclarationHash, rejected.DeclarationHash);
        Assert.NotEqual(profile.CanonicalHash, rejected.CanonicalHash);
        Assert.NotEqual(profile.CanonicalHash, SourceEnvironmentProfiles.Hash(profile with { CreatedUtc = s_now.AddMinutes(1) }));
    }

    [Fact]
    public void Probe_worker_text_is_bounded_secret_screened_and_uses_stable_identifiers()
    {
        SourceEnvironmentProfile profile = Profile();
        SourceEnvironmentProbeResult secret = ProbeResult(profile) with
        {
            Contradictions = ["password=hunter2"],
            Status = SourceEnvironmentProbeStatus.Contradicted,
            BlockedPrerequisites = [],
        };
        SourceEnvironmentProbeResult invalidId = ProbeResult(profile) with
        {
            Capabilities = [new("forms module extract", SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.OracleFormsInstallation, null, null, null, "run shell now")],
        };

        Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.RecordProbe(profile, secret, s_now));
        Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.RecordProbe(profile, invalidId, s_now));
    }

    [Fact]
    public async Task Malformed_probe_contract_returns_a_typed_bad_request()
    {
        FilePlatformStateStore store = new(Path.Combine(_root, "malformed.json"));
        await store.InitializeAsync(CancellationToken.None);
        PlatformAccessService service = new(store, sandbox: null, () => s_now);
        WorkbenchActor actor = WorkbenchActor.ForTenant(
            "tenant-a", "11111111-1111-1111-1111-111111111111", [WorkbenchRoles.MigrationOperator]);
        PlatformProject project = (await service.CreateProjectAsync(actor, "Malformed probe", CancellationToken.None)).Value!;
        SourceEnvironmentProfile profile = (await service.EnsureSourceEnvironmentProfileAsync(
            actor, project.ProjectId, Declaration(), CancellationToken.None)).Value!;
        ISourceEnvironmentProbe malformed = new StubProbe(ProbeResult(profile) with
        {
            Status = SourceEnvironmentProbeStatus.Verified,
            BlockedPrerequisites = [],
        });

        PlatformResult<SourceEnvironmentProbeResult> result = await service.ProbeSourceEnvironmentAsync(
            actor, project.ProjectId, profile.SourceEnvironmentId, malformed, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Status);
    }

    [Fact]
    public void Profile_inputs_are_bounded()
    {
        ArgumentException name = Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.Create(
            "tenant-a", "project-a", "legacy-order-entry", 1, new string('x', 121),
            SourceConnector.FormsBuilderWorker, "6i", "9i", "legacy-order-entry", ["LEGACY_LAB"], [], s_now));
        ArgumentException schemas = Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.Create(
            "tenant-a", "project-a", "legacy-order-entry", 1, "Legacy", SourceConnector.FormsBuilderWorker,
            "6i", "9i", "legacy-order-entry", Enumerable.Range(0, 33).Select(index => $"S{index}").ToArray(), [], s_now));

        Assert.Contains("unsupported", name.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("32", schemas.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SourceEnvironmentProfiles.Create(
            "tenant-a", "project-a", "legacy-order-entry", 1, "scott/tiger@ORCL",
            SourceConnector.FormsBuilderWorker, "6i", "9i", "legacy-order-entry", ["LEGACY_LAB"], [], s_now));
    }

    [Fact]
    public void Web_assembly_has_no_native_oracle_dependency_or_pinvoke_entrypoint()
    {
        System.Reflection.Assembly assembly = typeof(UnavailableSourceEnvironmentProbe).Assembly;

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.Contains("ndapi", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("Oracle.ManagedDataAccess", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("Oracle.DataAccess", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            assembly.GetTypes().SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)),
            method => method.GetCustomAttributes(typeof(DllImportAttribute), inherit: false).Length > 0);
    }

    private static SourceEnvironmentProfile Profile(
        string pathAlias = "legacy-order-entry",
        IReadOnlyList<string>? schemaAllowlist = null,
        IReadOnlyList<string>? secretReferences = null) =>
        SourceEnvironmentProfiles.Create(
            tenantId: "tenant-a",
            projectId: "project-a",
            sourceEnvironmentId: "legacy-order-entry",
            version: 1,
            name: "Legacy Order Entry",
            connector: SourceConnector.FormsBuilderWorker,
            expectedFormsVersion: "6i",
            expectedDatabaseVersion: "9i",
            pathAlias: pathAlias,
            schemaAllowlist: schemaAllowlist ?? ["LEGACY_LAB"],
            secretReferences: secretReferences ?? ["oracle-source-credential"],
            createdUtc: s_now);

    private static SourceEnvironmentDeclaration Declaration() => new(
        "legacy-order-entry",
        "Legacy Order Entry",
        SourceConnector.FormsBuilderWorker,
        "6i",
        "9i",
        "legacy-order-entry",
        ["LEGACY_LAB"],
        ["oracle-source-credential"]);

    private static SourceEnvironmentProbeResult ProbeResult(SourceEnvironmentProfile profile) => new(
        1,
        profile.SourceEnvironmentId,
        profile.Version,
        profile.CanonicalHash,
        SourceEnvironmentProbeStatus.BlockedPrerequisite,
        s_now,
        profile.Connector,
        new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
        new(null, null),
        [new("forms.module.extract", SourceEnvironmentProbeStatus.BlockedPrerequisite,
            SourcePrerequisite.OracleFormsInstallation, "6.0.8.22.1", "x86", "WindowsWorker", "operator.install.forms.6i.worker")],
        [SourcePrerequisite.OracleFormsInstallation],
        []);

    private sealed class StubProbe(SourceEnvironmentProbeResult result) : ISourceEnvironmentProbe
    {
        public string Description => "Test probe";
        public Task<SourceEnvironmentProbeResult> ProbeAsync(
            SourceEnvironmentProfile profile,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private static string Serialize(SourceEnvironmentProbeResult value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    });
}