// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.InteropServices;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The host the worker believes it is running on.
///
/// This exists so the architecture gate can be exercised without a 32-bit runner, and it grants nothing:
/// the native provider re-checks the real process architecture before it loads anything, so a declared
/// host that disagrees with the process reaches a closed door rather than an open one.
/// </summary>
public sealed record WorkerHost(bool IsWindows, Architecture ProcessArchitecture, string OperatingSystemDescription)
{
    public static WorkerHost Current { get; } =
        new(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSDescription);
}
