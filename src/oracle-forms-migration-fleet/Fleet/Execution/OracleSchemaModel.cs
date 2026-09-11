// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>Constraint categories the schema parser recognises.</summary>
public enum OracleConstraintKind
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
}

/// <summary>
/// One column of a <see cref="OracleTable"/>. <see cref="RawType"/> preserves what the DDL said so the
/// emitter can report the mapping it applied; <see cref="BaseType"/> is the normalized type keyword.
/// </summary>
public sealed record OracleColumn(
    string Name,
    string RawType,
    string BaseType,
    int? Precision,
    int? Scale,
    bool NotNull,
    string? Default);

/// <summary>An inline or out-of-line table constraint. Check expressions are kept verbatim.</summary>
public sealed record OracleConstraint
{
    public required OracleConstraintKind Kind { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public string? ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public string? CheckExpression { get; init; }
}

public sealed record OracleTable(
    string Name,
    IReadOnlyList<OracleColumn> Columns,
    IReadOnlyList<OracleConstraint> Constraints);

public sealed record OracleSequence(string Name, long? StartWith, long? IncrementBy);

public sealed record OracleIndex(string Name, string Table, IReadOnlyList<string> Columns, bool IsUnique);

/// <summary>
/// Structural model of a parsed Oracle DDL script. <see cref="Unparsed"/> holds every statement the
/// parser did not recognise, including PL/SQL program units, which are never interpreted here.
/// </summary>
public sealed record OracleSchema(
    IReadOnlyList<OracleTable> Tables,
    IReadOnlyList<OracleSequence> Sequences,
    IReadOnlyList<OracleIndex> Indexes,
    IReadOnlyList<string> Unparsed)
{
    public static OracleSchema Empty { get; } = new([], [], [], []);

    /// <summary>Combines several parsed files into one model, preserving the order they were supplied in.</summary>
    public static OracleSchema Merge(IEnumerable<OracleSchema> schemas)
    {
        List<OracleTable> tables = [];
        List<OracleSequence> sequences = [];
        List<OracleIndex> indexes = [];
        List<string> unparsed = [];

        foreach (OracleSchema schema in schemas)
        {
            tables.AddRange(schema.Tables);
            sequences.AddRange(schema.Sequences);
            indexes.AddRange(schema.Indexes);
            unparsed.AddRange(schema.Unparsed);
        }

        return new OracleSchema(tables, sequences, indexes, unparsed);
    }
}
