// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record ApplicationBuildResult(
    string Component,
    string Command,
    bool ToolAvailable,
    int ExitCode,
    string Output)
{
    public bool Succeeded => ToolAvailable && ExitCode == 0;
}

/// <summary>
/// Builds generated application code with host-owned commands. The adapter supplies only validated
/// workspace directories; migration source cannot select an executable or command-line argument.
/// </summary>
public interface IApplicationBuildGateway
{
    Task<ApplicationBuildResult> BuildJavaAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<ApplicationBuildResult> BuildReactAsync(string workingDirectory, CancellationToken cancellationToken);
}