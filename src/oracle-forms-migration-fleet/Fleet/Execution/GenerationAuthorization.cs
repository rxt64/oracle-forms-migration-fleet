using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// One disposition-ledger row carried to the generator exactly as the server read it, including the
/// canonical revision of the decision it was read at.
///
/// The decision, the rule, its version and the decision revision travel together because a decision
/// without the rule that authorized it is unreadable later, and a decision without the revision it was
/// read at cannot be told apart from the decision as it stands now.
///
/// <paramref name="DecisionRevision"/> is <see cref="DispositionLedgerEntry.DecisionRevision"/>, not the
/// row version. The row version also advances when the server enters evidence against the row, and an
/// artifact being recorded is not a decision changing.
/// </summary>
public sealed record GenerationScopedDecision(
    string EntryId,
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    DispositionDecision Decision,
    string? MappingRuleId,
    int MappingRuleVersion,
    string DecisionRevision);

/// <summary>
/// The server's statement that a named ledger's recorded decisions cover a named mapping, over a named
/// source snapshot, at a named moment.
///
/// It is never parsed from a manifest, a request body, or any other caller-supplied document, and it is
/// never carried into a phase as a value issued earlier: a generation phase obtains one only by calling
/// <see cref="IGenerationAuthorizationProvider"/> at the moment it is about to generate, so a decision
/// changed between issuance and generation is a decision this run does not act under.
///
/// It also carries no verdict field: there is no "approved" boolean to read and trust, only the enumerated
/// decisions and the digests, which <see cref="GenerationCoverage"/> re-adjudicates at the point of use.
/// </summary>
public sealed record GenerationAuthorization
{
    public required string LedgerId { get; init; }

    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>The run whose source snapshot and normalized representation this authorization is bound to.</summary>
    public required string RunId { get; init; }

    public required string SourceSnapshotHash { get; init; }

    /// <summary>SHA-256 of the normalized Forms representation the decisions were recorded against.</summary>
    public required string IntermediateContentSha256 { get; init; }

    /// <summary>SHA-256 of the exact target-mapping manifest bytes this authorization was issued over.</summary>
    public required string MappingManifestSha256 { get; init; }

    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>
    /// Every entry the ledger holds, not the subset a mapping anchors on. Scoping to anchored objects
    /// leaves a trigger declared beside a mapped block outside the authorization entirely, and generating
    /// next to undecided behaviour is the thing this type exists to prevent.
    /// </summary>
    public required IReadOnlyList<GenerationScopedDecision> Scope { get; init; }

    /// <summary>
    /// How many rows the ledger holds, read by the server at the same moment as <see cref="Scope"/>. A
    /// scope shorter than this is a partial reading of the ledger and authorizes nothing.
    /// </summary>
    public required int LedgerEntryCount { get; init; }

    /// <summary>
    /// Canonical digest over the decision set, including each row's decision revision. A later recording
    /// compares the stored rows against it, so a decision moved from Preserve to Defer after this was
    /// issued no longer matches what was generated under.
    /// </summary>
    public required string ScopeDigest { get; init; }
}

/// <summary>What a phase tells the server about the bytes it just read, before it is told what it may do.</summary>
public sealed record GenerationAuthorizationRequest(
    MigrationPhase Phase,
    string ObservedIntermediateSha256,
    string ObservedMappingManifestSha256,
    IReadOnlyList<string> NormalizedModulePaths);

/// <summary>
/// Exactly one side is set: an authorization the phase may generate under, or every reason authority could
/// not be established.
/// </summary>
public sealed record GenerationAuthorizationDecision(
    GenerationAuthorization? Authorization,
    IReadOnlyList<string> Denials)
{
    public static GenerationAuthorizationDecision Granted(GenerationAuthorization authorization) => new(authorization, []);

    public static GenerationAuthorizationDecision Denied(params string[] denials) => new(null, denials);
}

/// <summary>
/// The server's authority over one run's generation, consulted at the moment of generation.
///
/// An implementation is constructed by the host and holds the tenant, project, run and ledger itself. A
/// phase names none of them: it supplies only what it observed in the workspace, so an adapter cannot ask
/// about another tenant's ledger and an authorization cannot arrive from a request body.
///
/// A phase with no provider is a phase with no authority. For Oracle Forms source on the .NET path that
/// is a refusal, never a weaker check.
/// </summary>
public interface IGenerationAuthorizationProvider
{
    Task<GenerationAuthorizationDecision> AuthorizeAsync(
        GenerationAuthorizationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>One entry a generation was covered by, recorded with the decision state it was covered at.</summary>
public sealed record GenerationCoveredEntry(
    string EntryId,
    string DecisionRevision,
    DispositionDecision Decision,
    string? MappingRuleId,
    int MappingRuleVersion);

/// <summary>
/// What one generation phase wrote, written beside the output so the server can record the evidence from
/// the bytes rather than from this document's own claims.
///
/// <see cref="OutputSetSha256"/> is the generator's digest over every emitted file. The server recomputes
/// it from the workspace before recording anything, so an artifact edited after the phase ran no longer
/// matches the generation it is offered as evidence of.
/// </summary>
public sealed record GenerationCoverageRecord(
    string LedgerId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SourceSnapshotHash,
    string IntermediateContentSha256,
    string MappingManifestSha256,
    string GeneratorId,
    string OutputRoot,
    string OutputSetSha256,
    string ScopeDigest,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<string> OutputFiles,
    IReadOnlyList<GenerationCoveredEntry> CoveredEntries);

/// <summary>
/// The deterministic rules binding recorded decisions to a generation phase.
///
/// Nothing here consults a ledger, a store, or a network. It re-derives, from the mapping the generator
/// actually read and the decisions the server actually recorded, whether generating would be covered —
/// and it returns every reason it would not be. An unresolved or deferred property is a reason, because
/// deferring is a decision to decide later and generating over it would ship an untaken decision.
/// </summary>
public static class GenerationCoverage
{
    /// <summary>Conventional location of the coverage record inside the generated application tier.</summary>
    public const string RecordPath = "application/generation-coverage.json";

    /// <summary>
    /// Ceiling on one authorization, equal to the ceiling on one ledger. An authorization carries the whole
    /// ledger, so the two numbers are the same by construction: a ledger this server was willing to record
    /// is one it is willing to adjudicate in full, and a scope short of it is refused rather than truncated.
    /// </summary>
    public const int MaxScopeEntries = DispositionLedgerEntries.MaxEntries;

    /// <summary>Ceiling on the emitted file list one coverage record may describe.</summary>
    public const int MaxOutputFiles = 5_000;

    public static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>True when <paramref name="objectPath"/> is the anchored object or something declared under it.</summary>
    public static bool Covers(string anchorPath, string objectPath) =>
        string.Equals(anchorPath, objectPath, StringComparison.Ordinal) ||
        objectPath.StartsWith(anchorPath + "/", StringComparison.Ordinal);

    /// <summary>
    /// A digest over the emitted set: every file's path and content, ordered by path.
    ///
    /// Both the generator and the server compute this the same way over the same bytes, so it is a
    /// comparison of two independent readings rather than a value one side asserted to the other.
    /// </summary>
    public static string OutputSetDigest(IEnumerable<(string Path, string ContentSha256)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        StringBuilder canonical = new();
        foreach ((string path, string content) in files
            .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            canonical.Append(path).Append('\u0000').Append(content).Append('\n');
        }

        return Digest(canonical.ToString());
    }

    /// <summary>
    /// A digest over a decision set: every row's identity, the decision, the rule it cites, and the
    /// canonical revision of the decision it was read at, ordered by entry.
    ///
    /// The decision revision is in the digest because that is what makes the digest a statement about a
    /// decision at a moment. Two readings that agree on every disposition but disagree on the reason,
    /// the rule version, or who recorded it when are readings of different decisions, and generating
    /// under one is not evidence about the other.
    ///
    /// It is deliberately not the row version. Recording what a run generated rewrites the row without
    /// touching the decision, and a digest that moved then would make every evidence write look like an
    /// operator changing their mind — denying the next generation for a reason nobody caused.
    /// </summary>
    public static string ScopeDigest(IEnumerable<GenerationScopedDecision> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StringBuilder canonical = new();
        foreach (GenerationScopedDecision entry in scope.OrderBy(entry => entry.EntryId, StringComparer.Ordinal))
        {
            canonical
                .Append(entry.EntryId).Append('\u0000')
                .Append(entry.ModulePath).Append('\u0000')
                .Append(entry.ObjectPath).Append('\u0000')
                .Append(entry.PropertyName).Append('\u0000')
                .Append(entry.Decision.ToString()).Append('\u0000')
                .Append(entry.MappingRuleId ?? string.Empty).Append('\u0000')
                .Append(entry.MappingRuleVersion.ToString(CultureInfo.InvariantCulture)).Append('\u0000')
                .Append(entry.DecisionRevision).Append('\n');
        }

        return Digest(canonical.ToString());
    }

    /// <summary>
    /// Every reason this authorization does not cover generating from this resolved mapping over these
    /// bytes, adjudicated against what the generator actually emits.
    ///
    /// This is the overload a generation phase uses. It has the resolved mapping, so it can tell a
    /// property the emitted tier is derived from apart from one that reaches no file, and it refuses to
    /// read Preserve or Transform on the second kind as anything but an unbacked claim.
    /// </summary>
    public static IReadOnlyList<string> Reject(
        GenerationAuthorization? authorization,
        TargetMapping mapping,
        IReadOnlyList<string> normalizedModulePaths,
        string observedIntermediateSha256,
        string observedMappingSha256)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return Reject(
            authorization,
            mapping.Declaration.Sources,
            mapping.Carried,
            normalizedModulePaths,
            observedIntermediateSha256,
            observedMappingSha256);
    }

    /// <summary>
    /// The same rules minus the one that needs a resolved mapping.
    ///
    /// A caller holding only the manifest's anchors — the server deciding before a schema has been
    /// parsed — cannot tell which declared properties the generator re-expresses, so this overload does
    /// not adjudicate that and an empty result from it does not authorize generating. The phase-time
    /// overload taking a <see cref="TargetMapping"/> is the one that does.
    /// </summary>
    public static IReadOnlyList<string> Reject(
        GenerationAuthorization? authorization,
        IReadOnlyList<MappedSourceReference> sources,
        IReadOnlyList<string> normalizedModulePaths,
        string observedIntermediateSha256,
        string observedMappingSha256) =>
        Reject(authorization, sources, null, normalizedModulePaths, observedIntermediateSha256, observedMappingSha256);

    /// <summary>
    /// Every reason this authorization does not cover generating from this mapping over these bytes.
    ///
    /// An empty list is the only thing that permits generation, and it is produced by re-adjudicating the
    /// decisions rather than by reading a flag. The digests are compared against values the caller derived
    /// from the files it actually read, so a manifest swapped after the authorization was issued, or a
    /// normalized representation re-written under it, fails here instead of being generated from.
    ///
    /// Coverage is the whole ledger, not the part a mapping anchors on. A trigger declared beside a mapped
    /// block is behaviour this run would either carry forward or drop, so it is adjudicated the same way:
    /// decided under a rule that authorizes the decision, or the phase stops. No property is recorded as
    /// out of scope.
    /// </summary>
    private static IReadOnlyList<string> Reject(
        GenerationAuthorization? authorization,
        IReadOnlyList<MappedSourceReference> sources,
        IReadOnlyList<CarriedSourceProperty>? carried,
        IReadOnlyList<string> normalizedModulePaths,
        string observedIntermediateSha256,
        string observedMappingSha256)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(normalizedModulePaths);

        if (authorization is null)
        {
            return
            [
                "No recorded disposition covers this run. Generating an application from source whose properties " +
                "nobody has decided about would ship decisions the generator made for itself, so the phase stopped. " +
                "Record a disposition ledger for this run's source and decide the properties the mapping rests on.",
            ];
        }

        List<string> blockers = [];

        if (!Matches(authorization.IntermediateContentSha256, observedIntermediateSha256))
        {
            blockers.Add(
                $"The decisions were recorded against normalized source digesting to '{Short(authorization.IntermediateContentSha256)}', " +
                $"and this run's normalized source digests to '{Short(observedIntermediateSha256)}'. They describe different source, so " +
                "the decisions are not about what would be generated here.");
        }

        if (!Matches(authorization.MappingManifestSha256, observedMappingSha256))
        {
            blockers.Add(
                $"This authorization was issued over a target mapping digesting to '{Short(authorization.MappingManifestSha256)}', and the " +
                $"manifest read here digests to '{Short(observedMappingSha256)}'. A mapping that changed after it was covered rebinds roles " +
                "the decisions were never reviewed against.");
        }

        if (authorization.Scope.Count > MaxScopeEntries)
        {
            blockers.Add(
                $"This authorization carries {Count(authorization.Scope.Count)} decided properties, beyond the " +
                $"{MaxScopeEntries.ToString(CultureInfo.InvariantCulture)} one generation phase adjudicates.");
        }

        if (authorization.Scope.Count != authorization.LedgerEntryCount)
        {
            blockers.Add(
                $"This authorization carries {Count(authorization.Scope.Count)} of the ledger's " +
                $"{Count(authorization.LedgerEntryCount)} recorded properties. A partial reading of the ledger leaves the rest " +
                "undecided and unmentioned, so it covers nothing.");
        }

        if (!Matches(authorization.ScopeDigest, ScopeDigest(authorization.Scope)))
        {
            blockers.Add(
                "The decisions this authorization carries do not digest to the decision set it says it was issued over, so it is " +
                "not the authorization the server produced.");
        }

        HashSet<string> scopedModules = new(authorization.Scope.Select(entry => entry.ModulePath), StringComparer.Ordinal);

        foreach (string module in normalizedModulePaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!scopedModules.Contains(module))
            {
                blockers.Add(
                    $"This run normalized Oracle Forms module '{module}', and the recorded decisions say nothing about it. Generating " +
                    "beside a module nobody dispositioned would carry part of the estate forward and drop the rest silently.");
            }
        }

        MappedSourceReference[] formsAnchors =
            [.. sources.Where(source => source.Kind == MappedSourceKind.FormsSourceObject)];

        foreach (MappedSourceReference anchor in formsAnchors)
        {
            if (anchor.Module is not { Length: > 0 } module)
            {
                blockers.Add($"Source anchor '{anchor.Id}' names no module, so no recorded decision can be matched to it.");
                continue;
            }

            if (!authorization.Scope.Any(entry =>
                    string.Equals(entry.ModulePath, module, StringComparison.Ordinal) &&
                    Covers(anchor.Path, entry.ObjectPath)))
            {
                blockers.Add(
                    $"The mapping rests on source object '{anchor.Path}' in module '{module}', and no recorded disposition covers it. " +
                    "A mapping may not draw behaviour from source nobody decided the disposition of.");
            }
        }

        foreach (GenerationScopedDecision entry in authorization.Scope)
        {
            if (Refuse(entry, carried) is { } refusal)
            {
                blockers.Add(refusal);
            }
        }

        if (formsAnchors.Length > 0 && authorization.Scope.Count == 0)
        {
            blockers.Add(
                "This mapping rests on Oracle Forms source and the authorization carries no decision at all, so nothing about that " +
                "source has been dispositioned.");
        }

        return blockers;
    }

    /// <summary>
    /// Why one recorded decision does not permit generating over that property, or null when it does.
    ///
    /// A property the mapping never anchors on takes the same path as one it does. Retiring it under
    /// DR-RETIRE-NO-TARGET with a reason is a decision; leaving it undecided because no role happened to
    /// cite it is not.
    ///
    /// Preserve and Transform are claims that the property survives into the generated application, so
    /// they are additionally checked against the properties the resolved mapping says the generator
    /// re-expresses. Retire is not: it is the operator's decision to drop the property, and it asserts
    /// nothing about what was emitted.
    /// </summary>
    private static string? Refuse(GenerationScopedDecision entry, IReadOnlyList<CarriedSourceProperty>? carried)
    {
        string where = $"'{entry.PropertyName}' on '{entry.ObjectPath}' in module '{entry.ModulePath}'";

        switch (entry.Decision)
        {
            case DispositionDecision.Unresolved:
                return $"{where} has no recorded disposition. Unresolved is the absence of a decision, not a decision to proceed.";

            case DispositionDecision.Defer:
                return $"{where} was deferred, which is a decision to decide later. Generating over it now would take that decision silently.";

            case DispositionDecision.Preserve:
            case DispositionDecision.Transform:
            case DispositionDecision.Retire:
                break;

            default:
                return $"{where} carries a disposition this build does not recognise.";
        }

        if (DispositionMappingRules.Find(entry.MappingRuleId, entry.MappingRuleVersion) is not { } rule)
        {
            return $"{where} cites no mapping rule this build publishes at the version it was decided at, so why it was " +
                $"{entry.Decision} cannot be read back.";
        }

        if (!rule.Authorizes.Contains(entry.Decision))
        {
            return $"{where} is recorded {entry.Decision} under rule {rule.RuleId} version " +
                $"{rule.Version.ToString(CultureInfo.InvariantCulture)}, which does not authorize that disposition.";
        }

        if (carried is null || entry.Decision is not (DispositionDecision.Preserve or DispositionDecision.Transform))
        {
            return null;
        }

        return carried.Any(property =>
                string.Equals(property.ModulePath, entry.ModulePath, StringComparison.Ordinal) &&
                string.Equals(property.ObjectPath, entry.ObjectPath, StringComparison.Ordinal) &&
                string.Equals(property.PropertyName, entry.PropertyName, StringComparison.Ordinal))
            ? null
            : $"{where} is recorded {entry.Decision} under rule {rule.RuleId}, and this generator emits nothing derived from it. " +
                $"It re-expresses the table a mapped block declares it queries and the columns the items under that block are bound " +
                $"to; it does not translate this property, so calling it carried forward would claim behaviour no emitted file holds. " +
                $"An operator may decide to retire it under a rule that authorizes retiring, or leave it deferred until a generator " +
                $"carries it. This phase decides neither on their behalf.";
    }

    private static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Short(string? digest) =>
        string.IsNullOrWhiteSpace(digest) ? "(none)" : digest.Length <= 12 ? digest : digest[..12];

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
