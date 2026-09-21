// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class ProcessApplicationTestGateway : IApplicationTestGateway
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);

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
            return await RunAsync(
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
                    "const fs=require('node:fs');if(fs.existsSync(process.argv[1]))process.exit(2);const s=require('node:net').connect(80,'169.254.169.254');s.setTimeout(500,()=>process.exit(0));s.on('error',()=>process.exit(0));s.on('connect',()=>process.exit(3));",
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
        CancellationToken cancellationToken)
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
            AddSandboxArguments(start, runner, reportDirectory, readOnlyMavenRepository, readOnlyNpmCache);
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

    private static void AddSandboxArguments(
        ProcessStartInfo start,
        IsolatedRunner runner,
        string reportDirectory,
        string? readOnlyMavenRepository,
        string? readOnlyNpmCache)
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
        AddTriple(start, "--setenv", "HOME", "/home/tester");
        AddTriple(start, "--setenv", "USERPROFILE", "/home/tester");
        AddTriple(start, "--setenv", "TMPDIR", "/tmp");
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