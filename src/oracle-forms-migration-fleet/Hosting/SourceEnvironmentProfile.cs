// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Hosting;

public enum SourceConnector
{
    OperatorSuppliedExport,
    FormsBuilderWorker,
    OracleDatabaseReader,
}

public enum SourceEnvironmentReadiness
{
    Declared,
    BlockedPrerequisite,
    Verified,
    Contradicted,
    Rejected,
}

public sealed record SourceEnvironmentDeclaration(
    string SourceEnvironmentId,
    string Name,
    SourceConnector Connector,
    string ExpectedFormsVersion,
    string ExpectedDatabaseVersion,
    string PathAlias,
    IReadOnlyList<string> SchemaAllowlist,
    IReadOnlyList<string> SecretReferences);

public sealed record SourceEnvironmentProfile
{
    public const int CurrentProfileSchemaVersion = 1;

    public required string TenantId { get; init; }
    public required string ProjectId { get; init; }
    public required string SourceEnvironmentId { get; init; }
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required SourceConnector Connector { get; init; }
    public required string ExpectedFormsVersion { get; init; }
    public required string ExpectedDatabaseVersion { get; init; }
    public required string PathAlias { get; init; }
    public required IReadOnlyList<string> SchemaAllowlist { get; init; }
    public required IReadOnlyList<string> SecretReferences { get; init; }
    public string? ObservedFormsVersion { get; init; }
    public string? ObservedDatabaseVersion { get; init; }
    public SourceEnvironmentReadiness Readiness { get; init; } = SourceEnvironmentReadiness.Declared;
    public DateTimeOffset? LastVerifiedUtc { get; init; }
    public IReadOnlyList<SourceCapabilityResult> LastProbeCapabilities { get; init; } = [];
    public IReadOnlyList<SourcePrerequisite> LastBlockedPrerequisites { get; init; } = [];
    public IReadOnlyList<string> LastContradictions { get; init; } = [];
    public int ProfileSchemaVersion { get; init; } = CurrentProfileSchemaVersion;
    public required string DeclarationHash { get; init; }
    public required string CanonicalHash { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
}

public static partial class SourceEnvironmentProfiles
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]{0,29}$")]
    private static partial Regex SchemaPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,127}$")]
    private static partial Regex SecretReferencePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,99}$")]
    private static partial Regex StableIdentifierPattern();

    [GeneratedRegex("^[\\p{L}\\p{N}][\\p{L}\\p{N} ._()-]{0,119}$")]
    private static partial Regex DisplayNamePattern();

    public static SourceEnvironmentProfile Create(
        string tenantId,
        string projectId,
        string sourceEnvironmentId,
        int version,
        string name,
        SourceConnector connector,
        string expectedFormsVersion,
        string expectedDatabaseVersion,
        string pathAlias,
        IReadOnlyList<string> schemaAllowlist,
        IReadOnlyList<string> secretReferences,
        DateTimeOffset createdUtc)
    {
        SourceEnvironmentProfile candidate = new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            SourceEnvironmentId = sourceEnvironmentId,
            Version = version,
            Name = name,
            Connector = connector,
            ExpectedFormsVersion = expectedFormsVersion,
            ExpectedDatabaseVersion = expectedDatabaseVersion,
            PathAlias = pathAlias,
            SchemaAllowlist = [.. schemaAllowlist.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            SecretReferences = [.. secretReferences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            LastProbeCapabilities = [],
            LastBlockedPrerequisites = [],
            LastContradictions = [],
            DeclarationHash = string.Empty,
            CanonicalHash = string.Empty,
            CreatedUtc = NormalizeTimestamp(createdUtc),
        };
        IReadOnlyList<string> errors = Validate(candidate);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
        string declarationHash = DeclarationHash(candidate);
        candidate = candidate with { DeclarationHash = declarationHash };
        return candidate with { CanonicalHash = Hash(candidate) };
    }

    public static IReadOnlyList<string> Validate(SourceEnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<string> errors = [];
        if (profile.Version <= 0) errors.Add("Source environment version must be positive.");
        if (!Enum.IsDefined(profile.Connector)) errors.Add("Source connector is not recognized.");
        if (!AliasPattern().IsMatch(profile.SourceEnvironmentId)) errors.Add("Source environment ID must be a server-safe alias.");
        if (!AliasPattern().IsMatch(profile.PathAlias)) errors.Add("Source path alias must be a server-safe alias, not a filesystem path.");
        if (!DisplayNamePattern().IsMatch(profile.Name)) errors.Add("Source environment name contains unsupported characters or credential-like syntax.");
        if (profile.SchemaAllowlist.Count > 32) errors.Add("A source environment may allow at most 32 Oracle schemas.");
        if (profile.SecretReferences.Count > 32) errors.Add("A source environment may reference at most 32 secret names.");
        if (profile.SchemaAllowlist.Any(schema => !SchemaPattern().IsMatch(schema))) errors.Add("Source schema allowlist contains an invalid Oracle identifier.");
        if (profile.SecretReferences.Any(reference => !SecretReferencePattern().IsMatch(reference))) errors.Add("Secret references must be Key Vault secret names only.");
        IEnumerable<string?> strings =
        [
            profile.TenantId,
            profile.ProjectId,
            profile.SourceEnvironmentId,
            profile.Name,
            profile.ExpectedFormsVersion,
            profile.ExpectedDatabaseVersion,
            profile.PathAlias,
            .. profile.SchemaAllowlist,
            .. profile.SecretReferences,
        ];
        if (strings.Any(FleetGuardrails.ContainsPotentialSecret)) errors.Add("Source environment metadata appears to contain credential material and was rejected.");
        errors.AddRange(OracleVersionIntake.Validate(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion));
        return errors;
    }

    public static string DeclarationHash(SourceEnvironmentProfile profile)
    {
        object canonical = new
        {
            profile.TenantId,
            profile.ProjectId,
            profile.SourceEnvironmentId,
            profile.ProfileSchemaVersion,
            profile.Name,
            profile.Connector,
            profile.ExpectedFormsVersion,
            profile.ExpectedDatabaseVersion,
            profile.PathAlias,
            SchemaAllowlist = profile.SchemaAllowlist.Order(StringComparer.Ordinal).ToArray(),
            SecretReferences = profile.SecretReferences.Order(StringComparer.Ordinal).ToArray(),
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    public static string Hash(SourceEnvironmentProfile profile)
    {
        object canonical = new
        {
            profile.DeclarationHash,
            profile.Version,
            profile.ObservedFormsVersion,
            profile.ObservedDatabaseVersion,
            profile.Readiness,
            profile.LastVerifiedUtc,
            profile.CreatedUtc,
            profile.LastProbeCapabilities,
            profile.LastBlockedPrerequisites,
            profile.LastContradictions,
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    public static SourceEnvironmentProfile RecordProbe(
        SourceEnvironmentProfile current,
        SourceEnvironmentProbeResult result,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.SourceEnvironmentId, current.SourceEnvironmentId, StringComparison.Ordinal) ||
            result.ProfileVersion != current.Version ||
            !string.Equals(result.ProfileHash, current.CanonicalHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The probe result is not bound to the current source environment profile.", nameof(result));
        }
        ValidateProbeResult(current, result, createdUtc);

        SourceEnvironmentProfile updated = current with
        {
            Version = current.Version + 1,
            ObservedFormsVersion = result.Observed.Forms,
            ObservedDatabaseVersion = result.Observed.Database,
            Readiness = result.Status switch
            {
                SourceEnvironmentProbeStatus.Verified => SourceEnvironmentReadiness.Verified,
                SourceEnvironmentProbeStatus.Contradicted => SourceEnvironmentReadiness.Contradicted,
                SourceEnvironmentProbeStatus.Rejected => SourceEnvironmentReadiness.Rejected,
                SourceEnvironmentProbeStatus.BlockedPrerequisite => SourceEnvironmentReadiness.BlockedPrerequisite,
                _ => throw new ArgumentOutOfRangeException(nameof(result)),
            },
            LastVerifiedUtc = result.Status == SourceEnvironmentProbeStatus.Verified ? NormalizeTimestamp(result.ProbedUtc) : null,
            LastProbeCapabilities = [.. result.Capabilities],
            LastBlockedPrerequisites = [.. result.BlockedPrerequisites],
            LastContradictions = [.. result.Contradictions],
            CreatedUtc = NormalizeTimestamp(createdUtc),
            CanonicalHash = string.Empty,
        };
        return updated with { CanonicalHash = Hash(updated) };
    }

    public static SourceEnvironmentProfile VerifyStored(SourceEnvironmentProfile profile)
    {
        IReadOnlyList<string> errors = Validate(profile);
        if (errors.Count > 0 ||
            !string.Equals(profile.DeclarationHash, DeclarationHash(profile), StringComparison.Ordinal) ||
            !string.Equals(profile.CanonicalHash, Hash(profile), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A stored source environment profile failed validation or hash verification.");
        }
        return profile;
    }

    public static void ValidateProbeResult(
        SourceEnvironmentProfile current,
        SourceEnvironmentProbeResult result,
        DateTimeOffset serverUtc)
    {
        if (result.SchemaVersion != 1 || result.Connector != current.Connector)
        {
            throw new ArgumentException("The source probe result schema or connector does not match the profile.", nameof(result));
        }
        if (result.ProbedUtc < current.CreatedUtc.AddMinutes(-10) || result.ProbedUtc > serverUtc.AddMinutes(10))
        {
            throw new ArgumentException("The source probe timestamp is outside the allowed clock-skew window.", nameof(result));
        }
        if (!Enum.IsDefined(result.Status) ||
            result.Capabilities.Any(capability => !Enum.IsDefined(capability.State) || !Enum.IsDefined(capability.Prerequisite)) ||
            result.BlockedPrerequisites.Any(prerequisite => !Enum.IsDefined(prerequisite)))
        {
            throw new ArgumentException("The source probe result contains an unknown status or prerequisite.", nameof(result));
        }
        if (!string.Equals(result.Expected.Forms, current.ExpectedFormsVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.Expected.Database, current.ExpectedDatabaseVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The source probe expected versions do not match the profile declaration.", nameof(result));
        }
        if (result.Capabilities.Count > 32 || result.BlockedPrerequisites.Count > 32 || result.Contradictions.Count > 32)
        {
            throw new ArgumentException("The source probe result exceeds its bounded capability or finding count.", nameof(result));
        }
        IEnumerable<string?> workerStrings =
        [
            result.Expected.Forms,
            result.Expected.Database,
            result.Observed.Forms,
            result.Observed.Database,
            .. result.Contradictions,
            .. result.Capabilities.SelectMany(capability => new[]
            {
                capability.Id,
                capability.RequiredRelease,
                capability.RequiredArchitecture,
                capability.RequiredHost,
                capability.Remediation,
            }),
        ];
        if (workerStrings.Any(value => value is { Length: > 256 } || FleetGuardrails.ContainsPotentialSecret(value)))
        {
            throw new ArgumentException("The source probe result contains oversized or credential-like text.", nameof(result));
        }
        if (result.Contradictions.Any(string.IsNullOrWhiteSpace) ||
            result.Capabilities.Any(capability =>
                !StableIdentifierPattern().IsMatch(capability.Id) ||
                !StableIdentifierPattern().IsMatch(capability.Remediation)))
        {
            throw new ArgumentException("The source probe result contains an invalid stable identifier or empty contradiction.", nameof(result));
        }
        if (FleetGuardrails.ContainsPotentialSecret(result.Observed.Forms) ||
            FleetGuardrails.ContainsPotentialSecret(result.Observed.Database) ||
            OracleVersionIntake.Validate(result.Observed.Forms, result.Observed.Database).Count > 0)
        {
            throw new ArgumentException("The source probe observations were rejected before storage.", nameof(result));
        }
        if (result.Status == SourceEnvironmentProbeStatus.Verified &&
            (string.IsNullOrWhiteSpace(result.Observed.Forms) || string.IsNullOrWhiteSpace(result.Observed.Database) ||
             result.BlockedPrerequisites.Count > 0 || result.Contradictions.Count > 0 ||
             result.Capabilities.Any(capability => capability.State != SourceEnvironmentProbeStatus.Verified)))
        {
            throw new ArgumentException("A verified source probe must contain both observations and no blockers or contradictions.", nameof(result));
        }
        if (result.Status == SourceEnvironmentProbeStatus.Contradicted && result.Contradictions.Count == 0)
        {
            throw new ArgumentException("A contradicted source probe must identify at least one contradiction.", nameof(result));
        }
        if (result.Status == SourceEnvironmentProbeStatus.BlockedPrerequisite && result.BlockedPrerequisites.Count == 0)
        {
            throw new ArgumentException("A blocked source probe must identify at least one prerequisite.", nameof(result));
        }
        if (result.Status == SourceEnvironmentProbeStatus.Rejected && result.Contradictions.Count == 0)
        {
            throw new ArgumentException("A rejected source probe must identify why the environment was refused.", nameof(result));
        }
        if (result.Status != SourceEnvironmentProbeStatus.Verified &&
            !result.Capabilities.Any(capability => capability.State == result.Status))
        {
            throw new ArgumentException("The source probe capability states do not support its top-level status.", nameof(result));
        }
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}