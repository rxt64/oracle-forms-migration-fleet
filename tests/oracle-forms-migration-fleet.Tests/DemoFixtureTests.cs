// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// `demo/northstar-migrated` is generator output, not a fixture anyone edits. This runs the real phase over
/// the demo estate's own scripts and holds the checked-in tree to what the fleet produces, so a hand edit
/// fails the build instead of quietly becoming the thing the demo shows.
///
/// Set FLEET_REGENERATE_DEMO=1 to rewrite the tree from the generator rather than assert against it. That
/// is the supported way to refresh the demo; there is no separate script to drift from this test.
/// </summary>
public class DemoFixtureTests
{
    private const string DemoRoot = "demo/northstar-migrated";
    private const string RegenerateVariable = "FLEET_REGENERATE_DEMO";

    /// <summary>Checked in alongside the generated tree but owned elsewhere: deployment and npm artifacts.</summary>
    private static readonly string[] s_preserved =
    [
        "Dockerfile", ".dockerignore", "DEPLOYMENT.md", "model-review.md", "frontend/package-lock.json",
    ];

    private static readonly string[] s_preservedDirectories =
    [
        "frontend/node_modules/", "frontend/dist/", "backend/target/",
    ];

    [Fact]
    public async Task The_checked_in_demo_is_what_the_fleet_generates()
    {
        string repository = RepositoryRoot();
        using TemporaryWorkspace workspace = new();

        foreach (string script in Directory.EnumerateFiles(
            Path.Combine(repository, "infra", "legacy-estate", "oracle", "initdb"), "*.sql"))
        {
            workspace.WriteFile($"legacy/forms/db/{Path.GetFileName(script)}", File.ReadAllText(script));
        }

        string formsExport = Path.Combine(repository, "infra", "forms-demo", "estate", "005_bank_account_request_form.xml");
        workspace.WriteFile($"legacy/forms/ui/{Path.GetFileName(formsExport)}", File.ReadAllText(formsExport));

        MigrationExecutionResult result = await new MigrationExecutor(workspace.Root, MigrationExecutor.DefaultAdapters())
            .ExecuteAsync(Request(), "migration-operator@contoso.com");

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseExecutionState.Executed, outcome.State);

        string generatedRoot = workspace.Absolute("out/northstar/application");
        Dictionary<string, string> generated = Directory
            .EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(generatedRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                path => Normalize(File.ReadAllText(path)),
                StringComparer.Ordinal);

        Assert.NotEmpty(generated);

        string demo = Path.Combine(repository, DemoRoot.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Regenerate(demo, generated);
            return;
        }

        List<string> differences = [];

        foreach ((string relativePath, string contents) in generated.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string target = Path.Combine(demo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
            {
                differences.Add($"{relativePath} is missing from {DemoRoot}.");
            }
            else if (!string.Equals(Normalize(File.ReadAllText(target)), contents, StringComparison.Ordinal))
            {
                differences.Add($"{relativePath} in {DemoRoot} differs from what the generator produces.");
            }
        }

        differences.AddRange(Stale(demo, generated).Select(
            path => $"{path} is in {DemoRoot} but the generator no longer produces it."));

        Assert.True(
            differences.Count == 0,
            $"{DemoRoot} is not what the fleet generates. Re-run with {RegenerateVariable}=1 to refresh it.\n"
            + string.Join('\n', differences));
    }

    private static void Regenerate(string demo, Dictionary<string, string> generated)
    {
        foreach (string stale in Stale(demo, generated))
        {
            File.Delete(Path.Combine(demo, stale.Replace('/', Path.DirectorySeparatorChar)));
        }

        foreach ((string relativePath, string contents) in generated)
        {
            string target = Path.Combine(demo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        foreach (string directory in Directory.EnumerateDirectories(demo, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    /// <summary>Generator-owned files still on disk that the current generator no longer emits.</summary>
    private static IEnumerable<string> Stale(string demo, Dictionary<string, string> generated) =>
        Directory.Exists(demo)
            ? Directory.EnumerateFiles(demo, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(demo, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !generated.ContainsKey(path)
                               && !s_preserved.Contains(path, StringComparer.Ordinal)
                               && !s_preservedDirectories.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
            : [];

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-NORTHSTAR",
        ApplicationName = "Northstar Online Banking",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/northstar",
        Evidence =
        [
            Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
            Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
            Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        ],
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
        ExecutionApproval = HumanApproval.Pending,
    };

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
