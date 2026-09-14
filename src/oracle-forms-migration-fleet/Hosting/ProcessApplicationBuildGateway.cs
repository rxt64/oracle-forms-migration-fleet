// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>Runs only the two build commands owned by this host; generated content supplies no command text.</summary>
public sealed class ProcessApplicationBuildGateway : IApplicationBuildGateway
{
    private const int MaxOutputCharacters = 32_000;
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim s_buildLock = new(1, 1);

    public Task<ApplicationBuildResult> BuildJavaAsync(
        string workingDirectory,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(
            "Java/Spring Boot",
            OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn",
            ["--batch-mode", "--no-transfer-progress", "-DskipTests", "package"],
            workingDirectory,
            "target",
            cancellationToken);

    public async Task<ApplicationBuildResult> BuildReactAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string executable = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        await s_buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationBuildResult install = await RunAsync(
                "React/TypeScript",
                executable,
                ["install", "--ignore-scripts", "--no-audit", "--no-fund"],
                workingDirectory,
                cancellationToken).ConfigureAwait(false);

            if (!install.Succeeded)
            {
                return install;
            }

            ApplicationBuildResult build = await RunAsync(
                "React/TypeScript",
                executable,
                ["run", "build"],
                workingDirectory,
                cancellationToken).ConfigureAwait(false);

            return build with
            {
                Command = $"{install.Command} && {build.Command}",
                Output = Tail(string.Join('\n', new[] { install.Output, build.Output }.Where(value => value.Length > 0))),
            };
        }
        finally
        {
            DeleteBuildDirectory(workingDirectory, "node_modules");
            DeleteBuildDirectory(workingDirectory, "dist");
            s_buildLock.Release();
        }
    }

    private static async Task<ApplicationBuildResult> RunSerializedAsync(
        string component,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string cleanupDirectory,
        CancellationToken cancellationToken)
    {
        await s_buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(component, executable, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteBuildDirectory(workingDirectory, cleanupDirectory);
            s_buildLock.Release();
        }
    }

    private static async Task<ApplicationBuildResult> RunAsync(
        string component,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        ApplyRestrictedEnvironment(start);

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
            return new ApplicationBuildResult(component, command, false, -1, exception.Message);
        }

        using CancellationTokenSource timeout = new(s_commandTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> standardOutput = ReadTailAsync(process.StandardOutput, linked.Token);
        Task<string> standardError = ReadTailAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(standardOutput, standardError).ConfigureAwait(false);
            return new ApplicationBuildResult(component, command, true, -2, "Build timed out after 10 minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }

        string output = string.Join(
            '\n',
            new[] { await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ApplicationBuildResult(component, command, true, process.ExitCode, Tail(output));
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        string tail = string.Empty;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            tail = Tail(string.Concat(tail, new string(buffer, 0, read)));
        }

        return tail;
    }

    internal static string Tail(string value) =>
        value.Length <= MaxOutputCharacters ? value : value[^MaxOutputCharacters..];

    internal static void ApplyRestrictedEnvironment(ProcessStartInfo start)
    {
        string[] allowed =
        [
            "PATH", "HOME", "USERPROFILE", "LANG", "LC_ALL", "TMPDIR", "TMP", "TEMP",
            "JAVA_HOME", "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "SSL_CERT_FILE",
        ];
        Dictionary<string, string?> retained = allowed.ToDictionary(
            key => key,
            key => start.Environment.TryGetValue(key, out string? value) ? value : null,
            StringComparer.OrdinalIgnoreCase);

        start.Environment.Clear();
        foreach ((string key, string? value) in retained)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                start.Environment[key] = value;
            }
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

    private static void TryKill(Process process)
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

    private static void DeleteBuildDirectory(string workingDirectory, string name)
    {
        try
        {
            Directory.Delete(Path.Combine(workingDirectory, name), recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}