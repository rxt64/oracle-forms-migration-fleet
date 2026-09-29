// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The document this worker writes. <c>modules</c> carries the neutral Forms shape the host's intermediate
/// reader already consumes, so the host normalization phase can adopt these entries directly rather than
/// translating an intermediate format of this worker's own invention.
///
/// The worker deliberately does not write the host's generator name, and does not claim <c>normalized</c>.
/// Normalization is adjudicated by the host against a session source root this worker has no knowledge of;
/// writing those fields here would let extraction output pass a gate it never went through.
/// </summary>
public sealed record FormsIrDocument(
    string Generator,
    string SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    string ContentSha256,
    string FormsFamily,
    string VersionAuthority,
    IReadOnlyList<NeutralFormsModule> Modules);

public sealed record FormsIrWrite(string? Path, string? Sha256, string? Error);

public static class FormsIrWriter
{
    /// <summary>
    /// Serializes and writes the representation, returning the digest of the exact bytes on disk.
    ///
    /// The bytes are the hashed artifact, not a re-serialization of the object: a caller that verifies the
    /// digest is verifying the file it will read, and the two cannot drift apart. Serialization is
    /// deterministic — fixed property order from the record shape, no indentation, invariant formatting,
    /// UTF-8 with no byte order mark — so the same module through the same API yields the same digest.
    /// </summary>
    public static async Task<FormsIrWrite> WriteAsync(
        string outputRoot,
        FormsIrDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(document, WorkerProtocol.IrJson);
        string digest = ContentHash.OfBytes(content);
        string fileName = $"{Sanitize(document.Modules[0].Name)}-{document.ContentSha256[..16]}.forms-ir.json";
        string path = Path.Combine(outputRoot, fileName);

        if (!TrustedInput.IsWithin(outputRoot, path))
        {
            return new FormsIrWrite(null, null, "The derived artifact name did not resolve under the configured output root.");
        }

        try
        {
            Directory.CreateDirectory(outputRoot);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FormsIrWrite(null, null, "The intermediate representation could not be written to the configured output root.");
        }

        return new FormsIrWrite(path, digest, null);
    }

    private static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 64)];
        for (int index = 0; index < buffer.Length; index++)
        {
            char character = name[index];
            buffer[index] = char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_';
        }

        return buffer.IsEmpty ? "module" : new string(buffer);
    }
}
