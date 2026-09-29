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
/// sitting in the folder only this server writes into, has to be prepared output. Nothing outside that
/// folder is examined here, and a copy that has no prepared statements at all is not held to any of it.
/// </summary>
public static class PreparedSchemaConsumption
{
    /// <summary>
    /// Files this reads out of the reserved folder before refusing. Both ledgers together cannot name more
    /// than <see cref="PreparedSourceTrustStore.MaxClaims"/> * 2 + <see cref="PreparedSchemaTrustStore.MaxClaims"/> * 3
    /// files, so a folder larger than this holds files no claim could cover.
    /// </summary>
    private const int MaxPreparedFiles = 8_192;

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

        if (!reserved.Any(file => OracleSourceFile.IsSqlText(file.RelativePath)))
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
