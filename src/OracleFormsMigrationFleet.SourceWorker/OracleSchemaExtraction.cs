using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// Constants the host gateway already speaks. The media type is repeated by value rather than shared by
/// reference so the worker and the web host stay separately deployable; the gateway matches on this string.
/// </summary>
public static class OracleSchemaProtocol
{
    public const int SchemaVersion = 1;

    public const string Generator = "oracle-forms-migration-fleet/source-worker-oracle-catalog";

    public const string ArtifactSchemaVersion = "1";

    public const string MediaType = "application/vnd.oracle-forms-migration-fleet.oracle-schema+json";

    public const int MaxArtifactBytes = 16 * 1024 * 1024;

    public const int MaxSchemas = 16;
    public const int MaxObjects = 65536;
    public const int MaxTables = 2048;
    public const int MaxColumns = 32768;
    public const int MaxConstraints = 16384;
    public const int MaxConstraintColumns = 32768;
    public const int MaxSequences = 2048;
    public const int MaxIndexes = 8192;
    public const int MaxIndexColumns = 32768;
    public const int MaxGrants = 16384;
    public const int MaxProgramUnits = 4096;
    public const int MaxSourceLines = 400_000;
    public const int MaxDependencies = 32768;
    public const int MaxTriggers = 4096;

    /// <summary>Upper bound on one LONG catalog value (a column default or a check condition).</summary>
    public const int MaxLongCharacters = 32768;

    /// <summary>Owners whose objects are the server's own and are never expected in an allowlist.</summary>
    public static readonly string[] SystemOwners = ["SYS", "SYSTEM", "PUBLIC", "OUTLN", "CTXSYS", "MDSYS", "ORDSYS", "XDB", "WMSYS", "OLAPSYS", "DBSNMP"];
}

/// <summary>
/// The closed list of things this build is able to turn back into DDL, and the closed list of names it is
/// willing to treat as already covered when a dependency points outside the estate.
///
/// It is a catalog rather than a rule because the failure mode it exists to prevent is an object kind that
/// nobody enumerated silently leaving the artifact: ALL_OBJECTS is read first and every row it returns must
/// match one of these entries or the extraction fails and names the object.
/// </summary>
public static class OracleObjectCoverage
{
    /// <summary>ALL_OBJECTS.OBJECT_TYPE values this build rebuilds into a statement of its own.</summary>
    public static readonly string[] Emitted =
        ["FUNCTION", "INDEX", "PACKAGE", "PACKAGE BODY", "PROCEDURE", "SEQUENCE", "TABLE", "TYPE", "TYPE BODY"];

    /// <summary>
    /// Segments Oracle creates as a consequence of an emitted object and that carry no separate statement
    /// here. A LOB segment exists because a table declares a LOB column, which the table statement already
    /// carries; its storage clause is deliberately not reproduced.
    /// </summary>
    public static readonly string[] Derived = ["LOB"];

    /// <summary>The object types whose text comes from ALL_SOURCE.</summary>
    public static readonly string[] SourceTypes =
        ["FUNCTION", "PACKAGE", "PACKAGE BODY", "PROCEDURE", "TYPE", "TYPE BODY"];

    /// <summary>
    /// Names under a server-owned schema that every PL/SQL unit is compiled against whether or not its
    /// author wrote them. They are listed one by one rather than inferred from the owner, so a dependency
    /// on a server-owned package that genuinely has to be migrated still escapes and still blocks.
    /// </summary>
    public static readonly string[] SystemBuiltins =
        ["DBMS_STANDARD", "DUAL", "PLITBLM", "STANDARD", "SYS_STUB_FOR_PURITY_ANALYSIS"];

    /// <summary>ALL_INDEXES.INDEX_TYPE values whose definition can be rebuilt from ALL_IND_COLUMNS.</summary>
    public static readonly string[] IndexTypes = ["NORMAL"];

    /// <summary>Object privileges this build writes back as a GRANT statement.</summary>
    public static readonly string[] ObjectPrivileges =
        ["ALTER", "DEBUG", "DELETE", "EXECUTE", "INDEX", "INSERT", "READ", "REFERENCES", "SELECT", "UPDATE", "WRITE"];

    public static bool IsEmitted(string objectType) => Emitted.Contains(objectType, StringComparer.Ordinal);

    public static bool IsDerived(string objectType) => Derived.Contains(objectType, StringComparer.Ordinal);

    public static bool IsCovered(string objectType) => IsEmitted(objectType) || IsDerived(objectType);

    public static bool IsSystemBuiltin(string name) => SystemBuiltins.Contains(name, StringComparer.Ordinal);
}

/// <summary>
/// A bounded request for the schemas on the caller's profile. It carries no credential, no connect string
/// and no SQL: the worker holds its own connection identity and its own allowlist, and this request may
/// only narrow that allowlist.
/// </summary>
public sealed record OracleSchemaExtractionRequest(
    int SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> SchemaAllowlist);

/// <summary>One artifact as bytes. <paramref name="Sha256"/> is the digest of <paramref name="Content"/>.</summary>
public sealed record OracleSchemaArtifact(string MediaType, string Sha256, byte[] Content);

/// <summary>
/// The extraction result. Every correlation field the caller sent is echoed so the gateway can refuse a
/// response that does not belong to the request it made.
/// </summary>
public sealed record OracleSchemaExtractionResult(
    int SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> SchemaAllowlist,
    string Status,
    DateTimeOffset ExtractedUtc,
    string? SnapshotHash,
    OracleSchemaArtifact? SchemaArtifact,
    IReadOnlyList<WorkerCapability> Capabilities,
    IReadOnlyList<string> Findings);

public sealed record OracleCatalogColumn(
    string Schema,
    string Table,
    string Name,
    int Position,
    string DataType,
    int? Length,
    int? CharLength,
    string? CharUsed,
    int? Precision,
    int? Scale,
    bool NotNull,
    string? Default);

public sealed record OracleCatalogConstraint(
    string Schema,
    string Table,
    string Name,
    char Type,
    string? ReferencedOwner,
    string? ReferencedConstraint,
    string? DeleteRule,
    bool Enabled,
    string? SearchCondition,
    IReadOnlyList<string> Columns);

public sealed record OracleCatalogSequence(
    string Schema,
    string Name,
    string MinValue,
    string MaxValue,
    string IncrementBy,
    string CacheSize,
    string LastNumber,
    bool Cycle,
    bool Order);

public sealed record OracleCatalogProgramUnit(string Schema, string Type, string Name, string Body);

/// <summary>One row of the inventory: what ALL_OBJECTS says the schema contains.</summary>
public sealed record OracleCatalogObject(string Schema, string Name, string Type, string Status);

public sealed record OracleCatalogIndexColumn(string Name, bool Descending);

public sealed record OracleCatalogIndex(
    string Schema,
    string Name,
    string TableOwner,
    string TableName,
    bool Unique,
    string IndexType,
    IReadOnlyList<OracleCatalogIndexColumn> Columns);

public sealed record OracleCatalogGrant(
    string Schema,
    string ObjectName,
    string Grantee,
    string Privilege,
    bool Grantable);

/// <summary>Everything the catalog was actually observed to contain, in the order it will be emitted.</summary>
public sealed record OracleCatalogFacts(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<(string Schema, string Table)> Tables,
    IReadOnlyList<OracleCatalogColumn> Columns,
    IReadOnlyList<OracleCatalogConstraint> Constraints,
    IReadOnlyList<OracleCatalogSequence> Sequences,
    IReadOnlyList<OracleCatalogIndex> Indexes,
    IReadOnlyList<OracleCatalogGrant> Grants,
    IReadOnlyList<OracleCatalogProgramUnit> ProgramUnits);

/// <summary>
/// Reads the schemas an operator approved out of one Oracle instance and writes a canonical DDL and
/// PL/SQL artifact built from what the catalog actually returned.
///
/// Three rules shape the whole class:
///
/// 1. <b>Read-only and fully parameterized.</b> Every statement is a SELECT against an ALL_ catalog view
///    with the owner bound as a parameter. No identifier from a request is ever concatenated into SQL.
/// 2. <b>The inventory comes first.</b> ALL_OBJECTS is enumerated for every requested schema before any
///    object is read in detail, and every row it returns must be an object kind on
///    <see cref="OracleObjectCoverage"/> and valid. A view, a synonym, a materialized view, a trigger, a
///    database link or any other kind this build does not rebuild therefore fails the extraction by name
///    instead of being absent from an artifact that reports success.
/// 3. <b>Nothing is invented.</b> A column's precision, nullability, default, key membership, an index's
///    columns, a grant and a program unit's body are emitted exactly as the catalog reported them. A type
///    this build cannot render, an index whose definition is not in ALL_IND_COLUMNS, a foreign key whose
///    parent lies outside the allowlist, a dependency on an object the inventory does not cover, or a
///    database link all fail the extraction with an explicit finding rather than producing an artifact
///    that silently omits them.
/// 4. <b>Opening a connection is not success.</b> The result is <c>Extracted</c> only after every
///    required catalog view has been enumerated to completion for every requested schema and reconciled
///    against the inventory.
/// </summary>
public sealed class OracleSchemaExtractionService(IOracleConnectionFactory connections)
{
    private readonly IOracleConnectionFactory _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<OracleSchemaExtractionResult> ExtractAsync(
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is string rejection)
        {
            return Rejected(request, rejection);
        }

        if (!_connections.Configured)
        {
            return Blocked(request,
                $"Oracle extraction is not configured on this worker ({_connections.FirstMissingSetting} is unset), so no connection was attempted.");
        }

        IReadOnlyList<string> requested = [.. request.SchemaAllowlist.Select(schema => schema.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (requested.FirstOrDefault(schema => !_connections.SchemaAllowlist.Contains(schema, StringComparer.Ordinal)) is string outside)
        {
            return Rejected(request, $"Schema '{outside}' is not on this worker's configured allowlist.");
        }

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_connections.Timeout);

        try
        {
            return await ReadAsync(request, requested, budget.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(request, requested, "The catalog read exceeded this worker's configured time budget; no artifact was produced.");
        }
        catch (CatalogIncompleteException incomplete)
        {
            return Failed(request, requested, incomplete.Message);
        }
        catch (DbException exception)
        {
            // The provider's message can name the instance but never this worker's credential, which is
            // held by the factory alone. The type is reported; the text is not echoed onward.
            return Failed(request, requested, $"The Oracle client failed while reading the catalog ({exception.GetType().Name}); no artifact was produced.");
        }
    }

    private async Task<OracleSchemaExtractionResult> ReadAsync(
        OracleSchemaExtractionRequest request,
        IReadOnlyList<string> schemas,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = await _connections.OpenAsync(cancellationToken);
        if (connection.State != ConnectionState.Open)
        {
            return Failed(request, schemas, "The Oracle client reported a connection that was not open; no catalog read was attempted.");
        }

        OracleCatalogReader reader = new(connection, _connections.ParameterStyle, (int)_connections.Timeout.TotalSeconds);
        List<string> findings = [];

        List<OracleCatalogObject> inventory = [];
        HashSet<(string Schema, string Name)> covered = [];
        Dictionary<(string Schema, string Type), SortedSet<string>> byType = [];

        // The inventory is read for every requested schema before any object is read in detail, so a kind
        // this build does not rebuild is refused by name rather than being absent from a successful result.
        foreach (string schema in schemas)
        {
            IReadOnlyList<OracleCatalogObject> objects = await reader.ReadObjectsAsync(schema, cancellationToken);
            if (objects.Count == 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' returned no object from ALL_OBJECTS; it is either absent or invisible to this worker's connection identity.");
            }

            string[] unsupported =
            [
                .. objects.Where(entry => !OracleObjectCoverage.IsCovered(entry.Type))
                    .Select(entry => $"{entry.Schema}.{entry.Name} ({entry.Type})")
            ];

            if (unsupported.Length > 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' owns {unsupported.Length} object(s) this build does not extract ({Sample(unsupported)}); the extraction fails rather than reporting coverage it does not have.");
            }

            string[] invalid =
            [
                .. objects.Where(entry => !IsUsable(entry))
                    .Select(entry => $"{entry.Schema}.{entry.Name} ({entry.Type} is {entry.Status})")
            ];

            if (invalid.Length > 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' owns {invalid.Length} object(s) the instance does not report as VALID ({Sample(invalid)}); the extraction fails rather than copying a definition the source itself cannot compile.");
            }

            foreach (OracleCatalogObject entry in objects)
            {
                covered.Add((entry.Schema, entry.Name));
                if (!byType.TryGetValue((entry.Schema, entry.Type), out SortedSet<string>? names))
                {
                    byType[(entry.Schema, entry.Type)] = names = new(StringComparer.Ordinal);
                }

                names.Add(entry.Name);
            }

            inventory.AddRange(objects);
        }

        List<(string Schema, string Table)> tables = [];
        List<OracleCatalogColumn> columns = [];
        List<OracleCatalogConstraint> constraints = [];
        List<OracleCatalogSequence> sequences = [];
        List<OracleCatalogIndex> indexes = [];
        List<OracleCatalogGrant> grants = [];
        List<OracleCatalogProgramUnit> programUnits = [];

        foreach (string schema in schemas)
        {
            IReadOnlyList<string> schemaTables = await reader.ReadTablesAsync(schema, cancellationToken);
            IReadOnlyList<OracleCatalogColumn> schemaColumns = await reader.ReadColumnsAsync(schema, findings, cancellationToken);
            IReadOnlyList<OracleCatalogConstraint> schemaConstraints = await reader.ReadConstraintsAsync(schema, cancellationToken);
            IReadOnlyList<OracleCatalogSequence> schemaSequences = await reader.ReadSequencesAsync(schema, cancellationToken);
            IReadOnlyList<OracleCatalogIndex> schemaIndexes = await reader.ReadIndexesAsync(schema, cancellationToken);
            IReadOnlyList<OracleCatalogGrant> schemaGrants = await reader.ReadObjectGrantsAsync(schema, cancellationToken);
            IReadOnlyList<string> columnGrants = await reader.ReadColumnGrantTargetsAsync(schema, cancellationToken);
            IReadOnlyList<OracleCatalogProgramUnit> schemaUnits = await reader.ReadProgramUnitsAsync(schema, cancellationToken);
            IReadOnlyList<string> schemaTriggers = await reader.ReadTriggerNamesAsync(schema, cancellationToken);

            if (schemaTriggers.Count > 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' owns {schemaTriggers.Count} database trigger(s) ({Sample(schemaTriggers)}) whose bodies this build does not read; the extraction fails rather than omitting them.");
            }

            if (columnGrants.Count > 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' carries {columnGrants.Count} column-level privilege(s) ({Sample(columnGrants)}) which this build does not write back; the extraction fails rather than reporting a schema whose access control it did not cover.");
            }

            Reconcile(schema, "table", Named(byType, schema, "TABLE"), schemaTables);
            Reconcile(schema, "sequence", Named(byType, schema, "SEQUENCE"), schemaSequences.Select(entry => entry.Name));
            Reconcile(schema, "index", Named(byType, schema, "INDEX"), schemaIndexes.Select(entry => entry.Name));

            foreach (string sourceType in OracleObjectCoverage.SourceTypes)
            {
                Reconcile(
                    schema,
                    sourceType.ToLowerInvariant(),
                    Named(byType, schema, sourceType),
                    schemaUnits.Where(unit => unit.Type == sourceType).Select(unit => unit.Name));
            }

            IReadOnlyList<string> escapes = await reader.ReadDependencyEscapesAsync(schema, covered, schemas, cancellationToken);
            if (escapes.Count > 0)
            {
                throw new CatalogIncompleteException(
                    $"Schema '{schema}' depends on objects outside the covered inventory ({Sample(escapes)}); the extraction fails rather than covering part of the graph.");
            }

            tables.AddRange(schemaTables.Select(table => (schema, table)));
            columns.AddRange(schemaColumns);
            constraints.AddRange(schemaConstraints);
            sequences.AddRange(schemaSequences);
            indexes.AddRange(schemaIndexes);
            grants.AddRange(schemaGrants);
            programUnits.AddRange(schemaUnits);
        }

        OracleCatalogFacts facts = new(schemas, tables, columns, constraints, sequences, indexes, grants, programUnits);
        string ddl = OracleDdlEmitter.Emit(facts);

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new OracleSchemaArtifactDocument(
                OracleSchemaProtocol.Generator,
                OracleSchemaProtocol.ArtifactSchemaVersion,
                request.SourceEnvironmentId,
                request.ProfileVersion,
                request.ProfileHash,
                schemas,
                _connections.ProviderAlias,
                new OracleSchemaCoverage(
                    inventory.Count, tables.Count, columns.Count, constraints.Count,
                    sequences.Count, indexes.Count, grants.Count, programUnits.Count),
                [
                    .. tables.Select(table => new OracleSchemaObject(table.Schema, "TABLE", table.Table)),
                    .. indexes.Select(index => new OracleSchemaObject(index.Schema, "INDEX", index.Name)),
                    .. sequences.Select(sequence => new OracleSchemaObject(sequence.Schema, "SEQUENCE", sequence.Name)),
                    .. programUnits.Select(unit => new OracleSchemaObject(unit.Schema, unit.Type, unit.Name)),
                ],
                ddl),
            WorkerProtocol.IrJson);

        if (content.Length > OracleSchemaProtocol.MaxArtifactBytes)
        {
            return Failed(request, schemas,
                $"The canonical schema artifact is {content.Length} bytes, over this worker's {OracleSchemaProtocol.MaxArtifactBytes} byte ceiling.");
        }

        string digest = ContentHash.OfBytes(content);

        return new OracleSchemaExtractionResult(
            OracleSchemaProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            request.ProfileVersion,
            request.ProfileHash,
            schemas,
            "Extracted",
            DateTimeOffset.UtcNow,
            digest,
            new OracleSchemaArtifact(OracleSchemaProtocol.MediaType, digest, content),
            [
                new WorkerCapability("oracle.source.connect", CapabilityState.Verified, "OracleSourceConnection",
                    null, null, "SourceGatewayHost", "none", $"provider={_connections.ProviderAlias}"),
                new WorkerCapability("oracle.catalog.read", CapabilityState.Verified, "OracleCatalogReadAccess",
                    null, null, "SourceGatewayHost", "none",
                    $"schemas={schemas.Count};objects={inventory.Count};tables={tables.Count};columns={columns.Count};constraints={constraints.Count};sequences={sequences.Count};indexes={indexes.Count};grants={grants.Count};units={programUnits.Count}"),
                new WorkerCapability("oracle.schema.extract", CapabilityState.Verified, "OracleCatalogReadAccess",
                    null, null, "SourceGatewayHost", "none", $"artifactBytes={content.Length}"),
            ],
            findings);
    }

    /// <summary>An object the instance itself does not report as compiled is never copied forward.</summary>
    private static bool IsUsable(OracleCatalogObject entry) =>
        string.Equals(entry.Status, "VALID", StringComparison.Ordinal) ||
        (OracleObjectCoverage.IsDerived(entry.Type) && string.Equals(entry.Status, "N/A", StringComparison.Ordinal));

    private static IEnumerable<string> Named(
        IReadOnlyDictionary<(string Schema, string Type), SortedSet<string>> byType,
        string schema,
        string type) =>
        byType.TryGetValue((schema, type), out SortedSet<string>? names) ? names : [];

    /// <summary>
    /// Refuses any disagreement between what ALL_OBJECTS says a schema contains and what the detail read
    /// for that kind returned, in either direction. An object the inventory names and the detail read
    /// missed would otherwise be an artifact that is silently short; one the detail read returned and the
    /// inventory does not name means the two reads did not see the same schema.
    /// </summary>
    private static void Reconcile(string schema, string kind, IEnumerable<string> inventory, IEnumerable<string> read)
    {
        SortedSet<string> expected = new(inventory, StringComparer.Ordinal);
        SortedSet<string> actual = new(read, StringComparer.Ordinal);

        string[] missing = [.. expected.Except(actual, StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            throw new CatalogIncompleteException(
                $"Schema '{schema}' lists {missing.Length} {kind}(s) in ALL_OBJECTS that the {kind} read did not return ({Sample(missing)}); the inventory and the extraction disagree.");
        }

        string[] extra = [.. actual.Except(expected, StringComparer.Ordinal)];
        if (extra.Length > 0)
        {
            throw new CatalogIncompleteException(
                $"Schema '{schema}' returned {extra.Length} {kind}(s) ALL_OBJECTS does not list ({Sample(extra)}); the inventory and the extraction disagree.");
        }
    }

    private static string Sample(IReadOnlyList<string> values) =>
        values.Count > 8
            ? string.Join(", ", values.Take(8)) + $", and {values.Count - 8} more"
            : string.Join(", ", values);

    private static string? Validate(OracleSchemaExtractionRequest request)
    {
        if (request.SchemaVersion != OracleSchemaProtocol.SchemaVersion)
        {
            return $"Unsupported request schema version {request.SchemaVersion}.";
        }

        if (string.IsNullOrWhiteSpace(request.SourceEnvironmentId) || request.SourceEnvironmentId.Length > 128 ||
            !request.SourceEnvironmentId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return "The source environment identifier is missing or malformed.";
        }

        if (request.ProfileVersion <= 0)
        {
            return "The profile version is not a positive integer.";
        }

        if (!ContentHash.IsSha256(request.ProfileHash))
        {
            return "The profile hash is not a SHA-256 digest.";
        }

        if (request.SchemaAllowlist is null || request.SchemaAllowlist.Count == 0)
        {
            return "The request named no schema.";
        }

        if (request.SchemaAllowlist.Count > OracleSchemaProtocol.MaxSchemas)
        {
            return $"The request named {request.SchemaAllowlist.Count} schemas, over the {OracleSchemaProtocol.MaxSchemas} ceiling.";
        }

        foreach (string schema in request.SchemaAllowlist)
        {
            if (!OracleSourceConfiguration.IsSchemaName(schema?.ToUpperInvariant()))
            {
                return "A requested schema name is not a plain Oracle identifier.";
            }
        }

        return null;
    }

    private OracleSchemaExtractionResult Rejected(OracleSchemaExtractionRequest request, string detail) =>
        Terminal(request, request.SchemaAllowlist ?? [], CapabilityState.Rejected, "oracle.schema.extract",
            "OracleSourceProfile", "operator.correct.source.profile.allowlist", detail);
    private OracleSchemaExtractionResult Blocked(OracleSchemaExtractionRequest request, string detail) =>
        Terminal(request, request.SchemaAllowlist ?? [], CapabilityState.BlockedPrerequisite, "oracle.source.connect",
            "OracleSourceConnection", "operator.configure.oracle.connection.and.allowlist", detail);

    private OracleSchemaExtractionResult Failed(
        OracleSchemaExtractionRequest request,
        IReadOnlyList<string> schemas,
        string detail) =>
        Terminal(request, schemas, "ExtractionFailed", "oracle.catalog.read",
            "OracleCatalogReadAccess", "operator.grant.catalog.read.on.allowlisted.schemas", detail,
            CapabilityState.Rejected);

    private OracleSchemaExtractionResult Terminal(
        OracleSchemaExtractionRequest request,
        IReadOnlyList<string> schemas,
        string status,
        string capabilityId,
        string prerequisite,
        string remediation,
        string detail,
        string? capabilityState = null) =>
        new(OracleSchemaProtocol.SchemaVersion,
            request.SourceEnvironmentId ?? string.Empty,
            request.ProfileVersion,
            request.ProfileHash ?? string.Empty,
            schemas,
            status,
            DateTimeOffset.UtcNow,
            null,
            null,
            [new WorkerCapability(capabilityId, capabilityState ?? status, prerequisite, null, null,
                "SourceGatewayHost", remediation)],
            [detail]);
}

/// <summary>Raised when a required catalog part could not be covered. Never carries a credential.</summary>
public sealed class CatalogIncompleteException(string message) : Exception(message);

internal sealed record OracleSchemaCoverage(
    int Objects,
    int Tables,
    int Columns,
    int Constraints,
    int Sequences,
    int Indexes,
    int Grants,
    int ProgramUnits);

internal sealed record OracleSchemaObject(string Schema, string Kind, string Name);

/// <summary>
/// The artifact body. It deliberately declares neither <c>normalized</c> nor <c>sourceRoot</c>: this is
/// extraction output and must not read as something the host's normalization phase already adjudicated.
/// </summary>
internal sealed record OracleSchemaArtifactDocument(
    string Generator,
    string SchemaVersion,
    string SourceEnvironmentId,
    int ProfileVersion,
    string ProfileHash,
    IReadOnlyList<string> Schemas,
    string Provider,
    OracleSchemaCoverage Coverage,
    IReadOnlyList<OracleSchemaObject> Objects,
    string Ddl);
