// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class ProcessApplicationTestGateway : IApplicationTestGateway
{
    private const string DotNetTrxFileName = "dotnet-backend.trx";
    private const string DotNetReportFileName = "dotnet-backend.xml";

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);
    private static readonly XNamespace s_trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static readonly string s_dotNetSolution =
        Path.GetFileName(GeneratedApplicationLayout.BackendDescriptor(BackEndStack.AspNetCore));

    /// <summary>
    /// The .NET CLI's view of the sandbox. Caches and the CLI home are redirected onto writable sandbox
    /// paths; the generated suite is told a target is expected while being given none, so a run in here
    /// fails every case loudly instead of reporting a green result it never earned. Handing it a real
    /// connection string would put a target credential inside generated code, which this host will not do.
    /// </summary>
    internal static readonly (string Name, string Value)[] DotNetSandboxEnvironment =
    [
        ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
        ("DOTNET_NOLOGO", "1"),
        ("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"),
        ("DOTNET_CLI_UI_LANGUAGE", "en"),
        ("DOTNET_CLI_HOME", "/home/tester"),
        ("MSBUILDDISABLENODEREUSE", "1"),
        ("NUGET_PACKAGES", "/home/tester/.nuget/packages"),
        ("NUGET_HTTP_CACHE_PATH", "/tmp/nuget-http"),
        ("NUGET_FALLBACK_PACKAGES", ""),
        ("TARGET_POSTGRES_CONNECTION", ""),
        ("GENERATED_SUITE_REQUIRE_TARGET", "true"),
    ];

    public async Task<ApplicationTestRun> RunBackendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("mvn test");
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);
        string? repository = MavenRepository();
        if (repository is null)
        {
            return new ApplicationTestRun("mvn test", true, false, -1, "The read-only Maven dependency cache is unavailable.", SetupFailed: true);
        }

        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationTestRun result = await RunAsync(
                "mvn",
                [
                    "--batch-mode",
                    "--no-transfer-progress",
                    "--offline",
                    "-Dmaven.repo.local=/m2",
                    "-Dsurefire.reportsDirectory=/reports",
                    "test",
                ],
                runner,
                reportDirectory,
                repository,
                readOnlyNpmCache: null,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
            CopyJUnitReports(
                Path.Combine(runner.WorkingDirectory, "target", "surefire-reports"),
                reportDirectory);
            return result;
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    public async Task<ApplicationTestRun> RunFrontendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("npm run test");
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);
        string? npmCache = NpmCache();
        if (npmCache is null)
        {
            return new ApplicationTestRun("npm ci", true, false, -1, "The read-only npm dependency cache is unavailable.", SetupFailed: true);
        }
        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationTestRun install = await RunAsync(
                "npm",
                ["ci", "--offline", "--ignore-scripts", "--no-audit", "--no-fund", "--cache=/npm-cache"],
                runner,
                reportDirectory,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: npmCache,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
            if (!Succeeded(install))
            {
                return install with { SetupFailed = true };
            }

            ApplicationTestRun test = await RunAsync(
                "npm",
                ["run", "test", "--", "--reporter=junit", "--outputFile=/reports/vitest.xml"],
                runner,
                reportDirectory,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: npmCache,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
            return test with
            {
                Command = $"{install.Command} && {test.Command}",
                Output = ProcessApplicationBuildGateway.Tail(
                    string.Join('\n', new[] { install.Output, test.Output }.Where(value => value.Length > 0))),
            };
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    /// <summary>
    /// Restores, builds, and runs the generated ASP.NET Core suite inside the same network-isolated sandbox
    /// the other stacks use, then writes the one JUnit report this host authors from the runner's TRX. The
    /// sandbox reaches no database, and the suite is told to say so, so this leg reports a compile and a
    /// loud per-case "no target" rather than a pass the run did not earn.
    /// </summary>
    public async Task<ApplicationTestRun> RunDotNetBackendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("dotnet test");
        }

        string? packages = NuGetPackageSource();
        if (packages is null)
        {
            return new ApplicationTestRun("dotnet restore", true, false, -1, "The read-only NuGet dependency cache is unavailable.", SetupFailed: true);
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);

        // The generated suite writes into this directory, not into the caller's. The host is then the only
        // author of the JUnit report the verification phase reads, so generated code cannot forge a pass.
        string results = Path.Combine(runner.Root, "results");
        Directory.CreateDirectory(results);

        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationTestRun prepared = await RestoreAndBuildDotNetAsync(runner, results, packages, cancellationToken)
                .ConfigureAwait(false);
            if (!Succeeded(prepared))
            {
                return prepared with { SetupFailed = true };
            }

            ApplicationTestRun test = await RunAsync(
                "dotnet",
                [
                    "test",
                    s_dotNetSolution,
                    "-c", "Release",
                    "--no-restore",
                    "--no-build",
                    "--nologo",
                    "--results-directory", "/reports",
                    "--logger", $"trx;LogFileName={DotNetTrxFileName}",
                ],
                runner,
                results,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: null,
                isolateNetwork: true,
                cancellationToken: cancellationToken,
                readOnlyNuGetSource: packages,
                sandboxEnvironment: DotNetSandboxEnvironment).ConfigureAwait(false);

            return WriteDotNetReport(
                test with
                {
                    Command = $"{prepared.Command} && {test.Command}",
                    Output = Combine(prepared.Output, test.Output),
                },
                results,
                reportDirectory);
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    internal async Task<ApplicationTestRun> BuildDotNetBackendAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("dotnet build");
        }

        string? packages = NuGetPackageSource();
        if (packages is null)
        {
            return new ApplicationTestRun("dotnet restore", true, false, -1, "The read-only NuGet dependency cache is unavailable.", SetupFailed: true);
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);
        string results = Path.Combine(runner.Root, "results");
        Directory.CreateDirectory(results);
        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestoreAndBuildDotNetAsync(runner, results, packages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    private static async Task<ApplicationTestRun> RestoreAndBuildDotNetAsync(
        IsolatedRunner runner,
        string results,
        string packages,
        CancellationToken cancellationToken)
    {
        // --source is the only feed, so a restore cannot silently reach past the offline cache even if the
        // generated output ships a NuGet.config naming a remote one.
        ApplicationTestRun restore = await RunAsync(
            "dotnet",
            ["restore", s_dotNetSolution, "--source", "/nuget", "--disable-parallel", "--verbosity", "minimal"],
            runner,
            results,
            readOnlyMavenRepository: null,
            readOnlyNpmCache: null,
            isolateNetwork: true,
            cancellationToken: cancellationToken,
            readOnlyNuGetSource: packages,
            sandboxEnvironment: DotNetSandboxEnvironment).ConfigureAwait(false);
        if (!Succeeded(restore))
        {
            return restore;
        }

        ApplicationTestRun build = await RunAsync(
            "dotnet",
            ["build", s_dotNetSolution, "-c", "Release", "--no-restore", "--nologo"],
            runner,
            results,
            readOnlyMavenRepository: null,
            readOnlyNpmCache: null,
            isolateNetwork: true,
            cancellationToken: cancellationToken,
            readOnlyNuGetSource: packages,
            sandboxEnvironment: DotNetSandboxEnvironment).ConfigureAwait(false);
        return build with
        {
            Command = $"{restore.Command} && {build.Command}",
            Output = Combine(restore.Output, build.Output),
        };
    }

    internal async Task<ApplicationTestRun> BuildBackendAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("mvn package");
        }

        string? repository = MavenRepository();
        if (repository is null)
        {
            return new ApplicationTestRun("mvn package", true, false, -1, "The read-only Maven dependency cache is unavailable.", SetupFailed: true);
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);
        string reports = Path.Combine(runner.Root, "reports");
        Directory.CreateDirectory(reports);
        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(
                "mvn",
                ["--batch-mode", "--no-transfer-progress", "--offline", "-Dmaven.repo.local=/m2", "-DskipTests", "package"],
                runner,
                reports,
                repository,
                readOnlyNpmCache: null,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    internal async Task<ApplicationTestRun> BuildFrontendAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("npm run build");
        }

        string? npmCache = NpmCache();
        if (npmCache is null)
        {
            return new ApplicationTestRun("npm ci", true, false, -1, "The read-only npm dependency cache is unavailable.", SetupFailed: true);
        }

        using IsolatedRunner runner = IsolatedRunner.Create(workingDirectory);
        string reports = Path.Combine(runner.Root, "reports");
        Directory.CreateDirectory(reports);
        await ApplicationProcessGate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationTestRun install = await RunAsync(
                "npm",
                ["ci", "--offline", "--ignore-scripts", "--no-audit", "--no-fund", "--cache=/npm-cache"],
                runner,
                reports,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: npmCache,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
            if (!Succeeded(install))
            {
                return install with { SetupFailed = true };
            }

            ApplicationTestRun build = await RunAsync(
                "npm",
                ["run", "build"],
                runner,
                reports,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: npmCache,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
            return build with
            {
                Command = $"{install.Command} && {build.Command}",
                Output = ProcessApplicationBuildGateway.Tail(
                    string.Join('\n', new[] { install.Output, build.Output }.Where(value => value.Length > 0))),
            };
        }
        finally
        {
            ApplicationProcessGate.Lock.Release();
        }
    }

    internal async Task<ApplicationTestRun> VerifySandboxBoundaryAsync(
        string forbiddenHostPath,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable())
        {
            return SandboxUnavailable("sandbox boundary probe");
        }

        string source = Path.Combine(Path.GetTempPath(), $"ofm-sandbox-source-{Guid.NewGuid():N}");
        string reports = Path.Combine(Path.GetTempPath(), $"ofm-sandbox-reports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(reports);
        try
        {
            using IsolatedRunner runner = IsolatedRunner.Create(source);
            return await RunAsync(
                "node",
                [
                    "-e",
                    "const fs=require('node:fs'),dns=require('node:dns').promises,net=require('node:net');if(fs.existsSync(process.argv[1]))process.exit(2);dns.lookup('localhost').then(({address})=>{if(address!=='127.0.0.1'&&address!=='::1')process.exit(4);const s=net.connect(80,'169.254.169.254');s.setTimeout(500,()=>process.exit(0));s.on('error',()=>process.exit(0));s.on('connect',()=>process.exit(3));}).catch(()=>process.exit(5));",
                    forbiddenHostPath,
                ],
                runner,
                reports,
                readOnlyMavenRepository: null,
                readOnlyNpmCache: null,
                isolateNetwork: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(reports, recursive: true);
        }
    }

    private static async Task<ApplicationTestRun> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IsolatedRunner runner,
        string reportDirectory,
        string? readOnlyMavenRepository,
        string? readOnlyNpmCache,
        bool isolateNetwork,
        CancellationToken cancellationToken,
        string? readOnlyNuGetSource = null,
        IReadOnlyList<(string Name, string Value)>? sandboxEnvironment = null)
    {
        ProcessStartInfo start = new(isolateNetwork ? "/usr/bin/bwrap" : executable)
        {
            WorkingDirectory = runner.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        ProcessApplicationBuildGateway.ApplyRestrictedEnvironment(start);
        start.Environment["HOME"] = runner.HomeDirectory;
        start.Environment["USERPROFILE"] = runner.HomeDirectory;
        start.Environment["TMPDIR"] = runner.TempDirectory;
        start.Environment["TMP"] = runner.TempDirectory;
        start.Environment["TEMP"] = runner.TempDirectory;

        if (isolateNetwork)
        {
            AddSandboxArguments(start, runner, reportDirectory, readOnlyMavenRepository, readOnlyNpmCache, readOnlyNuGetSource, sandboxEnvironment);
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        string command = $"{executable} {string.Join(' ', arguments)}";
        using Process process = new() { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new ApplicationTestRun(command, false, false, -1, exception.Message);
        }

        using CancellationTokenSource timeout = new(s_timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> output = process.StandardOutput.ReadToEndAsync(linked.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(output, error).ConfigureAwait(false);
            return new ApplicationTestRun(command, true, true, -2, "Test execution timed out after 10 minutes.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(output, error).ConfigureAwait(false);
            throw;
        }

        string combined = string.Join('\n', new[] { await output.ConfigureAwait(false), await error.ConfigureAwait(false) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ApplicationTestRun(command, true, false, process.ExitCode, ProcessApplicationBuildGateway.Tail(combined));
    }

    private static bool Succeeded(ApplicationTestRun run) =>
        run.ToolAvailable && !run.TimedOut && run.ExitCode == 0;

    private static void CopyJUnitReports(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        string[] reports = Directory.GetFiles(sourceDirectory, "TEST-*.xml", SearchOption.TopDirectoryOnly);
        if (reports.Length > 1_000)
        {
            throw new IOException("Generated backend tests produced more than 1,000 JUnit reports.");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (string report in reports)
        {
            FileInfo file = new(report);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 4 * 1024 * 1024)
            {
                throw new IOException("Generated backend tests produced an unsafe JUnit report file.");
            }
            File.Copy(report, Path.Combine(destinationDirectory, file.Name), overwrite: false);
        }
    }

    private static async Task ObserveAsync(params Task<string>[] readers)
    {
        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool SandboxAvailable() =>
        OperatingSystem.IsLinux() && File.Exists("/usr/bin/bwrap");

    private static ApplicationTestRun SandboxUnavailable(string command) => new(
        command,
        ToolAvailable: false,
        TimedOut: false,
        ExitCode: -1,
        Output: "Network-isolated bubblewrap execution is unavailable; generated tests were not started.");

    private static string? MavenRepository()
    {
        string repository = Environment.GetEnvironmentVariable("WORKBENCH_VERIFICATION_MAVEN_REPOSITORY")
            ?? "/opt/ofm-verification-cache/m2";
        return Directory.Exists(repository) ? repository : null;
    }

    private static string? NpmCache()
    {
        string cache = Environment.GetEnvironmentVariable("WORKBENCH_VERIFICATION_NPM_CACHE")
            ?? "/opt/ofm-verification-cache/npm";
        return Directory.Exists(cache) ? cache : null;
    }

    /// <summary>The offline NuGet folder feed; it holds .nupkg files, mirroring the Maven and npm caches.</summary>
    private static string? NuGetPackageSource()
    {
        string cache = Environment.GetEnvironmentVariable("WORKBENCH_VERIFICATION_NUGET_CACHE")
            ?? "/opt/ofm-verification-cache/nuget";
        return Directory.Exists(cache) ? cache : null;
    }

    private static string Combine(params string[] outputs) =>
        ProcessApplicationBuildGateway.Tail(string.Join('\n', outputs.Where(value => value.Length > 0)));

    /// <summary>
    /// Translates the runner's TRX into the single JUnit document the verification phase reads, and refuses
    /// to let a run that executed nothing look like a pass.
    /// </summary>
    internal static ApplicationTestRun WriteDotNetReport(
        ApplicationTestRun run,
        string resultsDirectory,
        string reportDirectory)
    {
        List<TrxCase> cases;
        try
        {
            cases = ReadTrx(resultsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            return Unusable(exception.Message);
        }

        if (cases.Count == 0)
        {
            return Unusable("dotnet test recorded no TRX result, so no generated test outcome exists to read.");
        }

        int failures = cases.Count(result => result.Failure is not null);
        int skipped = cases.Count(result => result.Skipped);
        Directory.CreateDirectory(reportDirectory);
        new XDocument(new XElement(
            "testsuite",
            new XAttribute("name", "generated-dotnet-backend"),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors", 0),
            new XAttribute("skipped", skipped),
            cases.Select(result => new XElement(
                "testcase",
                new XAttribute("classname", result.ClassName),
                new XAttribute("name", result.Name),
                result.Failure is not null
                    ? new XElement("failure", new XAttribute("message", result.Failure))
                    : result.Skipped ? new XElement("skipped") : null))))
            .Save(Path.Combine(reportDirectory, DotNetReportFileName));

        return cases.Count == skipped
            ? run with
            {
                ExitCode = run.ExitCode == 0 ? -1 : run.ExitCode,
                Output = Combine(run.Output, $"All {cases.Count} generated .NET cases were skipped, so nothing was asserted."),
            }
            : run;

        ApplicationTestRun Unusable(string reason) => run with
        {
            ExitCode = run.ExitCode == 0 ? -1 : run.ExitCode,
            Output = Combine(run.Output, reason),
        };
    }

    private static List<TrxCase> ReadTrx(string resultsDirectory)
    {
        List<TrxCase> cases = [];
        string[] files = Directory.Exists(resultsDirectory)
            ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            : [];
        if (files.Length > 64)
        {
            throw new InvalidDataException("The generated .NET test run produced more than 64 TRX result files.");
        }

        foreach (string file in files)
        {
            FileInfo info = new(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 64L * 1024 * 1024)
            {
                throw new IOException("The generated .NET test run produced an unsafe TRX result file.");
            }

            XElement root = XDocument.Load(file).Root
                ?? throw new InvalidDataException("The TRX result file has no root element.");

            Dictionary<string, string> classNames = new(StringComparer.Ordinal);
            foreach (XElement unit in root.Descendants(s_trx + "UnitTest"))
            {
                if ((string?)unit.Attribute("id") is { Length: > 0 } id)
                {
                    classNames[id] = (string?)unit.Element(s_trx + "TestMethod")?.Attribute("className")
                        ?? "GeneratedApplication.Tests";
                }
            }

            foreach (XElement result in root.Descendants(s_trx + "UnitTestResult"))
            {
                string name = Text((string?)result.Attribute("testName") ?? "unnamed", 400);
                string outcome = (string?)result.Attribute("outcome") ?? "Failed";
                string className = (string?)result.Attribute("testId") is { Length: > 0 } testId &&
                    classNames.TryGetValue(testId, out string? declared)
                        ? Text(declared, 400)
                        : "GeneratedApplication.Tests";
                bool skipped = outcome is "NotExecuted" or "Inconclusive" or "Pending" or "Disconnected" or "Warning";
                bool passed = outcome == "Passed";
                string? failure = passed || skipped
                    ? null
                    : Text(
                        result.Descendants(s_trx + "Message").FirstOrDefault()?.Value is { Length: > 0 } message
                            ? $"{outcome}: {message}"
                            : outcome,
                        2_000);
                cases.Add(new TrxCase(className, name, failure, skipped));
            }
        }

        return cases;
    }

    /// <summary>Bounds a TRX string and drops characters XML cannot carry in an attribute.</summary>
    private static string Text(string value, int limit)
    {
        StringBuilder builder = new(Math.Min(value.Length, limit));
        foreach (char character in value)
        {
            if (builder.Length == limit)
            {
                break;
            }

            builder.Append(char.IsControl(character) && character is not '\t' ? ' ' : character);
        }

        return builder.ToString();
    }

    private readonly record struct TrxCase(string ClassName, string Name, string? Failure, bool Skipped);

    private static void AddSandboxArguments(
        ProcessStartInfo start,
        IsolatedRunner runner,
        string reportDirectory,
        string? readOnlyMavenRepository,
        string? readOnlyNpmCache,
        string? readOnlyNuGetSource = null,
        IReadOnlyList<(string Name, string Value)>? sandboxEnvironment = null)
    {
        foreach (string argument in new[]
        {
            "--unshare-net", "--unshare-pid", "--unshare-ipc", "--unshare-uts",
            "--new-session",
        })
        {
            start.ArgumentList.Add(argument);
        }

        AddReadOnlyMount(start, "/usr");
        AddReadOnlyMount(start, "/bin");
        AddReadOnlyMount(start, "/lib");
        AddReadOnlyMount(start, "/lib64");
        AddReadOnlyMount(start, "/opt");
        AddReadOnlyMount(start, "/etc/alternatives");
        AddReadOnlyMount(start, "/etc/ssl");
        AddReadOnlyMount(start, "/etc/java");
        AddReadOnlyMount(start, "/etc/passwd");
        AddReadOnlyMount(start, "/etc/group");
        AddReadOnlyMount(start, "/etc/nsswitch.conf");
        AddReadOnlyMount(start, "/etc/hosts");
        AddReadOnlyMount(start, "/etc/localtime");

        AddPair(start, "--dev", "/dev");
        AddPair(start, "--proc", "/proc");
        AddPair(start, "--tmpfs", "/tmp");
        AddMount(start, "--bind", runner.WorkingDirectory, "/work");
        AddMount(start, "--bind", runner.HomeDirectory, "/home/tester");
        AddMount(start, "--bind", reportDirectory, "/reports");
        if (readOnlyMavenRepository is not null)
        {
            AddMount(start, "--ro-bind", readOnlyMavenRepository, "/m2");
        }
        if (readOnlyNpmCache is not null)
        {
            AddMount(start, "--ro-bind", readOnlyNpmCache, "/npm-cache");
        }
        if (readOnlyNuGetSource is not null)
        {
            AddMount(start, "--ro-bind", readOnlyNuGetSource, "/nuget");
        }
        AddTriple(start, "--setenv", "HOME", "/home/tester");
        AddTriple(start, "--setenv", "USERPROFILE", "/home/tester");
        AddTriple(start, "--setenv", "TMPDIR", "/tmp");
        foreach ((string name, string value) in sandboxEnvironment ?? [])
        {
            AddTriple(start, "--setenv", name, value);
        }
        AddPair(start, "--chdir", "/work");
    }

    private static void AddReadOnlyMount(ProcessStartInfo start, string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            AddMount(start, "--ro-bind", path, path);
        }
    }

    private static void AddMount(ProcessStartInfo start, string option, string source, string destination)
    {
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);
    }

    private static void AddPair(ProcessStartInfo start, string option, string value)
    {
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(value);
    }

    private static void AddTriple(ProcessStartInfo start, string option, string name, string value)
    {
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private sealed class IsolatedRunner : IDisposable
    {
        private IsolatedRunner(string root)
        {
            Root = root;
            WorkingDirectory = Path.Combine(root, "work");
            HomeDirectory = Path.Combine(root, "home");
            TempDirectory = Path.Combine(root, "tmp");
        }

        public string Root { get; }
        public string WorkingDirectory { get; }
        public string HomeDirectory { get; }
        public string TempDirectory { get; }

        public static IsolatedRunner Create(string source)
        {
            IsolatedRunner runner = new(Path.Combine(Path.GetTempPath(), $"ofm-app-test-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(runner.WorkingDirectory);
            Directory.CreateDirectory(runner.HomeDirectory);
            Directory.CreateDirectory(runner.TempDirectory);
            Copy(source, runner.WorkingDirectory);
            return runner;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static void Copy(string source, string destination)
        {
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Generated application verification refuses symbolic links and junctions.");
                }
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Generated application verification refuses symbolic links and junctions.");
                }
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }
    }
}