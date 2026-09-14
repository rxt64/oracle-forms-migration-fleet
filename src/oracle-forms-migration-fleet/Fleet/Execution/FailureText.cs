// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Renders an exception for an operator-facing line.
///
/// Reporting only the type name names the messenger rather than the fault: "threw ClientResultException"
/// is true of a rate limit, a bad deployment name, and an expired token alike, and a reader cannot tell
/// which one they have. The service's own message carries the status code and the reason, so it is kept.
/// </summary>
internal static class FailureText
{
    private const int MaxMessageCharacters = 400;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = exception.Message.ReplaceLineEndings(" ").Trim();

        return message.Length == 0
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {Trim(message)}";
    }

    private static string Trim(string message) =>
        message.Length <= MaxMessageCharacters ? message : message[..MaxMessageCharacters];
}
