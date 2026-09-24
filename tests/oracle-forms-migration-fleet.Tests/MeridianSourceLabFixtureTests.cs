// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Diagnostics;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The Meridian order-entry source lab under `infra/source-lab/meridian-order-entry` is an owned,
/// original, fictional Oracle estate the fleet is allowed to read end to end.
///
/// These tests do not execute SQL: no Oracle runtime is assumed and none is reachable offline. What
/// they establish is that the lab is the same estate the mapping fixture already declares, that its
/// manifest and export are readable by the product's own readers rather than by a bespoke parser, and
/// that the installed text has not drifted from the canonical source text. Compilation, seeding, and
/// every behavioural expectation in the verifier remain NotExecuted until an operator runs them.
/// </summary>
public class MeridianSourceLabFixtureTests
{
    private static readonly string[] s_nativeModuleExtensions =
        [".fmb", ".fmx", ".mmb", ".mmx", ".pll", ".plx", ".olb", ".fmt", ".mmt", ".rdf", ".rep"];

    private static string LabRoot => Path.Combine(
        RepositoryRoot(), "infra", "source-lab", "meridian-order-entry");

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(LabRoot, Path.Combine(parts)));

    [Fact]
    public void The_lab_schema_is_the_estate_the_mapping_fixture_already_declares()
    {
        // One estate, not two that drift: the lab installs exactly the tables the pilot fixture binds.
        Assert.Equal(
            Normalize(DotNetPilotFixtures.MeridianSchema),
            Normalize(Read("source", "db", "schema.sql")));
    }

    [Fact]
    public void The_installed_scripts_carry_the_canonical_source_text_unchanged()
    {
        Assert.Contains(
            Normalize(Read("source", "db", "schema.sql")),
            Normalize(Read("initdb", "001_meridian_schema.sql")),
            StringComparison.Ordinal);

        Assert.Contains(
            Normalize(Read("source", "db", "package.sql")),
            Normalize(Read("initdb", "003_meridian_plsql.sql")),
            StringComparison.Ordinal);

        Assert.Contains(
            Normalize(Read("source", "db", "seed.sql")),
            Normalize(Read("initdb", "002_meridian_seed.sql")),
            StringComparison.Ordinal);

        Assert.Contains(
            Normalize(Read("source", "db", "sequences.sql")).Split('\n')
                .First(line => line.StartsWith("CREATE SEQUENCE MRD_ORDER_SEQ", StringComparison.Ordinal)),
            Normalize(Read("initdb", "001_meridian_schema.sql")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_parses_into_the_four_tables_the_lab_installs()
    {
        OracleSchema schema = OracleSchemaParser.Parse(Read("source", "db", "schema.sql"));

        Assert.Equal(
            ["MRD_ARTICLE", "MRD_CUSTOMER", "MRD_ORDER_HEAD", "MRD_ORDER_ITEM"],
            schema.Tables.Select(table => table.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_package_declares_the_routines_the_contract_promises()
    {
        OracleSchema schema = OracleSchemaParser.Parse(Read("source", "db", "package.sql"));

        Assert.Contains(schema.ProgramUnits, unit =>
            unit.Kind == OracleProgramUnitKind.PackageSpecification && unit.Name == "MRD_ORDER_ENTRY_API");
        Assert.Contains(schema.ProgramUnits, unit =>
            unit.Kind == OracleProgramUnitKind.PackageBody && unit.Name == "MRD_ORDER_ENTRY_API");
    }

    [Fact]
    public void The_manifest_resolves_against_the_lab_schema_through_the_product_reader()
    {
        OracleSchema schema = OracleSchemaParser.Parse(Read("source", "db", "schema.sql"));

        TargetMappingRead read = TargetMappingReader.Read(
            Read("source", "mapping", "target-mapping.json"), schema);

        Assert.Empty(read.Rejections);
        Assert.NotNull(read.Mapping);
        Assert.Equal(DotNetPilotFixtures.MeridianLabel, read.Mapping!.Declaration.FixtureLabel);
        Assert.Equal("MRD_ORDER_HEAD", read.Mapping.Header.Table.Name);
        Assert.Equal("MRD_ORDER_ITEM", read.Mapping.Detail.Table.Name);
        Assert.Equal("MRD_CUSTOMER", read.Mapping.Party.Table.Name);
        Assert.Equal("MRD_ARTICLE", read.Mapping.Item.Table.Name);
        Assert.True(read.Mapping.ItemIsVersioned);
    }

    [Fact]
    public void The_lab_manifest_binds_what_the_checked_in_pilot_manifest_binds()
    {
        using JsonDocument lab = JsonDocument.Parse(Read("source", "mapping", "target-mapping.json"));
        using JsonDocument pilot = JsonDocument.Parse(DotNetPilotFixtures.MeridianManifest);

        Assert.Equal(Bindings(pilot), Bindings(lab));
    }

    [Fact]
    public void The_synthetic_export_is_readable_by_the_forms_parser()
    {
        FormsModuleParse parse = FormsModuleParser.Parse(Read("source", "forms", "MRD_ORDER_ENTRY.xml"));

        FormsModule module = Assert.Single(parse.Modules);
        Assert.Equal("MRD_ORDER_ENTRY", module.Name);
        Assert.Equal("MRD_ORDER_HEAD", module.Blocks.Single(block => block.Name == "ORDER_BLOCK").BaseTable);
        Assert.Equal("MRD_ORDER_ITEM", module.Blocks.Single(block => block.Name == "LINE_BLOCK").BaseTable);
    }

    [Fact]
    public void The_export_reports_exactly_the_coverage_gaps_it_was_written_to_expose()
    {
        // Unsupported findings are what this fixture is for. Demanding none of them would force the
        // gap out of sight; accepting any of them would let a construct appear or vanish unnoticed.
        // The exact set is therefore pinned, so a newly dropped construct and a newly translated one
        // both fail here and have to be accounted for.
        FormsModuleParse parse = FormsModuleParser.Parse(Read("source", "forms", "MRD_ORDER_ENTRY.xml"));

        Assert.Equal(
            [
                "MRD_ORDER_ENTRY.LINE_BLOCK.POST-CHANGE",
                "MRD_ORDER_ENTRY.LINE_BLOCK.POST-TEXT-ITEM",
                "MRD_ORDER_ENTRY.LINE_BLOCK.WHEN-NEW-RECORD-INSTANCE",
                "MRD_ORDER_ENTRY.LINE_BLOCK.WHEN-VALIDATE-ITEM",
                "MRD_ORDER_ENTRY.MRD_ORDER_ENTRY.ON-ERROR",
                "MRD_ORDER_ENTRY.MRD_ORDER_ENTRY.WHEN-NEW-FORM-INSTANCE",
                "MRD_ORDER_ENTRY.ORDER_BLOCK.KEY-COMMIT",
                "MRD_ORDER_ENTRY.ORDER_BLOCK.POST-QUERY",
                "MRD_ORDER_ENTRY.ORDER_BLOCK.WHEN-BUTTON-PRESSED",
                "MRD_ORDER_ENTRY.ORDER_BLOCK.WHEN-VALIDATE-RECORD",
                "MRD_ORDER_ENTRY.REFRESH_TOTALS",
                "MRD_ORDER_ENTRY.SUBMIT_CURRENT_ORDER",
                "MRD_ORDER_ENTRY.TOTALS_BLOCK.WHEN-TIMER-EXPIRED",
            ],
            Constructs(parse, ConversionSeverity.Unsupported));

        Assert.Equal(
            [
                "MRD_ORDER_ENTRY.ARTICLE_LOV",
                "MRD_ORDER_ENTRY.CUSTOMER_LOV",
                "MRD_ORDER_ENTRY.TOTALS_BLOCK",
            ],
            Constructs(parse, ConversionSeverity.ManualReview));

        // Nothing else was reported at any other severity.
        Assert.Equal(16, parse.Findings.Count);
    }

    [Fact]
    public void Every_design_time_name_the_installed_export_references_resolves_inside_it()
    {
        string export = Read("source", "forms", "MRD_ORDER_ENTRY.xml");
        FormsModule module = Assert.Single(FormsModuleParser.Parse(export).Modules);

        // The named visual attribute the WHEN-NEW-RECORD-INSTANCE trigger applies is declared here.
        Assert.Contains(module.SourceFacts!.Facts, fact =>
            fact.LocalName == "VisualAttribute" && fact.DeclaredName == "VA_TOTALS");

        // Both record groups the two LOVs name are declared here.
        foreach (string recordGroup in (string[])["CUSTOMER_RG", "ARTICLE_RG"])
        {
            Assert.Contains(module.SourceFacts.Facts, fact =>
                fact.LocalName == "RecordGroup" && fact.DeclaredName == recordGroup);
        }

        // No multi-form navigation: this fixture contains one module, so CALL_FORM could only ever
        // name a module it does not carry.
        Assert.DoesNotContain("CALL_FORM", export, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_design_time_reference_is_exercised_without_breaking_the_installed_lab()
    {
        // A name that resolves to nothing is a defect in the *source*, not a gap in this fleet's
        // coverage, and the two must not be confused. The variant below exists only in this test:
        // it is never installed, never pointed at a run, and adds no file to the lab.
        const string DanglingExport = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4" FormsVersion="12.2.1.4">
              <FormModule Name="MRD_ORDER_ENTRY_DANGLING" Title="SyntheticExport negative variant">
                <Block Name="LINE_BLOCK" QueryDataSourceName="MRD_ORDER_ITEM" RecordsDisplayCount="8">
                  <Item Name="ITM_VALUE" ItemType="Display Item" DataType="Number" ColumnName="ITM_VALUE" Prompt="Line total"/>
                  <Trigger Name="WHEN-NEW-RECORD-INSTANCE" TriggerText="BEGIN SET_ITEM_PROPERTY('LINE_BLOCK.ITM_VALUE', VISUAL_ATTRIBUTE, 'VA_TOTALS'); END;"/>
                </Block>
                <ProgramUnit Name="OPEN_CUSTOMER_FILE" ProgramUnitType="Procedure"
                             ProgramUnitText="PROCEDURE OPEN_CUSTOMER_FILE IS BEGIN CALL_FORM('MRD_CUSTOMER_FILE', HIDE, DO_REPLACE, NO_QUERY_ONLY); END;"/>
              </FormModule>
            </Module>
            """;

        FormsModuleParse parse = FormsModuleParser.Parse(DanglingExport);
        FormsModule module = Assert.Single(parse.Modules);

        // Both names are referenced by retained source text...
        Assert.Contains(module.SourceFacts!.Facts, fact => References(fact, "VA_TOTALS"));
        Assert.Contains(module.SourceFacts.Facts, fact => References(fact, "MRD_CUSTOMER_FILE"));

        // ...and neither resolves: no VisualAttribute declares VA_TOTALS, and the export carries no
        // module named MRD_CUSTOMER_FILE for CALL_FORM to reach.
        Assert.DoesNotContain(module.SourceFacts.Facts, fact =>
            fact.LocalName == "VisualAttribute" && fact.DeclaredName == "VA_TOTALS");
        Assert.DoesNotContain(parse.Modules, candidate => candidate.Name == "MRD_CUSTOMER_FILE");

        // The constructs carrying the unresolved names are reported rather than silently dropped.
        // The parser does not resolve design-time references, so it reports them as untranslated
        // behaviour; that they are also unresolved is established above, not claimed here.
        Assert.Equal(
            [
                "MRD_ORDER_ENTRY_DANGLING.LINE_BLOCK.WHEN-NEW-RECORD-INSTANCE",
                "MRD_ORDER_ENTRY_DANGLING.OPEN_CUSTOMER_FILE",
            ],
            Constructs(parse, ConversionSeverity.Unsupported));
    }

    [Fact]
    public void The_export_declares_its_synthetic_provenance_and_no_native_module_is_present()
    {
        string export = Read("source", "forms", "MRD_ORDER_ENTRY.xml");

        Assert.Contains("SyntheticExport", export, StringComparison.Ordinal);
        Assert.Contains("DELIBERATE SYNTHETIC", export, StringComparison.Ordinal);

        // No fabricated native bytes: a hand-authored export must never be accompanied by a file that
        // would be read as a genuine compiled or binary Forms module.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(LabRoot, "*", SearchOption.AllDirectories),
            path => s_nativeModuleExtensions.Contains(
                Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_verifier_refuses_to_present_a_single_session_as_concurrency_evidence()
    {
        string verifier = Read("initdb", "004_meridian_verify.sql");

        Assert.Contains("NOT VERIFIED", verifier, StringComparison.Ordinal);
        Assert.Contains("RAISE_APPLICATION_ERROR", verifier, StringComparison.Ordinal);
        Assert.Contains("MERIDIAN ORDER LAB: SEED OK", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_installer_resumes_only_exact_owned_checkpoints()
    {
        string installer = Read("Provision-MeridianSourceLab.ps1");

        foreach (string checkpoint in (string[])["Absent", "Schema", "Seed", "Ready", "PartialUnsupported"])
        {
            Assert.Contains($"'{checkpoint}'", installer, StringComparison.Ordinal);
        }

        Assert.Contains("MERIDIAN_UNEXPECTED_OBJECTS", installer, StringComparison.Ordinal);
        Assert.Contains("MERIDIAN_BAD_CONSTRAINTS", installer, StringComparison.Ordinal);
        Assert.Contains("MERIDIAN_COMPILE_ERRORS", installer, StringComparison.Ordinal);
        Assert.Contains("Install refuses to drop, reset, or modify it", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP USER", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER USER BANKING", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_live_transport_and_concurrency_harness_are_bounded_and_single_exec()
    {
        string transport = Read("Invoke-MeridianSqlPlus.ps1");
        string concurrency = Read("Test-MeridianConcurrency.ps1");

        Assert.Contains("[ValidateRange(1, 30)]", transport, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutMinutes = 15", transport, StringComparison.Ordinal);
        Assert.Contains("Wait-ForSqlPlusPrompt", transport, StringComparison.Ordinal);
        Assert.Contains(" {0,2}\\d+ {2}", transport, StringComparison.Ordinal);
        Assert.Contains("-InputMode Raw", concurrency, StringComparison.Ordinal);
        Assert.Contains("-ExecCommand 'timeout 180s bash -s'", concurrency, StringComparison.Ordinal);
        Assert.Equal(1, Count(concurrency, "& $invokeSqlPlus"));
        Assert.DoesNotContain("Start-Job", concurrency, StringComparison.Ordinal);
        Assert.Contains("mkfifo", concurrency, StringComparison.Ordinal);
        Assert.Contains("sqlplus -s / as sysdba", concurrency, StringComparison.Ordinal);
        Assert.True(Count(concurrency, "sqlplus -s / as sysdba") >= 2);
        Assert.Contains("timeout __BLOCK_PROOF_SECONDS__s cat", concurrency, StringComparison.Ordinal);
        Assert.Contains(".Replace('__BLOCK_PROOF_SECONDS__'", concurrency, StringComparison.Ordinal);
        Assert.Contains("wait \"$s1_pid\"", concurrency, StringComparison.Ordinal);
        Assert.Contains("wait \"$s2_pid\"", concurrency, StringComparison.Ordinal);
        Assert.Contains("OFM_CONCURRENCY|COMMIT|S2|CODE=-20105", concurrency, StringComparison.Ordinal);
        Assert.Contains("OFM_CONCURRENCY|ROLLBACK|S2|CODE=0", concurrency, StringComparison.Ordinal);
        Assert.Contains("WAIT_CENTISECONDS", concurrency, StringComparison.Ordinal);
        Assert.Contains("fixture_owned=0", concurrency, StringComparison.Ordinal);
        Assert.Equal(1, Count(concurrency, "fixture_owned=1"));
        Assert.Equal(2, Count(concurrency, "fixture_owned=0"));
        Assert.Contains("if [ \"$fixture_owned\" -eq 1 ]; then", concurrency, StringComparison.Ordinal);
        Assert.Contains("WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK", concurrency, StringComparison.Ordinal);
        Assert.Contains("OFM_CONCURRENCY|FINAL|MERIDIAN_COMPILE_ERRORS=0", concurrency, StringComparison.Ordinal);
        Assert.Contains("OFM_CONCURRENCY|FINAL|SEED=5|6|2|3", concurrency, StringComparison.Ordinal);
        Assert.Contains("OFM_CONCURRENCY|FINAL|BANKING=19|0", concurrency, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP USER", concurrency, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER USER BANKING", concurrency, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("collision", 20, false)]
    [InlineData("owned", 0, true)]
    public void The_concurrency_harness_cleans_only_a_fixture_created_by_this_invocation(
        string setupResult,
        int expectedExitCode,
        bool expectCleanup)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), $"ofm-meridian-guard-{Guid.NewGuid():N}");
        string fakeBin = Path.Combine(temporaryRoot, "bin");
        string harnessPath = Path.Combine(temporaryRoot, "guard-probe.sh");
        string callLog = Path.Combine(temporaryRoot, "calls.log");
        string stdinLog = Path.Combine(temporaryRoot, "stdin.log");

        Directory.CreateDirectory(fakeBin);
        try
        {
            string harness = ExtractConcurrencyHarness();
            int scenarios = harness.IndexOf("run_scenario COMMIT -20105 COMMIT", StringComparison.Ordinal);
            Assert.True(scenarios > 0, "The concurrency scenario tail was not found.");

            string probe = harness[..scenarios] + """
                sqlplus -s / as sysdba @setup.sql
                fixture_owned=1
                exit 0
                """;
            File.WriteAllText(harnessPath, probe);

            string fakeSqlPlusPath = Path.Combine(fakeBin, "sqlplus");
            File.WriteAllText(fakeSqlPlusPath, """
                #!/usr/bin/env bash
                set -euo pipefail
                to_posix() {
                  if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s' "$1"; fi
                }
                call_log="$(to_posix "$OFM_FAKE_CALL_LOG")"
                stdin_log="$(to_posix "$OFM_FAKE_STDIN_LOG")"
                printf '%s\n' "$*" >>"$call_log"
                if [[ "$*" == *"@setup.sql"* ]]; then
                  if [ "$OFM_FAKE_SETUP_RESULT" = collision ]; then exit 20; fi
                  exit 0
                fi
                cat >>"$stdin_log"
                """);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    fakeSqlPlusPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            ProcessStartInfo start = new(FindBash(), harnessPath)
            {
                WorkingDirectory = temporaryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["OFM_FAKE_SQLPLUS_DIR"] = fakeBin;
            start.Environment["OFM_FAKE_CALL_LOG"] = callLog;
            start.Environment["OFM_FAKE_STDIN_LOG"] = stdinLog;
            start.Environment["OFM_FAKE_SETUP_RESULT"] = setupResult;

            string pathPrefix = OperatingSystem.IsWindows()
                ? "$(cygpath -u \"$OFM_FAKE_SQLPLUS_DIR\")"
                : "$OFM_FAKE_SQLPLUS_DIR";
            File.WriteAllText(
                harnessPath,
                $"export PATH=\"{pathPrefix}:$PATH\"\n" + probe);

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Bash did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == expectedExitCode,
                $"Unexpected Bash exit {process.ExitCode}. stdout: {stdout} stderr: {stderr}");
            string calls = File.ReadAllText(callLog);
            string cleanupSql = File.Exists(stdinLog) ? File.ReadAllText(stdinLog) : string.Empty;

            Assert.Equal(expectCleanup ? 2 : 1, calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            if (expectCleanup)
            {
                Assert.Contains("DELETE FROM MRD_ORDER_ITEM", cleanupSql, StringComparison.Ordinal);
                Assert.Contains("DELETE FROM MRD_ARTICLE WHERE ART_NO = 29001", cleanupSql, StringComparison.Ordinal);
                Assert.Contains("DELETE FROM MRD_CUSTOMER WHERE CUST_NO = 19001", cleanupSql, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("DELETE FROM", cleanupSql, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void The_lab_installs_into_its_own_schema_and_leaves_the_banking_estate_alone()
    {
        foreach (string script in Directory.EnumerateFiles(
            Path.Combine(LabRoot, "initdb"), "*.sql", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(script);
            Assert.Contains("ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CURRENT_SCHEMA = BANKING", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BANK_ACCOUNT", text, StringComparison.Ordinal);
            Assert.DoesNotContain("LEGACY_BANKING_API", text, StringComparison.Ordinal);
            Assert.DoesNotContain(" TO BANKING", text, StringComparison.Ordinal);
            Assert.DoesNotContain("'BANKING'", text, StringComparison.Ordinal);
        }
    }

    /// <summary>Role bindings only, ordered, so formatting and comments cannot make the two look different.</summary>
    private static IReadOnlyList<string> Bindings(JsonDocument manifest) =>
    [
        .. manifest.RootElement.GetProperty("objects").EnumerateArray()
            .SelectMany(entry => entry.GetProperty("fields").EnumerateArray()
                .Select(field =>
                    $"{entry.GetProperty("role").GetString()}.{field.GetProperty("role").GetString()}" +
                    $"={entry.GetProperty("table").GetString()}.{field.GetProperty("column").GetString()}"))
            .Order(StringComparer.Ordinal),
    ];

    private static IReadOnlyList<string> Constructs(FormsModuleParse parse, ConversionSeverity severity) =>
    [
        .. parse.Findings
            .Where(finding => finding.Severity == severity)
            .Select(finding => finding.Construct)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Whether a retained element names <paramref name="name"/> in any attribute it declared.</summary>
    private static bool References(FormsSourceFact fact, string name) =>
        fact.Attributes.Any(attribute => attribute.Value.Contains(name, StringComparison.Ordinal));

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string ExtractConcurrencyHarness()
    {
        string script = Normalize(Read("Test-MeridianConcurrency.ps1"));
        const string Start = "$harness = @'\n";
        const string End = "\n'@\n";
        int start = script.IndexOf(Start, StringComparison.Ordinal);
        Assert.True(start >= 0, "The embedded concurrency harness start was not found.");
        start += Start.Length;
        int end = script.IndexOf(End, start, StringComparison.Ordinal);
        Assert.True(end > start, "The embedded concurrency harness end was not found.");
        return script[start..end];
    }

    private static string FindBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/bin/bash";
        }

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        Assert.True(File.Exists(path), $"Git Bash was not found at {path}.");
        return path;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "oracle-forms-migration-fleet.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }
}
