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

/// <summary>Kinds of PL/SQL program unit whose identity the parser can read off the CREATE header.</summary>
public enum OracleProgramUnitKind
{
    PackageSpecification,
    PackageBody,
    Procedure,
    Function,
    Trigger,
}

/// <summary>
/// The identity of one PL/SQL program unit: what it is and what it is called, read from the statement
/// header and nothing else. The body is still never interpreted — <see cref="Statement"/> is the same text
/// that appears in <see cref="OracleSchema.Unparsed"/> — but a consumer can now decide whether it is
/// looking at a specific object instead of guessing from a substring.
/// </summary>
public sealed record OracleProgramUnit(OracleProgramUnitKind Kind, string Name, string Statement);

/// <summary>
/// Structural model of a parsed Oracle DDL script. <see cref="Unparsed"/> holds every statement the
/// parser did not recognise, including PL/SQL program units, which are never interpreted here.
/// <see cref="ProgramUnits"/> carries the identity of those of them whose CREATE header names an object.
/// </summary>
public sealed record OracleSchema(
    IReadOnlyList<OracleTable> Tables,
    IReadOnlyList<OracleSequence> Sequences,
    IReadOnlyList<OracleIndex> Indexes,
    IReadOnlyList<string> Unparsed)
{
    public IReadOnlyList<OracleProgramUnit> ProgramUnits { get; init; } = [];

    public static OracleSchema Empty { get; } = new([], [], [], []);

    /// <summary>Combines several parsed files into one model, preserving the order they were supplied in.</summary>
    public static OracleSchema Merge(IEnumerable<OracleSchema> schemas)
    {
        List<OracleTable> tables = [];
        List<OracleSequence> sequences = [];
        List<OracleIndex> indexes = [];
        List<string> unparsed = [];
        List<OracleProgramUnit> programUnits = [];

        foreach (OracleSchema schema in schemas)
        {
            tables.AddRange(schema.Tables);
            sequences.AddRange(schema.Sequences);
            indexes.AddRange(schema.Indexes);
            unparsed.AddRange(schema.Unparsed);
            programUnits.AddRange(schema.ProgramUnits);
        }

        return new OracleSchema(tables, sequences, indexes, unparsed) { ProgramUnits = programUnits };
    }
}
