// Copyright (c) Microsoft. All rights reserved.

using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

internal static class GeneratedApplicationVerificationTemplates
{
    internal static string Read(string relativePath)
    {
        string resource = $"OracleFormsMigrationFleet.Fleet.Execution.Templates.Verification.{relativePath.Replace('/', '.')}";
        using Stream stream = typeof(GeneratedApplicationVerificationTemplates).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The generated-application verification template '{resource}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}