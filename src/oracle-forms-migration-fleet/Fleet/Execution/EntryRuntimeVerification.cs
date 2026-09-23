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
    public string TestId => EntryVerificationCoverage.TestId(Kind, Table, Column);

    /// <summary>This case as the record states it was asked, before anything answered it.</summary>
    public EntryVerificationPlannedCase AsPlanned() => new(EntryId, TestId, Kind, Table, Column);
}

/// <summary>
/// One question this verification decided to ask, written down before any answer arrived.
///
/// The executed cases alone cannot say whether a property was completely covered: a record holding one
/// passing probe reads identically whether that was the only probe planned for the property or the only
/// one that came back. Stating the plan separately is what makes "every probe this property needed was
/// executed" a checkable fact rather than an assumption, and it carries the coordinates so the probe
/// family can be re-derived instead of parsed back out of an identifier.
/// </summary>
public sealed record EntryVerificationPlannedCase(
    string EntryId,
    string TestId,
    EntryVerificationCaseKind Kind,
    string Table,
    string? Column);

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
/// Why a verification does not speak about something: because it never asked, or because it asked and
/// got no answer.
///
/// The two are not the same shortfall and must not be collapsed. A property this generator does not
/// re-express, or a decision to retire, is a gap by design — the run never planned a probe there, and a
/// record full of those is still a complete account of what it set out to check. A planned probe that
/// came back with nothing is an incomplete account: the property it belongs to was only partly asked
/// about, so a sibling probe that passed cannot stand in for the one that never ran.
/// </summary>
public enum EntryVerificationGapKind
{
    /// <summary>
    /// No case was ever planned here. Stated so the record says what it leaves undemonstrated, and
    /// deliberately not a shortfall against the plan: it is the plan.
    /// </summary>
    NotProbed,

    /// <summary>
    /// A case this run planned returned no executed result. The property it names is not completely
    /// covered, and no result about it may be recorded until it is.
    /// </summary>
    PlannedCaseUnanswered,
}

/// <summary>
/// Something this verification does not demonstrate, stated rather than omitted.
///
/// <paramref name="EntryId"/> is empty for a gap about a class of behaviour rather than one row, and
/// <paramref name="PlannedTestId"/> is set only when <paramref name="Kind"/> is
/// <see cref="EntryVerificationGapKind.PlannedCaseUnanswered"/>, so an unanswered probe names the exact
/// question that went unanswered instead of describing it in prose nothing can check. A gap is never a
/// softer pass: the ledger row it names stays unexecuted and keeps blocking completion.
/// </summary>
public sealed record EntryVerificationGap(
    EntryVerificationGapKind Kind,
    string EntryId,
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    string Reason,
    string? PlannedTestId = null);

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
///
/// <paramref name="Planned"/> is every case this run decided to ask, stated before any of them was
/// answered. It is what makes completeness checkable: the server reconciles it against
/// <paramref name="Cases"/> and <paramref name="Gaps"/> and refuses a record that does not account for
/// each planned probe exactly once, so a property cannot read back verified on the strength of one probe
/// while a sibling probe it also needed never ran.
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
    IReadOnlyList<EntryVerificationPlannedCase> Planned,
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
    public const string SchemaVersion = "3.0";

    /// <summary>Where the record sits, relative to the run's own output root.</summary>
    public const string RecordPath = "reports/entry-verification.json";

    /// <summary>Identifier of the planner that produced these cases, retained in the record.</summary>
    public const string VerifierId = "ofm.entry-runtime-verification/3";

    /// <summary>How many refusals one audit reports. The record always holds everything they are about.</summary>
    private const int MaxAuditRefusals = 25;

    /// <summary>
    /// The identity of one case within one entry, derived from what it asks rather than stored beside it.
    ///
    /// Both the planned statement and the executed result spell it through here, so a record whose
    /// identifier and coordinates disagree is a record the server can catch rather than one it has to
    /// believe.
    /// </summary>
    public static string TestId(EntryVerificationCaseKind kind, string table, string? column) =>
        column is { Length: > 0 } named ? $"{kind}:{table}.{named}" : $"{kind}:{table}";

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
                    EntryVerificationGapKind.NotProbed,
                    string.Empty, property.ModulePath, property.ObjectPath, property.PropertyName,
                    "The normalized representation this verification read declares no module at that path, so the ledger row " +
                    "this property belongs to could not be identified and no case was executed for it."));
                continue;
            }

            string entryId = DispositionLedgerEntries.EntryId(qualified, property.ObjectPath, property.PropertyName);

            if (!byEntry.TryGetValue(entryId, out GenerationCoveredEntry? decision))
            {
                gaps.Add(new EntryVerificationGap(
                    EntryVerificationGapKind.NotProbed,
                    entryId, property.ModulePath, property.ObjectPath, property.PropertyName,
                    "The generation this verification read does not cover that property, so there is no recorded decision a " +
                    "result about it could be bound to."));
                continue;
            }

            if (decision.Decision is not (DispositionDecision.Preserve or DispositionDecision.Transform))
            {
                gaps.Add(new EntryVerificationGap(
                    EntryVerificationGapKind.NotProbed,
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
                    EntryVerificationGapKind.NotProbed,
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

        gaps.Add(new EntryVerificationGap(
            EntryVerificationGapKind.NotProbed, string.Empty, string.Empty, string.Empty, string.Empty, RuntimeServiceGap));

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
                    EntryVerificationGapKind.PlannedCaseUnanswered,
                    expectation.EntryId, expectation.ModulePath, expectation.ObjectPath, expectation.PropertyName,
                    $"Case '{expectation.TestId}' was planned and the verifier returned no executed result for it" +
                    (observed is null ? "." : $": {observed.Detail}"),
                    expectation.TestId));
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

    /// <summary>
    /// Why a record does not completely account for the cases it planned, or nothing at all.
    ///
    /// This is the rule the whole file exists for. A property is covered when every probe planned for it
    /// was executed, and not when one of them passed: the table a block queries existing says nothing
    /// about whether the run's identity may read it, so a record carrying only the first has demonstrated
    /// half of what it set out to and must not be recorded as evidence about that property. The plan is
    /// reconciled as a whole, so a single unanswered probe anywhere refuses the record rather than
    /// admitting the properties that happened to come back complete — one verification is one fact about
    /// one output set, and half of one is not a smaller fact.
    ///
    /// Gaps of kind <see cref="EntryVerificationGapKind.NotProbed"/> are ignored here on purpose. They are
    /// the plan stating what it never asked — a retired decision, a name no catalog is asked about, the
    /// absence of any executed application behaviour — and a structurally valid property is free to record
    /// alongside them, because nothing in the record claims those gaps were verified.
    ///
    /// What it cannot check is a plan that was shrunk before it was written: this reads the record
    /// against itself, and only the source says how many probes it implied. The pairing it does check is
    /// the part that is unconditional — a table probe always comes with a readability probe, and a
    /// column's shape is never asked without its existence. <see cref="Reconcile"/> is what closes the
    /// rest, by rebuilding the plan the source requires instead of believing the one the record states.
    /// </summary>
    public static IReadOnlyList<string> Audit(EntryVerificationCoverageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        List<string> refusals = [];

        if (record.Planned.Count == 0)
        {
            refusals.Add(
                "The record states no planned case, so what it executed cannot be read as everything it set out to ask.");
            return refusals;
        }

        if (record.Planned.Count > MaxCases)
        {
            refusals.Add("The record states more planned cases than one record describes.");
            return refusals;
        }

        Dictionary<string, EntryVerificationPlannedCase> planned = [];
        foreach (EntryVerificationPlannedCase candidate in record.Planned)
        {
            if (!string.Equals(candidate.TestId, TestId(candidate.Kind, candidate.Table, candidate.Column), StringComparison.Ordinal))
            {
                Refuse(refusals,
                    $"Planned case '{candidate.TestId}' on '{candidate.EntryId}' does not name the probe its own coordinates describe.");
            }
            else if (!planned.TryAdd(Key(candidate.EntryId, candidate.TestId), candidate))
            {
                Refuse(refusals,
                    $"Planned case '{candidate.TestId}' on '{candidate.EntryId}' is stated more than once, so how many results it needs is ambiguous.");
            }
        }

        foreach (string incomplete in IncompleteProbeFamilies(record.Planned))
        {
            Refuse(refusals, incomplete);
        }

        HashSet<string> executed = new(StringComparer.Ordinal);
        foreach (EntryVerificationCase item in record.Cases)
        {
            string key = Key(item.EntryId, item.TestId);
            if (!planned.TryGetValue(key, out EntryVerificationPlannedCase? match))
            {
                Refuse(refusals,
                    $"Case '{item.TestId}' on '{item.EntryId}' reports a result for a probe this run never planned, so what it " +
                    "answers is not a question the source asked.");
                continue;
            }

            if (match.Kind != item.Kind)
            {
                Refuse(refusals,
                    $"Case '{item.TestId}' on '{item.EntryId}' reports a different probe than the one planned under that identifier.");
            }

            executed.Add(key);
        }

        HashSet<string> stated = new(StringComparer.Ordinal);
        foreach (EntryVerificationGap gap in record.Gaps)
        {
            if (gap.Kind != EntryVerificationGapKind.PlannedCaseUnanswered)
            {
                continue;
            }

            if (gap.PlannedTestId is not { Length: > 0 } testId || !planned.ContainsKey(Key(gap.EntryId, testId)))
            {
                Refuse(refusals,
                    $"A gap on '{gap.EntryId}' says a planned case went unanswered without naming one this run planned.");
                continue;
            }

            stated.Add(Key(gap.EntryId, testId));
        }

        foreach ((string key, EntryVerificationPlannedCase candidate) in planned)
        {
            bool ran = executed.Contains(key);
            bool unanswered = stated.Contains(key);

            if (ran && unanswered)
            {
                Refuse(refusals,
                    $"Case '{candidate.TestId}' on '{candidate.EntryId}' is reported both as executed and as unanswered.");
            }
            else if (ran)
            {
                continue;
            }
            else if (unanswered)
            {
                Refuse(refusals,
                    $"Case '{candidate.TestId}' on '{candidate.EntryId}' was planned and went unanswered, so that property was " +
                    "only partly asked about and no result about it is complete.");
            }
            else
            {
                Refuse(refusals,
                    $"Case '{candidate.TestId}' on '{candidate.EntryId}' was planned and the record neither reports a result for " +
                    "it nor states it went unanswered, so the record does not account for what it asked.");
            }
        }

        return refusals;

        static void Refuse(List<string> refusals, string reason)
        {
            if (refusals.Count < MaxAuditRefusals)
            {
                refusals.Add(reason);
            }
        }
    }

    /// <summary>
    /// Every way a record's account of what it asked differs from the plan the source requires of it.
    ///
    /// <see cref="Audit"/> reads a record against itself, so a record that removed a probe from its plan
    /// and from its results together is internally consistent and passes: the family it leaves behind is
    /// one this build legitimately produces when the source resolves no type for a column. This reads the
    /// record against something it did not write — <paramref name="expected"/>, planned from the retained
    /// mapping, the normalized source, and the decisions the server holds — so a probe the source
    /// requires is missing whether or not the record admits it.
    ///
    /// The expected value is compared too, and not only the identity of the case. A record free to name
    /// its own expectation could answer a question the source never asked: a column declared
    /// <c>numeric(12,2)</c> in the source and reported as expecting <c>text</c> passes against a target
    /// that lost the precision, and passes correctly, because it was asked the easier question.
    ///
    /// An empty <paramref name="expected"/> refuses outright. A ledger whose source and standing
    /// decisions derive no case has nothing a result could be evidence of, so a record offered against it
    /// is a record about nothing.
    /// </summary>
    public static IReadOnlyList<string> Reconcile(
        VerificationPlan expected,
        EntryVerificationCoverageRecord record)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(record);

        List<string> refusals = [];

        if (expected.Expectations.Count == 0)
        {
            refusals.Add(
                "This ledger's retained source and standing decisions require no case at all, so there is nothing a result " +
                "could be evidence of.");
            return refusals;
        }

        Dictionary<string, EntryVerificationExpectation> required = [];
        foreach (EntryVerificationExpectation expectation in expected.Expectations)
        {
            required[Key(expectation.EntryId, expectation.TestId)] = expectation;
        }

        Dictionary<string, EntryVerificationPlannedCase> stated = [];
        foreach (EntryVerificationPlannedCase candidate in record.Planned)
        {
            stated[Key(candidate.EntryId, candidate.TestId)] = candidate;
        }

        foreach ((string key, EntryVerificationExpectation expectation) in required)
        {
            if (!stated.TryGetValue(key, out EntryVerificationPlannedCase? claimed))
            {
                Refuse(refusals,
                    $"The source requires case '{expectation.TestId}' on '{expectation.EntryId}' and the record states no such " +
                    "planned case, so it accounts completely for a plan smaller than the one its source asks for.");
                continue;
            }

            if (claimed.Kind != expectation.Kind ||
                !string.Equals(claimed.Table, expectation.Table, StringComparison.Ordinal) ||
                !string.Equals(claimed.Column ?? string.Empty, expectation.Column ?? string.Empty, StringComparison.Ordinal))
            {
                Refuse(refusals,
                    $"Planned case '{expectation.TestId}' on '{expectation.EntryId}' names a different probe than the source " +
                    "derives under that identifier.");
            }
        }

        foreach ((string key, EntryVerificationPlannedCase claimed) in stated)
        {
            if (!required.ContainsKey(key))
            {
                Refuse(refusals,
                    $"The record states planned case '{claimed.TestId}' on '{claimed.EntryId}', which this ledger's source and " +
                    "standing decisions do not derive.");
            }
        }

        foreach (EntryVerificationCase item in record.Cases)
        {
            if (!required.TryGetValue(Key(item.EntryId, item.TestId), out EntryVerificationExpectation? expectation))
            {
                Refuse(refusals,
                    $"Case '{item.TestId}' on '{item.EntryId}' reports a result for a probe the source does not derive.");
                continue;
            }

            if (item.Kind != expectation.Kind ||
                !string.Equals(item.Expected, expectation.Expected, StringComparison.Ordinal))
            {
                Refuse(refusals,
                    $"Case '{item.TestId}' on '{item.EntryId}' was executed against '{item.Expected}' where the source resolves " +
                    $"'{expectation.Expected}', so it answers an easier question than the one the source asks.");
            }
        }

        return refusals;

        static void Refuse(List<string> refusals, string reason)
        {
            if (refusals.Count < MaxAuditRefusals)
            {
                refusals.Add(reason);
            }
        }
    }

    private static string Key(string entryId, string testId) => $"{entryId}\u0000{testId}";

    /// <summary>
    /// The probe pairings that hold for every planned case regardless of the source, so a plan trimmed to
    /// match what came back is recognisable as trimmed.
    ///
    /// A block's table is always asked both whether it exists and whether this run's identity may read it,
    /// because either alone would let a migrated application that cannot query the table read back as
    /// verified. A column's declared shape is never asked without asking whether the column is there at
    /// all. Whether a shape probe was planned depends on the source type resolving, so its absence is not
    /// a shortfall and is not treated as one.
    /// </summary>
    private static IEnumerable<string> IncompleteProbeFamilies(IReadOnlyList<EntryVerificationPlannedCase> planned)
    {
        foreach (IGrouping<(string Entry, string Table, string Column), EntryVerificationPlannedCase> family in
            planned.GroupBy(item => (Entry: item.EntryId, Table: item.Table, Column: item.Column ?? string.Empty)))
        {
            HashSet<EntryVerificationCaseKind> kinds = [.. family.Select(item => item.Kind)];
            bool scopedToColumn = family.Key.Column.Length > 0;

            if (scopedToColumn != (kinds.Contains(EntryVerificationCaseKind.TargetColumnExists) ||
                    kinds.Contains(EntryVerificationCaseKind.TargetColumnShape)))
            {
                yield return $"The planned cases for '{family.Key.Table}' on '{family.Key.Entry}' mix table probes with column " +
                    "probes, so which object this run asked about is ambiguous.";
                continue;
            }

            if (scopedToColumn)
            {
                if (!kinds.Contains(EntryVerificationCaseKind.TargetColumnExists))
                {
                    yield return $"'{family.Key.Table}.{family.Key.Column}' on '{family.Key.Entry}' was planned with a shape probe " +
                        "and no existence probe, so the plan is not one this run could have produced.";
                }
            }
            else if (!kinds.Contains(EntryVerificationCaseKind.TargetTableExists) ||
                !kinds.Contains(EntryVerificationCaseKind.TargetTableReadable))
            {
                yield return $"'{family.Key.Table}' on '{family.Key.Entry}' was planned without both the existence and the " +
                    "readability probe this run always plans together, so the plan is not one this run could have produced.";
            }
        }
    }

    /// <summary>A one-line reading of a record, for a run event and the aggregate report.</summary>
    public static string Describe(EntryVerificationCoverageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        int passed = record.Cases.Count(item => item.Outcome == DispositionVerificationStatus.Passed);
        int failed = record.Cases.Count(item => item.Outcome == DispositionVerificationStatus.Failed);
        int entries = record.Cases.Select(item => item.EntryId).Distinct(StringComparer.Ordinal).Count();
        int unanswered = record.Gaps.Count(gap => gap.Kind == EntryVerificationGapKind.PlannedCaseUnanswered);

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture,
            $"{record.Cases.Count} of {record.Planned.Count} planned case(s) executed over {entries} recorded decision(s): " +
            $"{passed} passed, {failed} failed. ");
        text.Append(CultureInfo.InvariantCulture,
            $"{unanswered} planned case(s) went unanswered and {record.Gaps.Count - unanswered} gap(s) were never probed. ");
        text.Append("The approved target was read only; no generated service was started, so nothing here is evidence of " +
            "application behaviour.");
        return text.ToString();
    }
}
