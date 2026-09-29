using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// Pins the native binding's shape against the Forms 6i SDK actually installed on the source VM: the exact
/// exported names, the exact header constants, and the exact argument counts of the delegates those names
/// are bound to.
///
/// None of these tests load <c>ifd2f60.dll</c> or call into it. They can only prove that this binding still
/// describes the SDK the headers describe; they prove nothing about whether a module can be read.
/// </summary>
public sealed class SourceWorkerNativeAbiTests
{
    private static readonly Assembly s_worker = typeof(WorkerConfiguration).Assembly;

    private static Type Delegate(string name) =>
        s_worker.GetType($"OracleFormsMigrationFleet.SourceWorker.D2FEntryPoints+{name}", throwOnError: true)!;

    private static ParameterInfo[] Parameters(string delegateName) =>
        Delegate(delegateName).GetMethod("Invoke")!.GetParameters();

    [Fact]
    public void The_module_load_entry_point_takes_the_four_arguments_the_installed_header_declares()
    {
        // d2ffmdld_Load(d2fctx *, d2ffmd **, text *formname, boolean db). A fifth argument would mean this
        // binding had invented a connect string parameter the SDK does not have.
        ParameterInfo[] parameters = Parameters("LoadModuleDelegate");

        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(nint), parameters[0].ParameterType);
        Assert.Equal(typeof(nint).MakeByRefType(), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOut);
        Assert.Equal(typeof(string), parameters[2].ParameterType);
        Assert.Equal(typeof(int), parameters[3].ParameterType);
        Assert.False(parameters[3].IsOut);
    }

    [Theory]
    [InlineData("GetTextPropertyDelegate", typeof(nint))]
    [InlineData("GetNumberPropertyDelegate", typeof(uint))]
    [InlineData("GetBooleanPropertyDelegate", typeof(int))]
    [InlineData("GetObjectPropertyDelegate", typeof(nint))]
    public void Every_property_getter_takes_a_context_an_object_a_ub2_property_and_one_out_parameter(
        string delegateName,
        Type outType)
    {
        ParameterInfo[] parameters = Parameters(delegateName);

        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(nint), parameters[0].ParameterType);
        Assert.Equal(typeof(nint), parameters[1].ParameterType);
        Assert.Equal(typeof(ushort), parameters[2].ParameterType);
        Assert.Equal(outType.MakeByRefType(), parameters[3].ParameterType);
        Assert.True(parameters[3].IsOut);
    }

    [Fact]
    public void The_context_lifecycle_entry_points_keep_their_declared_arity()
    {
        Assert.Equal(2, Parameters("CreateContextDelegate").Length);
        Assert.Single(Parameters("DestroyContextDelegate"));
        Assert.Equal(2, Parameters("DestroyModuleDelegate").Length);
    }

    [Theory]
    [InlineData("CreateContextDelegate")]
    [InlineData("DestroyContextDelegate")]
    [InlineData("LoadModuleDelegate")]
    [InlineData("DestroyModuleDelegate")]
    [InlineData("GetTextPropertyDelegate")]
    [InlineData("GetNumberPropertyDelegate")]
    [InlineData("GetBooleanPropertyDelegate")]
    [InlineData("GetObjectPropertyDelegate")]
    public void Every_entry_point_is_bound_as_cdecl(string delegateName)
    {
        UnmanagedFunctionPointerAttribute? convention =
            Delegate(delegateName).GetCustomAttribute<UnmanagedFunctionPointerAttribute>();

        Assert.NotNull(convention);
        Assert.Equal(CallingConvention.Cdecl, convention!.CallingConvention);
    }
}

/// <summary>
/// The golden token fixture. The names below were read off the installed SDK; if this binding ever binds a
/// different spelling, that is a change to what it claims the estate exports, and it has to be made here.
/// </summary>
public sealed class SourceWorkerSdkTokenTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ofm-sdk-tokens-").FullName;

    /// <summary>The header constants this binding requires, paired with numbers the fixture invents.</summary>
    private static readonly (string Constant, uint Code)[] s_verifiedConstants =
    [
        ("D2FP_NAME", 300),
        ("D2FP_TITLE", 474),
        ("D2FP_NEXT", 700),
        ("D2FP_BLOCK", 701),
        ("D2FP_ITEM", 702),
        ("D2FP_TRIGGER", 703),
        ("D2FP_PROG_UNIT", 704),
        ("D2FP_LOV", 705),
        ("D2FP_TRG_TXT", 489),
        ("D2FP_ITM_TYP", 231),
        ("D2FP_DAT_TYP", 82),
        ("D2FP_COL_NAM", 62),
        ("D2FP_PRMPT", 355),
        ("D2FP_REQUIRED", 408),
        ("D2FP_VISIBLE", 515),
        ("D2FP_MAX_LEN", 273),
        ("D2FP_RECS_DISP_COUNT", 706),
        ("D2FP_QRY_DAT_SRC_NAM", 707),
    ];

    private FormsApiDefinitions Header(IEnumerable<(string Constant, uint Code)> defines)
    {
        string path = Path.Combine(_root, "D2FDEF.H");
        File.WriteAllLines(path, defines.Select(define => $"#define {define.Constant} {define.Code}"));
        return FormsApiDefinitions.Load(path, new string('a', 64))!;
    }

    [Fact]
    public void The_binding_requires_exactly_the_exports_the_installed_library_declares()
    {
        Assert.Equal(
            [
                "d2fctxcr_Create",
                "d2fctxde_Destroy",
                "d2ffmdld_Load",
                "d2ffmdde_Destroy",
                "d2fobgt_GetTextProp",
                "d2fobgn_GetNumProp",
                "d2fobgb_GetBoolProp",
                "d2fobgo_GetObjProp",
            ],
            D2FExports.Required);
    }

    [Fact]
    public void The_binding_requires_exactly_the_header_constants_the_installed_header_declares()
    {
        Assert.Equal(
            [.. s_verifiedConstants.Select(define => define.Constant)],
            D2FPropertyCodes.RequiredConstants);
    }

    [Fact]
    public void A_header_that_defines_the_verified_constants_resolves_every_code_from_the_header()
    {
        FormsApiDefinitions definitions = Header(s_verifiedConstants);

        (FormsPropertyCodes? codes, string? missing) = D2FPropertyCodes.Resolve(definitions);

        Assert.Null(missing);
        Assert.NotNull(codes);

        // The numbers above are the fixture's, not this binding's: the point is that each code came from the
        // header that was read, not from anything compiled into the worker.
        Assert.Equal(300, codes!.Name);
        Assert.Equal(489, codes.TriggerText);
        Assert.Equal(231, codes.ItemType);
        Assert.Equal(707, codes.BaseTable);
        Assert.Equal([.. s_verifiedConstants.Select(define => define.Constant).Order(StringComparer.Ordinal)], definitions.ResolvedNames);
    }

    [Theory]
    [InlineData("D2FP_BLOCK", "D2FP_FRST_BLK")]
    [InlineData("D2FP_TRG_TXT", "D2FP_TRIGGER_TEXT")]
    [InlineData("D2FP_QRY_DAT_SRC_NAM", "D2FP_BASE_TABLE")]
    [InlineData("D2FP_RECS_DISP_COUNT", "D2FP_RECORDS_DISPLAYED")]
    public void An_older_spelling_is_not_accepted_in_place_of_the_verified_constant(string verified, string older)
    {
        FormsApiDefinitions definitions = Header(
            s_verifiedConstants
                .Where(define => define.Constant != verified)
                .Append((older, 999u)));

        (FormsPropertyCodes? codes, string? missing) = D2FPropertyCodes.Resolve(definitions);

        Assert.Null(codes);
        Assert.Equal(verified, missing);
        Assert.DoesNotContain(older, definitions.ResolvedNames);
    }

    [Fact]
    public void A_code_the_header_puts_outside_the_property_word_is_refused_rather_than_truncated()
    {
        FormsApiDefinitions definitions = Header(
            s_verifiedConstants.Select(define => define.Constant == "D2FP_MAX_LEN" ? (define.Constant, 70_000u) : define));

        (FormsPropertyCodes? codes, string? missing) = D2FPropertyCodes.Resolve(definitions);

        Assert.Null(codes);
        Assert.Equal("D2FP_MAX_LEN", missing);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The approved-version check, including the sentinel for a binary that carries no file version resource —
/// which is what the <c>ifd2f60.dll</c> verified on this estate is.
/// </summary>
public sealed class SourceWorkerApprovedVersionTests
{
    private static WorkerConfiguration Read(Dictionary<string, string?> values) =>
        WorkerConfiguration.FromEnvironment(name => values.GetValueOrDefault(name));

    private static Dictionary<string, string?> Pinned(string? version) => new()
    {
        [WorkerConfiguration.InputRootVariable] = Path.Combine(Path.GetTempPath(), "ofm-version", "input"),
        [WorkerConfiguration.OutputRootVariable] = Path.Combine(Path.GetTempPath(), "ofm-version", "output"),
        [WorkerConfiguration.FormsHomeVariable] = Path.Combine(Path.GetTempPath(), "ofm-version", "orant"),
        [WorkerConfiguration.LibraryHashVariable] = new string('a', 64),
        [WorkerConfiguration.LibraryVersionVariable] = version,
    };

    [Theory]
    [InlineData("unversioned")]
    [InlineData("Unversioned")]
    [InlineData("  unversioned  ")]
    public void An_operator_can_declare_that_the_approved_binary_carries_no_file_version(string declared)
    {
        WorkerConfiguration configuration = Read(Pinned(declared));

        Assert.Equal(WorkerConfiguration.UnversionedSentinel, configuration.ApprovedLibraryFileVersion);
        Assert.True(configuration.RequiresUnversionedLibrary);
        Assert.True(configuration.NativeProviderConfigured);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_version_is_not_the_sentinel_and_still_leaves_the_provider_off(string? declared)
    {
        WorkerConfiguration configuration = Read(Pinned(declared));

        Assert.Null(configuration.ApprovedLibraryFileVersion);
        Assert.False(configuration.RequiresUnversionedLibrary);
        Assert.False(configuration.NativeProviderConfigured);
        Assert.Equal(WorkerConfiguration.LibraryVersionVariable, configuration.FirstMissingSetting);
    }

    [Fact]
    public void A_dotted_release_does_not_make_the_binary_unversioned()
    {
        Assert.False(Read(Pinned("6.0.8.7.3")).RequiresUnversionedLibrary);
    }

    [Fact]
    public void The_sentinel_matches_only_a_version_that_is_actually_absent()
    {
        Assert.Null(ApprovedLibraryVersion.Mismatch(null, WorkerConfiguration.UnversionedSentinel));
        Assert.Contains(
            "6.0.8.7.3",
            ApprovedLibraryVersion.Mismatch("6.0.8.7.3", WorkerConfiguration.UnversionedSentinel),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_version_never_passes_as_a_dotted_release()
    {
        Assert.Contains("(none)", ApprovedLibraryVersion.Mismatch(null, "6.0.8.7.3"), StringComparison.Ordinal);
        Assert.Null(ApprovedLibraryVersion.Mismatch("6.0.8.7.3", "6.0.8.7.3"));
        Assert.NotNull(ApprovedLibraryVersion.Mismatch("6.0.8.11.3", "6.0.8.7.3"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 6.0.8.7.3 ", "6.0.8.7.3")]
    public void A_blank_version_resource_is_read_as_no_version_rather_than_an_empty_one(
        string? resourceValue,
        string? expected) =>
        Assert.Equal(expected, ApprovedLibraryVersion.Observed(resourceValue));
}

/// <summary>The registry's version validation, which is the operator-facing half of the same sentinel.</summary>
public sealed class SourceWorkerRegistryVersionTests
{
    private static readonly string s_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ofm-registry-version"));

    private static string Registry(string? version, string? digest = null) =>
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                sources = new[]
                {
                    new
                    {
                        sourceEnvironmentId = "meridian-forms6i",
                        authorizedTenantId = GatewayWorkspace.TenantId,
                        authorizedProjectIds = new[] { "prj-native-binding-test" },
                        supportedFormsRelease = "6.0.8.7.3",
                        inputRoot = Path.Combine(s_root, "in"),
                        outputRoot = Path.Combine(s_root, "out"),
                        formsHome = Path.Combine(s_root, "orant"),
                        approvedLibrarySha256 = digest,
                        approvedLibraryFileVersion = version,
                    },
                },
            },
            GatewayProtocol.Json);

    [Fact]
    public void A_registry_may_declare_the_approved_binary_unversioned_when_it_pins_a_digest()
    {
        string json = Registry(WorkerConfiguration.UnversionedSentinel, new string('a', 64));

        Assert.True(GatewaySourceRegistry.TryParse(json, out GatewaySourceRegistry registry, out IReadOnlyList<string> errors));
        Assert.Empty(errors);
        Assert.Equal(
            WorkerConfiguration.UnversionedSentinel,
            registry.Find("meridian-forms6i")!.ApprovedLibraryFileVersion);
    }

    [Fact]
    public void A_registry_that_declares_it_unversioned_without_a_digest_is_refused()
    {
        Assert.False(GatewaySourceRegistry.TryParse(Registry(WorkerConfiguration.UnversionedSentinel), out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("to pin the binary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release-candidate")]
    [InlineData("6.0.8; whoami")]
    [InlineData("unversioned-ish")]
    public void A_version_that_is_neither_a_dotted_release_nor_the_sentinel_is_refused(string? version)
    {
        Assert.False(GatewaySourceRegistry.TryParse(Registry(version, new string('a', 64)), out _, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("approvedLibraryFileVersion", StringComparison.Ordinal));
    }
}

/// <summary>
/// What the traversal does and does not capture. The fake graph exercises the real traversal; it is not
/// evidence that the native binding reads anything.
/// </summary>
public sealed class SourceWorkerTraversalCoverageTests
{
    private static (FakeFormsObjectGraph Graph, nint Module) Module(int programUnits, int lovs)
    {
        FakeFormsObjectGraph graph = new();
        nint module = graph.Create();
        graph.At(module).Texts[FakeFormsObjectGraph.Codes.Name] = "MRD_ORDER_ENTRY";

        for (int index = 0; index < programUnits; index++)
        {
            nint unit = graph.Append(module, FakeFormsObjectGraph.Codes.FirstProgramUnit);
            graph.At(unit).Texts[FakeFormsObjectGraph.Codes.Name] = $"PKG_ORDER_{index}";
        }

        for (int index = 0; index < lovs; index++)
        {
            nint lov = graph.Append(module, FakeFormsObjectGraph.Codes.FirstLov);
            graph.At(lov).Texts[FakeFormsObjectGraph.Codes.Name] = $"LOV_CUSTOMER_{index}";
        }

        return (graph, module);
    }

    private static NativeModuleRead Read(FakeFormsObjectGraph graph, nint module) =>
        new FormsModuleTraversal(graph, FakeFormsObjectGraph.Codes, FakeFormsObjectGraph.NameOfCode)
            .ReadModule(module, CancellationToken.None);

    [Theory]
    [InlineData(true, "program unit")]
    [InlineData(false, "list of values")]
    public void An_unnamed_unsupported_child_fails_the_read(bool programUnit, string kind)
    {
        (FakeFormsObjectGraph graph, nint module) = Module(programUnits: 0, lovs: 0);
        graph.Append(module, programUnit
            ? FakeFormsObjectGraph.Codes.FirstProgramUnit
            : FakeFormsObjectGraph.Codes.FirstLov);

        NativeModuleRead read = Read(graph, module);

        Assert.Null(read.Module);
        Assert.Contains($"unnamed {kind}", Assert.Single(read.Findings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_declaring_program_units_fails_the_read_rather_than_naming_bodies_it_never_opened()
    {
        (FakeFormsObjectGraph graph, nint module) = Module(programUnits: 2, lovs: 0);

        NativeModuleRead read = Read(graph, module);

        Assert.Null(read.Module);
        string finding = Assert.Single(read.Findings);
        Assert.Contains("D2FP_PGU_TXT", finding, StringComparison.Ordinal);
        Assert.Contains("PKG_ORDER_0", finding, StringComparison.Ordinal);
        Assert.Contains("PKG_ORDER_1", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_declaring_lists_of_values_fails_the_read_rather_than_naming_record_groups_it_never_opened()
    {
        (FakeFormsObjectGraph graph, nint module) = Module(programUnits: 0, lovs: 1);

        NativeModuleRead read = Read(graph, module);

        Assert.Null(read.Module);
        string finding = Assert.Single(read.Findings);
        Assert.Contains("record groups", finding, StringComparison.Ordinal);
        Assert.Contains("LOV_CUSTOMER_0", finding, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is scoped to the features this worker cannot read, not to native extraction as such:
    /// a module of blocks, items and trigger bodies is exactly what it does read, and still does.
    /// </summary>
    [Fact]
    public void A_module_with_neither_is_read_and_reports_no_coverage_finding_it_does_not_owe()
    {
        (FakeFormsObjectGraph graph, nint module) = Module(programUnits: 0, lovs: 0);
        nint block = graph.Append(module, FakeFormsObjectGraph.Codes.FirstBlock);
        graph.At(block).Texts[FakeFormsObjectGraph.Codes.Name] = "ORDER_ENTRY";

        nint button = graph.Append(block, FakeFormsObjectGraph.Codes.FirstItem);
        graph.At(button).Texts[FakeFormsObjectGraph.Codes.Name] = "POST_ORDER";
        graph.At(button).Numbers[FakeFormsObjectGraph.Codes.ItemType] = 4;

        nint trigger = graph.Append(button, FakeFormsObjectGraph.Codes.FirstTrigger);
        graph.At(trigger).Texts[FakeFormsObjectGraph.Codes.Name] = "WHEN-BUTTON-PRESSED";
        graph.At(trigger).Texts[FakeFormsObjectGraph.Codes.TriggerText] = "BEGIN MRD_ORDERS_PKG.POST; END;";

        NativeModuleRead read = Read(graph, module);

        Assert.Empty(read.Findings);
        Assert.Empty(read.Module!.ProgramUnits);
        Assert.Empty(read.Module.Lovs);

        NeutralFormsTrigger pressed = Assert.Single(Assert.Single(read.Module.Blocks).Triggers);
        Assert.Equal("ORDER_ENTRY.POST_ORDER", pressed.Scope);
        Assert.Equal("BEGIN MRD_ORDERS_PKG.POST; END;", pressed.Body);
    }
}
