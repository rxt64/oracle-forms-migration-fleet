using System.Globalization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Whether the SQL a phase is about to read out of the reserved prepared-source folder is statements this
/// server admitted.
///
/// The source gateway writes prepared schema statements into <see cref="PreparedSchemaTrustStore.PreparedFolder"/>
/// inside the operator's session copy, and records the claim over them outside it. Every phase that walks
/// the source tree for <c>.sql</c> text therefore picks those statements up as ordinary files, which means
/// the ledger check that <see cref="MigrationPhase.SourceNormalization"/> performs decides nothing for any
/// of them: a copy whose prepared schema was never claimed, was edited after it was claimed, or was
/// prepared against a different source environment would still be converted, deployed, and reconciled
/// against, with normalization's refusal sitting one phase upstream where it changed nothing.
///
/// So the check belongs where the bytes are consumed, and each consumer performs it for itself rather than
/// inheriting a verdict from another phase.
///
/// The namespace is what is trusted, not the extension. SQL an operator uploaded with their estate is
/// ordinary source and stays readable without any claim; SQL that presents itself as prepared output, by
/// sitting in the folder only this server writes into, has to be prepared output.
///
/// A claim is recorded against the source root it was prepared for, so the reserved folder this checks is
/// the one belonging to the root the run selected. Consumers walk the whole selected tree, which means a
/// reserved folder sitting anywhere else under it carries statements no claim in this run could cover,
/// and those are refused outright rather than mapped to some other root's record. A copy that holds no
/// reserved folder at all, and no outstanding claim over one, is not held to any of this.
/// </summary>
public static class PreparedSchemaConsumption
{
    /// <summary>
    /// Files this reads out of the reserved folder before refusing. Both ledgers together cannot name more
    /// than <see cref="PreparedSourceTrustStore.MaxClaims"/> * 2 + <see cref="PreparedSchemaTrustStore.MaxClaims"/> * 3
    /// files, so a folder larger than this holds files no claim could cover.
    /// </summary>
    private const int MaxPreparedFiles = 8_192;

    /// <summary>The ceiling every source-reading adapter enumerates the selected tree with.</summary>
    private const int MaxSourceFiles = 20_000;

    /// <summary>How many misplaced reserved files a refusal names before it stops listing them.</summary>
    private const int MaxNamedStrays = 16;

    /// <summary>The leading segment of <see cref="PreparedSchemaTrustStore.PreparedFolder"/>, matched whole.</summary>
    private const string ReservedSegment = ".fleet-source";

    /// <summary>The protocol's own ceiling on an artifact, so every file this server could have admitted is digestible here.</summary>
    private const long MaxPreparedFileBytes = SourceGatewayProtocol.MaxArtifactBytes;

    /// <summary>The reserved folder for a source root, as a workspace-relative path.</summary>
    public static string Folder(string sourceRoot) =>
        sourceRoot.Length == 0
            ? PreparedSchemaTrustStore.PreparedFolder
            : $"{sourceRoot}/{PreparedSchemaTrustStore.PreparedFolder}";

    /// <summary>
    /// Why the prepared statements under <paramref name="sourceRoot"/> may not be consumed, or null when
    /// there are none to consume or every one of them is admitted.
    ///
    /// <paramref name="consequence"/> is the calling phase's own description of what it did not do, so the
    /// refusal names the phase that refused rather than pointing at normalization.
    /// </summary>
    public static string? Refusal(PhaseExecutionContext context, string sourceRoot, string consequence)
    {
        ArgumentNullException.ThrowIfNull(context);

        string preparedRoot = Folder(sourceRoot);

        if (Misplaced(context, sourceRoot, preparedRoot, consequence) is { } elsewhere)
        {
            return elsewhere;
        }

        IReadOnlyList<WorkspaceFile> reserved;
        try
        {
            reserved = context.Workspace.EnumerateFiles(preparedRoot, MaxPreparedFiles);
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return
                $"The prepared-source folder `{preparedRoot}` could not be listed in full, so which of the statements in it this " +
                $"server admitted could not be established and {consequence}: {limit.Message}";
        }
        catch (WorkspacePathException)
        {
            return
                $"The prepared-source folder `{preparedRoot}` could not be resolved safely, so which of the statements in it this " +
                $"server admitted could not be established and {consequence}.";
        }

        bool statements = reserved.Any(file => OracleSourceFile.IsSqlText(file.RelativePath));
        if (!statements && PreparedSchemaTrustStore.LedgerPath(context.WorkspaceRoot) is null)
        {
            return null;
        }

        PreparedSourceTrustRead moduleRead = PreparedSourceTrustStore.Read(context.WorkspaceRoot, context.PreparedSourceBinding);
        if (moduleRead.Ledger is not { } moduleLedger)
        {
            return $"{moduleRead.Error} Nothing in `{preparedRoot}` was read, and {consequence}.";
        }

        PreparedSchemaTrustRead schemaRead = PreparedSchemaTrustStore.Read(context.WorkspaceRoot, context.PreparedSourceBinding);
        if (schemaRead.Ledger is not { } schemaLedger)
        {
            return $"{schemaRead.Error} Nothing in `{preparedRoot}` was read, and {consequence}.";
        }

        List<PreparedSourceClaim> modules =
        [
            .. moduleLedger.Claims
                .Where(claim => string.Equals(WorkspacePath.Normalize(claim.SourceRoot), sourceRoot, StringComparison.Ordinal))
                .OrderBy(claim => claim.ArtifactPath, StringComparer.Ordinal),
        ];

        List<PreparedSchemaClaim> schemas =
        [
            .. schemaLedger.Claims
                .Where(claim => string.Equals(WorkspacePath.Normalize(claim.SourceRoot), sourceRoot, StringComparison.Ordinal))
                .OrderBy(claim => claim.SchemaDdlPath, StringComparer.Ordinal),
        ];

        if (!statements && schemas.Count == 0)
        {
            return null;
        }

        HashSet<string> admitted = new(StringComparer.Ordinal);
        foreach (PreparedSourceClaim claim in modules)
        {
            admitted.Add(WorkspacePath.Normalize(claim.ArtifactPath));
            admitted.Add(WorkspacePath.Normalize(claim.ProvenancePath));
        }

        foreach (PreparedSchemaClaim claim in schemas)
        {
            foreach ((string path, _) in Files(claim))
            {
                admitted.Add(path);
            }
        }

        List<string> unclaimed = [.. reserved.Select(file => file.RelativePath).Where(path => !admitted.Contains(path))];

        if (unclaimed.Count > 0)
        {
            return
                $"{unclaimed.Count.ToString(CultureInfo.InvariantCulture)} file(s) in this source copy sit in the prepared-source " +
                $"folder and this server holds no record of preparing them: {string.Join(", ", unclaimed.Select(path => $"`{path}`"))}. " +
                "Those bytes arrived with the copy rather than from the source gateway, and statements are not evidence of " +
                $"themselves, so nothing in that folder was read and {consequence}. Remove them and prepare the source from the " +
                "workbench, which is what records the server-side claim that makes them readable.";
        }

        foreach (PreparedSchemaClaim claim in schemas)
        {
            foreach ((string path, string expected) in Files(claim))
            {
                if (Intact(context, path, expected, consequence) is { } rejection)
                {
                    return rejection;
                }
            }
        }

        return Consistent(modules, schemas, consequence);
    }

    /// <summary>The files one admitted schema claim covers, each with the digest recorded for it.</summary>
    private static IEnumerable<(string Path, string Sha256)> Files(PreparedSchemaClaim claim)
    {
        yield return (WorkspacePath.Normalize(claim.SchemaDdlPath), claim.SchemaDdlSha256);
        yield return (WorkspacePath.Normalize(claim.ProvenancePath), claim.ProvenanceSha256);

        if (claim.ProgramUnitPath is { Length: > 0 } units && claim.ProgramUnitSha256 is { Length: > 0 } digest)
        {
            yield return (WorkspacePath.Normalize(units), digest);
        }
    }

    /// <summary>
    /// Why the selected tree holds a reserved prepared-source folder somewhere other than its own, or
    /// nothing.
    ///
    /// Selecting a root above the one a source was prepared against leaves the claims filed under the
    /// deeper root while every consumer still walks down into it, so the statements are read as ordinary
    /// source and the record that would have judged them is never consulted. Mapping the deeper folder
    /// back to its own claims would make one run trust records it was not given, so the layout is refused
    /// instead and the operator selects the root the source was prepared against.
    /// </summary>
    private static string? Misplaced(PhaseExecutionContext context, string sourceRoot, string preparedRoot, string consequence)
    {
        IReadOnlyList<WorkspaceFile> tree;
        try
        {
            tree = context.Workspace.EnumerateFiles(sourceRoot, MaxSourceFiles);
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return
                "Whether this source copy holds prepared-source folders other than " +
                $"`{preparedRoot}` could not be established from a tree that was never fully listed, so {consequence}: {limit.Message}";
        }
        catch (WorkspacePathException)
        {
            return
                "Whether this source copy holds prepared-source folders other than " +
                $"`{preparedRoot}` could not be established, because the selected source root could not be resolved safely, so " +
                $"{consequence}.";
        }

        List<string> stray = [];
        foreach (WorkspaceFile file in tree)
        {
            string path = WorkspacePath.Normalize(file.RelativePath).TrimStart('/');

            if (!Reserved(path))
            {
                continue;
            }

            if (WorkspacePath.IsWithin(preparedRoot, path)
                && path.Length > preparedRoot.Length
                && !Reserved(path[(preparedRoot.Length + 1)..]))
            {
                continue;
            }

            stray.Add(path);
            if (stray.Count == MaxNamedStrays)
            {
                break;
            }
        }

        return stray.Count == 0
            ? null
            : $"{stray.Count.ToString(CultureInfo.InvariantCulture)} file(s) in this source copy sit in a reserved " +
                $"prepared-source folder this run does not admit: {string.Join(", ", stray.Select(path => $"`{path}`"))}. " +
                $"`{preparedRoot}` is the only such folder the claims for the selected source root were recorded against, and " +
                "every phase reading this tree would take the rest as ordinary source with no record standing behind it. " +
                $"Nothing was read and {consequence}. Select the source root the statements were prepared against, or remove " +
                "the folders that are not it.";
    }

    /// <summary>Whether a workspace-relative path carries the reserved folder name as a whole segment.</summary>
    private static bool Reserved(string path) =>
        path.Split('/').Any(segment => segment.Equals(ReservedSegment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why the statements on disk are not the ones the claim was recorded over, or nothing.</summary>
    private static string? Intact(PhaseExecutionContext context, string path, string expectedSha256, string consequence)
    {
        if (!context.Workspace.FileExists(path))
        {
            return
                $"This server recorded preparing `{path}` into this source copy and the file is not there. Prepared schema " +
                "statements and the record of them are committed together, so a record without its bytes is an incomplete " +
                $"admission rather than a smaller estate, and {consequence}. Prepare the source schema again from the workbench.";
        }

        try
        {
            if (!string.Equals(context.Workspace.Sha256(path, MaxPreparedFileBytes), expectedSha256, StringComparison.Ordinal))
            {
                return
                    $"`{path}` does not hash to the digest this server recorded when it admitted those bytes. The schema " +
                    $"statements in this source copy are not the ones the source gateway returned, so {consequence}.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceLimitExceededException or WorkspacePathException)
        {
            return $"`{path}` could not be digested against the record this server holds for it, so {consequence}.";
        }

        return null;
    }

    /// <summary>
    /// Why the claims in this copy describe more than one source environment, or nothing.
    ///
    /// A schema extraction has no file in the estate to pin it to, because its original source is the
    /// database the source environment profile names, so the profile IS that pin: the same immutable
    /// environment, version and canonical hash every other claim in the copy is held to. A copy carrying
    /// statements prepared against a different profile describes two source environments, and one estate
    /// cannot have come from both.
    /// </summary>
    private static string? Consistent(
        IReadOnlyList<PreparedSourceClaim> modules,
        IReadOnlyList<PreparedSchemaClaim> schemas,
        string consequence)
    {
        if (schemas.Count == 0)
        {
            return null;
        }

        (string Subject, string Environment, int Version, string Hash) pinned = modules.Count > 0
            ? ($"`{modules[0].ModuleAlias}`", modules[0].SourceEnvironmentId, modules[0].ProfileVersion, modules[0].ProfileHash)
            : ($"`{schemas[0].SchemaDdlPath}`", schemas[0].SourceEnvironmentId, schemas[0].ProfileVersion, schemas[0].ProfileHash);

        foreach ((string subject, string environment, int version, string hash) in
            modules.Select(claim => ($"`{claim.ModuleAlias}`", claim.SourceEnvironmentId, claim.ProfileVersion, claim.ProfileHash))
                .Concat(schemas.Select(claim => ($"`{claim.SchemaDdlPath}`", claim.SourceEnvironmentId, claim.ProfileVersion, claim.ProfileHash))))
        {
            if (string.Equals(environment, pinned.Environment, StringComparison.Ordinal)
                && version == pinned.Version
                && string.Equals(hash, pinned.Hash, StringComparison.Ordinal))
            {
                continue;
            }

            return
                $"{subject} was prepared against source environment '{environment}' version " +
                $"{version.ToString(CultureInfo.InvariantCulture)} and {pinned.Subject} against '{pinned.Environment}' version " +
                $"{pinned.Version.ToString(CultureInfo.InvariantCulture)}. A source environment profile is an immutable version " +
                "because the release and schema allowlist behind it are what an operator approved, so this copy holds extractions " +
                $"of two different source environments and {consequence}. Take a fresh copy of the source and prepare every module " +
                "and its schema against the current profile version.";
        }

        return null;
    }
}
