using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// Every catalog statement this worker is willing to run. All of them are SELECTs against ALL_ views with
/// the owner bound as a parameter, so a schema name can never reach the parser as text.
///
/// The column lists avoid anything added after Oracle 9i, numeric columns are projected through TO_CHAR so
/// no provider-specific numeric conversion sits between the catalog and the artifact, and the LONG columns
/// (DATA_DEFAULT, SEARCH_CONDITION) are selected last because several clients can only fetch a LONG once
/// the preceding columns of the row have been read.
/// </summary>
internal sealed class OracleCatalogReader(DbConnection connection, OracleParameterStyle parameterStyle, int commandTimeoutSeconds)
{
    /// <summary>
    /// The inventory of record. It is read before anything else so an object kind this build does not
    /// rebuild is refused by name rather than being absent from a result that reports success.
    /// </summary>
    private const string ObjectsSql = """
        SELECT OBJECT_NAME, OBJECT_TYPE, STATUS
        FROM ALL_OBJECTS WHERE OWNER = :owner ORDER BY OBJECT_TYPE, OBJECT_NAME
        """;

    private const string TablesSql = """
        SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :owner ORDER BY TABLE_NAME
        """;

    private const string ColumnsSql = """
        SELECT TABLE_NAME, COLUMN_NAME, TO_CHAR(COLUMN_ID) AS COLUMN_ID, DATA_TYPE,
               TO_CHAR(DATA_LENGTH) AS DATA_LENGTH, TO_CHAR(CHAR_LENGTH) AS CHAR_LENGTH, CHAR_USED,
               TO_CHAR(DATA_PRECISION) AS DATA_PRECISION, TO_CHAR(DATA_SCALE) AS DATA_SCALE, NULLABLE,
               DATA_DEFAULT
        FROM ALL_TAB_COLUMNS WHERE OWNER = :owner ORDER BY TABLE_NAME, COLUMN_ID
        """;

    /// <summary>The same read for a release whose ALL_TAB_COLUMNS has no character-semantics columns.</summary>
    private const string ColumnsWithoutCharacterSemanticsSql = """
        SELECT TABLE_NAME, COLUMN_NAME, TO_CHAR(COLUMN_ID) AS COLUMN_ID, DATA_TYPE,
               TO_CHAR(DATA_LENGTH) AS DATA_LENGTH,
               TO_CHAR(DATA_PRECISION) AS DATA_PRECISION, TO_CHAR(DATA_SCALE) AS DATA_SCALE, NULLABLE,
               DATA_DEFAULT
        FROM ALL_TAB_COLUMNS WHERE OWNER = :owner ORDER BY TABLE_NAME, COLUMN_ID
        """;

    private const string ConstraintsSql = """
        SELECT TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE, R_OWNER, R_CONSTRAINT_NAME, DELETE_RULE, STATUS,
               SEARCH_CONDITION
        FROM ALL_CONSTRAINTS WHERE OWNER = :owner AND CONSTRAINT_TYPE IN ('P', 'U', 'R', 'C')
        ORDER BY TABLE_NAME, CONSTRAINT_NAME
        """;

    private const string ConstraintColumnsSql = """
        SELECT CONSTRAINT_NAME, COLUMN_NAME, TO_CHAR(POSITION) AS POSITION
        FROM ALL_CONS_COLUMNS WHERE OWNER = :owner ORDER BY CONSTRAINT_NAME, POSITION
        """;

    private const string SequencesSql = """
        SELECT SEQUENCE_NAME, TO_CHAR(MIN_VALUE) AS MIN_VALUE, TO_CHAR(MAX_VALUE) AS MAX_VALUE,
               TO_CHAR(INCREMENT_BY) AS INCREMENT_BY, TO_CHAR(CACHE_SIZE) AS CACHE_SIZE,
               TO_CHAR(LAST_NUMBER) AS LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG
        FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :owner ORDER BY SEQUENCE_NAME
        """;

    private const string SourceSql = """
        SELECT TYPE, NAME, TO_CHAR(LINE) AS LINE, TEXT
        FROM ALL_SOURCE
        WHERE OWNER = :owner AND TYPE IN ('TYPE', 'TYPE BODY', 'PACKAGE', 'PACKAGE BODY', 'FUNCTION', 'PROCEDURE')
        ORDER BY TYPE, NAME, LINE
        """;

    /// <summary>
    /// INDEX_TYPE and UNIQUENESS are both present in 9i. The index's own owner is bound, and the table it
    /// covers is projected rather than assumed, so an index that sits on a table in another schema is
    /// visible to the coverage check instead of being attributed to this one.
    /// </summary>
    private const string IndexesSql = """
        SELECT INDEX_NAME, TABLE_OWNER, TABLE_NAME, UNIQUENESS, INDEX_TYPE
        FROM ALL_INDEXES WHERE OWNER = :owner ORDER BY INDEX_NAME
        """;

    private const string IndexColumnsSql = """
        SELECT INDEX_NAME, COLUMN_NAME, TO_CHAR(COLUMN_POSITION) AS COLUMN_POSITION, DESCEND
        FROM ALL_IND_COLUMNS WHERE INDEX_OWNER = :owner ORDER BY INDEX_NAME, COLUMN_POSITION
        """;

    private const string ObjectPrivilegesSql = """
        SELECT TABLE_NAME, GRANTEE, PRIVILEGE, GRANTABLE
        FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :owner ORDER BY TABLE_NAME, GRANTEE, PRIVILEGE
        """;

    /// <summary>
    /// Column-level privileges are read only to refuse them: this build writes object grants back and has
    /// no statement for a column grant, so one has to fail the extraction rather than go unmentioned.
    /// </summary>
    private const string ColumnPrivilegesSql = """
        SELECT TABLE_NAME, COLUMN_NAME, GRANTEE, PRIVILEGE
        FROM ALL_COL_PRIVS WHERE TABLE_SCHEMA = :owner ORDER BY TABLE_NAME, COLUMN_NAME, GRANTEE, PRIVILEGE
        """;

    private const string TriggersSql = """
        SELECT TRIGGER_NAME FROM ALL_TRIGGERS WHERE OWNER = :owner ORDER BY TRIGGER_NAME
        """;

    private const string DependenciesSql = """
        SELECT NAME, REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_LINK_NAME
        FROM ALL_DEPENDENCIES WHERE OWNER = :owner ORDER BY NAME, REFERENCED_OWNER, REFERENCED_NAME
        """;

    private static readonly string[] s_sourceOrder = ["TYPE", "TYPE BODY", "PACKAGE", "PACKAGE BODY", "FUNCTION", "PROCEDURE"];

    private readonly DbConnection _connection = connection;

    public async Task<IReadOnlyList<OracleCatalogObject>> ReadObjectsAsync(string schema, CancellationToken cancellationToken)
    {
        List<OracleCatalogObject> objects = [];
        await foreach (Row row in QueryAsync(ObjectsSql, schema, cancellationToken))
        {
            Bound(objects.Count, OracleSchemaProtocol.MaxObjects, "objects");
            objects.Add(new OracleCatalogObject(
                schema,
                row.Required("OBJECT_NAME"),
                row.Required("OBJECT_TYPE").Trim().ToUpperInvariant(),
                row.Text("STATUS")?.Trim().ToUpperInvariant() ?? "UNKNOWN"));
        }

        return objects;
    }

    public async Task<IReadOnlyList<string>> ReadTablesAsync(string schema, CancellationToken cancellationToken)
    {
        List<string> tables = [];
        await foreach (Row row in QueryAsync(TablesSql, schema, cancellationToken))
        {
            Bound(tables.Count, OracleSchemaProtocol.MaxTables, "tables");
            tables.Add(row.Required("TABLE_NAME"));
        }

        return tables;
    }

    public async Task<IReadOnlyList<OracleCatalogColumn>> ReadColumnsAsync(
        string schema,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        bool characterSemantics = true;
        List<OracleCatalogColumn> columns = [];

        try
        {
            await ReadInto(ColumnsSql);
        }
        catch (DbException)
        {
            // A release without CHAR_LENGTH/CHAR_USED is a real 9i shape, not a failure. The narrower read
            // is recorded because it is why a character-semantics column would later fail closed.
            columns.Clear();
            characterSemantics = false;
            findings.Add($"ALL_TAB_COLUMNS on this instance has no CHAR_LENGTH/CHAR_USED columns; '{schema}' was read with byte lengths only.");
            await ReadInto(ColumnsWithoutCharacterSemanticsSql);
        }

        return columns;

        async Task ReadInto(string sql)
        {
            await foreach (Row row in QueryAsync(sql, schema, cancellationToken))
            {
                Bound(columns.Count, OracleSchemaProtocol.MaxColumns, "columns");
                string table = row.Required("TABLE_NAME");
                string name = row.Required("COLUMN_NAME");
                int? position = row.Integer("COLUMN_ID");
                string dataType = row.Required("DATA_TYPE");
                int? length = row.Integer("DATA_LENGTH");
                int? charLength = characterSemantics ? row.Integer("CHAR_LENGTH") : null;
                string? charUsed = characterSemantics ? row.Text("CHAR_USED") : null;
                int? precision = row.Integer("DATA_PRECISION");
                int? scale = row.Integer("DATA_SCALE");
                bool notNull = string.Equals(row.Text("NULLABLE"), "N", StringComparison.Ordinal);
                string? @default = row.Long("DATA_DEFAULT");

                if (position is null)
                {
                    throw new CatalogIncompleteException($"Column '{schema}.{table}.{name}' reported no column position.");
                }

                columns.Add(new OracleCatalogColumn(
                    schema, table, name, position.Value, dataType, length, charLength, charUsed, precision, scale, notNull, @default));
            }
        }
    }

    public async Task<IReadOnlyList<OracleCatalogConstraint>> ReadConstraintsAsync(string schema, CancellationToken cancellationToken)
    {
        List<(string Table, string Name, char Type, string? ReferencedOwner, string? ReferencedConstraint, string? DeleteRule, bool Enabled, string? Condition)> heads = [];

        await foreach (Row row in QueryAsync(ConstraintsSql, schema, cancellationToken))
        {
            Bound(heads.Count, OracleSchemaProtocol.MaxConstraints, "constraints");
            string table = row.Required("TABLE_NAME");
            string name = row.Required("CONSTRAINT_NAME");
            string type = row.Required("CONSTRAINT_TYPE");
            string? referencedOwner = row.Text("R_OWNER");
            string? referencedConstraint = row.Text("R_CONSTRAINT_NAME");
            string? deleteRule = row.Text("DELETE_RULE");
            bool enabled = !string.Equals(row.Text("STATUS"), "DISABLED", StringComparison.OrdinalIgnoreCase);
            string? condition = row.Long("SEARCH_CONDITION");

            heads.Add((table, name, type[0], referencedOwner, referencedConstraint, deleteRule, enabled, condition));
        }

        Dictionary<string, List<(int Position, string Column)>> members = new(StringComparer.Ordinal);
        int memberCount = 0;
        await foreach (Row row in QueryAsync(ConstraintColumnsSql, schema, cancellationToken))
        {
            Bound(memberCount++, OracleSchemaProtocol.MaxConstraintColumns, "constraint columns");
            string name = row.Required("CONSTRAINT_NAME");
            string column = row.Required("COLUMN_NAME");
            int position = row.Integer("POSITION") ?? 0;

            if (!members.TryGetValue(name, out List<(int, string)>? list))
            {
                members[name] = list = [];
            }

            list.Add((position, column));
        }

        List<OracleCatalogConstraint> constraints = [];
        foreach ((string table, string name, char type, string? referencedOwner, string? referencedConstraint, string? deleteRule, bool enabled, string? condition) in heads)
        {
            IReadOnlyList<string> columns = members.TryGetValue(name, out List<(int Position, string Column)>? list)
                ? [.. list.OrderBy(entry => entry.Position).Select(entry => entry.Column)]
                : [];

            constraints.Add(new OracleCatalogConstraint(
                schema, table, name, type, referencedOwner, referencedConstraint, deleteRule, enabled, condition, columns));
        }

        return constraints;
    }

    public async Task<IReadOnlyList<OracleCatalogSequence>> ReadSequencesAsync(string schema, CancellationToken cancellationToken)
    {
        List<OracleCatalogSequence> sequences = [];
        await foreach (Row row in QueryAsync(SequencesSql, schema, cancellationToken))
        {
            Bound(sequences.Count, OracleSchemaProtocol.MaxSequences, "sequences");
            sequences.Add(new OracleCatalogSequence(
                schema,
                row.Required("SEQUENCE_NAME"),
                row.Required("MIN_VALUE"),
                row.Required("MAX_VALUE"),
                row.Required("INCREMENT_BY"),
                row.Required("CACHE_SIZE"),
                row.Required("LAST_NUMBER"),
                string.Equals(row.Text("CYCLE_FLAG"), "Y", StringComparison.OrdinalIgnoreCase),
                string.Equals(row.Text("ORDER_FLAG"), "Y", StringComparison.OrdinalIgnoreCase)));
        }

        return sequences;
    }

    /// <summary>
    /// Rebuilds each program unit from its stored source lines. The text is concatenated in LINE order and
    /// only line endings are normalized, so the body in the artifact is the body in the database.
    /// </summary>
    public async Task<IReadOnlyList<OracleCatalogProgramUnit>> ReadProgramUnitsAsync(string schema, CancellationToken cancellationToken)
    {
        Dictionary<(string Type, string Name), List<(int Line, string Text)>> units = [];
        int lines = 0;

        await foreach (Row row in QueryAsync(SourceSql, schema, cancellationToken))
        {
            Bound(lines++, OracleSchemaProtocol.MaxSourceLines, "source lines");
            string type = row.Required("TYPE");
            string name = row.Required("NAME");
            int line = row.Integer("LINE") ?? 0;
            string text = row.Text("TEXT") ?? string.Empty;

            if (!units.TryGetValue((type, name), out List<(int, string)>? body))
            {
                Bound(units.Count, OracleSchemaProtocol.MaxProgramUnits, "program units");
                units[(type, name)] = body = [];
            }

            body.Add((line, text.TrimEnd('\r', '\n')));
        }

        return
        [
            .. units
                .OrderBy(entry => Array.IndexOf(s_sourceOrder, entry.Key.Type))
                .ThenBy(entry => entry.Key.Name, StringComparer.Ordinal)
                .Select(entry => new OracleCatalogProgramUnit(
                    schema,
                    entry.Key.Type,
                    entry.Key.Name,
                    string.Join('\n', entry.Value.OrderBy(line => line.Line).Select(line => line.Text)).TrimEnd()))
        ];
    }

    /// <summary>
    /// Reads every index the schema owns, with its columns in key order. Constraint-backed indexes are
    /// returned too: the emitter decides which of them a constraint already declares, so the count the
    /// artifact reports is the count the catalog holds rather than the subset that needed a statement.
    /// </summary>
    public async Task<IReadOnlyList<OracleCatalogIndex>> ReadIndexesAsync(string schema, CancellationToken cancellationToken)
    {
        List<(string Name, string TableOwner, string TableName, bool Unique, string IndexType)> heads = [];
        await foreach (Row row in QueryAsync(IndexesSql, schema, cancellationToken))
        {
            Bound(heads.Count, OracleSchemaProtocol.MaxIndexes, "indexes");
            heads.Add((
                row.Required("INDEX_NAME"),
                row.Required("TABLE_OWNER"),
                row.Required("TABLE_NAME"),
                string.Equals(row.Text("UNIQUENESS"), "UNIQUE", StringComparison.OrdinalIgnoreCase),
                row.Text("INDEX_TYPE")?.Trim().ToUpperInvariant() ?? "UNKNOWN"));
        }

        Dictionary<string, List<(int Position, OracleCatalogIndexColumn Column)>> members = new(StringComparer.Ordinal);
        int memberCount = 0;
        await foreach (Row row in QueryAsync(IndexColumnsSql, schema, cancellationToken))
        {
            Bound(memberCount++, OracleSchemaProtocol.MaxIndexColumns, "index columns");
            string name = row.Required("INDEX_NAME");
            OracleCatalogIndexColumn column = new(
                row.Required("COLUMN_NAME"),
                string.Equals(row.Text("DESCEND"), "DESC", StringComparison.OrdinalIgnoreCase));

            if (!members.TryGetValue(name, out List<(int, OracleCatalogIndexColumn)>? list))
            {
                members[name] = list = [];
            }

            list.Add((row.Integer("COLUMN_POSITION") ?? 0, column));
        }

        return
        [
            .. heads.Select(head => new OracleCatalogIndex(
                schema,
                head.Name,
                head.TableOwner,
                head.TableName,
                head.Unique,
                head.IndexType,
                members.TryGetValue(head.Name, out List<(int Position, OracleCatalogIndexColumn Column)>? list)
                    ? [.. list.OrderBy(entry => entry.Position).Select(entry => entry.Column)]
                    : []))
        ];
    }

    public async Task<IReadOnlyList<OracleCatalogGrant>> ReadObjectGrantsAsync(string schema, CancellationToken cancellationToken)
    {
        SortedSet<(string Object, string Grantee, string Privilege, bool Grantable)> distinct = [];
        int rows = 0;

        await foreach (Row row in QueryAsync(ObjectPrivilegesSql, schema, cancellationToken))
        {
            Bound(rows++, OracleSchemaProtocol.MaxGrants, "object privileges");

            // The same privilege granted by two grantors is one grant in the schema being rebuilt.
            distinct.Add((
                row.Required("TABLE_NAME"),
                row.Required("GRANTEE"),
                row.Required("PRIVILEGE").Trim().ToUpperInvariant(),
                string.Equals(row.Text("GRANTABLE"), "YES", StringComparison.OrdinalIgnoreCase)));
        }

        return [.. distinct.Select(entry => new OracleCatalogGrant(schema, entry.Object, entry.Grantee, entry.Privilege, entry.Grantable))];
    }

    /// <summary>Names every column-level privilege, which this build refuses rather than rewriting.</summary>
    public async Task<IReadOnlyList<string>> ReadColumnGrantTargetsAsync(string schema, CancellationToken cancellationToken)
    {
        SortedSet<string> targets = new(StringComparer.Ordinal);
        int rows = 0;

        await foreach (Row row in QueryAsync(ColumnPrivilegesSql, schema, cancellationToken))
        {
            Bound(rows++, OracleSchemaProtocol.MaxGrants, "column privileges");
            targets.Add($"{row.Required("PRIVILEGE").Trim().ToUpperInvariant()} on {schema}.{row.Required("TABLE_NAME")}.{row.Required("COLUMN_NAME")} to {row.Required("GRANTEE")}");
        }

        return [.. targets];
    }

    public async Task<IReadOnlyList<string>> ReadTriggerNamesAsync(string schema, CancellationToken cancellationToken)
    {
        List<string> triggers = [];
        await foreach (Row row in QueryAsync(TriggersSql, schema, cancellationToken))
        {
            Bound(triggers.Count, OracleSchemaProtocol.MaxTriggers, "triggers");
            triggers.Add(row.Required("TRIGGER_NAME"));
        }

        return triggers;
    }

    /// <summary>
    /// Names every dependency that leaves the covered set. A reference resolves only when the referenced
    /// object is in the inventory this run actually read, so a same-owner reference is checked as strictly
    /// as a cross-owner one and no longer passes merely because the owner was requested. A server-owned
    /// reference is tolerated only for the named language builtins every PL/SQL unit compiles against;
    /// anything else under a server-owned schema is a real escape and is reported.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadDependencyEscapesAsync(
        string schema,
        IReadOnlySet<(string Schema, string Name)> inventory,
        IReadOnlyList<string> covered,
        CancellationToken cancellationToken)
    {
        SortedSet<string> escapes = new(StringComparer.Ordinal);
        int rows = 0;

        await foreach (Row row in QueryAsync(DependenciesSql, schema, cancellationToken))
        {
            Bound(rows++, OracleSchemaProtocol.MaxDependencies, "dependencies");
            string name = row.Required("NAME");
            string? referencedOwner = row.Text("REFERENCED_OWNER");
            string? referencedName = row.Text("REFERENCED_NAME");
            string? link = row.Text("REFERENCED_LINK_NAME");

            if (!string.IsNullOrWhiteSpace(link))
            {
                escapes.Add($"{schema}.{name} -> remote object over a database link");
                continue;
            }

            if (referencedOwner is null || referencedName is null)
            {
                escapes.Add($"{schema}.{name} -> a dependency the catalog did not name");
                continue;
            }

            if (OracleSchemaProtocol.SystemOwners.Contains(referencedOwner, StringComparer.Ordinal))
            {
                if (!OracleObjectCoverage.IsSystemBuiltin(referencedName))
                {
                    escapes.Add($"{schema}.{name} -> {referencedOwner}.{referencedName} (server-owned object this build does not cover)");
                }

                continue;
            }

            if (!covered.Contains(referencedOwner, StringComparer.Ordinal))
            {
                escapes.Add($"{schema}.{name} -> {referencedOwner}.{referencedName}");
                continue;
            }

            if (!inventory.Contains((referencedOwner, referencedName)))
            {
                escapes.Add($"{schema}.{name} -> {referencedOwner}.{referencedName} (absent from the inventory this run read)");
            }
        }

        return [.. escapes];
    }

    private static void Bound(int count, int ceiling, string what)
    {
        if (count >= ceiling)
        {
            throw new CatalogIncompleteException($"The catalog returned more than {ceiling} {what}, over this worker's ceiling.");
        }
    }

    private async IAsyncEnumerable<Row> QueryAsync(
        string sql,
        string owner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using DbCommand command = _connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = parameterStyle == OracleParameterStyle.Positional
            ? sql.Replace(":owner", "?", StringComparison.Ordinal)
            : sql;

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "owner";
        parameter.DbType = DbType.String;
        parameter.Direction = ParameterDirection.Input;
        parameter.Value = owner;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        Row row = new(reader);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return row;
        }
    }

    /// <summary>
    /// Ordinal-resolving access over the current row. Values are read by the alias in the SELECT list, so
    /// the two column-list variants above share one set of call sites.
    /// </summary>
    private sealed class Row(DbDataReader reader)
    {
        private readonly Dictionary<string, int> _ordinals = Map(reader);

        private static Dictionary<string, int> Map(DbDataReader reader)
        {
            Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                ordinals[reader.GetName(index)] = index;
            }

            return ordinals;
        }

        public string Required(string column) =>
            Text(column) ?? throw new CatalogIncompleteException($"The catalog returned no value for {column}.");

        public string? Text(string column)
        {
            if (!_ordinals.TryGetValue(column, out int ordinal) || reader.IsDBNull(ordinal))
            {
                return null;
            }

            string value = reader.GetString(ordinal);
            return value.Length > OracleSchemaProtocol.MaxLongCharacters
                ? throw new CatalogIncompleteException($"The catalog value for {column} exceeded this worker's text ceiling.")
                : value;
        }

        /// <summary>Reads a LONG. Clients that refuse <c>GetString</c> on a LONG are streamed instead.</summary>
        public string? Long(string column)
        {
            if (!_ordinals.TryGetValue(column, out int ordinal))
            {
                return null;
            }

            try
            {
                return Text(column);
            }
            catch (Exception exception) when (exception is InvalidCastException or NotSupportedException or InvalidOperationException)
            {
                return Stream(ordinal, column);
            }
        }

        public int? Integer(string column)
        {
            string? value = Text(column);
            if (value is null)
            {
                return null;
            }

            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new CatalogIncompleteException($"The catalog value for {column} was not an integer.");
        }

        private string Stream(int ordinal, string column)
        {
            StringBuilder builder = new();
            char[] buffer = new char[4096];
            long offset = 0;

            while (true)
            {
                long read;
                try
                {
                    read = reader.GetChars(ordinal, offset, buffer, 0, buffer.Length);
                }
                catch (Exception exception) when (exception is InvalidCastException or NotSupportedException or InvalidOperationException)
                {
                    throw new CatalogIncompleteException($"The catalog value for {column} could not be read by this client.");
                }

                if (read <= 0)
                {
                    return builder.ToString();
                }

                builder.Append(buffer, 0, (int)read);
                offset += read;

                if (builder.Length > OracleSchemaProtocol.MaxLongCharacters)
                {
                    throw new CatalogIncompleteException($"The catalog value for {column} exceeded this worker's text ceiling.");
                }
            }
        }
    }
}
