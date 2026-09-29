// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The wire contract between the host gateway and this worker. Both commands read one versioned JSON
/// object from standard input and write one versioned JSON object to standard output.
///
/// Neither request carries a path, a command line, a library name or a connect string. Every filesystem
/// location the worker touches comes from its own fixed configuration, so a caller that is fully
/// compromised can still only ask for a module alias the operator already placed under the trusted input
/// root, pinned to a hash the caller has to state in advance.
/// </summary>
public static class WorkerProtocol
{
    public const int SchemaVersion = 1;

    /// <summary>The generator recorded in every intermediate representation this worker writes.</summary>
    public const string IrGenerator = "oracle-forms-migration-fleet/source-worker-native";

    /// <summary>The IR body version. The module shape matches what the host's Forms IR reader consumes.</summary>
    public const string IrSchemaVersion = "3";

    /// <summary>
    /// Protocol I/O. Null members stay on the wire: the host already parses this shape, and quietly
    /// dropping a field it reads would be a protocol change disguised as a formatting choice.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Artifact serialization. Absent optional members are omitted so the digest stays tight.</summary>
    public static readonly JsonSerializerOptions IrJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Capability states. <c>Verified</c> is only ever written by code that actually exercised the capability.</summary>
public static class CapabilityState
{
    public const string Verified = "Verified";
    public const string BlockedPrerequisite = "BlockedPrerequisite";
    public const string Rejected = "Rejected";
}

public sealed record WorkerProbeRequest(int SchemaVersion, string SourceEnvironmentId, string ExpectedFormsRelease);

/// <param name="Observed">
/// What the worker actually saw while testing this capability — a resolved export list, a file version, a
/// hash prefix. Null where the capability was never exercised. Never a path, a command or a credential.
/// </param>
public sealed record WorkerCapability(
    string Id,
    string State,
    string Prerequisite,
    string? RequiredRelease,
    string? RequiredArchitecture,
    string? RequiredHost,
    string Remediation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Observed = null);

public sealed record WorkerProbeResult(
    int SchemaVersion,
    string SourceEnvironmentId,
    string Status,
    DateTimeOffset ProbedUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    string ExpectedFormsRelease,
    IReadOnlyList<WorkerCapability> Capabilities);

/// <summary>
/// A bounded request to extract one already-present module. <paramref name="ModuleAlias"/> is a single
/// file name resolved under the configured trusted input root and nowhere else;
/// <paramref name="ExpectedContentSha256"/> pins the bytes the caller believes it is asking about.
/// </summary>
public sealed record WorkerExtractionRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    string ExpectedFormsRelease,
    int ProfileVersion,
    string ProfileHash,
    string ModuleAlias,
    string ExpectedContentSha256);

/// <summary>What the worker observed about the native libraries it actually loaded. Absent when none were loaded.</summary>
public sealed record WorkerNativeEvidence(
    string LibraryAlias,
    string? FileVersion,
    string? ProductVersion,
    string LibrarySha256,
    string DefinitionHeaderSha256,
    IReadOnlyList<string> ResolvedExports,
    IReadOnlyList<string> ResolvedConstants);

public sealed record WorkerExtractionResult(
    int SchemaVersion,
    string SourceEnvironmentId,
    string Status,
    DateTimeOffset ExtractedUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    string ExpectedFormsRelease,
    string? ModuleIdentity,
    string? ObservedContentSha256,
    string? IntermediateRepresentationPath,
    string? IntermediateRepresentationSha256,
    WorkerNativeEvidence? Native,
    IReadOnlyList<WorkerCapability> Capabilities,
    IReadOnlyList<string> Findings);

/// <summary>Process exit codes. The host distinguishes a refused prerequisite from a malformed protocol.</summary>
public static class WorkerExit
{
    public const int Success = 0;
    public const int UsageRejected = 64;
    public const int RequestRejected = 65;
    public const int BlockedPrerequisite = 2;
    public const int ExtractionFailed = 3;
    public const int Cancelled = 4;
}
