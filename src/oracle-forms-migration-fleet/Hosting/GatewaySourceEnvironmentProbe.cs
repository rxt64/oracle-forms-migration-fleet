// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// What admitting a probe response produced. The capabilities are set only when
/// <see cref="Admitted"/> is true, and they are the ones this host re-derived rather than the ones the
/// gateway claimed: a status is a conclusion, and a conclusion that its own evidence does not support is
/// refused rather than repeated.
/// </summary>
public sealed record GatewayProbeAdmission(
    bool Admitted,
    SourceEnvironmentProbeStatus Status,
    DateTimeOffset ProbedUtc,
    string? ObservedForms,
    string? ObservedDatabase,
    IReadOnlyList<SourceCapabilityResult> Capabilities,
    IReadOnlyList<SourcePrerequisite> BlockedPrerequisites,
    string Reason);

/// <summary>
/// Decides whether a source-environment probe response may be believed.
///
/// It is a pure function over the request, the response, the profile it was asked about and the server
/// clock, for the same reason the extraction validation beside it is: this is the single place where a
/// remote answer becomes a recorded fact about a customer's environment, and every refusal here has to be
/// reachable from a test without a gateway, a worker, a Forms installation or an Oracle database.
/// </summary>
public static partial class GatewayProbeValidation
{
    /// <summary>Mirrors the identifier shape <see cref="SourceEnvironmentProfiles"/> will store.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,99}$")]
    private static partial Regex StableIdentifier();

    private const int MaxDescriptorCharacters = 64;

    /// <summary>The ceiling the profile store enforces on any text a probe result carries.</summary>
    internal const int MaxRefusalCharacters = 256;

    public static GatewayProbeAdmission Admit(
        SourceGatewayProbeRequest request,
        SourceGatewayProbeResponse? response,
        SourceEnvironmentProfile profile,
        DateTimeOffset serverUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);

        // The scope is this server's own answer to "who is this for". A request that never carried one is
        // a defect here, not a fact about the gateway, and it is checked before the response is read so a
        // malformed scope can never be compared into a match.
        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            return Refuse(
                serverUtc,
                "This host did not derive a server-owned tenant and project scope for the probe, so nothing was contacted.");
        }

        if (response is null)
        {
            return Refuse(serverUtc, "The source gateway returned no probe response document.");
        }

        if (response.SchemaVersion != SourceGatewayProtocol.SchemaVersion)
        {
            return Refuse(
                serverUtc,
                $"The source gateway answered the probe with schema version {response.SchemaVersion}; " +
                $"this build speaks version {SourceGatewayProtocol.SchemaVersion} only.");
        }

        // Correlation before any observation. A response bound to another source environment, another
        // immutable profile version or another scope is the shape a replay takes.
        if (!string.Equals(response.SourceEnvironmentId, request.SourceEnvironmentId, StringComparison.Ordinal) ||
            !string.Equals(response.ExpectedFormsRelease, request.ExpectedFormsRelease, StringComparison.Ordinal) ||
            !string.Equals(response.ExpectedDatabaseRelease, request.ExpectedDatabaseRelease, StringComparison.Ordinal) ||
            response.ProfileVersion != request.ProfileVersion ||
            !string.Equals(response.ProfileHash, request.ProfileHash, StringComparison.Ordinal) ||
            !request.Scope.Matches(response.Scope))
        {
            return Refuse(
                serverUtc,
                "The source gateway probe response is not correlated with the request: it names a different source " +
                "environment, release, profile version, profile digest, or authorization scope.");
        }

        if (response.ProbedUtc < serverUtc - SourceGatewayProtocol.MaxClockSkew ||
            response.ProbedUtc > serverUtc + SourceGatewayProtocol.MaxClockSkew ||
            response.ProbedUtc < profile.CreatedUtc - SourceGatewayProtocol.MaxClockSkew)
        {
            return Refuse(serverUtc, "The source gateway probe timestamp is outside the allowed clock-skew window.");
        }

        if (!SourceGatewayProbeStatus.IsKnown(response.Status))
        {
            return Refuse(serverUtc, "The source gateway reported a probe status this build does not recognize.");
        }

        if (response.Capabilities is not { Count: > 0 } reported ||
            reported.Count > SourceGatewayProtocol.MaxCapabilities ||
            response.Findings is null ||
            response.Findings.Count > SourceGatewayProtocol.MaxFindings)
        {
            return Refuse(
                serverUtc,
                "The source gateway probe response reports no capability, or more capabilities or findings than the " +
                "protocol allows.");
        }

        foreach (string finding in response.Findings)
        {
            if (finding is null ||
                finding.Length > SourceGatewayProtocol.MaxFindingCharacters ||
                FleetGuardrails.ContainsPotentialSecret(finding))
            {
                return Refuse(
                    serverUtc,
                    "A source gateway probe finding is missing, oversized, or looked like credential material.");
            }
        }

        List<SourceCapabilityResult> capabilities = new(reported.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (SourceGatewayCapability capability in reported)
        {
            if (capability is null ||
                !StableIdentifier().IsMatch(capability.Id ?? string.Empty) ||
                !seen.Add(capability.Id!) ||
                !StableIdentifier().IsMatch(capability.Remediation ?? string.Empty) ||
                !IsDescriptor(capability.RequiredRelease) ||
                !IsDescriptor(capability.RequiredArchitecture) ||
                !IsDescriptor(capability.RequiredHost) ||
                !IsObservation(capability.Observed) ||
                !TryParseExact(capability.State, out SourceEnvironmentProbeStatus state) ||
                state is SourceEnvironmentProbeStatus.Contradicted ||
                !TryParseExact(capability.Prerequisite, out SourcePrerequisite prerequisite))
            {
                return Refuse(
                    serverUtc,
                    "A source gateway probe capability is malformed, duplicated, or declares a state or prerequisite " +
                    "this build does not recognize.");
            }

            capabilities.Add(new(
                capability.Id!,
                state,
                prerequisite,
                capability.RequiredRelease,
                capability.RequiredArchitecture,
                capability.RequiredHost,
                capability.Remediation!));
        }

        string? observedForms = Trimmed(response.ObservedFormsRelease);
        string? observedDatabase = Trimmed(response.ObservedDatabaseRelease);
        if (observedForms is { Length: > 256 } || observedDatabase is { Length: > 256 } ||
            FleetGuardrails.ContainsPotentialSecret(observedForms) ||
            FleetGuardrails.ContainsPotentialSecret(observedDatabase) ||
            OracleVersionIntake.Validate(observedForms, observedDatabase).Count > 0)
        {
            return Refuse(
                serverUtc,
                "The source gateway reported an observed release this host will not store.");
        }

        SourcePrerequisite[] blocked = [.. capabilities
            .Where(capability => capability.State == SourceEnvironmentProbeStatus.BlockedPrerequisite)
            .Select(capability => capability.Prerequisite)
            .Distinct()];

        // A claim is only as good as the evidence beside it. Verified means every capability was exercised
        // and both releases were observed to be the ones the profile declares; anything short of that is
        // reported as what it is rather than promoted to the status the gateway asked for.
        bool verifiable =
            capabilities.All(capability => capability.State == SourceEnvironmentProbeStatus.Verified) &&
            observedForms is { Length: > 0 } &&
            observedDatabase is { Length: > 0 } &&
            string.Equals(observedForms, profile.ExpectedFormsVersion, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(observedDatabase, profile.ExpectedDatabaseVersion, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(response.Status, SourceGatewayProbeStatus.Verified, StringComparison.Ordinal))
        {
            // Coverage before conclusion. A response may legitimately carry more capabilities than the
            // connector requires, but a verified claim that never mentions one of them has verified
            // something narrower than the environment it names, and is refused rather than widened.
            if (MissingRequired(capabilities, profile.Connector) is { Length: > 0 } uncovered)
            {
                return Refuse(
                    serverUtc,
                    "The source gateway reported a verified source environment without reporting every capability " +
                    $"this connector requires; nothing exercised {string.Join(", ", uncovered)}.");
            }

            return verifiable
                ? new GatewayProbeAdmission(
                    true,
                    SourceEnvironmentProbeStatus.Verified,
                    response.ProbedUtc,
                    observedForms,
                    observedDatabase,
                    capabilities,
                    [],
                    "The source gateway verified every capability it reported.")
                : Refuse(
                    serverUtc,
                    "The source gateway reported a verified source environment without verifying every capability it " +
                    "listed, or without observing the Forms and database releases the profile declares.");
        }

        if (string.Equals(response.Status, SourceGatewayProbeStatus.BlockedPrerequisite, StringComparison.Ordinal))
        {
            return blocked.Length > 0
                ? new GatewayProbeAdmission(
                    true,
                    SourceEnvironmentProbeStatus.BlockedPrerequisite,
                    response.ProbedUtc,
                    observedForms,
                    observedDatabase,
                    capabilities,
                    blocked,
                    "The source gateway named at least one unmet prerequisite.")
                : Refuse(
                    serverUtc,
                    "The source gateway reported a blocked source environment while naming no unmet prerequisite.");
        }

        return Refuse(serverUtc, RefusalReason(capabilities, response.Findings));
    }

    /// <summary>
    /// Why the gateway refused, in this host's own words whenever it named a prerequisite this build
    /// shares with it. The gateway's own prose is the last resort rather than the first, so an operator
    /// is asked to act on a code both sides agree on and never on a remote string.
    /// </summary>
    private static string RefusalReason(
        IReadOnlyList<SourceCapabilityResult> capabilities,
        IReadOnlyList<string> findings)
    {
        foreach (SourceCapabilityResult capability in capabilities)
        {
            if (capability.State != SourceEnvironmentProbeStatus.Rejected)
            {
                continue;
            }

            switch (capability.Prerequisite)
            {
                case SourcePrerequisite.RegisteredSourceEnvironment:
                    return "The source gateway serves no source environment with this identifier, so nothing was inspected.";
                case SourcePrerequisite.AuthorizedSourceEnvironmentScope:
                    return "The source gateway does not authorize this tenant and project to read that source environment.";
                case SourcePrerequisite.ApprovedSourceProfileVersion:
                    return "The source gateway has not approved this source profile version and digest, so the probe was refused.";
                case SourcePrerequisite.OracleFormsInstallation:
                    return "The source gateway serves that source environment at a different Oracle Forms release than this profile declares.";
            }
        }

        return findings.Count > 0
            ? SourceGatewayText.Safe(findings[0], MaxRefusalCharacters)
            : "The source gateway refused the probe without naming a reason.";
    }

    /// <summary>
    /// The capabilities a probe has to have exercised before this host will call a connector's source
    /// environment verified. They are the identifiers the worker and the gateway already emit — the
    /// native half the worker reports on its succeeding path, plus the Oracle half the gateway appends —
    /// rather than a vocabulary invented here.
    /// </summary>
    private static string[] MissingRequired(
        IReadOnlyList<SourceCapabilityResult> capabilities,
        SourceConnector connector)
    {
        string[] required = connector switch
        {
            SourceConnector.FormsBuilderWorker => ["forms.installation", "forms.openapi.load", "oracle.schema.extract"],
            SourceConnector.OracleDatabaseReader => ["oracle.schema.extract"],
            _ => ["source.export.collect"],
        };

        return [.. required.Where(id =>
            !capabilities.Any(capability => string.Equals(capability.Id, id, StringComparison.Ordinal)))];
    }

    /// <summary>
    /// Everything that is not an admitted answer. The capability is synthesized here rather than carried
    /// over from the response, because a response this host refused is not evidence of anything about the
    /// source: the only thing it establishes is that the gateway could not be believed.
    /// </summary>
    private static GatewayProbeAdmission Refuse(DateTimeOffset serverUtc, string reason) => new(
        false,
        SourceEnvironmentProbeStatus.Rejected,
        serverUtc,
        null,
        null,
        [],
        [],
        reason);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsDescriptor(string? value) =>
        value is null ||
        (value.Length is > 0 and <= MaxDescriptorCharacters &&
         !value.Any(char.IsControl) &&
         !FleetGuardrails.ContainsPotentialSecret(value));

    /// <summary>
    /// What the gateway says it saw while exercising a capability — a resolved export list, a file
    /// version, a digest prefix. It is validated and then dropped: the host stores capability states, not
    /// remote prose, so this never has to be trusted beyond proving the response is readable.
    /// </summary>
    private static bool IsObservation(string? value) =>
        value is null ||
        (value.Length is > 0 and <= SourceGatewayProtocol.MaxFindingCharacters &&
         !value.Any(char.IsControl) &&
         !FleetGuardrails.ContainsPotentialSecret(value));

    /// <summary>Accepts only the declared enum name; numeric, combined and case variants are refused.</summary>
    private static bool TryParseExact<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = default;
        return !string.IsNullOrEmpty(value) &&
            Enum.TryParse(value, ignoreCase: false, out parsed) &&
            Enum.IsDefined(parsed) &&
            string.Equals(Enum.GetName(parsed), value, StringComparison.Ordinal);
    }
}

/// <summary>
/// Which probe this host runs, decided by whether a source gateway was configured and by nothing else.
///
/// It is a function rather than two registrations so the choice has one definition that a test can state
/// a fact about. The absent-gateway answer stays <see cref="UnavailableSourceEnvironmentProbe"/>, which
/// reports every capability blocked because nothing was tried — that is the supported default, not a
/// degraded mode.
/// </summary>
public static class SourceEnvironmentProbeSelection
{
    public static ISourceEnvironmentProbe Create(ISourceGatewayClient? gateway) =>
        gateway is null ? new UnavailableSourceEnvironmentProbe() : new GatewaySourceEnvironmentProbe(gateway);
}

/// <summary>
/// Probes a source environment through the separately deployed source gateway.
///
/// NO NATIVE WORKER RUNS IN THIS PROCESS. This adapter exists because the honest alternative —
/// <see cref="UnavailableSourceEnvironmentProbe"/> — can only ever report that nothing was tried, while
/// the gateway host is the one machine that actually has the 32-bit Forms installation and the registered
/// Oracle connection. It posts one bounded document to the configured origin and reads one bounded
/// document back; it starts no process, loads no library and opens no connection.
///
/// The tenant and project come from the stored profile, which the platform wrote from the authenticated
/// actor and a membership check that already succeeded. They are never read from a request body, and no
/// credential and no authority ever travels: the gateway holds its own identity and resolves every
/// filesystem location and every schema from its own registry, so the operator-declared context on the
/// profile cannot redirect it.
/// </summary>
public sealed class GatewaySourceEnvironmentProbe(
    ISourceGatewayClient gateway,
    Func<DateTimeOffset>? clock = null) : ISourceEnvironmentProbe
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string Description => gateway.Description;

    public async Task<SourceEnvironmentProbeResult> ProbeAsync(
        SourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        SourceGatewayProbeRequest request = new(
            SourceGatewayProtocol.SchemaVersion,
            profile.SourceEnvironmentId,
            profile.ExpectedFormsVersion,
            profile.ExpectedDatabaseVersion,
            profile.Version,
            profile.CanonicalHash,
            new SourceGatewayAuthorizationScope(profile.TenantId, profile.ProjectId));

        SourceGatewayProbeCall call = await gateway
            .ProbeSourceEnvironmentAsync(request, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = _clock();
        if (call.Response is null)
        {
            return Rejected(
                profile,
                now,
                call.TransportError is { Length: > 0 } transport
                    ? SourceGatewayText.Safe(transport, GatewayProbeValidation.MaxRefusalCharacters)
                    : "The source gateway could not be asked about this source environment.");
        }

        GatewayProbeAdmission admission = GatewayProbeValidation.Admit(request, call.Response, profile, now);
        if (!admission.Admitted)
        {
            return Rejected(profile, now, admission.Reason);
        }

        return new SourceEnvironmentProbeResult(
            1,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            admission.Status,
            admission.ProbedUtc,
            profile.Connector,
            new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
            new(admission.ObservedForms, admission.ObservedDatabase),
            admission.Capabilities,
            admission.BlockedPrerequisites,
            []);
    }

    private static SourceEnvironmentProbeResult Rejected(
        SourceEnvironmentProfile profile,
        DateTimeOffset probedUtc,
        string reason) => new(
            1,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            SourceEnvironmentProbeStatus.Rejected,
            probedUtc,
            profile.Connector,
            new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
            new(null, null),
            [new(
                "source.environment.probe",
                SourceEnvironmentProbeStatus.Rejected,
                SourcePrerequisite.SourceWorkerExecutable,
                profile.ExpectedFormsVersion,
                null,
                "SourceGateway",
                "operator.review.source.gateway.configuration")],
            [],
            [reason]);
}
