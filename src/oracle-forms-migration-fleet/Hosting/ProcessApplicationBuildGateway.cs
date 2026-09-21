// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>Runs only the two build commands owned by this host; generated content supplies no command text.</summary>
public sealed class ProcessApplicationBuildGateway : IApplicationBuildGateway
{
    private const int MaxOutputCharacters = 32_000;

    public Task<ApplicationBuildResult> BuildJavaAsync(
        string workingDirectory,
        CancellationToken cancellationToken) => BuildAsync(
            "Java/Spring Boot",
            gateway => gateway.BuildBackendAsync(workingDirectory, cancellationToken));

    public async Task<ApplicationBuildResult> BuildReactAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return await BuildAsync(
            "React/TypeScript",
            gateway => gateway.BuildFrontendAsync(workingDirectory, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<ApplicationBuildResult> BuildAsync(
        string component,
        Func<ProcessApplicationTestGateway, Task<ApplicationTestRun>> execute)
    {
        ApplicationTestRun result = await execute(new ProcessApplicationTestGateway()).ConfigureAwait(false);
        return new ApplicationBuildResult(
            component,
            result.Command,
            result.ToolAvailable,
            result.TimedOut ? -2 : result.ExitCode,
            result.Output);
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

}