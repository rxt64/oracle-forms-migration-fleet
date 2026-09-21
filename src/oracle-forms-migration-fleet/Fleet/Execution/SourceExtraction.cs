// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record FormsModuleExtractionRequest(
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    string ExpectedContentSha256);

public sealed record FormsModuleExtractionResult(
    bool Succeeded,
    string? ModuleIdentity,
    string? ExtractedContentSha256,
    string? IntermediateRepresentationPath,
    IReadOnlyList<string> Findings);

public interface IFormsModuleExtractor
{
    IReadOnlyList<string> SupportedModuleExtensions { get; }

    Task<FormsModuleExtractionResult> ExtractAsync(
        FormsModuleExtractionRequest request,
        CancellationToken cancellationToken);
}

public sealed record OracleSchemaExtractionRequest(
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> SchemaAllowlist);

public sealed record OracleSchemaExtractionResult(
    bool Succeeded,
    string? SnapshotHash,
    string? SchemaArtifactPath,
    IReadOnlyList<string> Findings);

public interface IOracleSchemaExtractor
{
    Task<OracleSchemaExtractionResult> ExtractAsync(
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken);
}

public sealed record SourceSnapshotFacts(
    string SnapshotId,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    string ContentSha256,
    DateTimeOffset RecordedUtc,
    IReadOnlyList<ArtifactReference> Artifacts);

public interface ISourceSnapshotStore
{
    Task<SourceSnapshotFacts?> ResolveAsync(
        string owner,
        string workspaceId,
        string sourceRoot,
        SourceEnvironmentProfileBinding profile,
        CancellationToken cancellationToken);
}

public sealed record SourceEnvironmentProfileBinding(
    string SourceEnvironmentId,
    int Version,
    string CanonicalHash);

public sealed class UnavailableFormsModuleExtractor : IFormsModuleExtractor
{
    public IReadOnlyList<string> SupportedModuleExtensions => [".fmb", ".mmb", ".pll", ".olb"];

    public Task<FormsModuleExtractionResult> ExtractAsync(
        FormsModuleExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FormsModuleExtractionResult(
            false,
            null,
            null,
            null,
            ["No separately deployed native Forms extraction worker is configured. No module was opened."]));
    }
}

public sealed class UnavailableOracleSchemaExtractor : IOracleSchemaExtractor
{
    public Task<OracleSchemaExtractionResult> ExtractAsync(
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OracleSchemaExtractionResult(
            false,
            null,
            null,
            ["No separately deployed Oracle source gateway is configured. No database connection was attempted."]));
    }
}