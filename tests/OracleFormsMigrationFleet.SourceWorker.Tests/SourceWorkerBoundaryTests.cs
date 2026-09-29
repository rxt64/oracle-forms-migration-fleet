// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.SourceWorker;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

public sealed class SourceWorkerConfigurationTests
{
    private static WorkerConfiguration Read(Dictionary<string, string?> values) =>
        WorkerConfiguration.FromEnvironment(name => values.GetValueOrDefault(name));

    [Fact]
    public void An_empty_environment_leaves_the_native_provider_off()
    {
        WorkerConfiguration configuration = Read([]);

        Assert.False(configuration.NativeProviderConfigured);
        Assert.False(configuration.ExtractionConfigured);
        Assert.Null(configuration.LibraryPath);
        Assert.Equal(WorkerConfiguration.InputRootVariable, configuration.FirstMissingSetting);
    }

    [Fact]
    public void A_forms_home_without_an_approved_binary_digest_does_not_enable_the_provider()
    {
        WorkerConfiguration configuration = Read(new()
        {
            [WorkerConfiguration.FormsHomeVariable] = @"C:\orant",
            [WorkerConfiguration.LibraryVersionVariable] = "6.0.8.7.3",
        });

        Assert.False(configuration.NativeProviderConfigured);
        Assert.Null(configuration.ApprovedLibrarySha256);
        Assert.Equal("6.0.8.7.3", configuration.ApprovedLibraryFileVersion);
    }

    [Theory]
    [InlineData("not-a-digest")]
    [InlineData("ABCD")]
    [InlineData("zz00865a74d164bf409538278f634b5fa79115f2dd3ad06c9d66a442c7cc74d2")]
    public void A_library_digest_that_is_not_a_sha256_is_refused(string digest)
    {
        WorkerConfiguration configuration = Read(new()
        {
            [WorkerConfiguration.FormsHomeVariable] = @"C:\orant",
            [WorkerConfiguration.LibraryHashVariable] = digest,
            [WorkerConfiguration.LibraryVersionVariable] = "6.0.8.7.3",
        });

        Assert.Null(configuration.ApprovedLibrarySha256);
        Assert.False(configuration.NativeProviderConfigured);
    }

    [Theory]
    [InlineData("6.0.8; whoami")]
    [InlineData("release-candidate")]
    [InlineData("6.0.8.22.1-unverified")]
    public void A_library_version_that_is_not_a_dotted_release_is_refused(string version)
    {
        WorkerConfiguration configuration = Read(new()
        {
            [WorkerConfiguration.FormsHomeVariable] = @"C:\orant",
            [WorkerConfiguration.LibraryHashVariable] = new string('a', 64),
            [WorkerConfiguration.LibraryVersionVariable] = version,
        });

        Assert.Null(configuration.ApprovedLibraryFileVersion);
        Assert.False(configuration.NativeProviderConfigured);
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("C:\\orant\\*")]
    public void A_root_that_is_not_a_fully_qualified_literal_directory_is_refused(string root)
    {
        Assert.Null(Read(new() { [WorkerConfiguration.InputRootVariable] = root }).TrustedInputRoot);
    }

    [Fact]
    public void The_library_and_header_are_fixed_aliases_under_the_configured_home()
    {
        WorkerConfiguration configuration = Read(new() { [WorkerConfiguration.FormsHomeVariable] = @"C:\orant" });

        Assert.Equal(Path.Combine(@"C:\orant", "bin", "ifd2f60.dll"), configuration.LibraryPath);
        Assert.Equal(Path.Combine(@"C:\orant", "FORMS60", "API", "D2FDEF.H"), configuration.DefinitionHeaderPath);
    }

    [Fact]
    public void The_time_budget_is_bounded_regardless_of_what_the_environment_asks_for()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Read(new() { [WorkerConfiguration.TimeoutVariable] = "100000" }).Timeout);
        Assert.Equal(TimeSpan.FromMinutes(2), Read(new() { [WorkerConfiguration.TimeoutVariable] = "-1" }).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(45), Read(new() { [WorkerConfiguration.TimeoutVariable] = "45" }).Timeout);
    }
}

public sealed class SourceWorkerApiDefinitionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ofm-d2fdef-").FullName;

    private string WriteHeader(string content)
    {
        string path = Path.Combine(_root, "D2FDEF.H");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Codes_come_from_the_installed_header_and_comments_do_not_define_them()
    {
        string path = WriteHeader("""
            #define D2FP_NAME 1
            /* #define D2FP_NAME 999 */
            #define D2FP_TITLE (2)
            #define D2FC_ITTY_TI 11
            """);

        FormsApiDefinitions? definitions = FormsApiDefinitions.Load(path, new string('a', 64));

        Assert.NotNull(definitions);
        Assert.Equal(1u, definitions!.Resolve("name", "D2FP_NAME"));
        Assert.Equal(2u, definitions.Resolve("title", "D2FP_TITLE"));
        Assert.Equal("TI", definitions.NameOfCode("D2FC_ITTY_", 11));
    }

    [Fact]
    public void A_name_the_header_does_not_define_resolves_to_nothing_rather_than_a_guess()
    {
        string path = WriteHeader("#define D2FP_NAME 1\n");
        FormsApiDefinitions definitions = FormsApiDefinitions.Load(path, new string('a', 64))!;

        Assert.Null(definitions.Resolve("firstBlock", "D2FP_BLK_OBJ", "D2FP_FRST_BLK"));
        Assert.DoesNotContain("D2FP_BLK_OBJ", definitions.ResolvedNames);
    }

    [Fact]
    public void The_first_candidate_the_header_actually_defines_is_the_one_recorded()
    {
        string path = WriteHeader("#define D2FP_FRST_BLK 40\n");
        FormsApiDefinitions definitions = FormsApiDefinitions.Load(path, new string('a', 64))!;

        Assert.Equal(40u, definitions.Resolve("firstBlock", "D2FP_BLK_OBJ", "D2FP_FRST_BLK"));
        Assert.Contains("D2FP_FRST_BLK", definitions.ResolvedNames);
    }

    [Fact]
    public void An_ambiguous_code_family_yields_no_name_rather_than_an_arbitrary_one()
    {
        string path = WriteHeader("#define D2FC_ITTY_TI 11\n#define D2FC_ITTY_ALIAS 11\n");
        FormsApiDefinitions definitions = FormsApiDefinitions.Load(path, new string('a', 64))!;

        Assert.Null(definitions.NameOfCode("D2FC_ITTY_", 11));
    }

    [Fact]
    public void A_header_that_defines_nothing_is_refused()
    {
        Assert.Null(FormsApiDefinitions.Load(WriteHeader("/* no definitions here */\n"), new string('a', 64)));
        Assert.Null(FormsApiDefinitions.Load(Path.Combine(_root, "absent.H"), new string('a', 64)));
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

public sealed class SourceWorkerTrustedInputTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("../orders.fmb")]
    [InlineData("..\\orders.fmb")]
    [InlineData("sub/orders.fmb")]
    [InlineData("C:\\orders.fmb")]
    [InlineData("\\\\server\\share\\orders.fmb")]
    [InlineData("orders.fmb:stream")]
    [InlineData(".hidden.fmb")]
    [InlineData("")]
    public void An_alias_that_is_not_a_bare_file_name_is_refused_before_a_path_is_built(string alias) =>
        Assert.False(TrustedInput.IsWellFormedAlias(alias));

    [Theory]
    [InlineData("ORDERS.fmb")]
    [InlineData("MRD_ORDER_ENTRY.fmb")]
    [InlineData("shared-library.pll")]
    public void A_bare_supported_module_name_is_accepted(string alias) =>
        Assert.True(TrustedInput.IsWellFormedAlias(alias));

    [Fact]
    public void Containment_is_decided_on_whole_segments_so_a_sibling_prefix_cannot_pass()
    {
        Assert.False(TrustedInput.IsWithin(@"C:\lab\input", @"C:\lab\input-other\orders.fmb"));
        Assert.True(TrustedInput.IsWithin(@"C:\lab\input", @"C:\lab\input\orders.fmb"));
        Assert.False(TrustedInput.IsWithin(@"C:\lab\input", @"C:\lab\input"));
    }

    [Fact]
    public async Task A_module_that_is_present_and_matches_its_pinned_digest_resolves()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("ORDERS.fmb", "module bytes");

        TrustedInputResolution resolution =
            await TrustedInput.ResolveAsync(workspace.InputRoot, "ORDERS.fmb", hash, CancellationToken.None);

        Assert.Null(resolution.Rejection);
        Assert.Equal(hash, resolution.ObservedSha256);
        Assert.Equal(Path.Combine(workspace.InputRoot, "ORDERS.fmb"), resolution.Path);
    }

    [Fact]
    public async Task A_digest_mismatch_reports_what_was_observed_and_resolves_no_path()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("ORDERS.fmb", "module bytes");

        TrustedInputResolution resolution = await TrustedInput.ResolveAsync(
            workspace.InputRoot, "ORDERS.fmb", new string('f', 64), CancellationToken.None);

        Assert.Null(resolution.Path);
        Assert.Equal(hash, resolution.ObservedSha256);
        Assert.Contains("does not match", resolution.Rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_module_is_refused_without_naming_a_path()
    {
        using WorkerWorkspace workspace = new();

        TrustedInputResolution resolution = await TrustedInput.ResolveAsync(
            workspace.InputRoot, "ABSENT.fmb", new string('f', 64), CancellationToken.None);

        Assert.Null(resolution.Path);
        Assert.DoesNotContain(workspace.InputRoot, resolution.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_request_that_pins_no_digest_is_refused_before_the_file_is_read()
    {
        using WorkerWorkspace workspace = new();
        workspace.WriteModule("ORDERS.fmb", "module bytes");

        TrustedInputResolution resolution =
            await TrustedInput.ResolveAsync(workspace.InputRoot, "ORDERS.fmb", string.Empty, CancellationToken.None);

        Assert.Null(resolution.Path);
        Assert.Null(resolution.ObservedSha256);
    }
}
