using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Executes the generated PostgreSQL, and then the generated ASP.NET Core application, against a real
/// PostgreSQL instance.
///
/// The legs are gated, and none of them invents a result. Without
/// <c>PLATFORM_POSTGRES_INTEGRATION_CONNECTION</c> there is no database to execute against; without a .NET
/// toolchain plus <c>RUN_GENERATED_DOTNET_INTEGRATION</c> there is nothing compiling the generated
/// application; without Node plus <c>RUN_GENERATED_NPM_INTEGRATION</c> there is nothing building its front
/// end. A gated leg that returns early has asserted nothing, and the run that skipped it is unverified for
/// those behaviours — not passing.
///
/// One leg is deliberately not gated on a database:
/// <see cref="The_generated_backend_suite_refuses_to_report_a_pass_without_a_target(string)"/> builds and
/// runs the generated suite with no target configured and requires every case to fail. It executes
/// wherever this suite executes, so a generator that emitted no-ops, or a pipeline that never ran the
/// generated suite at all, cannot reach a green result unnoticed.
///
/// A pipeline that intends these legs to run sets <c>REQUIRE_GENERATED_DOTNET_INTEGRATION=true</c> (or
/// <c>REQUIRE_POSTGRES_INTEGRATION=true</c> for the SQL legs), which turns a missing prerequisite from a
/// silent early return into a failure. With <c>GENERATED_APP_INTEGRATION_EVIDENCE</c> set, each leg copies
/// the runner's own report out and writes a per-fixture sentinel, but only after every assertion in that
/// leg has already held — a passing test count cannot stand in for evidence that the leg executed.
/// </summary>
public sealed class DotNetGeneratedApplicationPostgresTests
{
    private const string OptimisticFixture = "meridian";

    /// <summary>
    /// Fixture one's estate with both optional header roles unbound and the columns behind them left to
    /// their Oracle defaults. It exercises the read-back path that has no column to select for either role
    /// and the insert path that never names them, neither of which the other two fixtures reach.
    /// </summary>
    private const string UnboundHeaderFixture = "meridian-unbound";

    private static string? Target =>
        Environment.GetEnvironmentVariable("PLATFORM_POSTGRES_INTEGRATION_CONNECTION") is { Length: > 0 } value
            ? value
            : null;

    private static bool RunGeneratedSuite =>
        string.Equals(
            Environment.GetEnvironmentVariable("RUN_GENERATED_DOTNET_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string? EvidenceDirectory =>
        Environment.GetEnvironmentVariable("GENERATED_APP_INTEGRATION_EVIDENCE") is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// Turns a missing prerequisite into a failure when the pipeline declared the leg required. Outside
    /// such a pipeline the variable is unset, the leg still returns early, and that run stays unverified
    /// for the behaviour rather than being reported as a pass.
    /// </summary>
    private static void RequireConfigured(string missing, string requirement)
    {
        if (string.Equals(
            Environment.GetEnvironmentVariable(requirement), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{missing} is required when {requirement}=true.");
        }
    }

    /// <summary>
    /// Retains the runner's own report and then writes the sentinel. Every call site is placed after the
    /// leg's assertions, so a sentinel cannot exist for a leg that stopped short of asserting them.
    /// </summary>
    private static void RecordEvidence(string leg, string fixture, string? report)
    {
        if (EvidenceDirectory is not { } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        if (report is not null && File.Exists(report))
        {
            File.Copy(
                report,
                Path.Combine(directory, $"{leg}-{fixture}{Path.GetExtension(report)}"),
                overwrite: true);
        }

        File.WriteAllText(Path.Combine(directory, $"{leg}-{fixture}.passed"), leg);
    }

    private static string Quote(string name) => $"\"{name.ToLowerInvariant()}\"";

    /// <summary>
    /// The value that marks a row unavailable, read out of the check constraint the estate declares on the
    /// column the mapping bound to <see cref="MappedFieldRole.ActiveFlag"/>.
    ///
    /// There is no universal opposite of "available": Meridian's domain is A/I and Kestrel's is Y/N, so a
    /// literal chosen here would seed a row one estate's generated schema rejects outright. Deriving it
    /// from the same parsed domain the reader validates the available constant against keeps the two
    /// halves of that pair in agreement no matter what the estate calls them. An estate that declares no
    /// domain, or one that admits more than the two states this scenario distinguishes, is refused rather
    /// than guessed at — a seeded row that does not mean what the test assumes would report the generated
    /// routine as filtering correctly when it never saw an unavailable row at all.
    /// </summary>
    private static string UnavailableFlag(ResolvedObject role)
    {
        ResolvedField flag = role.Require(MappedFieldRole.ActiveFlag);
        string available = role.Constant(MappedFieldRole.ActiveFlag);
        IReadOnlyList<string> domain = TargetMappingReader.DomainValues(role.Table, flag.Column.Name);

        string[] unavailable =
        [.. domain.Where(value => !string.Equals(value, available, StringComparison.Ordinal))];

        Assert.True(
            unavailable.Length == 1,
            $"{role.Role} ('{role.Table.Name}.{flag.Column.Name}') declares the domain " +
            $"[{string.Join(", ", domain)}] against the available constant '{available}'. This scenario " +
            "needs exactly one value that means unavailable, and cannot invent one.");

        return unavailable[0];
    }

    private static (TargetMapping Mapping, IReadOnlyList<GeneratedFile> Files) Generate(string fixture)
    {
        (string schemaText, string manifest, string application) = fixture switch
        {
            OptimisticFixture => (DotNetPilotFixtures.MeridianSchema, DotNetPilotFixtures.MeridianManifest, "Meridian Order Entry"),
            UnboundHeaderFixture => (
                DotNetPilotFixtures.MeridianUnboundHeaderSchema,
                DotNetPilotFixtures.MeridianUnboundHeaderManifest,
                "Meridian Order Entry (unbound header)"),
            "kestrel" => (DotNetPilotFixtures.KestrelSchema, DotNetPilotFixtures.KestrelManifest, "Kestrel Dispatch"),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Unknown fixture."),
        };

        OracleSchema schema = OracleSchemaParser.Parse(schemaText);
        TargetMappingRead read = TargetMappingReader.Read(manifest, schema);
        Assert.True(read.Mapping is not null, string.Join(" | ", read.Rejections));

        ApplicationConversion conversion = DotNetApplicationEmitter.Convert(
            schema, read.Mapping!, application, DatabaseTarget.PostgreSql);
        Assert.NotEmpty(conversion.Files);

        return (read.Mapping!, conversion.Files);
    }

    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "PostgresIntegration")]
    public async Task The_generated_schema_and_routine_enforce_every_acceptance_scenario(string fixture)
    {
        if (Target is not { } connectionString)
        {
            RequireConfigured("PLATFORM_POSTGRES_INTEGRATION_CONNECTION", "REQUIRE_POSTGRES_INTEGRATION");
            return;
        }

        (TargetMapping mapping, IReadOnlyList<GeneratedFile> files) = Generate(fixture);
        await using GeneratedTargetSchema target = await GeneratedTargetSchema.CreateAsync(connectionString, mapping, files);

        ResolvedObject party = mapping.Party;
        ResolvedObject item = mapping.Item;
        ResolvedObject header = mapping.Header;
        ResolvedObject detail = mapping.Detail;

        long available = await target.SeedPartyAsync(mapping, "Available party", party.Constant(MappedFieldRole.ActiveFlag));
        long withdrawn = await target.SeedPartyAsync(mapping, "Withdrawn party", UnavailableFlag(party));
        long first = await target.SeedItemAsync(mapping, "First item", 12.34m, 50, item.Constant(MappedFieldRole.ActiveFlag));
        long second = await target.SeedItemAsync(mapping, "Second item", 0.07m, 50, item.Constant(MappedFieldRole.ActiveFlag));
        long retired = await target.SeedItemAsync(mapping, "Retired item", 1.00m, 50, UnavailableFlag(item));

        // Server-computed decimal totals, atomic header and detail, stock decremented in the same transaction.
        (long headerId, decimal total) = await target.PlaceAsync(available,
            [(first, 3), (second, 7)]);

        int scale = detail.Require(MappedFieldRole.LineAmount).Column.Scale ?? 2;
        decimal expected = decimal.Round(12.34m * 3, scale) + decimal.Round(0.07m * 7, scale);
        Assert.Equal(expected, total);
        Assert.Equal(expected, await target.HeaderTotalAsync(mapping, headerId));
        Assert.Equal(expected, await target.LineSumAsync(mapping, headerId));
        Assert.Equal(2, await target.LineCountAsync(mapping, headerId));
        Assert.Equal(47, await target.StockAsync(mapping, first));
        Assert.Equal(43, await target.StockAsync(mapping, second));

        long headersAfterValid = await target.CountAsync(header.Table.Name);
        long linesAfterValid = await target.CountAsync(detail.Table.Name);

        async Task RefusedAsync(string expectedState, long partyId, params (long Item, long Quantity)[] lines)
        {
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
                () => target.PlaceAsync(partyId, lines));
            Assert.Equal(expectedState, failure.SqlState);

            // Nothing partial survived the refusal.
            Assert.Equal(headersAfterValid, await target.CountAsync(header.Table.Name));
            Assert.Equal(linesAfterValid, await target.CountAsync(detail.Table.Name));
            Assert.Equal(47, await target.StockAsync(mapping, first));
            Assert.Equal(43, await target.StockAsync(mapping, second));
        }

        await RefusedAsync("OFM03", available, (first, 0));
        await RefusedAsync("OFM03", available, (first, -2));
        await RefusedAsync("OFM01", 987654321, (first, 1));
        await RefusedAsync("OFM01", withdrawn, (first, 1));
        await RefusedAsync("OFM02", available, (first, 1), (987654321, 1));
        await RefusedAsync("OFM02", available, (first, 1), (retired, 1));
        await RefusedAsync("OFM04", available, (first, 1), (second, 44));

        // A fractional quantity never becomes a rounded one.
        PostgresException fractional = await Assert.ThrowsAsync<PostgresException>(
            () => target.PlaceRawAsync(available, $"[{{\"itemId\":{first.ToString(CultureInfo.InvariantCulture)},\"quantity\":2.5}}]"));
        Assert.Equal("OFM03", fractional.SqlState);
        Assert.Equal(headersAfterValid, await target.CountAsync(header.Table.Name));

        // A submission carrying the same item twice is checked against the stock it has already drawn.
        await RefusedAsync("OFM04", available, (second, 22), (second, 22));

        RecordEvidence("sql", fixture, report: null);
    }

    /// <summary>
    /// Guards the seed the acceptance scenario depends on, and needs no database to do it.
    ///
    /// The unavailable flag used to be a literal chosen against 'N'. Kestrel's domain is Y/N so it passed;
    /// Meridian's is A/I so every run inserted 'N' into a column whose generated check admits neither, and
    /// the scenario died at the seed before it reached a single assertion about the routine.
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    public void The_seeded_unavailable_flag_is_one_the_generated_schema_admits(string fixture)
    {
        (TargetMapping mapping, IReadOnlyList<GeneratedFile> files) = Generate(fixture);
        string schema = files.Single(file => file.Path == "database/schema.sql").Contents;

        foreach (ResolvedObject role in new[] { mapping.Party, mapping.Item })
        {
            string available = role.Constant(MappedFieldRole.ActiveFlag);
            string unavailable = UnavailableFlag(role);

            Assert.NotEqual(available, unavailable);
            Assert.Contains(
                TargetMappingReader.DomainValues(role.Table, role.Require(MappedFieldRole.ActiveFlag).Column.Name),
                value => string.Equals(value, unavailable, StringComparison.Ordinal));

            // The check the generated DDL carries is the one the insert has to satisfy, so the derived
            // value is held against the emitted text and not only against the parsed source.
            Assert.Contains($"'{unavailable}'", schema, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Concurrent_submissions_cannot_oversell(string fixture)
    {
        if (Target is not { } connectionString)
        {
            RequireConfigured("PLATFORM_POSTGRES_INTEGRATION_CONNECTION", "REQUIRE_POSTGRES_INTEGRATION");
            return;
        }

        (TargetMapping mapping, IReadOnlyList<GeneratedFile> files) = Generate(fixture);
        await using GeneratedTargetSchema target = await GeneratedTargetSchema.CreateAsync(connectionString, mapping, files);

        long party = await target.SeedPartyAsync(mapping, "Party", mapping.Party.Constant(MappedFieldRole.ActiveFlag));
        const long stock = 20;
        const long perSubmission = 3;
        long item = await target.SeedItemAsync(mapping, "Contended", 2.50m, stock, mapping.Item.Constant(MappedFieldRole.ActiveFlag));

        Task<object>[] attempts =
        [
            .. Enumerable.Range(0, 16).Select(async _ =>
            {
                try
                {
                    return (object)await target.PlaceAsync(party, [(item, perSubmission)]);
                }
                catch (PostgresException failure)
                {
                    return failure.SqlState;
                }
            }),
        ];

        object[] outcomes = await Task.WhenAll(attempts);
        int accepted = outcomes.Count(outcome => outcome is not string);

        foreach (string refusal in outcomes.OfType<string>())
        {
            Assert.Contains(refusal, new[] { "OFM04", "OFM05" });
        }

        long remaining = await target.StockAsync(mapping, item);
        Assert.True(remaining >= 0, $"Stock went negative: {remaining.ToString(CultureInfo.InvariantCulture)}.");
        Assert.Equal(stock - (accepted * perSubmission), remaining);
        Assert.Equal(accepted, await target.CountAsync(mapping.Header.Table.Name));
        Assert.Equal(accepted, await target.CountAsync(mapping.Detail.Table.Name));

        RecordEvidence("sql-concurrency", fixture, report: null);
    }

    /// <summary>
    /// Compiles the generated solution. This needs no database: it separates "the generator emitted code a
    /// compiler accepts" from "that code behaves", so a failure says which of the two went wrong.
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task The_generated_solution_compiles(string fixture)
    {
        if (!RunGeneratedSuite)
        {
            RequireConfigured("RUN_GENERATED_DOTNET_INTEGRATION", "REQUIRE_GENERATED_DOTNET_INTEGRATION");
            return;
        }

        using TemporaryWorkspace workspace = new();
        Materialize(workspace, fixture);

        (int exitCode, string output) = await RunAsync(
            DotNetHost,
            workspace.Absolute("generated"),
            ["build", "backend/GeneratedBackend.slnx", "-c", "Release", "--nologo"],
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        Assert.True(exitCode == 0, output);

        RecordEvidence("compile", fixture, report: null);
    }

    /// <summary>
    /// Starts the generated application and drives the request surface that never reaches a database.
    ///
    /// The target it is given cannot be connected to, deliberately: a body the API refuses on its own
    /// terms has to be refused before any connection is attempted, so this leg needs no PostgreSQL and
    /// cannot be satisfied by one. It exists because the two refusals are easy to confuse and a caller
    /// cannot tell them apart from the status alone — a body that does not parse is a transport fault and
    /// answers 400, while a body that parses and carries a quantity of 2.5 has been read and understood
    /// and answers 422 with the code the database would have used. Binding the typed record made every
    /// one of those a 400, which reported a stated business rule as a malformed request.
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task The_generated_api_separates_an_unreadable_body_from_a_refused_one(string fixture)
    {
        if (!RunGeneratedSuite)
        {
            RequireConfigured("RUN_GENERATED_DOTNET_INTEGRATION", "REQUIRE_GENERATED_DOTNET_INTEGRATION");
            return;
        }

        using TemporaryWorkspace workspace = new();
        Materialize(workspace, fixture);
        string generated = workspace.Absolute("generated");

        (int buildExit, string buildOutput) = await RunAsync(
            DotNetHost,
            generated,
            ["build", "backend/Api/Api.csproj", "-c", "Release", "--nologo"],
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        Assert.True(buildExit == 0, buildOutput);

        int port = FreeLoopbackPort();
        ProcessStartInfo start = new(DotNetHost)
        {
            WorkingDirectory = generated,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in new[]
        {
            Path.Combine("backend", "Api", "bin", "Release", "net10.0", "GeneratedApplication.Api.dll"),
            $"--urls=http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            // A real connection string shape pointing at a port nothing listens on. It carries a password
            // so the generated data source takes its local path and asks no identity provider for a token.
            "--ConnectionStrings:Target=Host=127.0.0.1;Port=1;Database=unreachable;Username=unused;Password=unused",
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process app = Process.Start(start) ?? throw new InvalidOperationException("The generated application did not start.");
        Task<string> appOutput = app.StandardOutput.ReadToEndAsync();
        Task<string> appError = app.StandardError.ReadToEndAsync();

        try
        {
            using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}") };
            await WaitForHealthAsync(client, app);

            async Task<(int Status, string Code)> PostAsync(string body, string contentType)
            {
                using StringContent content = new(body, System.Text.Encoding.UTF8, contentType);
                using HttpResponseMessage response = await client.PostAsync("/api/submissions", content);
                string payload = await response.Content.ReadAsStringAsync();

                string code = string.Empty;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(payload);
                    code = document.RootElement.TryGetProperty("code", out JsonElement element)
                        ? element.GetString() ?? string.Empty
                        : string.Empty;
                }
                catch (JsonException)
                {
                    // Not every status carries this application's problem body; the status still stands.
                }

                return ((int)response.StatusCode, code);
            }

            const string valid = "{\"partyId\":1,\"lines\":[{\"itemId\":1,\"quantity\":1}]}";

            Assert.Equal((422, "OFM03"), await PostAsync("{\"partyId\":1,\"lines\":[{\"itemId\":1,\"quantity\":2.5}]}", "application/json"));
            Assert.Equal((422, "OFM03"), await PostAsync("{\"partyId\":1,\"lines\":[{\"itemId\":1,\"quantity\":\"2\"}]}", "application/json"));
            Assert.Equal((422, "OFM03"), await PostAsync("{\"partyId\":1,\"lines\":[{\"itemId\":1}]}", "application/json"));
            Assert.Equal((422, "OFM03"), await PostAsync("{\"partyId\":1,\"lines\":[]}", "application/json"));

            Assert.Equal((400, "OFM00"), await PostAsync("{", "application/json"));
            Assert.Equal((400, "OFM00"), await PostAsync(string.Empty, "application/json"));
            Assert.Equal((415, "OFM00"), await PostAsync(valid, "text/plain"));

            // A body with nothing wrong with it reaches the database and fails there. Without this the
            // same suite would pass against a handler that refused everything.
            (int status, _) = await PostAsync(valid, "application/json");
            Assert.True(
                status is not (400 or 415 or 422),
                $"A well-formed submission was refused with {status.ToString(CultureInfo.InvariantCulture)} before it reached the target.");
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
            }

            await app.WaitForExitAsync();
            await Task.WhenAll(appOutput, appError);
        }

        RecordEvidence("request-contract", fixture, report: null);
    }

    /// <summary>A loopback port nothing is listening on, so the generated host can be given one up front.</summary>
    private static int FreeLoopbackPort()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Waits for the generated host to serve its own health route, and fails if it exits first rather than
    /// letting every later request fail as a connection refusal with nothing to say about why.
    /// </summary>
    private static async Task WaitForHealthAsync(HttpClient client, Process app)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            Assert.False(app.HasExited, "The generated application exited before it served /healthz.");

            try
            {
                using HttpResponseMessage response = await client.GetAsync("/healthz");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250);
        }

        Assert.Fail("The generated application never served /healthz.");
    }

    /// <summary>
    /// Builds and runs the generated acceptance suite with no database configured and
    /// <c>GENERATED_SUITE_REQUIRE_TARGET</c> set, and requires every case to have failed for that reason.
    ///
    /// This is the leg that makes the others hard to fake. It needs no PostgreSQL, so it runs wherever this
    /// suite runs, and it proves three things at once: the generated suite compiles, its cases actually
    /// execute, and a run without a target reports failures instead of an empty green result. A generator
    /// that emitted a suite of no-ops, or a pipeline that skipped the suite entirely, fails here.
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task The_generated_backend_suite_refuses_to_report_a_pass_without_a_target(string fixture)
    {
        if (!RunGeneratedSuite)
        {
            RequireConfigured("RUN_GENERATED_DOTNET_INTEGRATION", "REQUIRE_GENERATED_DOTNET_INTEGRATION");
            return;
        }

        using TemporaryWorkspace workspace = new();
        Materialize(workspace, fixture);
        string reports = workspace.Absolute("reports");

        (int exitCode, string output) = await RunAsync(
            DotNetHost,
            workspace.Absolute("generated"),
            [
                "test", "backend/GeneratedBackend.slnx", "-c", "Release", "--nologo",
                "--results-directory", reports,
                "--logger", $"trx;LogFileName={ReportName}",
            ],
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                // Explicitly absent: the child must not inherit a target from whatever set one for this run.
                ["TARGET_POSTGRES_CONNECTION"] = null,
                ["GENERATED_SUITE_REQUIRE_TARGET"] = "true",
            });

        Assert.True(exitCode != 0, $"The generated suite reported success with no database:\n{output}");

        IReadOnlyDictionary<string, string> outcomes = ReadTrx(Path.Combine(reports, ReportName), output);
        AssertEveryCaseIsPresent(outcomes, output);

        foreach ((string name, string outcome) in outcomes)
        {
            Assert.True(
                outcome == "Failed",
                $"{name} reported '{outcome}' with no database configured. Every case has to fail loudly.");
        }

        RecordEvidence("no-target", fixture, Path.Combine(reports, ReportName));
    }

    /// <summary>
    /// Builds the generated solution and runs its own acceptance suite, which starts the generated ASP.NET
    /// Core application and drives it over HTTP. This is the leg that proves the generated back end runs;
    /// the legs above prove only that the generated SQL does.
    ///
    /// The assertion is over the generated suite's own result file, per case, rather than over its exit
    /// code: an exit code cannot distinguish "every behaviour was exercised and held" from "nothing ran".
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task The_generated_backend_test_suite_executes_against_postgresql(string fixture)
    {
        if (Target is not { } connectionString || !RunGeneratedSuite)
        {
            RequireConfigured(
                Target is null ? "PLATFORM_POSTGRES_INTEGRATION_CONNECTION" : "RUN_GENERATED_DOTNET_INTEGRATION",
                "REQUIRE_GENERATED_DOTNET_INTEGRATION");
            return;
        }

        using TemporaryWorkspace workspace = new();
        Materialize(workspace, fixture);
        string reports = workspace.Absolute("reports");

        (int exitCode, string output) = await RunAsync(
            DotNetHost,
            workspace.Absolute("generated"),
            [
                "test", "backend/GeneratedBackend.slnx", "-c", "Release", "--nologo",
                "--results-directory", reports,
                "--logger", $"trx;LogFileName={ReportName}",
            ],
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TARGET_POSTGRES_CONNECTION"] = connectionString,
                ["GENERATED_SUITE_REQUIRE_TARGET"] = "true",
            });

        Assert.False(
            FleetGuardrails.ContainsPotentialSecret(output) && exitCode != 0,
            "The generated suite failed and its output may carry credential material, so it was withheld.");
        Assert.True(exitCode == 0, output);

        IReadOnlyDictionary<string, string> outcomes = ReadTrx(Path.Combine(reports, ReportName), output);
        AssertEveryCaseIsPresent(outcomes, output);

        foreach ((string name, string outcome) in outcomes)
        {
            Assert.True(outcome == "Passed", $"{name} reported '{outcome}'.");
        }

        RecordEvidence("postgres", fixture, Path.Combine(reports, ReportName));
    }

    /// <summary>
    /// Installs the generated front end from its own lock file, type-checks and builds it, and runs its
    /// interaction tests, asserting over the runner's report rather than its exit code.
    ///
    /// Separately gated because it needs Node and a package feed, neither of which the .NET legs need.
    /// </summary>
    [Theory]
    [InlineData(OptimisticFixture)]
    [InlineData("kestrel")]
    [InlineData(UnboundHeaderFixture)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task The_generated_frontend_builds_and_its_own_tests_pass(string fixture)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("RUN_GENERATED_NPM_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            RequireConfigured("RUN_GENERATED_NPM_INTEGRATION", "REQUIRE_GENERATED_DOTNET_INTEGRATION");
            return;
        }

        using TemporaryWorkspace workspace = new();
        Materialize(workspace, fixture);
        string frontend = workspace.Absolute("generated/frontend");
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
        string[][] steps = [["ci", "--no-audit", "--no-fund"], ["run", "build"]];

        foreach (string[] step in steps)
        {
            (int stepExit, string stepOutput) = await RunAsync(NpmCommand, frontend, step, environment);
            Assert.True(stepExit == 0, $"npm {string.Join(' ', step)} failed:\n{stepOutput}");
        }

        (int exitCode, string output) = await RunAsync(
            NpmCommand,
            frontend,
            ["run", "test", "--", "--reporter=junit", $"--outputFile={FrontendReportName}"],
            environment);

        Assert.True(exitCode == 0, output);

        IReadOnlyDictionary<string, string> outcomes = ReadJUnit(
            Path.Combine(frontend, FrontendReportName), output);

        foreach (string expected in ExpectedFrontendCases)
        {
            // Contains, because the runner reports each case under its describe block. An exact lookup
            // matched nothing and could only ever have been reached once the suite itself passed.
            Assert.True(
                outcomes.Keys.Any(name => name.Contains(expected, StringComparison.Ordinal)),
                $"The generated front-end suite reported no result for '{expected}'. Reported: " +
                $"{string.Join(", ", outcomes.Keys)}\n{output}");
        }

        foreach ((string name, string outcome) in outcomes)
        {
            Assert.True(outcome == "Passed", $"{name} reported '{outcome}'.");
        }

        RecordEvidence("frontend", fixture, Path.Combine(frontend, FrontendReportName));
    }

    private const string ReportName = "generated-backend.trx";

    private const string FrontendReportName = "generated-frontend.junit.xml";

    private static string DotNetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static string NpmCommand => ResolveOnPath(OperatingSystem.IsWindows() ? "npm.cmd" : "npm");

    /// <summary>
    /// The absolute path of an executable on PATH, or the name unchanged when PATH does not carry it.
    ///
    /// Windows runs a bare <c>.cmd</c> through an implicit cmd.exe that takes the batch file's own
    /// directory from the name it was handed, so launching "npm.cmd" makes npm look for its modules under
    /// the working directory and fail before it reads an argument. Handing it the resolved path is what
    /// makes this leg runnable on a workstation as well as on the runner.
    /// </summary>
    private static string ResolveOnPath(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return command;
    }

    /// <summary>
    /// Every case the generated back-end suite is expected to report. Named here rather than counted, so
    /// deleting a behaviour from the generator's template fails this suite instead of quietly shrinking the
    /// generated one.
    /// </summary>
    private static readonly string[] ExpectedBackendCases =
    [
        "The_suite_had_a_target_to_execute_against",
        "A_valid_submission_stores_lines_server_computed_totals_and_decrements_stock",
        "A_non_positive_quantity_changes_nothing",
        "A_fractional_quantity_changes_nothing",
        "A_quantity_that_is_not_a_whole_number_changes_nothing",
        "A_body_that_is_not_json_is_a_bad_request",
        "A_submission_missing_a_required_field_changes_nothing",
        "An_unknown_party_changes_nothing",
        "An_unknown_item_changes_nothing",
        "Insufficient_stock_changes_nothing",
        "Concurrent_submissions_cannot_oversell",
        "Lookups_exclude_rows_the_mapping_marks_unavailable",
        "An_unrouted_api_path_answers_a_json_404",
    ];

    private static readonly string[] ExpectedFrontendCases =
    [
        "renders the total the server stored, not the one the browser previewed",
        "will not add a fractional or non-positive quantity",
        "will not draft more than the reported stock",
        "shows the server's refusal and keeps the draft",
    ];

    private static void Materialize(TemporaryWorkspace workspace, string fixture)
    {
        foreach (GeneratedFile file in Generate(fixture).Files)
        {
            workspace.WriteFile($"generated/{file.Path}", file.Contents);
        }
    }

    private static void AssertEveryCaseIsPresent(IReadOnlyDictionary<string, string> outcomes, string output)
    {
        foreach (string expected in ExpectedBackendCases)
        {
            Assert.True(
                outcomes.Keys.Any(name => name.Contains(expected, StringComparison.Ordinal)),
                $"The generated suite reported no result for '{expected}'. Reported: " +
                $"{string.Join(", ", outcomes.Keys)}\n{output}");
        }
    }

    /// <summary>Reads case names and outcomes out of a TRX. A missing or empty report is a failed run.</summary>
    private static IReadOnlyDictionary<string, string> ReadTrx(string path, string output)
    {
        Assert.True(File.Exists(path), $"The generated suite wrote no result file at {path}.\n{output}");

        Dictionary<string, string> outcomes = new(StringComparer.Ordinal);
        foreach (XElement result in XDocument.Load(path).Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            if (result.Attribute("testName")?.Value is { Length: > 0 } name)
            {
                outcomes[name] = result.Attribute("outcome")?.Value ?? "Unknown";
            }
        }

        Assert.True(outcomes.Count > 0, $"The generated suite executed no case.\n{output}");
        return outcomes;
    }

    /// <summary>Reads case names and outcomes out of a JUnit report produced by the generated front end.</summary>
    private static IReadOnlyDictionary<string, string> ReadJUnit(string path, string output)
    {
        Assert.True(File.Exists(path), $"The generated front end wrote no result file at {path}.\n{output}");

        Dictionary<string, string> outcomes = new(StringComparer.Ordinal);
        foreach (XElement result in XDocument.Load(path).Descendants()
            .Where(element => element.Name.LocalName == "testcase"))
        {
            if (result.Attribute("name")?.Value is not { Length: > 0 } name)
            {
                continue;
            }

            bool failed = result.Elements().Any(child =>
                child.Name.LocalName is "failure" or "error");
            bool skipped = result.Elements().Any(child => child.Name.LocalName == "skipped");
            outcomes[name] = failed ? "Failed" : skipped ? "Skipped" : "Passed";
        }

        Assert.True(outcomes.Count > 0, $"The generated front end executed no case.\n{output}");
        return outcomes;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        ProcessStartInfo start = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string key, string? value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(key);
            }
            else
            {
                start.Environment[key] = value;
            }
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"{fileName} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, $"{await output}\n{await error}");
    }

    /// <summary>A disposable PostgreSQL schema carrying the generated DDL and routine.</summary>
    private sealed class GeneratedTargetSchema : IAsyncDisposable
    {
        private readonly string _administrative;

        private GeneratedTargetSchema(string administrative, string schema, string scoped, string partyKeyType)
        {
            _administrative = administrative;
            Schema = schema;
            ConnectionString = scoped;
            _partyKeyType = partyKeyType;
        }

        private readonly string _partyKeyType;

        public string Schema { get; }

        public string ConnectionString { get; }

        public static async Task<GeneratedTargetSchema> CreateAsync(
            string administrative,
            TargetMapping mapping,
            IReadOnlyList<GeneratedFile> files)
        {
            string schema = "ofm_gen_" + Guid.NewGuid().ToString("N")[..16];

            await using (NpgsqlConnection connection = new(administrative))
            {
                await connection.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE SCHEMA \"{schema}\"", connection);
                await create.ExecuteNonQueryAsync();
            }

            NpgsqlConnectionStringBuilder builder = new(administrative) { SearchPath = schema };
            GeneratedTargetSchema target = new(
                administrative,
                schema,
                builder.ConnectionString,
                mapping.Party.Require(MappedFieldRole.Identifier).PostgreSqlType);

            await using (NpgsqlConnection connection = new(target.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (string path in new[] { "database/schema.sql", "database/routines.sql" })
                {
                    await using NpgsqlCommand command = new(
                        files.Single(file => file.Path == path).Contents,
                        connection);
                    await command.ExecuteNonQueryAsync();
                }
            }

            return target;
        }

        public async Task<long> SeedPartyAsync(TargetMapping mapping, string name, string flag)
        {
            ResolvedObject party = mapping.Party;
            List<string> columns = [Quote(party.Require(MappedFieldRole.DisplayName).Column.Name)];
            List<string> values = ["@name"];

            if (party.Optional(MappedFieldRole.ActiveFlag) is { } active)
            {
                columns.Add(Quote(active.Column.Name));
                values.Add("@flag");
            }

            return await ScalarAsync(
                $"INSERT INTO {Quote(party.Table.Name)} ({string.Join(", ", columns)}) " +
                $"VALUES ({string.Join(", ", values)}) RETURNING {Quote(party.Require(MappedFieldRole.Identifier).Column.Name)}",
                command =>
                {
                    command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                    command.Parameters.Add(new NpgsqlParameter("flag", NpgsqlDbType.Text) { Value = flag });
                });
        }

        public async Task<long> SeedItemAsync(TargetMapping mapping, string name, decimal price, long stock, string flag)
        {
            ResolvedObject item = mapping.Item;
            List<string> columns =
            [
                Quote(item.Require(MappedFieldRole.DisplayName).Column.Name),
                Quote(item.Require(MappedFieldRole.UnitPrice).Column.Name),
                Quote(item.Require(MappedFieldRole.StockOnHand).Column.Name),
            ];
            List<string> values = ["@name", "@price", "@stock"];

            if (item.Optional(MappedFieldRole.ActiveFlag) is { } active)
            {
                columns.Add(Quote(active.Column.Name));
                values.Add("@flag");
            }

            if (item.Optional(MappedFieldRole.ConcurrencyVersion) is { } version)
            {
                columns.Add(Quote(version.Column.Name));
                values.Add("1");
            }

            return await ScalarAsync(
                $"INSERT INTO {Quote(item.Table.Name)} ({string.Join(", ", columns)}) " +
                $"VALUES ({string.Join(", ", values)}) RETURNING {Quote(item.Require(MappedFieldRole.Identifier).Column.Name)}",
                command =>
                {
                    command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                    command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
                    command.Parameters.Add(new NpgsqlParameter("stock", NpgsqlDbType.Bigint) { Value = stock });
                    command.Parameters.Add(new NpgsqlParameter("flag", NpgsqlDbType.Text) { Value = flag });
                });
        }

        public Task<(long HeaderId, decimal Total)> PlaceAsync(long party, IReadOnlyList<(long Item, long Quantity)> lines) =>
            PlaceRawAsync(party, JsonSerializer.Serialize(
                lines.Select(line => new { itemId = line.Item, quantity = line.Quantity })));

        public async Task<(long HeaderId, decimal Total)> PlaceRawAsync(long party, string lines)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                $"SELECT header_id, header_total FROM \"{DotNetApplicationEmitter.RoutineName}\"(@party::{_partyKeyType}, @lines::jsonb)",
                connection);
            command.Parameters.Add(new NpgsqlParameter("party", NpgsqlDbType.Bigint) { Value = party });
            command.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Jsonb) { Value = lines });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "The generated routine returned no row.");
            return (
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture));
        }

        public Task<long> StockAsync(TargetMapping mapping, long item) => ScalarAsync(
            $"SELECT {Quote(mapping.Item.Require(MappedFieldRole.StockOnHand).Column.Name)} " +
            $"FROM {Quote(mapping.Item.Table.Name)} " +
            $"WHERE {Quote(mapping.Item.Require(MappedFieldRole.Identifier).Column.Name)} = @id",
            command => command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = item }));

        public Task<long> LineCountAsync(TargetMapping mapping, long headerId) => ScalarAsync(
            $"SELECT count(*) FROM {Quote(mapping.Detail.Table.Name)} " +
            $"WHERE {Quote(mapping.Detail.Require(MappedFieldRole.ParentReference).Column.Name)} = @id",
            command => command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = headerId }));

        public Task<long> CountAsync(string table) => ScalarAsync($"SELECT count(*) FROM {Quote(table)}", _ => { });

        public async Task<decimal> HeaderTotalAsync(TargetMapping mapping, long headerId) => await DecimalAsync(
            $"SELECT {Quote(mapping.Header.Require(MappedFieldRole.TotalAmount).Column.Name)} " +
            $"FROM {Quote(mapping.Header.Table.Name)} " +
            $"WHERE {Quote(mapping.Header.Require(MappedFieldRole.Identifier).Column.Name)} = @id",
            headerId);

        public async Task<decimal> LineSumAsync(TargetMapping mapping, long headerId) => await DecimalAsync(
            $"SELECT coalesce(sum({Quote(mapping.Detail.Require(MappedFieldRole.LineAmount).Column.Name)}), 0) " +
            $"FROM {Quote(mapping.Detail.Table.Name)} " +
            $"WHERE {Quote(mapping.Detail.Require(MappedFieldRole.ParentReference).Column.Name)} = @id",
            headerId);

        private async Task<decimal> DecimalAsync(string sql, long id)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private async Task<long> ScalarAsync(string sql, Action<NpgsqlCommand> bind)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            bind(command);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await using NpgsqlConnection connection = new(_administrative);
            await connection.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
