// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Startup validation and schema migration for the platform state store.
///
/// Two things must be true before this host serves a request, and both are checked here rather than
/// lazily on first use. The authentication mode must be legal for the environment the host actually
/// resolved — a process that read <c>Development</c> from configuration but is running as Production has
/// no identity boundary, and a self-asserted actor would be an operator. And the configured state store
/// must be at the schema version this build expects, because a store that is behind is one whose
/// approvals cannot be read.
///
/// Both failures throw. A host that cannot establish who is calling, or cannot read the records that
/// authorize them, should not start; starting and denying everything looks identical to an outage that
/// someone will try to fix by removing the check.
/// </summary>
public sealed class PlatformStartupService(
    IHostEnvironment environment,
    WorkbenchAuthenticationOptions options,
    ILogger<PlatformStartupService> logger,
    IPlatformStateStore? store = null,
    bool migrateStore = false) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Mode == WorkbenchAuthenticationMode.Development && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{WorkbenchAuthenticationOptions.ModeVariable}={nameof(WorkbenchAuthenticationMode.Development)} but the host " +
                $"environment resolved to '{environment.EnvironmentName}'. Development actors are self-asserted and are refused " +
                "outside the Development environment.");
        }

        if (store is null)
        {
            return;
        }

        if (!migrateStore)
        {
            // The development file adapter has no database and no DDL to apply; initialization only
            // stamps the schema version so a later reader can tell which shape it is looking at.
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        logger.LogInformation("Applying platform schema migrations to {Store}.", store.Description);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Platform schema is at version {Version}.", PlatformSchema.CurrentVersion);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
