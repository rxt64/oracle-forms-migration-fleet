using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// What a migration decided to do about one declared source property.
///
/// <see cref="Unresolved"/> is the state every entry is discovered in. It is not a decision and no actor
/// can select it: it is the absence of one, and it blocks completion for exactly that reason.
/// </summary>
public enum DispositionDecision
{
    Unresolved,
    Preserve,
    Transform,
    Retire,
    Defer,
}

/// <summary>
/// Whether a generated artifact for an entry was actually executed against a test, and what happened.
///
/// <see cref="NotExecuted"/> is the only state a count, a file listing, or a successful build can
/// produce. Moving off it requires evidence from something that ran.
/// </summary>
public enum DispositionVerificationStatus
{
    NotExecuted,
    Passed,
    Failed,
}

/// <summary>The identity of one declared source property, scoped so two estates can never collide.</summary>
/// <param name="ModuleName">The module name the export declared.</param>
/// <param name="FilePath">Workspace-relative path of the export the module was read from.</param>
/// <param name="ObjectPath">The source-object path within that module, as the fact inventory records it.</param>
/// <param name="ObjectType">The declared element's local name. A label, not an adjudicated Forms type.</param>
/// <param name="PropertyName">
/// The declared attribute's name, <see cref="DispositionLedgerEntries.ObjectPresenceProperty"/> for the
/// object's own existence, or <see cref="DispositionLedgerEntries.TextProperty"/> for its direct text.
/// </param>
/// <param name="BehaviorGroup">Which part of the application this object belongs to, for the operator summary.</param>
public sealed record DispositionSourceFactIdentity(
    string ModuleName,
    string FilePath,
    string ObjectPath,
    string ObjectType,
    string PropertyName,
    string BehaviorGroup);

/// <summary>An artifact a generation phase wrote for one entry, bound to the run that wrote it.</summary>
/// <param name="DecisionRevision">
/// The exact decision revision of the row this artifact was produced under. An artifact is a statement
/// about a decision, never about a property in the abstract, so a reference recorded under a revision the
/// row no longer carries stops describing it and re-deciding requires generating again. An empty value —
/// a row written before this binding existed — matches no revision, which is the fail-closed reading.
/// </param>
/// <param name="RunFenceToken">
/// The run ownership token held by whoever wrote this reference. A reference counts as evidence only
/// while it is still the run's current fence, so a write by an owner that was already fenced out is
/// durably inert rather than authoritative. Zero means no token was recorded and never matches a
/// claimed run, which is the fail-closed reading.
/// </param>
public sealed record DispositionGeneratedReference(
    string RunId,
    string ArtifactPath,
    string ContentSha256,
    DateTimeOffset RecordedUtc,
    string DecisionRevision,
    long RunFenceToken = 0);

/// <summary>
/// One executed test and the result it actually returned, bound to the exact thing it was executed
/// against.
///
/// The binding is the producer's own immutable statement of what it tested: which disposition of the
/// property stood, which source snapshot it read, which generated content it ran against, and under which
/// ownership claim it ran. The server checks that statement against what it holds and never fills any of
/// it in, because a binding supplied on a late producer's behalf is the agreement it failed to
/// demonstrate. Any part left empty or zero matches nothing, so a record written before this binding
/// existed reads back as evidence about nothing.
/// </summary>
/// <param name="DecisionRevision">The decision revision the tested artifact was generated under.</param>
/// <param name="GeneratedContentSha256">The digest of the generated content this test actually ran against.</param>
/// <param name="SourceSnapshotHash">The workspace source snapshot the tested artifact was generated from.</param>
/// <param name="ProducerFenceToken">The run ownership token held by the worker that executed this test.</param>
public sealed record DispositionTestReference(
    string RunId,
    string TestId,
    DispositionVerificationStatus Outcome,
    string Detail,
    DateTimeOffset RecordedUtc,
    string DecisionRevision,
    string GeneratedContentSha256,
    string SourceSnapshotHash,
    long ProducerFenceToken);

/// <summary>
/// One durable ledger row: a source property, what the export declared for it, what an operator decided,
/// which versioned rule authorized that decision, what was generated, and what was actually executed.
///
/// Every field an actor may set is a decision field. The snapshot hash, the identity, the observed value,
/// the generated references, and the verification all come from the server's own view of a run.
/// </summary>
public sealed record DispositionLedgerEntry
{
    public required string LedgerId { get; init; }

    public required string EntryId { get; init; }

    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>
    /// The workspace source snapshot this entry was read from. It is the run's snapshot hash, not the
    /// export's text digest: a digest of parsed text says nothing about which acquired source it came from.
    /// </summary>
    public required string SourceSnapshotHash { get; init; }

    public required DispositionSourceFactIdentity Identity { get; init; }

    /// <summary>The value the export declared, verbatim up to <see cref="DispositionLedgerEntries.MaxObservedValueCharacters"/>.</summary>
    public required string ObservedValue { get; init; }

    public bool ObservedValueTruncated { get; init; }

    /// <summary>How this value came to be known. Retained so a reader never has to assume the route.</summary>
    public required string ObservedEvidence { get; init; }

    public DispositionDecision Decision { get; init; } = DispositionDecision.Unresolved;

    public string? Rationale { get; init; }

    public string? MappingRuleId { get; init; }

    public int MappingRuleVersion { get; init; }

    public string? DecidedByObjectId { get; init; }

    public DateTimeOffset? DecidedUtc { get; init; }

    public IReadOnlyList<DispositionGeneratedReference> GeneratedRefs { get; init; } = [];

    public IReadOnlyList<DispositionTestReference> TestRefs { get; init; } = [];

    public DispositionVerificationStatus Verification { get; init; } = DispositionVerificationStatus.NotExecuted;

    public int Version { get; init; } = 1;

    /// <summary>
    /// How many times a decision has been recorded on this row, counted by the server.
    ///
    /// The decided timestamp cannot separate two decisions on its own: a clock has a resolution, and
    /// restating the same disposition for the same reason within one tick digests to the revision that
    /// was just superseded, which would let evidence proved under the earlier decision read back as
    /// evidence for the later one. This counter advances on every accepted decision and nothing else, so
    /// each decision is a distinct revision however fast they arrive. Zero is a row written before the
    /// counter existed; it digests differently from every counted decision, so the evidence recorded
    /// against such a row stops matching and the property reads back unverified until it is generated
    /// and retested — the fail-closed reading.
    /// </summary>
    public int DecisionSequence { get; init; }

    /// <summary>
    /// Canonical digest of this row's decision state: which property it is about, what was decided, the
    /// rule cited, the reason given, which actor recorded it when, and how many decisions preceded it.
    ///
    /// This is what an authorization is issued over, not <see cref="Version"/>. The row version advances
    /// every time anything on the row is rewritten, including evidence the server itself records, so
    /// digesting it makes entering an artifact against a decision indistinguishable from the decision
    /// changing. This changes when, and only when, the decision does — including a decision restated
    /// with a different reason, at a different rule version, or by a different actor.
    /// </summary>
    public string DecisionRevision => DispositionLedgerRules.DecisionRevision(this);

    /// <summary>A decision exists only when a rule and a rationale exist beside it.</summary>
    public bool IsDecided =>
        Decision != DispositionDecision.Unresolved &&
        !string.IsNullOrWhiteSpace(Rationale) &&
        !string.IsNullOrWhiteSpace(MappingRuleId);

    public bool IsGenerated => GeneratedRefs.Count > 0;

    public bool IsVerified => Verification == DispositionVerificationStatus.Passed;
}

/// <summary>
/// One ingested source snapshot's ledger. The header is server-written in full; nothing on it is a
/// caller's claim about which source was read.
/// </summary>
public sealed record DispositionLedger
{
    public required string LedgerId { get; init; }

    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>The owned, completed run whose intermediate representation these entries were read from.</summary>
    public required string RunId { get; init; }

    public required string SourceSnapshotHash { get; init; }

    public required string SourceRoot { get; init; }

    /// <summary>SHA-256 of the intermediate artifact bytes, as the run's own artifact manifest recorded it.</summary>
    public required string IntermediateContentSha256 { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string CreatedByObjectId { get; init; }

    public required int ModuleCount { get; init; }

    public required int EntryCount { get; init; }

    public int Version { get; init; } = 1;
}

/// <summary>Separate counts. A decision is not a generation and a generation is not a verification.</summary>
public sealed record DispositionLedgerCounts(
    int Discovered,
    int Decided,
    int Generated,
    int Verified,
    int Unresolved,
    int Deferred,
    int FailedVerification);

/// <summary>Whether a ledger may be reported Complete, and every reason it may not be.</summary>
public sealed record DispositionCompletionState(bool CanComplete, IReadOnlyList<string> Blockers);

/// <summary>
/// A versioned rule a decision must cite.
///
/// A rule is a named reason a class of property may be disposed of a particular way. It is versioned so a
/// decision recorded against version 1 stays readable as a version 1 decision after version 2 exists,
/// rather than silently acquiring the newer meaning.
/// </summary>
public sealed record DispositionMappingRule(
    string RuleId,
    int Version,
    string Summary,
    IReadOnlyList<DispositionDecision> Authorizes);

/// <summary>The rules this build recognises. A decision citing anything else is refused.</summary>
public static class DispositionMappingRules
{
    public static IReadOnlyList<DispositionMappingRule> All { get; } =
    [
        new("DR-PRESERVE-DECLARED", 1,
            "The declared value is carried into the generated application unchanged.",
            [DispositionDecision.Preserve]),
        new("DR-TRANSFORM-EQUIVALENT", 1,
            "The declared value has a documented Azure-stack equivalent and is carried across as that equivalent.",
            [DispositionDecision.Transform]),
        new("DR-RETIRE-NO-TARGET", 1,
            "The declared value describes Oracle Forms runtime machinery with no counterpart in the target stack.",
            [DispositionDecision.Retire]),
        new("DR-DEFER-NEEDS-EVIDENCE", 1,
            "The declared value cannot be disposed of until evidence this run does not have is available.",
            [DispositionDecision.Defer]),
    ];

    public static DispositionMappingRule? Find(string? ruleId, int version) =>
        All.FirstOrDefault(rule =>
            string.Equals(rule.RuleId, ruleId, StringComparison.Ordinal) && rule.Version == version);
}

/// <summary>
/// The deterministic rules of the ledger: what a decision must carry, what the counts are, and when a
/// ledger may be called complete.
///
/// Nothing here opens a gate or signs anything. It refuses decisions that are missing their reason, and
/// it refuses to call a ledger complete while anything is unresolved, deferred, ungenerated, or unverified.
/// </summary>
public static class DispositionLedgerRules
{
    public const int MinimumRationaleCharacters = 10;

    public const int MaximumRationaleCharacters = 1_000;

    /// <summary>Returns the reason a decision is refused, or null when it may be recorded.</summary>
    public static string? RejectDecision(DispositionDecision decision, string? rationale, string? ruleId, int ruleVersion)
    {
        if (!Enum.IsDefined(decision))
        {
            return "That disposition is not one this build recognises.";
        }

        if (decision == DispositionDecision.Unresolved)
        {
            return "Unresolved is the absence of a decision, so it cannot be recorded as one.";
        }

        string trimmed = (rationale ?? string.Empty).Trim();
        if (trimmed.Length < MinimumRationaleCharacters || trimmed.Length > MaximumRationaleCharacters)
        {
            return $"A disposition records why, in {MinimumRationaleCharacters} to {MaximumRationaleCharacters} characters.";
        }

        if (FleetGuardrails.ContainsPotentialSecret(trimmed))
        {
            return "That rationale looked like credential material and was refused before it was stored.";
        }

        if (DispositionMappingRules.Find(ruleId, ruleVersion) is not { } rule)
        {
            return "A disposition cites a mapping rule this build publishes, at the version it was read at.";
        }

        return rule.Authorizes.Contains(decision)
            ? null
            : $"Rule {rule.RuleId} version {rule.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} does not authorize {decision}.";
    }

    /// <summary>
    /// The canonical decision state of one row, as a digest.
    ///
    /// It covers the property the decision is about, the decision itself, the rule and rule version it
    /// cites, the reason recorded beside it, the actor and moment it was recorded at, and the count of
    /// decisions the server has accepted on the row. Every difference — including the same disposition
    /// restated under a new rationale, and the same disposition restated for the same reason within one
    /// tick of the clock — is a different revision, because the reason a property was disposed of that
    /// way is part of what was decided and a later decision is not the earlier one.
    ///
    /// Deliberately not derived from the row version. The row version also advances when the server
    /// records what a run generated, and evidence arriving is not a decision changing.
    /// </summary>
    public static string DecisionRevision(DispositionLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        StringBuilder canonical = new();
        canonical
            .Append(entry.EntryId).Append('\u0000')
            .Append(entry.SourceSnapshotHash).Append('\u0000')
            .Append(entry.Identity.FilePath).Append('\u0000')
            .Append(entry.Identity.ObjectPath).Append('\u0000')
            .Append(entry.Identity.PropertyName).Append('\u0000')
            .Append(entry.Decision.ToString()).Append('\u0000')
            .Append(entry.MappingRuleId ?? string.Empty).Append('\u0000')
            .Append(entry.MappingRuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\u0000')
            .Append(entry.Rationale ?? string.Empty).Append('\u0000')
            .Append(entry.DecidedByObjectId ?? string.Empty).Append('\u0000')
            .Append(entry.DecidedUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty).Append('\u0000')
            .Append(entry.DecisionSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// The generated references that describe the decision a row records right now.
    ///
    /// An artifact produced under a disposition the operator has since moved off is an artifact for a
    /// decision nobody holds any more, so it stops describing the property. Re-deciding therefore requires
    /// generating again under the decision that stands; the superseded image is retained as the audit
    /// trail and is never what the next result is read against.
    /// </summary>
    public static IReadOnlyList<DispositionGeneratedReference> GenerationsUnder(
        string? decisionRevision,
        IReadOnlyList<DispositionGeneratedReference> generated)
    {
        ArgumentNullException.ThrowIfNull(generated);

        return string.IsNullOrEmpty(decisionRevision)
            ? []
            : [.. generated.Where(reference =>
                string.Equals(reference.DecisionRevision, decisionRevision, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The generation a reader is currently looking at: the most recently recorded one produced under the
    /// decision this row now records.
    ///
    /// A property may carry references from several runs, and only the newest describes the artifact that
    /// would ship. Older references are kept as the audit trail and are not what execution evidence is
    /// read against.
    /// </summary>
    public static DispositionGeneratedReference? StandingGeneration(
        string? decisionRevision,
        IReadOnlyList<DispositionGeneratedReference> generated)
    {
        DispositionGeneratedReference? standing = null;
        foreach (DispositionGeneratedReference reference in GenerationsUnder(decisionRevision, generated))
        {
            if (standing is null || reference.RecordedUtc >= standing.RecordedUtc)
            {
                standing = reference;
            }
        }

        return standing;
    }

    /// <summary>
    /// Why an artifact a run is offering is not an artifact about this property as it stands, or null.
    ///
    /// The path is checked for containment before it is ever stored, because a durable reference is read
    /// back and shown to an operator, and a rooted or traversing path recorded here would describe
    /// something outside the workspace the run was confined to.
    /// </summary>
    public static string? RejectGeneratedBinding(
        DispositionLedgerEntry entry,
        DispositionGeneratedReference reference,
        long runFenceToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(reference);

        if (WorkspacePath.Validate(reference.ArtifactPath, "A generated artifact path") is { } path)
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(reference.ContentSha256))
        {
            return "A generated artifact is recorded with the digest of what was written, so a reference naming no content was refused.";
        }

        if (!string.Equals(reference.DecisionRevision, entry.DecisionRevision, StringComparison.Ordinal))
        {
            return "That artifact names a different disposition of this property than the one recorded now, so it was generated " +
                "under a decision nobody holds any more. Generate again under the decision that stands.";
        }

        return reference.RunFenceToken == runFenceToken
            ? null
            : "That artifact was written under an ownership claim this run no longer holds, so it is not output this run speaks for.";
    }

    /// <summary>
    /// Why a result is not a statement about the generation that stands for this property, or null.
    ///
    /// Every part of the binding is the producer's own claim about what it tested, and every part is
    /// matched exactly: the disposition that stood, the source snapshot, the generated content, the run,
    /// and the ownership claim that run held. Matching the run alone would let a result executed before a
    /// regeneration, or before the operator re-decided, re-attach itself to output it never saw.
    /// </summary>
    public static string? RejectTestBinding(
        DispositionLedgerEntry entry,
        DispositionGeneratedReference? standing,
        DispositionTestReference test)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(test);

        if (standing is null)
        {
            return "No generation produced under the decision this property now records stands for it, so there is no artifact " +
                "a result could be about.";
        }

        if (string.IsNullOrEmpty(test.DecisionRevision) ||
            !string.Equals(test.DecisionRevision, entry.DecisionRevision, StringComparison.Ordinal))
        {
            return "That result names a different disposition of this property than the one recorded now, so it proves something " +
                "about a decision nobody holds any more.";
        }

        if (string.IsNullOrEmpty(test.SourceSnapshotHash) ||
            !string.Equals(test.SourceSnapshotHash, entry.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return "That result names a different source snapshot than the one this property was read from.";
        }

        if (!string.Equals(test.RunId, standing.RunId, StringComparison.Ordinal) ||
            test.ProducerFenceToken != standing.RunFenceToken)
        {
            return "That result was produced by a different run, or under an ownership claim a newer one displaced, than the " +
                "generation that stands for this property.";
        }

        return !string.IsNullOrEmpty(test.GeneratedContentSha256) &&
            string.Equals(test.GeneratedContentSha256, standing.ContentSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "That result names different generated content than the artifact that stands for this property, so it was executed " +
                "against output this one has replaced.";
    }

    /// <summary>
    /// The execution evidence that still stands, given which generated references still do.
    ///
    /// A test result is a statement about a generated artifact, never about a property in the abstract.
    /// It counts only while the exact artifact it named, under the exact decision it named, is the one
    /// that stands, so a result from a superseded run, from a displaced owner, from a decision that has
    /// since moved, or from an image that has since been regenerated stops being presented and stops
    /// being counted. Nothing is deleted: the row keeps what was written.
    /// </summary>
    public static (IReadOnlyList<DispositionTestReference> Tests, DispositionVerificationStatus Verification) AdmitEvidence(
        DispositionLedgerEntry entry,
        IReadOnlyList<DispositionGeneratedReference> generated,
        IReadOnlyList<DispositionTestReference> tests)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(tests);

        DispositionGeneratedReference? standing = StandingGeneration(entry.DecisionRevision, generated);

        DispositionTestReference[] admitted =
            [.. tests.Where(test => RejectTestBinding(entry, standing, test) is null)];

        DispositionVerificationStatus verification =
            admitted.Any(test => test.Outcome == DispositionVerificationStatus.Failed)
                ? DispositionVerificationStatus.Failed
                : admitted.Any(test => test.Outcome == DispositionVerificationStatus.Passed)
                    ? DispositionVerificationStatus.Passed
                    : DispositionVerificationStatus.NotExecuted;

        return (admitted, verification);
    }

    public static DispositionLedgerCounts Count(IEnumerable<DispositionLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int discovered = 0, decided = 0, generated = 0, verified = 0, unresolved = 0, deferred = 0, failed = 0;
        foreach (DispositionLedgerEntry entry in entries)
        {
            discovered++;
            if (entry.IsDecided) decided++;
            if (entry.IsGenerated) generated++;
            if (entry.IsVerified) verified++;
            if (!entry.IsDecided) unresolved++;
            if (entry.Decision == DispositionDecision.Defer) deferred++;
            if (entry.Verification == DispositionVerificationStatus.Failed) failed++;
        }

        return new DispositionLedgerCounts(discovered, decided, generated, verified, unresolved, deferred, failed);
    }

    /// <summary>
    /// Complete means every discovered property was decided with a reason, nothing was deferred, every
    /// decision produced something, and every generated thing was executed and passed. Each shortfall is
    /// reported separately, because "97% verified" and "97% decided" are different migrations.
    /// </summary>
    public static DispositionCompletionState Completion(DispositionLedgerCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        List<string> blockers = [];

        if (counts.Discovered == 0)
        {
            blockers.Add("No source property was discovered, so there is nothing this ledger could show as migrated.");
        }

        if (counts.Unresolved > 0)
        {
            blockers.Add($"{Plural(counts.Unresolved, "source property", "source properties")} have no recorded disposition.");
        }

        if (counts.Deferred > 0)
        {
            blockers.Add($"{Plural(counts.Deferred, "source property", "source properties")} were deferred, which is a decision to decide later.");
        }

        if (counts.Generated < counts.Decided)
        {
            blockers.Add($"{Plural(counts.Decided - counts.Generated, "decision has", "decisions have")} produced no generated artifact.");
        }

        if (counts.Verified < counts.Generated)
        {
            blockers.Add($"{Plural(counts.Generated - counts.Verified, "generated property is", "generated properties are")} not backed by a test that executed and passed.");
        }

        if (counts.FailedVerification > 0)
        {
            blockers.Add($"{Plural(counts.FailedVerification, "property", "properties")} have an executed test that failed.");
        }

        return new DispositionCompletionState(blockers.Count == 0, blockers);
    }

    private static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}

/// <summary>
/// Projects a normalized Forms intermediate representation into ledger entries.
///
/// One entry is produced for every declared thing: each retained element's own existence, each attribute
/// it declared, and its direct text where it declared any. That is deliberately conservative — an export
/// property this build does not interpret still gets a row, because the alternative is a ledger that is
/// complete only over the subset something already understood.
///
/// Nothing here interprets Oracle Forms semantics. The behaviour grouping is a label for the operator
/// summary, and calling an element a trigger is not a claim about what it would do at runtime.
/// </summary>
public static class DispositionLedgerEntries
{
    /// <summary>Property name for the declared object's own existence.</summary>
    public const string ObjectPresenceProperty = "#object";

    /// <summary>Property name for an element's direct text.</summary>
    public const string TextProperty = "#text";

    /// <summary>
    /// Ceiling on one ledger. A larger estate is refused rather than truncated: a ledger silently missing
    /// rows reads back as an estate that declared fewer properties than it did.
    /// </summary>
    public const int MaxEntries = 60_000;

    /// <summary>Observed values beyond this are recorded truncated; the run's IR artifact keeps the full value.</summary>
    public const int MaxObservedValueCharacters = 4_000;

    public const string UngroupedBehavior = "Other declared objects";

    /// <summary>Exactly one side is set: the full entry list, or the reason the projection was refused.</summary>
    public static (IReadOnlyList<DispositionLedgerEntry>? Entries, string? Rejection) Project(
        string ledgerId,
        string tenantId,
        string projectId,
        string sourceSnapshotHash,
        IReadOnlyList<FormsModule> modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotHash);
        ArgumentNullException.ThrowIfNull(modules);

        List<DispositionLedgerEntry> entries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (FormsModule module in modules)
        {
            if (module.SourceFacts is not { } facts || facts.Facts.Count == 0)
            {
                return (null, $"Module '{module.Name}' carries no retained source facts, so its declared properties " +
                    "cannot be enumerated. Re-run source normalization on this snapshot.");
            }

            string filePath = module.SourcePath ?? string.Empty;
            string evidence =
                $"Declared in the Oracle Forms export at '{filePath}' and retained by source normalization; export text digest {facts.TextDigest}.";

            foreach (FormsSourceFact fact in facts.Facts)
            {
                string behavior = Behavior(fact.LocalName);

                if (!TryAdd(entries, seen, ledgerId, tenantId, projectId, sourceSnapshotHash, module, filePath, fact,
                        behavior, ObjectPresenceProperty,
                        fact.DeclaredName is { Length: > 0 } named ? $"declared as '{named}'" : "declared",
                        evidence, out string? duplicate))
                {
                    return (null, duplicate);
                }

                foreach (FormsSourceAttribute attribute in fact.Attributes)
                {
                    string property = attribute.Namespace.Length == 0
                        ? attribute.Name
                        : $"{{{attribute.Namespace}}}{attribute.Name}";

                    if (!TryAdd(entries, seen, ledgerId, tenantId, projectId, sourceSnapshotHash, module, filePath, fact,
                            behavior, property, attribute.Value, evidence, out duplicate))
                    {
                        return (null, duplicate);
                    }
                }

                if (fact.Text is { } text &&
                    !TryAdd(entries, seen, ledgerId, tenantId, projectId, sourceSnapshotHash, module, filePath, fact,
                        behavior, TextProperty, text, evidence, out duplicate))
                {
                    return (null, duplicate);
                }

                if (entries.Count > MaxEntries)
                {
                    return (null, $"This snapshot declares more than {MaxEntries.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                        "properties. A ledger that dropped the remainder would read as an estate that declared fewer, so none was written.");
                }
            }
        }

        return entries.Count == 0
            ? (null, "The normalized representation declared no source property, so there is nothing to record.")
            : (entries, null);
    }

    /// <summary>The stable identity of one property, independent of where it sat in an ingest order.</summary>
    public static string EntryId(string moduleQualifiedName, string objectPath, string propertyName)
    {
        string canonical = $"{moduleQualifiedName}\u0000{objectPath}\u0000{propertyName}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>A non-secret digest over the ingested identities, so two ingests of one snapshot are comparable.</summary>
    public static string EntrySetHash(IEnumerable<DispositionLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string[] ordered = [.. entries.Select(entry => entry.EntryId).Order(StringComparer.Ordinal)];
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ordered)));
    }

    private static bool TryAdd(
        List<DispositionLedgerEntry> entries,
        HashSet<string> seen,
        string ledgerId,
        string tenantId,
        string projectId,
        string sourceSnapshotHash,
        FormsModule module,
        string filePath,
        FormsSourceFact fact,
        string behavior,
        string property,
        string value,
        string evidence,
        out string? duplicate)
    {
        string entryId = EntryId(module.QualifiedName, fact.Id, property);

        if (!seen.Add(entryId))
        {
            duplicate = $"Module '{module.Name}' declares '{property}' on '{fact.Id}' more than once. Two rows sharing one " +
                "identity cannot both be decided, so the ledger was refused rather than written with one of them hidden.";
            return false;
        }

        bool truncated = value.Length > MaxObservedValueCharacters;

        entries.Add(new DispositionLedgerEntry
        {
            LedgerId = ledgerId,
            EntryId = entryId,
            TenantId = tenantId,
            ProjectId = projectId,
            SourceSnapshotHash = sourceSnapshotHash,
            Identity = new DispositionSourceFactIdentity(
                module.Name, filePath, fact.Id, fact.LocalName, property, behavior),
            ObservedValue = truncated ? value[..MaxObservedValueCharacters] : value,
            ObservedValueTruncated = truncated,
            ObservedEvidence = evidence,
        });

        duplicate = null;
        return true;
    }

    /// <summary>
    /// Which part of the application an operator would look for this object under. It is a reading aid over
    /// the declared element name and adjudicates nothing.
    /// </summary>
    public static string Behavior(string localName) => localName switch
    {
        "Trigger" => "Behaviour: triggers",
        "ProgramUnit" => "Behaviour: program units",
        "Block" or "DataBlock" => "Data blocks",
        "Item" => "Fields",
        "LOV" or "RecordGroup" or "RecordGroupColumn" or "LOVColumnMapping" => "Lists of values",
        "Canvas" or "Window" or "TabPage" or "Graphics" or "VisualAttribute" => "Layout",
        "Relation" => "Block relations",
        "Alert" or "Editor" or "Menu" or "MenuItem" => "Dialogs and menus",
        "FormModule" or "Module" => "Module",
        "Parameter" or "FormModuleProperty" or "PropertyClass" or "ObjectGroup" or "ObjectGroupChild" => "Module settings",
        _ => UngroupedBehavior,
    };
}
