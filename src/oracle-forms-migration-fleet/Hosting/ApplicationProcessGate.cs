// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Hosting;

internal static class ApplicationProcessGate
{
    internal static SemaphoreSlim Lock { get; } = new(1, 1);
}