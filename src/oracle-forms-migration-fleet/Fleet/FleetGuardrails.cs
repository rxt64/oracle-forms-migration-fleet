// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// Security boundaries enforced by every role in the fleet. These are stated in the outer
/// agent instructions and echoed on every plan so the constraints stay auditable.
/// </summary>
public static partial class FleetGuardrails
{
    public static IReadOnlyList<string> Boundaries { get; } =
    [
        "Never accept, request, echo, or store secrets — passwords, connection strings, keys, or tokens. Reference artifacts by name only.",
        "Never execute destructive or state-changing operations against Oracle, Azure SQL, or any other system.",
        "Never make autonomous changes to production. Generated artifacts are proposals that require human review and acceptance before use, and sandbox mutation and production cutover each require their own separate recorded approval.",
        "Never claim a migration was performed. This fleet produces assessments and plans only.",
        "Never emit executable DDL, DML, or migration scripts. This fleet produces planning tasks only.",
        "Always cite the evidence identifiers backing a finding, and label anything not backed by evidence as an assumption.",
    ];

    public static IReadOnlyList<string> Disclaimers { get; } =
    [
        "This output is a migration assessment and plan. No migration, schema change, or data movement was performed.",
        "Conversion effort and risk are estimates derived only from the supplied evidence.",
        "Azure SQL Database and Azure SQL Managed Instance feature parity must be re-confirmed against current Microsoft documentation before execution.",
    ];

    [GeneratedRegex(
        @"(?i)\b(password|pwd|secret|api[-_ ]?key|client[-_ ]?secret|access[-_ ]?token|sas[-_ ]?token)\b\s*[:=]|BEGIN\s+(RSA\s+|EC\s+|OPENSSH\s+)?PRIVATE\s+KEY",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    /// <summary>Rejects obvious credential material before it reaches the model or the audit trail.</summary>
    public static bool ContainsPotentialSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SecretPattern().IsMatch(value);
}
