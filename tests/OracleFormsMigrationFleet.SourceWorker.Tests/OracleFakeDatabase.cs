using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// An in-memory ADO.NET provider. It answers whatever SELECT the worker actually sends, so a test using it
/// exercises the real catalog statements, the real reader and the real emitter. It is not, and must never
/// be reported as, evidence that a real Oracle instance was read.
/// </summary>
public sealed class FakeOracleDatabase
{
    private readonly List<(Func<string, bool> Matches, Func<FakeResultSet> Rows)> _handlers = [];

    public List<FakeExecution> Executed { get; } = [];

    public int Opened { get; private set; }

    public FakeOracleDatabase On(Func<string, bool> matches, FakeResultSet rows)
    {
        _handlers.Add((matches, () => rows));
        return this;
    }

    public FakeOracleDatabase OnView(string view, FakeResultSet rows) =>
        On(sql => sql.Contains(view, StringComparison.Ordinal), rows);

    /// <summary>Answers ahead of any handler already registered, so a fixture can be narrowed in one test.</summary>
    public FakeOracleDatabase Override(Func<string, bool> matches, FakeResultSet rows)
    {
        _handlers.Insert(0, (matches, () => rows));
        return this;
    }

    public FakeOracleDatabase OverrideView(string view, FakeResultSet rows) =>
        Override(sql => sql.Contains(view, StringComparison.Ordinal), rows);

    public FakeOracleDatabase Fail(Func<string, bool> matches)
    {
        _handlers.Insert(0, (matches, () => throw new FakeDbException("ORA-00904: invalid identifier")));
        return this;
    }

    public DbConnection Connect(string connectionString)
    {
        Opened++;
        return new FakeDbConnection(this, connectionString);
    }

    internal FakeResultSet Resolve(string sql, string? owner)
    {
        Executed.Add(new FakeExecution(sql, owner));
        foreach ((Func<string, bool> matches, Func<FakeResultSet> rows) in _handlers)
        {
            if (matches(sql))
            {
                return rows();
            }
        }

        return FakeResultSet.Empty;
    }
}

public sealed record FakeExecution(string CommandText, string? Owner);

/// <summary>A value the client will only surrender through <c>GetChars</c>, as some clients do for LONG.</summary>
public sealed record FakeStreamedValue(string Text);

public sealed class FakeResultSet(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
{
    public static FakeResultSet Empty { get; } = new([], []);

    public IReadOnlyList<string> Columns { get; } = columns;

    public IReadOnlyList<object?[]> Rows { get; } = rows;

    public static FakeResultSet Of(string columns, params object?[][] rows) =>
        new([.. columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)], rows);
}

public sealed class FakeDbException(string message) : DbException(message);

internal sealed class FakeDbConnection(FakeOracleDatabase database, string connectionString) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    [AllowNull]
    public override string ConnectionString { get; set; } = connectionString;

    public override string Database => "FAKE";

    public override string DataSource => "fake";

    public override string ServerVersion => "9.2.0.1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(database) { Connection = this };
}

internal sealed class FakeDbCommand(FakeOracleDatabase database) : DbCommand
{
    private readonly FakeDbParameterCollection _parameters = [];

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => throw new NotSupportedException("This worker is read-only.");

    public override object? ExecuteScalar() => throw new NotSupportedException("This worker is read-only.");

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        new FakeDbDataReader(database.Resolve(CommandText, _parameters.Count > 0 ? _parameters[0].Value as string : null));
}

internal sealed class FakeDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (object value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((DbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}

internal sealed class FakeDbDataReader(FakeResultSet result) : DbDataReader
{
    private int _index = -1;

    public override int Depth => 0;

    public override int FieldCount => result.Columns.Count;

    public override bool HasRows => result.Rows.Count > 0;

    public override bool IsClosed => _index >= result.Rows.Count;

    public override int RecordsAffected => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_index < result.Rows.Count;

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => result.Columns[ordinal];

    public override int GetOrdinal(string name)
    {
        for (int index = 0; index < result.Columns.Count; index++)
        {
            if (string.Equals(result.Columns[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IndexOutOfRangeException(name);
    }

    public override bool IsDBNull(int ordinal) => Raw(ordinal) is null;

    public override string GetString(int ordinal) => Raw(ordinal) switch
    {
        string text => text,
        FakeStreamedValue => throw new InvalidCastException("This client only surrenders a LONG through GetChars."),
        object value => value.ToString()!,
        _ => throw new InvalidCastException("null"),
    };

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string text = Raw(ordinal) switch
        {
            FakeStreamedValue streamed => streamed.Text,
            string value => value,
            _ => string.Empty,
        };

        if (buffer is null || dataOffset >= text.Length)
        {
            return 0;
        }

        int count = Math.Min(length, text.Length - (int)dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override object GetValue(int ordinal) => Raw(ordinal) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, FieldCount);
        for (int index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    public override Type GetFieldType(int ordinal) => typeof(string);

    public override string GetDataTypeName(int ordinal) => "VARCHAR2";

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

    public override byte GetByte(int ordinal) => throw new NotSupportedException();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override char GetChar(int ordinal) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

    public override double GetDouble(int ordinal) => throw new NotSupportedException();

    public override float GetFloat(int ordinal) => throw new NotSupportedException();

    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

    public override short GetInt16(int ordinal) => throw new NotSupportedException();

    public override int GetInt32(int ordinal) => throw new NotSupportedException();

    public override long GetInt64(int ordinal) => throw new NotSupportedException();

    private object? Raw(int ordinal) => result.Rows[_index][ordinal];
}

/// <summary>
/// A connection factory over the fake. The connect string is still held where the production factory holds
/// it, so a test can assert that nothing downstream ever sees it.
/// </summary>
public sealed class FakeOracleConnectionFactory(
    FakeOracleDatabase database,
    IReadOnlyList<string> allowlist,
    OracleParameterStyle parameterStyle = OracleParameterStyle.Named,
    bool configured = true) : IOracleConnectionFactory
{
    public const string Secret = "Dsn=MERIDIAN9I;Uid=ofm_reader;Pwd=n0t-in-any-output";

    public bool Configured { get; } = configured;

    public string? FirstMissingSetting => Configured ? null : OracleSourceConfiguration.ConnectionStringVariable;

    public string ProviderAlias => "fake-odbc";

    public OracleParameterStyle ParameterStyle { get; } = parameterStyle;

    public IReadOnlyList<string> SchemaAllowlist { get; } = allowlist;

    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = database.Connect(Secret);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

/// <summary>The MERIDIAN catalog as an Oracle 9i instance reports it, one result set per catalog view.</summary>
public static class MeridianCatalog
{
    /// <summary>
    /// Every object DBA_OBJECTS reports for MERIDIAN: four tables, the five indexes its primary and unique
    /// keys are built on, one sequence and one procedure. The detail reads below must reconcile against
    /// exactly this list, which is what makes an unlisted view or synonym a failure rather than a silence.
    /// </summary>
    public const int ObjectCount = 11;

    public static string ProcedureBody { get; } = """
        PROCEDURE PLACE_ORDER(p_customer_id IN NUMBER,
                              p_product_id  IN NUMBER,
                              p_quantity    IN NUMBER,
                              p_order_id    OUT NUMBER) IS
          v_price NUMBER(12,2);
        BEGIN
          SELECT UNIT_PRICE INTO v_price FROM PRODUCTS WHERE PRODUCT_ID = p_product_id;
          SELECT SEQ_ORDER_ID.NEXTVAL INTO p_order_id FROM DUAL;
          INSERT INTO ORDERS (ORDER_ID, CUSTOMER_ID, ORDER_DATE, STATUS)
            VALUES (p_order_id, p_customer_id, SYSDATE, 'NEW');
          INSERT INTO ORDER_ITEMS (ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE)
            VALUES (p_order_id, p_product_id, p_quantity, v_price);
        END PLACE_ORDER;
        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static FakeOracleDatabase Build()
    {
        FakeOracleDatabase database = new();

        database.OnView("DBA_OBJECTS", Objects());

        database.OnView("DBA_TABLES", FakeResultSet.Of("TABLE_NAME",
            ["CUSTOMERS"], ["ORDERS"], ["ORDER_ITEMS"], ["PRODUCTS"]));

        database.OnView("DBA_TAB_COLUMNS", FakeResultSet.Of(
            "TABLE_NAME, COLUMN_NAME, COLUMN_ID, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED, DATA_PRECISION, DATA_SCALE, NULLABLE, DATA_DEFAULT",
            ["CUSTOMERS", "CUSTOMER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["CUSTOMERS", "CUSTOMER_NAME", "2", "VARCHAR2", "100", "100", "B", null, null, "N", null],
            ["CUSTOMERS", "CREDIT_LIMIT", "3", "NUMBER", "22", null, null, "12", "2", "Y", "0"],
            ["ORDERS", "ORDER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["ORDERS", "CUSTOMER_ID", "2", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["ORDERS", "ORDER_DATE", "3", "DATE", "7", null, null, null, null, "N", new FakeStreamedValue("SYSDATE")],
            ["ORDERS", "STATUS", "4", "VARCHAR2", "20", "20", "B", null, null, "N", "'NEW'"],
            ["ORDER_ITEMS", "ORDER_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["ORDER_ITEMS", "PRODUCT_ID", "2", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["ORDER_ITEMS", "QUANTITY", "3", "NUMBER", "22", null, null, "8", "0", "N", "1"],
            ["ORDER_ITEMS", "UNIT_PRICE", "4", "NUMBER", "22", null, null, "12", "2", "N", null],
            ["PRODUCTS", "PRODUCT_ID", "1", "NUMBER", "22", null, null, "10", "0", "N", null],
            ["PRODUCTS", "PRODUCT_NAME", "2", "VARCHAR2", "80", "80", "B", null, null, "N", null],
            ["PRODUCTS", "UNIT_PRICE", "3", "NUMBER", "22", null, null, "12", "2", "N", null]));

        database.OnView("DBA_CONSTRAINTS", FakeResultSet.Of(
            "TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE, R_OWNER, R_CONSTRAINT_NAME, DELETE_RULE, STATUS, SEARCH_CONDITION",
            ["CUSTOMERS", "PK_CUSTOMERS", "P", null, null, null, "ENABLED", null],
            ["CUSTOMERS", "SYS_C0011001", "C", null, null, null, "ENABLED", "\"CUSTOMER_NAME\" IS NOT NULL"],
            ["ORDERS", "FK_ORDERS_CUSTOMER", "R", "MERIDIAN", "PK_CUSTOMERS", "NO ACTION", "ENABLED", null],
            ["ORDERS", "PK_ORDERS", "P", null, null, null, "ENABLED", null],
            ["ORDER_ITEMS", "CK_ORDER_ITEMS_QTY", "C", null, null, null, "ENABLED", "QUANTITY > 0"],
            ["ORDER_ITEMS", "FK_ORDER_ITEMS_ORDER", "R", "MERIDIAN", "PK_ORDERS", "CASCADE", "ENABLED", null],
            ["ORDER_ITEMS", "FK_ORDER_ITEMS_PRODUCT", "R", "MERIDIAN", "PK_PRODUCTS", "NO ACTION", "ENABLED", null],
            ["ORDER_ITEMS", "PK_ORDER_ITEMS", "P", null, null, null, "ENABLED", null],
            ["PRODUCTS", "PK_PRODUCTS", "P", null, null, null, "ENABLED", null],
            ["PRODUCTS", "UQ_PRODUCTS_NAME", "U", null, null, null, "ENABLED", null]));

        database.OnView("DBA_CONS_COLUMNS", FakeResultSet.Of(
            "CONSTRAINT_NAME, COLUMN_NAME, POSITION",
            ["FK_ORDERS_CUSTOMER", "CUSTOMER_ID", "1"],
            ["FK_ORDER_ITEMS_ORDER", "ORDER_ID", "1"],
            ["FK_ORDER_ITEMS_PRODUCT", "PRODUCT_ID", "1"],
            ["PK_CUSTOMERS", "CUSTOMER_ID", "1"],
            ["PK_ORDERS", "ORDER_ID", "1"],
            ["PK_ORDER_ITEMS", "ORDER_ID", "1"],
            ["PK_ORDER_ITEMS", "PRODUCT_ID", "2"],
            ["PK_PRODUCTS", "PRODUCT_ID", "1"],
            ["UQ_PRODUCTS_NAME", "PRODUCT_NAME", "1"]));

        database.OnView("DBA_SEQUENCES", FakeResultSet.Of(
            "SEQUENCE_NAME, MIN_VALUE, MAX_VALUE, INCREMENT_BY, CACHE_SIZE, LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG",
            ["SEQ_ORDER_ID", "1", "999999999999999999999999999", "1", "20", "1041", "N", "N"]));

        database.OnView("DBA_INDEXES", Indexes());
        database.OnView("DBA_IND_COLUMNS", IndexColumns());

        database.OnView("DBA_TAB_PRIVS", ObjectPrivileges());
        database.OnView("DBA_COL_PRIVS", ColumnPrivileges());

        database.OnView("DBA_SOURCE", Source(ProcedureBody));
        database.OnView("DBA_TRIGGERS", FakeResultSet.Of("TRIGGER_NAME"));
        database.OnView("DBA_DEPENDENCIES", Dependencies());

        return database;
    }

    /// <summary>DBA_TAB_PRIVS as the dictionary reports it, with the object owner the reader checks.</summary>
    public static FakeResultSet ObjectPrivileges(params object?[][] rows) =>
        FakeResultSet.Of("OWNER, TABLE_NAME, GRANTEE, PRIVILEGE, GRANTABLE", rows);

    /// <summary>DBA_COL_PRIVS as the dictionary reports it, with the object owner the reader checks.</summary>
    public static FakeResultSet ColumnPrivileges(params object?[][] rows) =>
        FakeResultSet.Of("OWNER, TABLE_NAME, COLUMN_NAME, GRANTEE, PRIVILEGE", rows);

    /// <summary>The inventory, optionally with extra rows a negative test wants DBA_OBJECTS to report.</summary>
    public static FakeResultSet Objects(params object?[][] extra) =>
        FakeResultSet.Of("OBJECT_NAME, OBJECT_TYPE, STATUS",
        [
            .. new object?[][]
            {
                ["PK_CUSTOMERS", "INDEX", "VALID"],
                ["PK_ORDERS", "INDEX", "VALID"],
                ["PK_ORDER_ITEMS", "INDEX", "VALID"],
                ["PK_PRODUCTS", "INDEX", "VALID"],
                ["UQ_PRODUCTS_NAME", "INDEX", "VALID"],
                ["PLACE_ORDER", "PROCEDURE", "VALID"],
                ["SEQ_ORDER_ID", "SEQUENCE", "VALID"],
                ["CUSTOMERS", "TABLE", "VALID"],
                ["ORDERS", "TABLE", "VALID"],
                ["ORDER_ITEMS", "TABLE", "VALID"],
                ["PRODUCTS", "TABLE", "VALID"],
            },
            .. extra,
        ]);

    /// <summary>The five constraint-backed indexes, plus anything a test adds.</summary>
    public static FakeResultSet Indexes(params object?[][] extra) =>
        FakeResultSet.Of("INDEX_NAME, TABLE_OWNER, TABLE_NAME, UNIQUENESS, INDEX_TYPE",
        [
            .. new object?[][]
            {
                ["PK_CUSTOMERS", "MERIDIAN", "CUSTOMERS", "UNIQUE", "NORMAL"],
                ["PK_ORDERS", "MERIDIAN", "ORDERS", "UNIQUE", "NORMAL"],
                ["PK_ORDER_ITEMS", "MERIDIAN", "ORDER_ITEMS", "UNIQUE", "NORMAL"],
                ["PK_PRODUCTS", "MERIDIAN", "PRODUCTS", "UNIQUE", "NORMAL"],
                ["UQ_PRODUCTS_NAME", "MERIDIAN", "PRODUCTS", "UNIQUE", "NORMAL"],
            },
            .. extra,
        ]);

    public static FakeResultSet IndexColumns(params object?[][] extra) =>
        FakeResultSet.Of("INDEX_NAME, COLUMN_NAME, COLUMN_POSITION, DESCEND",
        [
            .. new object?[][]
            {
                ["PK_CUSTOMERS", "CUSTOMER_ID", "1", "ASC"],
                ["PK_ORDERS", "ORDER_ID", "1", "ASC"],
                ["PK_ORDER_ITEMS", "ORDER_ID", "1", "ASC"],
                ["PK_ORDER_ITEMS", "PRODUCT_ID", "2", "ASC"],
                ["PK_PRODUCTS", "PRODUCT_ID", "1", "ASC"],
                ["UQ_PRODUCTS_NAME", "PRODUCT_NAME", "1", "ASC"],
            },
            .. extra,
        ]);

    /// <summary>The dependency rows MERIDIAN really has, plus anything a test adds.</summary>
    public static FakeResultSet Dependencies(params object?[][] extra) =>
        FakeResultSet.Of("NAME, REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_LINK_NAME",
        [
            .. new object?[][]
            {
                ["PLACE_ORDER", "MERIDIAN", "ORDERS", null],
                ["PLACE_ORDER", "SYS", "STANDARD", null],
            },
            .. extra,
        ]);

    public static FakeResultSet Source(string body, string type = "PROCEDURE", string name = "PLACE_ORDER")
    {
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        object?[][] rows = new object?[lines.Length][];
        for (int index = 0; index < lines.Length; index++)
        {
            rows[index] = [type, name, (index + 1).ToString(), lines[index] + "\n"];
        }

        return FakeResultSet.Of("TYPE, NAME, LINE, TEXT", rows);
    }

    public static OracleSchemaExtractionRequest Request(params string[] schemas) =>
        new(OracleSchemaProtocol.SchemaVersion,
            "meridian-9i",
            3,
            new string('a', 64),
            schemas.Length == 0 ? ["MERIDIAN"] : schemas);

    public static string Ddl(OracleSchemaExtractionResult result) =>
        Encoding.UTF8.GetString(result.SchemaArtifact!.Content) is string json &&
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("ddl").GetString() is string ddl
            ? ddl
            : throw new InvalidOperationException("The artifact carried no DDL.");
}
