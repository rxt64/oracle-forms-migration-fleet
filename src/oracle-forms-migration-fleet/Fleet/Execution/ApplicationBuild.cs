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

    /// <summary>
    /// Builds a generated .NET back end. The default reports that this host cannot, rather than reporting a
    /// build that did not happen as a passing one; a gateway that can build .NET overrides it.
    /// </summary>
    Task<ApplicationBuildResult> BuildDotNetAsync(string workingDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicationBuildResult(
            ".NET/ASP.NET Core",
            "dotnet build",
            ToolAvailable: false,
            ExitCode: -1,
            Output: "This build gateway has no .NET toolchain, so the generated ASP.NET Core back end was not compiled."));
}