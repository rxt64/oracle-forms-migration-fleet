// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.SourceWorker;

internal static class Program
{
    private const int MaxRequestBytes = 8 * 1024;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--probe"])
        {
            await Console.Error.WriteLineAsync("This worker accepts only --probe until a validated native provider is installed.");
            return 64;
        }

        WorkerProbeRequest? request;
        try
        {
            await using Stream input = Console.OpenStandardInput();
            using MemoryStream retained = new(MaxRequestBytes);
            byte[] buffer = new byte[1024];
            int total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > MaxRequestBytes)
                {
                    await Console.Error.WriteLineAsync("The worker probe request exceeded its byte limit.");
                    return 65;
                }
                retained.Write(buffer, 0, read);
            }
            retained.Position = 0;
            request = await JsonSerializer.DeserializeAsync<WorkerProbeRequest>(retained, s_json);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            await Console.Error.WriteLineAsync("The worker probe request was malformed.");
            return 65;
        }
        if (request is null || request.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(request.ExpectedFormsRelease) || request.ExpectedFormsRelease.Length > 40 ||
            string.IsNullOrWhiteSpace(request.SourceEnvironmentId) || request.SourceEnvironmentId.Length > 63)
        {
            await Console.Error.WriteLineAsync("The worker probe request was rejected.");
            return 65;
        }

        List<WorkerCapability> capabilities = [];
        if (!OperatingSystem.IsWindows())
        {
            capabilities.Add(Blocked("forms.worker.os", "WorkerHostArchitecture", "WindowsWorker", null,
                "operator.provision.windows.x86.worker"));
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
        {
            capabilities.Add(Blocked("forms.worker.architecture", "WorkerHostArchitecture", "WindowsWorker", "x86",
                "operator.provision.windows.x86.worker"));
        }
        if (!string.Equals(request.ExpectedFormsRelease, "6.0.8.22.1", StringComparison.Ordinal))
        {
            capabilities.Add(Blocked("forms.worker.release", "OracleFormsInstallation", "WindowsWorker", "x86",
                "operator.install.forms.6i.worker"));
        }

        capabilities.Add(Blocked("forms.installation", "OracleFormsInstallation", "WindowsWorker", "x86",
            "operator.install.forms.6i.worker"));

        // The worker does not inspect arbitrary paths or PATH. A future native provider receives a configured
        // home alias resolved by its own service wrapper. This executable deliberately ships with no NDAPI or
        // Oracle client reference and therefore cannot claim the native libraries are present.
        capabilities.Add(Blocked("forms.openapi.load", "OracleFormsOpenApiLibraries", "WindowsWorker", "x86",
            "operator.supply.authorized.forms.libraries"));
        capabilities.Add(Blocked("forms.module.extract", "OperatorSuppliedExport", "WindowsWorker", "x86",
            "operator.supply.authorized.source.export"));

        WorkerProbeResult result = new(
            1,
            request.SourceEnvironmentId,
            "BlockedPrerequisite",
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease,
            capabilities);
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, s_json);
        return 2;
    }

    private static WorkerCapability Blocked(
        string id,
        string prerequisite,
        string requiredHost,
        string? requiredArchitecture,
        string remediation) =>
        new(id, "BlockedPrerequisite", prerequisite, "6.0.8.22.1", requiredArchitecture, requiredHost, remediation);
}

public sealed record WorkerProbeRequest(int SchemaVersion, string SourceEnvironmentId, string ExpectedFormsRelease);

public sealed record WorkerCapability(
    string Id,
    string State,
    string Prerequisite,
    string? RequiredRelease,
    string? RequiredArchitecture,
    string? RequiredHost,
    string Remediation);

public sealed record WorkerProbeResult(
    int SchemaVersion,
    string SourceEnvironmentId,
    string Status,
    DateTimeOffset ProbedUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    string ExpectedFormsRelease,
    IReadOnlyList<WorkerCapability> Capabilities);