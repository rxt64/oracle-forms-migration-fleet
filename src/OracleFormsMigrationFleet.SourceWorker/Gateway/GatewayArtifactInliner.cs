namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

public sealed record GatewayArtifactResult(GatewayInlineArtifact? Artifact, string? Failure);

/// <summary>
/// Turns the local path a worker reported into inline bytes the caller may keep, or refuses.
///
/// THE PATH NEVER LEAVES THIS MACHINE. The worker writes its representation to this host's disk and names
/// the file it wrote; the caller has no use for that name and returning it would hand a remote process a
/// filesystem location on a machine that can reach the source estate. So the path is validated against the
/// output root this gateway configured for the source environment, the file is hashed here, and only the
/// bytes and their digest are reported.
///
/// The digest is computed over the bytes that are inlined, not over the file as a separate step, so the
/// caller's verification and this gateway's cannot drift apart.
/// </summary>
public static class GatewayArtifactInliner
{
    public static async Task<GatewayArtifactResult> InlineAsync(
        string outputRoot,
        string? reportedPath,
        string? reportedSha256,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportedPath))
        {
            return new GatewayArtifactResult(null, "The extraction worker reported success but named no artifact.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(reportedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new GatewayArtifactResult(null, "The extraction worker named an artifact path that does not resolve.");
        }

        if (!TrustedInput.IsWithin(outputRoot, resolved))
        {
            return new GatewayArtifactResult(null,
                "The extraction worker named an artifact outside the output root configured for this source environment.");
        }

        FileInfo file = new(resolved);
        if (!file.Exists)
        {
            return new GatewayArtifactResult(null, "The artifact the extraction worker named is not present.");
        }

        // A link under the output root can still point outside it, and the containment check above cannot
        // see that. Refusing is cheaper than resolving a link chain correctly on every filesystem.
        if (file.LinkTarget is not null)
        {
            return new GatewayArtifactResult(null, "The artifact the extraction worker named is a link rather than a regular file.");
        }

        if (file.Length is 0 || file.Length > GatewayProtocol.MaxArtifactBytes)
        {
            return new GatewayArtifactResult(null, "The artifact is empty or larger than the protocol allows.");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayArtifactResult(null, "The artifact could not be read from the configured output root.");
        }

        if (bytes.Length is 0 || bytes.Length > GatewayProtocol.MaxArtifactBytes)
        {
            return new GatewayArtifactResult(null, "The artifact is empty or larger than the protocol allows.");
        }

        string observed = ContentHash.OfBytes(bytes);
        if (reportedSha256 is not null && !ContentHash.Matches(reportedSha256, observed))
        {
            return new GatewayArtifactResult(null,
                "The artifact on disk does not hash to the digest the extraction worker reported, so it was not inlined.");
        }

        return new GatewayArtifactResult(
            new GatewayInlineArtifact(mediaType, observed, Convert.ToBase64String(bytes)),
            null);
    }
}
