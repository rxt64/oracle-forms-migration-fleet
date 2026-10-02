// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Reports, once per start, whether this replica has a source gateway at all.
///
/// Startup conclusions were previously written with <c>Console.WriteLine</c>, which reaches the
/// container's stdout and no telemetry pipeline. An operator looking at an all-blocked source
/// environment therefore had no way to tell a host that was never given a gateway apart from a host
/// whose gateway refused, because neither produced an event. This emits the first case as a structured
/// record through the logger the host already exports, naming only the configuration keys that are
/// unset — never their values.
/// </summary>
public sealed class SourceGatewayStartupDiagnostics(
    ILogger<SourceGatewayStartupDiagnostics> logger,
    string configurationCode,
    string authorityHost,
    IReadOnlyList<string> missingKeys) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SourceGatewayDiagnostics.Configuration(logger, configurationCode, authorityHost, missingKeys);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
