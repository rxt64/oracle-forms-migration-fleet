using System.Text;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// Writes canonical Oracle DDL and PL/SQL from observed catalog rows and nothing else.
///
/// Every identifier is quoted, so a name that is lower case, reserved, or contains a separator survives
/// the round trip unchanged. Statement order is fixed — sequences, then tables, then constraints, then
/// indexes, then grants, then program units — so a foreign key is never written before the table it points
/// at, every statement the host reads as schema DDL precedes the program-unit section, and the same catalog
/// always produces the same bytes. Anything that cannot be rendered from what the catalog reported throws
/// rather than being approximated.
/// </summary>
public static class OracleDdlEmitter
{
    public static string Emit(OracleCatalogFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Dictionary<(string Schema, string Table), List<OracleCatalogColumn>> columns = [];
        foreach (OracleCatalogColumn column in facts.Columns)
        {
            if (!columns.TryGetValue((column.Schema, column.Table), out List<OracleCatalogColumn>? list))
            {
                columns[(column.Schema, column.Table)] = list = [];
            }

            list.Add(column);
        }

        Dictionary<(string Owner, string Name), OracleCatalogConstraint> byName = [];
        foreach (OracleCatalogConstraint constraint in facts.Constraints)
        {
            byName[(constraint.Schema, constraint.Name)] = constraint;
        }

        HashSet<(string Schema, string Name)> tableNames = [.. facts.Tables.Select(table => (table.Schema, table.Table))];
        HashSet<(string Schema, string Name)> grantable =
        [
            .. facts.Tables.Select(table => (table.Schema, table.Table)),
            .. facts.Sequences.Select(sequence => (sequence.Schema, sequence.Name)),
            .. facts.ProgramUnits.Select(unit => (unit.Schema, unit.Name)),
        ];

        StringBuilder builder = new();

        foreach (string schema in facts.Schemas)
        {
            foreach (OracleCatalogSequence sequence in facts.Sequences.Where(entry => entry.Schema == schema).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                AppendSequence(builder, sequence);
            }
        }

        foreach (string schema in facts.Schemas)
        {
            foreach ((string _, string table) in facts.Tables.Where(entry => entry.Schema == schema).OrderBy(entry => entry.Table, StringComparer.Ordinal))
            {
                if (!columns.TryGetValue((schema, table), out List<OracleCatalogColumn>? tableColumns) || tableColumns.Count == 0)
                {
                    throw new CatalogIncompleteException($"Table '{schema}.{table}' was listed but returned no columns.");
                }

                AppendTable(builder, schema, table, tableColumns);
            }
        }

        foreach (string schema in facts.Schemas)
        {
            IEnumerable<OracleCatalogConstraint> ordered = facts.Constraints
                .Where(constraint => constraint.Schema == schema)
                .OrderBy(constraint => constraint.Table, StringComparer.Ordinal)
                .ThenBy(Rank)
                .ThenBy(constraint => constraint.Name, StringComparer.Ordinal);

            foreach (OracleCatalogConstraint constraint in ordered)
            {
                columns.TryGetValue((constraint.Schema, constraint.Table), out List<OracleCatalogColumn>? owner);
                AppendConstraint(builder, constraint, owner ?? [], byName);
            }
        }

        foreach (string schema in facts.Schemas)
        {
            IEnumerable<OracleCatalogIndex> ordered = facts.Indexes
                .Where(index => index.Schema == schema)
                .OrderBy(index => index.Name, StringComparer.Ordinal);

            foreach (OracleCatalogIndex index in ordered)
            {
                AppendIndex(builder, index, facts.Constraints, tableNames);
            }
        }

        foreach (string schema in facts.Schemas)
        {
            IEnumerable<OracleCatalogGrant> ordered = facts.Grants
                .Where(grant => grant.Schema == schema)
                .OrderBy(grant => grant.ObjectName, StringComparer.Ordinal)
                .ThenBy(grant => grant.Grantee, StringComparer.Ordinal)
                .ThenBy(grant => grant.Privilege, StringComparer.Ordinal);

            foreach (OracleCatalogGrant grant in ordered)
            {
                AppendGrant(builder, grant, grantable);
            }
        }

        foreach (string schema in facts.Schemas)
        {
            List<OracleCatalogProgramUnit> units = [.. facts.ProgramUnits.Where(unit => unit.Schema == schema)];
            if (units.Count == 0)
            {
                continue;
            }

            builder.Append("ALTER SESSION SET CURRENT_SCHEMA = ").Append(Quote(schema)).Append(";\n\n");
            foreach (OracleCatalogProgramUnit unit in units)
            {
                AppendProgramUnit(builder, unit);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when a primary key or unique constraint on the same table already declares this index, either
    /// because Oracle named the index after the constraint or because the constraint was built on an index
    /// that already covered exactly those columns in that order. Such an index is not written again; any
    /// other index is a real object of its own and gets a CREATE INDEX statement.
    /// </summary>
    internal static bool IsConstraintBacked(OracleCatalogIndex index, IReadOnlyList<OracleCatalogConstraint> constraints)
    {
        foreach (OracleCatalogConstraint constraint in constraints)
        {
            if (constraint.Type is not ('P' or 'U') ||
                constraint.Schema != index.TableOwner ||
                constraint.Table != index.TableName)
            {
                continue;
            }

            if (constraint.Name == index.Name)
            {
                return true;
            }

            if (index.Unique &&
                constraint.Columns.Count == index.Columns.Count &&
                constraint.Columns.SequenceEqual(index.Columns.Select(column => column.Name), StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendIndex(
        StringBuilder builder,
        OracleCatalogIndex index,
        IReadOnlyList<OracleCatalogConstraint> constraints,
        IReadOnlySet<(string Schema, string Name)> tables)
    {
        if (!OracleObjectCoverage.IndexTypes.Contains(index.IndexType, StringComparer.Ordinal))
        {
            throw new CatalogIncompleteException(
                $"Index '{index.Schema}.{index.Name}' is of type '{index.IndexType}', whose definition this build cannot rebuild from DBA_IND_COLUMNS; the extraction fails rather than emitting a different index.");
        }

        if (!tables.Contains((index.TableOwner, index.TableName)))
        {
            throw new CatalogIncompleteException(
                $"Index '{index.Schema}.{index.Name}' covers table '{index.TableOwner}.{index.TableName}', which is not in the covered set; the schema is not fully covered.");
        }

        if (index.Columns.Count == 0)
        {
            throw new CatalogIncompleteException($"Index '{index.Schema}.{index.Name}' returned no columns.");
        }

        if (IsConstraintBacked(index, constraints))
        {
            return;
        }

        builder.Append("CREATE ").Append(index.Unique ? "UNIQUE INDEX " : "INDEX ")
            .Append(Quote(index.Schema)).Append('.').Append(Quote(index.Name))
            .Append(" ON ").Append(Quote(index.TableOwner)).Append('.').Append(Quote(index.TableName))
            .Append(" (")
            .Append(string.Join(", ", index.Columns.Select(column => column.Descending ? $"{Quote(column.Name)} DESC" : Quote(column.Name))))
            .Append(");\n\n");
    }

    private static void AppendGrant(
        StringBuilder builder,
        OracleCatalogGrant grant,
        IReadOnlySet<(string Schema, string Name)> grantable)
    {
        if (!OracleObjectCoverage.ObjectPrivileges.Contains(grant.Privilege, StringComparer.Ordinal))
        {
            throw new CatalogIncompleteException(
                $"Object privilege '{grant.Privilege}' on '{grant.Schema}.{grant.ObjectName}' is not one this build writes back; the extraction fails rather than reporting access control it did not reproduce.");
        }

        if (!grantable.Contains((grant.Schema, grant.ObjectName)))
        {
            throw new CatalogIncompleteException(
                $"A privilege was granted on '{grant.Schema}.{grant.ObjectName}', which is not an object this run covered; the schema is not fully covered.");
        }

        builder.Append("GRANT ").Append(grant.Privilege).Append(" ON ")
            .Append(Quote(grant.Schema)).Append('.').Append(Quote(grant.ObjectName))
            .Append(" TO ").Append(grant.Grantee == "PUBLIC" ? "PUBLIC" : Quote(grant.Grantee));

        if (grant.Grantable)
        {
            builder.Append(" WITH GRANT OPTION");
        }

        builder.Append(";\n\n");
    }

    private static int Rank(OracleCatalogConstraint constraint) => constraint.Type switch
    {
        'P' => 0,
        'U' => 1,
        'C' => 2,
        _ => 3,
    };

    private static void AppendSequence(StringBuilder builder, OracleCatalogSequence sequence)
    {
        builder.Append("CREATE SEQUENCE ").Append(Quote(sequence.Schema)).Append('.').Append(Quote(sequence.Name))
            .Append(" START WITH ").Append(Integer(sequence.LastNumber, "LAST_NUMBER"))
            .Append(" INCREMENT BY ").Append(Integer(sequence.IncrementBy, "INCREMENT_BY"))
            .Append(" MINVALUE ").Append(Integer(sequence.MinValue, "MIN_VALUE"))
            .Append(" MAXVALUE ").Append(Integer(sequence.MaxValue, "MAX_VALUE"));

        string cache = Integer(sequence.CacheSize, "CACHE_SIZE");
        builder.Append(cache == "0" ? " NOCACHE" : $" CACHE {cache}")
            .Append(sequence.Cycle ? " CYCLE" : " NOCYCLE")
            .Append(sequence.Order ? " ORDER" : " NOORDER")
            .Append(";\n\n");
    }

    private static void AppendTable(StringBuilder builder, string schema, string table, List<OracleCatalogColumn> columns)
    {
        builder.Append("CREATE TABLE ").Append(Quote(schema)).Append('.').Append(Quote(table)).Append(" (\n");

        List<OracleCatalogColumn> ordered = [.. columns.OrderBy(column => column.Position)];
        for (int index = 0; index < ordered.Count; index++)
        {
            OracleCatalogColumn column = ordered[index];
            builder.Append("    ").Append(Quote(column.Name)).Append(' ').Append(RenderType(column));

            if (column.Default is string expression && Normalize(expression) is { Length: > 0 } rendered)
            {
                builder.Append(" DEFAULT ").Append(rendered);
            }

            if (column.NotNull)
            {
                builder.Append(" NOT NULL");
            }

            builder.Append(index == ordered.Count - 1 ? "\n" : ",\n");
        }

        builder.Append(");\n\n");
    }

    private static void AppendConstraint(
        StringBuilder builder,
        OracleCatalogConstraint constraint,
        IReadOnlyList<OracleCatalogColumn> owner,
        IReadOnlyDictionary<(string Owner, string Name), OracleCatalogConstraint> byName)
    {
        if (constraint.Type == 'C' && IsColumnNotNullCheck(constraint, owner))
        {
            // Oracle stores a NOT NULL column as a check constraint. The column already carries it, and
            // writing it twice would claim a table constraint the schema does not separately declare.
            return;
        }

        builder.Append("ALTER TABLE ").Append(Quote(constraint.Schema)).Append('.').Append(Quote(constraint.Table))
            .Append(" ADD CONSTRAINT ").Append(Quote(constraint.Name)).Append(' ');

        switch (constraint.Type)
        {
            case 'P':
                builder.Append("PRIMARY KEY (").Append(Columns(constraint)).Append(')');
                break;
            case 'U':
                builder.Append("UNIQUE (").Append(Columns(constraint)).Append(')');
                break;
            case 'C':
                builder.Append("CHECK (").Append(Condition(constraint)).Append(')');
                break;
            default:
                AppendForeignKey(builder, constraint, byName);
                break;
        }

        if (!constraint.Enabled)
        {
            builder.Append(" DISABLE");
        }

        builder.Append(";\n\n");
    }

    private static void AppendForeignKey(
        StringBuilder builder,
        OracleCatalogConstraint constraint,
        IReadOnlyDictionary<(string Owner, string Name), OracleCatalogConstraint> byName)
    {
        if (constraint.ReferencedOwner is not string referencedOwner ||
            constraint.ReferencedConstraint is not string referencedName ||
            !byName.TryGetValue((referencedOwner, referencedName), out OracleCatalogConstraint? parent))
        {
            throw new CatalogIncompleteException(
                $"Foreign key '{constraint.Schema}.{constraint.Name}' points at constraint '{constraint.ReferencedOwner}.{constraint.ReferencedConstraint}', which was not read; the schema is not fully covered.");
        }

        builder.Append("FOREIGN KEY (").Append(Columns(constraint)).Append(") REFERENCES ")
            .Append(Quote(parent.Schema)).Append('.').Append(Quote(parent.Table))
            .Append(" (").Append(Columns(parent)).Append(')');

        string rule = constraint.DeleteRule?.Trim().ToUpperInvariant() ?? string.Empty;
        if (rule == "CASCADE")
        {
            builder.Append(" ON DELETE CASCADE");
        }
        else if (rule == "SET NULL")
        {
            builder.Append(" ON DELETE SET NULL");
        }
    }

    private static void AppendProgramUnit(StringBuilder builder, OracleCatalogProgramUnit unit)
    {
        string body = Normalize(unit.Body);
        if (body.Length == 0)
        {
            throw new CatalogIncompleteException($"Program unit '{unit.Schema}.{unit.Name}' returned no source text.");
        }

        if (!body.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("CREATE OR REPLACE ");
        }

        builder.Append(body).Append("\n/\n\n");
    }

    private static string Columns(OracleCatalogConstraint constraint)
    {
        if (constraint.Columns.Count == 0)
        {
            throw new CatalogIncompleteException($"Constraint '{constraint.Schema}.{constraint.Name}' returned no columns.");
        }

        return string.Join(", ", constraint.Columns.Select(Quote));
    }

    private static string Condition(OracleCatalogConstraint constraint)
    {
        string condition = Normalize(constraint.SearchCondition ?? string.Empty);
        return condition.Length > 0
            ? condition
            : throw new CatalogIncompleteException($"Check constraint '{constraint.Schema}.{constraint.Name}' returned no condition.");
    }

    private static bool IsColumnNotNullCheck(OracleCatalogConstraint constraint, IReadOnlyList<OracleCatalogColumn> owner)
    {
        string condition = Normalize(constraint.SearchCondition ?? string.Empty).Replace('\n', ' ').Trim();
        while (condition.Contains("  ", StringComparison.Ordinal))
        {
            condition = condition.Replace("  ", " ", StringComparison.Ordinal);
        }

        foreach (OracleCatalogColumn column in owner)
        {
            if (!column.NotNull)
            {
                continue;
            }

            if (condition.Equals($"\"{column.Name}\" IS NOT NULL", StringComparison.OrdinalIgnoreCase) ||
                condition.Equals($"{column.Name} IS NOT NULL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the declared type from the catalog's own type name, length, precision and scale. A type
    /// this build cannot rebuild exactly is refused, because a near-miss type is a silent data contract
    /// change in whatever is generated downstream.
    /// </summary>
    internal static string RenderType(OracleCatalogColumn column)
    {
        string type = column.DataType.Trim().ToUpperInvariant();
        string where = $"{column.Schema}.{column.Table}.{column.Name}";

        // TIMESTAMP(n), INTERVAL DAY(n) TO SECOND(n) and friends carry their own parameters in DATA_TYPE.
        if (type.Contains('(', StringComparison.Ordinal))
        {
            return type;
        }

        switch (type)
        {
            case "NUMBER":
                return column.Precision is null
                    ? column.Scale is null or 0 ? "NUMBER" : $"NUMBER(*,{column.Scale})"
                    : column.Scale is null ? $"NUMBER({column.Precision})" : $"NUMBER({column.Precision},{column.Scale})";

            case "FLOAT":
                return column.Precision is null ? "FLOAT" : $"FLOAT({column.Precision})";

            case "NVARCHAR2":
            case "NCHAR":
                return column.CharLength is > 0
                    ? $"{type}({column.CharLength})"
                    : throw new CatalogIncompleteException(
                        $"Column '{where}' is {type} but this instance reported no character length, so its declared size cannot be rebuilt.");

            case "VARCHAR2":
            case "VARCHAR":
            case "CHAR":
                if (string.Equals(column.CharUsed, "C", StringComparison.OrdinalIgnoreCase))
                {
                    return column.CharLength is > 0
                        ? $"{type}({column.CharLength} CHAR)"
                        : throw new CatalogIncompleteException($"Column '{where}' uses character semantics but reported no character length.");
                }

                return column.Length is > 0
                    ? $"{type}({column.Length})"
                    : throw new CatalogIncompleteException($"Column '{where}' is {type} but reported no length.");

            case "RAW":
            case "UROWID":
                return column.Length is > 0
                    ? $"{type}({column.Length})"
                    : throw new CatalogIncompleteException($"Column '{where}' is {type} but reported no length.");

            case "DATE":
            case "LONG":
            case "LONG RAW":
            case "ROWID":
            case "CLOB":
            case "NCLOB":
            case "BLOB":
            case "BFILE":
            case "BINARY_FLOAT":
            case "BINARY_DOUBLE":
                return type;

            default:
                throw new CatalogIncompleteException(
                    $"Column '{where}' has type '{column.DataType}', which this build does not render; the extraction fails rather than guessing it.");
        }
    }

    /// <summary>Quotes an identifier, refusing one that could not survive quoting unambiguously.</summary>
    internal static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 128 ||
            identifier.Any(character => character is '"' || char.IsControl(character)))
        {
            throw new CatalogIncompleteException("The catalog reported an identifier that cannot be safely quoted.");
        }

        return $"\"{identifier}\"";
    }

    private static string Integer(string value, string what)
    {
        string trimmed = value.Trim();
        bool numeric = trimmed.Length > 0 &&
            (char.IsAsciiDigit(trimmed[0]) || trimmed[0] == '-') &&
            trimmed.Skip(1).All(char.IsAsciiDigit) &&
            (trimmed[0] != '-' || trimmed.Length > 1);

        return numeric
            ? trimmed
            : throw new CatalogIncompleteException(
                FormattableString.Invariant($"The catalog reported a non-integer {what} that cannot be written as DDL."));
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
}
