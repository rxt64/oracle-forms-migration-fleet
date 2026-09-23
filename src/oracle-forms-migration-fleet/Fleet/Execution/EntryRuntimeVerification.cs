// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// What one executed case actually checked against the migrated target.
///
/// Every member names something a catalog can be asked and answers about, so the result is an observation
/// of a database somebody already migrated rather than a reading of the generator's own output. There is
/// deliberately no member for application behaviour: nothing here starts the generated service, so a case
/// claiming a screen or an endpoint works would be a claim no execution backs.
/// </summary>
public enum EntryVerificationCaseKind
{
    /// <summary>The table a mapped block declares it queries exists in the approved target schema.</summary>
    TargetTableExists,

    /// <summary>The run's own execution identity may read that table, so the migrated application could.</summary>
    TargetTableReadable,

    /// <summary>The column a mapped item is bound to exists on that table.</summary>
    TargetColumnExists,

    /// <summary>That column carries the type, precision and length the source column resolved to.</summary>
    TargetColumnShape,
}

/// <summary>
/// The four coordinates of the destination a verification may speak about: the approved target profile,
/// restated as something a gateway can be held to.
///
/// It is derived from the project's immutable target profile and never from a request, a manifest, or a
/// verifier-specific environment variable. The gateway compares it against the coordinates the host wired
/// it to and refuses when they differ, so there is exactly one approved destination and no second one a
/// verifier could have been pointed at.
/// </summary>
public sealed record EntryVerificationTargetBinding(
    string EndpointHost,
    string DatabaseName,
    string SchemaName,
    string ExecutionIdentity)
{
    /// <summary>A non-secret description, retained beside the results.</summary>
    public string Describe() =>
        $"{EndpointHost}/{DatabaseName}, schema '{SchemaName}', as '{ExecutionIdentity}'";

    /// <summary>True when both name the same destination. Host and identity compare as DNS and Entra do.</summary>
    public bool SameTargetAs(EntryVerificationTargetBinding? other) =>
        other is not null &&
        string.Equals(EndpointHost, other.EndpointHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DatabaseName, other.DatabaseName, StringComparison.Ordinal) &&
        string.Equals(SchemaName, other.SchemaName, StringComparison.Ordinal) &&
        string.Equals(ExecutionIdentity, other.ExecutionIdentity, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One case a verifier is asked to execute, bound to the exact ledger row and decision it is about.
///
/// The expectation is derived from the source: the base table a block declared, the column an item
/// declared it is bound to, and the target type that column resolved to through the parsed Oracle
/// schema. It is never read back out of the generated DDL, because output compared against itself
/// demonstrates nothing.
/// </summary>
public sealed record EntryVerificationExpectation(
    string EntryId,
    string DecisionRevision,
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    EntryVerificationCaseKind Kind,
    string Table,
    string? Column,
    string Expected)
{
    /// <summary>Stable identity of this case within one entry, so re-running it is the same fact twice.</summary>
    public string TestId => Column is { Length: > 0 } column
        ? $"{Kind}:{Table}.{column}"
        : $"{Kind}:{Table}";
}

/// <summary>What the verifier observed for one expectation. <paramref name="Actual"/> is null when nothing answered.</summary>
public sealed record EntryVerificationObservation(
    string EntryId,
    string TestId,
    DispositionVerificationStatus Outcome,
    string? Actual,
    string Detail);

/// <summary>
/// One trusted verifier execution: whether it could run at all, and what it observed.
///
/// <paramref name="Failure"/> describes why the run as a whole could not be trusted. When it is set the
/// observations are not evidence, because a setup that failed halfway leaves the target in a state
/// nothing here established.
/// </summary>
public sealed record EntryRuntimeVerificationRun(
    string Command,
    bool ToolAvailable,
    bool TimedOut,
    bool SetupFailed,
    string? Failure,
    IReadOnlyList<EntryVerificationObservation> Observations);

/// <summary>
/// The host's read-only route to the approved migration target.
///
/// An implementation holds the coordinates and the credential; nothing generated is given either, and
/// nothing generated is executed through it. It runs a fixed set of parameterized catalog probes written
/// in this build and nothing else: no DDL, no statement assembled from a generated file, and no statement
/// a caller supplied. The connection is opened read-only, so a probe that somehow tried to change the
/// target is refused by the server rather than by this code's good intentions.
///
/// It inspects the destination a migration already landed in. It never constructs one: a schema this
/// process created by running generated DDL under its own privileges would only show that the DDL parses,
/// which the aggregate disposable-schema leg already reports, and would say nothing about the database an
/// operator approved.
/// </summary>
public interface IEntryRuntimeVerificationGateway
{
    /// <summary>The destination this gateway is wired to. Non-secret coordinates only.</summary>
    EntryVerificationTargetBinding Target { get; }

    /// <summary>
    /// Reads the approved target and answers each expectation.
    ///
    /// <paramref name="approved"/> is the project's target profile. An implementation refuses outright
    /// when it does not name the destination the host wired it to, so a verifier can never answer about a
    /// database nobody approved for this run.
    /// </summary>
    Task<EntryRuntimeVerificationRun> InspectAsync(
        EntryVerificationTargetBinding approved,
        IReadOnlyList<EntryVerificationExpectation> expectations,
        CancellationToken cancellationToken);
}

/// <summary>
/// The generation a verification phase is allowed to speak about, as the server holds it at the moment
/// the phase asks.
///
/// <paramref name="Covered"/> is the decision state of every covered property read back out of the
/// ledger, not out of the run's own coverage document: a property re-decided between generating and
/// verifying is carried here as it now stands, so a case is never planned against a decision nobody
/// holds. <paramref name="OutputSetSha256"/> is the generation the ledger recorded, so a phase whose
/// output has since changed gets no claim at all.
/// </summary>
public sealed record EntryVerificationGenerationClaim(
    string LedgerId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SourceSnapshotHash,
    string OutputSetSha256,
    EntryVerificationTargetBinding ApprovedTarget,
    IReadOnlyList<GenerationCoveredEntry> Covered);

/// <summary>Exactly one side is set: a claim the phase may verify under, or the reason it has none.</summary>
public sealed record EntryVerificationClaimDecision(
    EntryVerificationGenerationClaim? Claim,
    IReadOnlyList<string> Denials)
{
    public static EntryVerificationClaimDecision Granted(EntryVerificationGenerationClaim claim) => new(claim, []);

    public static EntryVerificationClaimDecision Denied(string denial) => new(null, [denial]);
}

/// <summary>
/// The server's authority over verifying one run's generation, consulted at the moment of verification.
///
/// A phase names no tenant, project, run, or ledger: the host constructs the provider holding all of
/// them. The phase offers only the digest it observed on disk, and the provider answers from a generation
/// the ledger already recorded durably under the claim this worker still holds. A phase with no provider,
/// or with a provider that denies, reads no database at all — probing a customer target to prove
/// something about a generation nothing has recorded would produce evidence with nothing to attach it to.
/// </summary>
public interface IEntryVerificationGenerationProvider
{
    Task<EntryVerificationClaimDecision> ClaimAsync(
        string observedOutputSetSha256,
        CancellationToken cancellationToken);
}

/// <summary>One executed case as it is written down: the expectation, the observation, and the binding.</summary>
public sealed record EntryVerificationCase(
    string EntryId,
    string DecisionRevision,
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    string TestId,
    EntryVerificationCaseKind Kind,
    string Expected,
    string? Actual,
    DispositionVerificationStatus Outcome,
    string Detail);

/// <summary>
/// Something this verification does not demonstrate, stated rather than omitted.
///
/// <paramref name="EntryId"/> is empty for a gap about a class of behaviour rather than one row. A gap is
/// never a softer pass: the ledger row it names stays unexecuted and keeps blocking completion.
/// </summary>
public sealed record EntryVerificationGap(
    string EntryId,
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    string Reason);

/// <summary>
/// What one run's trusted verifier read from the approved target, written beside the run's reports so the
/// server can record the evidence from a document it re-checks rather than from a phase's own assertion.
///
/// <paramref name="TestedOutputSetSha256"/> is the generation these cases were executed against, taken
/// from the claim the server granted rather than from the phase's own hashing. The server recomputes the
/// same digest from the run's own generation coverage before recording anything, so a result offered
/// against output that has since been regenerated has nothing to attach to.
///
/// <paramref name="Target"/> is the destination that was read. The server compares it against the
/// project's target profile, so a result read from somewhere nobody approved is refused rather than
/// recorded.
/// </summary>
public sealed record EntryVerificationCoverageRecord(
    string SchemaVersion,
    string LedgerId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SourceSnapshotHash,
    string OutputRoot,
    string TestedOutputSetSha256,
    string VerifierId,
    EntryVerificationTargetBinding Target,
    DateTimeOffset VerifiedUtc,
    IReadOnlyList<EntryVerificationCase> Cases,
    IReadOnlyList<EntryVerificationGap> Gaps);

/// <summary>
/// The deterministic rules of per-entry runtime verification: which recorded decisions a case may be
/// planned for, what the case expects, and how an observed type is compared with a declared one.
///
/// Nothing here connects to anything or decides anything an operator recorded. It turns a resolved
/// mapping and a generation's covered decisions into the list of questions a trusted verifier will be
/// asked, and it states every property it will not ask about.
/// </summary>
public static partial class EntryVerificationCoverage
{
    /// <summary>The only schema version this build reads. A record is data, so the version is frozen text.</summary>
    public const string SchemaVersion = "2.0";

    /// <summary>Where the record sits, relative to the run's own output root.</summary>
    public const string RecordPath = "reports/entry-verification.json";

    /// <summary>Identifier of the planner that produced these cases, retained in the record.</summary>
    public const string VerifierId = "ofm.entry-runtime-verification/2";

    /// <summary>Ceiling on one record, applied to cases and to gaps separately.</summary>
    public const int MaxCases = 20_000;

    /// <summary>
    /// The standing statement that this verification is a schema contract read out of the migrated
    /// database, and not an acceptance of the generated application.
    ///
    /// It is written into every record, because a reader who sees only passing cases would otherwise be
    /// entitled to read them as the application working.
    /// </summary>
    public const string RuntimeServiceGap =
        "The generated HTTP service was not built, started, or called by this verification, and no screen, endpoint, " +
        "trigger body, or business rule was executed. Only the approved target database was read, and only for properties " +
        "this generator re-expresses. Nothing here is evidence that the migrated application behaves like the source.";

    /// <summary>Identifiers this verifier is willing to name in a probe, after the generator's own lowercasing.</summary>
    [GeneratedRegex("^[a-z_][a-z0-9_$]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();

    /// <summary>True when a name may be asked about rather than reported as a gap.</summary>
    public static bool IsVerifiableIdentifier(string? name) =>
        name is { Length: > 0 } && SafeIdentifier().IsMatch(name);

    /// <summary>The name the generator emits for a parsed Oracle object, which is what the target actually holds.</summary>
    public static string TargetName(string sourceName) => (sourceName ?? string.Empty).ToLowerInvariant();

    /// <summary>
    /// A declared PostgreSQL type written the way the server reports it back.
    ///
    /// The comparison is against <c>format_type</c>, which spells every type in full. Normalising the
    /// declared side through a fixed alias table keeps a real difference — a narrower length, a lost
    /// scale — reported as a failure, instead of being lost among spelling differences.
    /// </summary>
    public static string CanonicalType(string? declared)
    {
        string text = (declared ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        int open = text.IndexOf('(', StringComparison.Ordinal);
        string baseName = (open < 0 ? text : text[..open]).Trim();
        string arguments = open < 0
            ? string.Empty
            : text[open..].Replace(" ", string.Empty, StringComparison.Ordinal);

        baseName = baseName switch
        {
            "varchar" or "character varying" => "character varying",
            "char" or "bpchar" or "character" => "character",
            "int" or "int4" or "integer" => "integer",
            "int2" or "smallint" => "smallint",
            "int8" or "bigint" => "bigint",
            "bool" or "boolean" => "boolean",
            "decimal" or "numeric" => "numeric",
            "float8" or "double precision" => "double precision",
            "float4" or "real" => "real",
            "timestamp" or "timestamp without time zone" => "timestamp without time zone",
            "timestamptz" or "timestamp with time zone" => "timestamp with time zone",
            _ => baseName,
        };

        return baseName + arguments;
    }

    /// <summary>The cases a verifier will be asked to execute, and everything it will not be asked.</summary>
    public sealed record VerificationPlan(
        IReadOnlyList<EntryVerificationExpectation> Expectations,
        IReadOnlyList<EntryVerificationGap> Gaps);

    /// <summary>
    /// Turns one generation's covered decisions into the cases a trusted verifier may execute.
    ///
    /// A case is planned only where two things hold at once: the resolved mapping says this generator
    /// re-expresses the property in the target, and the recorded decision is to carry it forward. A
    /// property recorded <see cref="DispositionDecision.Retire"/> is a decision to drop it, so executing
    /// a case that would pass because the table happens to exist would report a retirement as a
    /// preservation. Those become gaps, and the rows stay unexecuted.
    /// </summary>
    public static VerificationPlan Plan(
        TargetMapping mapping,
        IReadOnlyList<FormsModule> modules,
        IReadOnlyList<GenerationCoveredEntry> covered)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(covered);

        Dictionary<string, string> qualifiedByPath = [];
        foreach (FormsModule module in modules)
        {
            if (module.SourcePath is { Length: > 0 } path)
            {
                qualifiedByPath[path] = module.QualifiedName;
            }
        }

        Dictionary<string, GenerationCoveredEntry> byEntry = [];
        foreach (GenerationCoveredEntry entry in covered)
        {
            byEntry[entry.EntryId] = entry;
        }

        List<EntryVerificationExpectation> expectations = [];
        List<EntryVerificationGap> gaps = [];
        HashSet<string> planned = new(StringComparer.Ordinal);

        foreach (CarriedSourceProperty property in mapping.Carried)
        {
            if (!qualifiedByPath.TryGetValue(property.ModulePath, out string? qualified))
            {
                gaps.Add(new EntryVerificationGap(
                    string.Empty, property.ModulePath, property.ObjectPath, property.PropertyName,
                    "The normalized representation this verification read declares no module at that path, so the ledger row " +
                    "this property belongs to could not be identified and no case was executed for it."));
                continue;
            }

            string entryId = DispositionLedgerEntries.EntryId(qualified, property.ObjectPath, property.PropertyName);

            if (!byEntry.TryGetValue(entryId, out GenerationCoveredEntry? decision))
            {
                gaps.Add(new EntryVerificationGap(
                    entryId, property.ModulePath, property.ObjectPath, property.PropertyName,
                    "The generation this verification read does not cover that property, so there is no recorded decision a " +
                    "result about it could be bound to."));
                continue;
            }

            if (decision.Decision is not (DispositionDecision.Preserve or DispositionDecision.Transform))
            {
                gaps.Add(new EntryVerificationGap(
                    entryId, property.ModulePath, property.ObjectPath, property.PropertyName,
                    $"That property is recorded {decision.Decision}, which is not a decision to carry it into the target. " +
                    "Executing a case that passed because the target happens to hold the object would report that decision " +
                    "as a preservation, so nothing was executed for it."));
                continue;
            }

            string table = TargetName(property.TargetTable ?? string.Empty);
            string? column = property.TargetColumn is { Length: > 0 } declaredColumn
                ? TargetName(declaredColumn)
                : null;

            if (!IsVerifiableIdentifier(table) || (column is not null && !IsVerifiableIdentifier(column)))
            {
                gaps.Add(new EntryVerificationGap(
                    entryId, property.ModulePath, property.ObjectPath, property.PropertyName,
                    "The target object this property maps to is named in a way this verifier will not ask a catalog about, " +
                    "so no case was executed for it."));
                continue;
            }

            foreach (EntryVerificationExpectation expectation in column is null
                ? Table(entryId, decision, property, table)
                : Column(entryId, decision, property, table, column))
            {
                if (planned.Add($"{expectation.EntryId}\u0000{expectation.TestId}"))
                {
                    expectations.Add(expectation);
                }
            }
        }

        gaps.Add(new EntryVerificationGap(string.Empty, string.Empty, string.Empty, string.Empty, RuntimeServiceGap));

        return new VerificationPlan(expectations, gaps);
    }

    private static IEnumerable<EntryVerificationExpectation> Table(
        string entryId,
        GenerationCoveredEntry decision,
        CarriedSourceProperty property,
        string table)
    {
        yield return new EntryVerificationExpectation(
            entryId, decision.DecisionRevision, property.ModulePath, property.ObjectPath, property.PropertyName,
            EntryVerificationCaseKind.TargetTableExists, table, null, table);

        yield return new EntryVerificationExpectation(
            entryId, decision.DecisionRevision, property.ModulePath, property.ObjectPath, property.PropertyName,
            EntryVerificationCaseKind.TargetTableReadable, table, null, table);
    }

    private static IEnumerable<EntryVerificationExpectation> Column(
        string entryId,
        GenerationCoveredEntry decision,
        CarriedSourceProperty property,
        string table,
        string column)
    {
        yield return new EntryVerificationExpectation(
            entryId, decision.DecisionRevision, property.ModulePath, property.ObjectPath, property.PropertyName,
            EntryVerificationCaseKind.TargetColumnExists, table, column, $"{table}.{column}");

        if (CanonicalType(property.TargetType) is { Length: > 0 } shape)
        {
            yield return new EntryVerificationExpectation(
                entryId, decision.DecisionRevision, property.ModulePath, property.ObjectPath, property.PropertyName,
                EntryVerificationCaseKind.TargetColumnShape, table, column, shape);
        }
    }

    /// <summary>
    /// Joins each planned case to what the verifier observed, and refuses to invent the ones it did not
    /// answer.
    ///
    /// An expectation with no observation becomes a gap rather than a pass or a silent omission: the case
    /// was planned, the verifier did not report on it, and the row it belongs to stays unexecuted.
    /// </summary>
    public static (IReadOnlyList<EntryVerificationCase> Cases, IReadOnlyList<EntryVerificationGap> Gaps) Join(
        IReadOnlyList<EntryVerificationExpectation> expectations,
        IReadOnlyList<EntryVerificationObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(observations);

        Dictionary<string, EntryVerificationObservation> byCase = [];
        foreach (EntryVerificationObservation observation in observations)
        {
            byCase[$"{observation.EntryId}\u0000{observation.TestId}"] = observation;
        }

        List<EntryVerificationCase> cases = [];
        List<EntryVerificationGap> gaps = [];

        foreach (EntryVerificationExpectation expectation in expectations)
        {
            if (!byCase.TryGetValue($"{expectation.EntryId}\u0000{expectation.TestId}", out EntryVerificationObservation? observed) ||
                observed.Outcome == DispositionVerificationStatus.NotExecuted)
            {
                gaps.Add(new EntryVerificationGap(
                    expectation.EntryId, expectation.ModulePath, expectation.ObjectPath, expectation.PropertyName,
                    $"Case '{expectation.TestId}' was planned and the verifier returned no executed result for it" +
                    (observed is null ? "." : $": {observed.Detail}")));
                continue;
            }

            cases.Add(new EntryVerificationCase(
                expectation.EntryId,
                expectation.DecisionRevision,
                expectation.ModulePath,
                expectation.ObjectPath,
                expectation.PropertyName,
                expectation.TestId,
                expectation.Kind,
                expectation.Expected,
                observed.Actual,
                observed.Outcome,
                observed.Detail));
        }

        return (cases, gaps);
    }

    /// <summary>A one-line reading of a record, for a run event and the aggregate report.</summary>
    public static string Describe(EntryVerificationCoverageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        int passed = record.Cases.Count(item => item.Outcome == DispositionVerificationStatus.Passed);
        int failed = record.Cases.Count(item => item.Outcome == DispositionVerificationStatus.Failed);
        int entries = record.Cases.Select(item => item.EntryId).Distinct(StringComparer.Ordinal).Count();

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture,
            $"{record.Cases.Count} executed case(s) over {entries} recorded decision(s): {passed} passed, {failed} failed. ");
        text.Append(CultureInfo.InvariantCulture, $"{record.Gaps.Count} stated gap(s). ");
        text.Append("The approved target was read only; no generated service was started, so nothing here is evidence of " +
            "application behaviour.");
        return text.ToString();
    }
}
