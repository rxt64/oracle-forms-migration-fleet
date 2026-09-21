// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Hosting;

public enum SourceEnvironmentProbeStatus
{
    Verified,
    BlockedPrerequisite,
    Contradicted,
    Rejected,
}

public enum SourcePrerequisite
{
    OracleFormsInstallation,
    OracleFormsOpenApiLibraries,
    OracleClientConnectivity,
    OperatorSuppliedExport,
    WorkerHostArchitecture,
}

public sealed record SourceExpectedVersions(string Forms, string Database);

public sealed record SourceObservedVersions(string? Forms, string? Database);

public sealed record SourceCapabilityResult(
    string Id,
    SourceEnvironmentProbeStatus State,
    SourcePrerequisite Prerequisite,
    string? RequiredRelease,
    string? RequiredArchitecture,
    string? RequiredHost,
    string Remediation);

public sealed record SourceEnvironmentProbeResult(
    int SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    SourceEnvironmentProbeStatus Status,
    DateTimeOffset ProbedUtc,
    SourceConnector Connector,
    SourceExpectedVersions Expected,
    SourceObservedVersions Observed,
    IReadOnlyList<SourceCapabilityResult> Capabilities,
    IReadOnlyList<SourcePrerequisite> BlockedPrerequisites,
    IReadOnlyList<string> Contradictions);

public interface ISourceEnvironmentProbe
{
    string Description { get; }
    Task<SourceEnvironmentProbeResult> ProbeAsync(
        SourceEnvironmentProfile profile,
        CancellationToken cancellationToken);
}

public sealed class UnavailableSourceEnvironmentProbe(Func<DateTimeOffset>? clock = null) : ISourceEnvironmentProbe
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string Description => "No native Oracle source worker is configured.";

    public Task<SourceEnvironmentProbeResult> ProbeAsync(
        SourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        List<SourceCapabilityResult> capabilities = [];
        if (profile.Connector == SourceConnector.FormsBuilderWorker)
        {
            capabilities.Add(new(
                "forms.module.extract",
                SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.OracleFormsInstallation,
                "6.0.8.22.1",
                "x86",
                "WindowsWorker",
                "operator.install.forms.6i.worker"));
            capabilities.Add(new(
                "forms.openapi.load",
                SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.OracleFormsOpenApiLibraries,
                "6.0.8.22.1",
                "x86",
                "WindowsWorker",
                "operator.supply.authorized.forms.libraries"));
            capabilities.Add(new(
                "forms.worker.architecture",
                SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.WorkerHostArchitecture,
                "6.0.8.22.1",
                "x86",
                "WindowsWorker",
                "operator.provision.windows.x86.worker"));
        }
        if (profile.Connector is SourceConnector.FormsBuilderWorker or SourceConnector.OracleDatabaseReader)
        {
            capabilities.Add(new(
                "oracle.schema.extract",
                SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.OracleClientConnectivity,
                null,
                null,
                "SourceGateway",
                "operator.configure.oracle.source.connector"));
        }
        if (profile.Connector == SourceConnector.OperatorSuppliedExport)
        {
            capabilities.Add(new(
                "source.export.collect",
                SourceEnvironmentProbeStatus.BlockedPrerequisite,
                SourcePrerequisite.OperatorSuppliedExport,
                null,
                null,
                "OperatorUpload",
                "operator.supply.authorized.source.export"));
        }

        SourcePrerequisite[] blocked = [.. capabilities.Select(capability => capability.Prerequisite).Distinct()];
        return Task.FromResult(new SourceEnvironmentProbeResult(
            1,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            SourceEnvironmentProbeStatus.BlockedPrerequisite,
            _clock(),
            profile.Connector,
            new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
            new(null, null),
            capabilities,
            blocked,
            []));
    }
}