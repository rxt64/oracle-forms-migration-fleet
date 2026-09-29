namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The neutral Forms shape this worker emits. The field names are the ones the host's Forms intermediate
/// reader already consumes, so extraction output needs no translation step and no modern-XML detour.
/// </summary>
public sealed record NeutralFormsItem(
    string Name,
    string ItemType,
    string? DataType,
    string? ColumnName,
    string? Prompt,
    bool Required,
    bool Visible,
    int? MaxLength);

public sealed record NeutralFormsTrigger(string Name, string Scope, string? Body);

public sealed record NeutralFormsBlock(
    string Name,
    string? BaseTable,
    int RecordsDisplayed,
    IReadOnlyList<NeutralFormsItem> Items,
    IReadOnlyList<NeutralFormsTrigger> Triggers);

public sealed record NeutralFormsModule(
    string Name,
    string? Title,
    IReadOnlyList<NeutralFormsBlock> Blocks,
    IReadOnlyList<NeutralFormsTrigger> Triggers,
    IReadOnlyList<string> ProgramUnits,
    IReadOnlyList<string> Lovs);

public sealed record NativeModuleRead(NeutralFormsModule? Module, IReadOnlyList<string> Findings)
{
    public static NativeModuleRead Failed(string finding) => new(null, [finding]);
}

/// <summary>
/// Opens one Forms module and reads its structure. The only implementation that ships is a binding to the
/// installed Forms API; the interface exists so the extraction pipeline is testable without an Oracle
/// install, and a test double is never evidence that the native path works.
/// </summary>
public interface INativeFormsModuleReader : IDisposable
{
    WorkerNativeEvidence Evidence { get; }

    NativeModuleRead Read(string modulePath, CancellationToken cancellationToken);
}

/// <summary>
/// Opens the configured provider, or explains why it stayed closed. Separate from the reader so the probe
/// can exercise exactly the same load path it reports on without extracting anything.
/// </summary>
public interface INativeFormsProvider
{
    Task<NativeProviderOpen> OpenAsync(WorkerConfiguration configuration, CancellationToken cancellationToken);
}

public sealed record NativeProviderOpen(
    INativeFormsModuleReader? Reader,
    WorkerNativeEvidence? Evidence,
    string? Prerequisite,
    string? Remediation,
    string? Detail)
{
    public static NativeProviderOpen Blocked(string prerequisite, string remediation, string detail) =>
        new(null, null, prerequisite, remediation, detail);
}
