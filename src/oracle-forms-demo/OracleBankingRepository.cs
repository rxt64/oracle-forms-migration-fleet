using System.Data;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace OracleFormsDemo;

/// <summary>
/// All Oracle access for the demo. Every statement is parameterized, every connection is opened
/// for the duration of a single operation, and mutations run inside an explicit transaction.
/// </summary>
public sealed class OracleBankingRepository
{
    private readonly string _connectionString;

    public OracleBankingRepository(IConfiguration configuration)
    {
        _connectionString = configuration["Oracle:ConnectionString"] ?? string.Empty;
    }

    public async Task<bool> IsDatabaseAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = CreateCommand(connection, "SELECT 1 FROM DUAL");
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return ToInt64(result) == 1;
        }
        catch (Exception exception) when (exception is OracleException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<CustomerProfile?> AuthenticateCustomerAsync(
        long accountId,
        string password,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.ACCOUNT_ID,
                   r.GIVEN_NAME || ' ' || r.FAMILY_NAME AS ACCOUNT_HOLDER,
                   a.BRANCH_CODE,
                   a.ACCOUNT_KIND
              FROM BANK_ACCOUNT a
              JOIN BANK_ACCOUNT_REQUEST r ON r.REQUEST_ID = a.REQUEST_ID
             WHERE a.ACCOUNT_ID = :accountId
               AND a.ONLINE_ENABLED = 'Y'
               AND a.ONLINE_PASSWORD_HASH = STANDARD_HASH(:password, 'SHA256')
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add(Number("accountId", accountId));
        command.Parameters.Add(Text("password", password, 64));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task<string?> AuthenticateManagerAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT USERNAME
              FROM BANK_STAFF_USER
             WHERE USERNAME = LOWER(TRIM(:username))
               AND ROLE_CODE = 'MANAGER'
               AND ACTIVE_FLAG = 'Y'
               AND PASSWORD_HASH = STANDARD_HASH(:password, 'SHA256')
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add(Text("username", username, 40));
        command.Parameters.Add(Text("password", password, 64));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
    }

    public async Task<CustomerProfile?> GetProfileAsync(long accountId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.ACCOUNT_ID,
                   r.GIVEN_NAME || ' ' || r.FAMILY_NAME AS ACCOUNT_HOLDER,
                   a.BRANCH_CODE,
                   a.ACCOUNT_KIND
              FROM BANK_ACCOUNT a
              JOIN BANK_ACCOUNT_REQUEST r ON r.REQUEST_ID = a.REQUEST_ID
             WHERE a.ACCOUNT_ID = :accountId
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add(Number("accountId", accountId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task<StatementResponse?> GetStatementAsync(long accountId, CancellationToken cancellationToken)
    {
        const string profileSql = """
            SELECT a.ACCOUNT_ID,
                   r.GIVEN_NAME || ' ' || r.FAMILY_NAME AS ACCOUNT_HOLDER,
                   a.BRANCH_CODE,
                   a.ACCOUNT_KIND,
                   LEGACY_BANKING_API.CURRENT_BALANCE(a.ACCOUNT_ID) AS CURRENT_BALANCE
              FROM BANK_ACCOUNT a
              JOIN BANK_ACCOUNT_REQUEST r ON r.REQUEST_ID = a.REQUEST_ID
             WHERE a.ACCOUNT_ID = :accountId
            """;

        const string linesSql = """
            SELECT TRANSACTION_ID, TRANSACTION_TS, DIRECTION_CODE, AMOUNT, REFERENCE_CODE
              FROM BANK_ACCOUNT_STATEMENT
             WHERE ACCOUNT_ID = :accountId
             ORDER BY TRANSACTION_TS, TRANSACTION_ID
            """;

        await using var connection = await OpenAsync(cancellationToken);

        CustomerProfile profile;
        decimal balance;
        await using (var command = CreateCommand(connection, profileSql))
        {
            command.Parameters.Add(Number("accountId", accountId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            profile = ReadProfile(reader);
            balance = reader.GetDecimal(4);
        }

        var lines = new List<StatementLine>();
        await using (var command = CreateCommand(connection, linesSql))
        {
            command.Parameters.Add(Number("accountId", accountId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new StatementLine(
                    (long)reader.GetDecimal(0),
                    reader.GetDateTime(1),
                    reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetString(4)));
            }
        }

        return new StatementResponse(profile, balance, lines);
    }

    public async Task<decimal?> CalculateSimpleInterestAsync(
        decimal principal,
        decimal annualRate,
        decimal years,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT LEGACY_BANKING_API.CALCULATE_SIMPLE_INTEREST(:principal, :annualRate, :years)
              FROM DUAL
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add(Number("principal", principal));
        command.Parameters.Add(Number("annualRate", annualRate));
        command.Parameters.Add(Number("years", years));

        return ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<RegistrationOutcome> EnableOnlineAccessAsync(
        long accountId,
        string emailAddress,
        string password,
        CancellationToken cancellationToken)
    {
        const string lookupSql = """
            SELECT a.ONLINE_ENABLED
              FROM BANK_ACCOUNT a
              JOIN BANK_ACCOUNT_REQUEST r ON r.REQUEST_ID = a.REQUEST_ID
             WHERE a.ACCOUNT_ID = :accountId
               AND LOWER(r.EMAIL_ADDRESS) = LOWER(:emailAddress)
               FOR UPDATE OF a.ONLINE_ENABLED
            """;

        const string updateSql = """
            UPDATE BANK_ACCOUNT
               SET ONLINE_ENABLED = 'Y',
                   ONLINE_PASSWORD_HASH = STANDARD_HASH(:password, 'SHA256')
             WHERE ACCOUNT_ID = :accountId
               AND ONLINE_ENABLED = 'N'
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string onlineEnabled;
        await using (var command = CreateCommand(connection, lookupSql))
        {
            command.Transaction = (OracleTransaction)transaction;
            command.Parameters.Add(Number("accountId", accountId));
            command.Parameters.Add(Text("emailAddress", emailAddress, 120));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return RegistrationOutcome.AccountNotFound;
            }

            onlineEnabled = reader.GetString(0);
        }

        if (onlineEnabled == "Y")
        {
            return RegistrationOutcome.AlreadyEnabled;
        }

        await using (var command = CreateCommand(connection, updateSql))
        {
            command.Transaction = (OracleTransaction)transaction;
            command.Parameters.Add(Text("password", password, BankingValidation.MaxPasswordLength));
            command.Parameters.Add(Number("accountId", accountId));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return RegistrationOutcome.AlreadyEnabled;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return RegistrationOutcome.Enabled;
    }

    public async Task<AccountRequestCreated> CreateAccountRequestAsync(
        AccountRequestSubmission request,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO BANK_ACCOUNT_REQUEST
                (REQUEST_ID, BRANCH_CODE, ACCOUNT_KIND, HONORIFIC, GIVEN_NAME, FAMILY_NAME, DATE_OF_BIRTH,
                 WORK_PHONE, HOME_PHONE, STREET_ADDRESS, REGION_CODE, POSTAL_CODE, EMAIL_ADDRESS,
                 REQUEST_STATUS, SUBMITTED_AT, DECIDED_AT)
            VALUES
                (:requestId, :branchCode, :accountKind, :honorific, :givenName, :familyName, :dateOfBirth,
                 :workPhone, :homePhone, :streetAddress, :regionCode, :postalCode, :emailAddress,
                 'SUBMITTED', SYSTIMESTAMP, NULL)
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long requestId;
        await using (var command = CreateCommand(connection, "SELECT BANK_REQUEST_SEQ.NEXTVAL FROM DUAL"))
        {
            command.Transaction = (OracleTransaction)transaction;
            requestId = ToInt64(await command.ExecuteScalarAsync(cancellationToken))
                ?? throw new InvalidOperationException("BANK_REQUEST_SEQ did not return a value.");
        }

        await using (var command = CreateCommand(connection, insertSql))
        {
            command.Transaction = (OracleTransaction)transaction;
            command.Parameters.Add(Number("requestId", requestId));
            command.Parameters.Add(Text("branchCode", request.BranchCode, 12));
            command.Parameters.Add(Text("accountKind", request.AccountKind, 12));
            command.Parameters.Add(Text("honorific", request.Honorific, 8));
            command.Parameters.Add(Text("givenName", request.GivenName, 40));
            command.Parameters.Add(Text("familyName", request.FamilyName, 40));
            command.Parameters.Add(new OracleParameter("dateOfBirth", OracleDbType.Date)
            {
                Value = request.DateOfBirth!.Value.ToDateTime(TimeOnly.MinValue)
            });
            command.Parameters.Add(Text("workPhone", request.WorkPhone, 20));
            command.Parameters.Add(Text("homePhone", request.HomePhone, 20));
            command.Parameters.Add(Text("streetAddress", request.StreetAddress, 120));
            command.Parameters.Add(Text("regionCode", request.RegionCode, 20));
            command.Parameters.Add(Text("postalCode", request.PostalCode, 12));
            command.Parameters.Add(Text("emailAddress", request.EmailAddress, 120));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AccountRequestCreated(requestId, "SUBMITTED");
    }

    public async Task<TransactionCreated?> RecordTransactionAsync(
        long accountId,
        decimal amount,
        string directionCode,
        string referenceCode,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO BANK_TRANSACTION
                (TRANSACTION_ID, ACCOUNT_ID, TRANSACTION_TS, AMOUNT, REFERENCE_CODE, DIRECTION_CODE)
            VALUES
                (:transactionId, :accountId, SYSTIMESTAMP, :amount, :referenceCode, :directionCode)
            """;

        const string readBackSql = """
            SELECT t.TRANSACTION_TS,
                   LEGACY_BANKING_API.CURRENT_BALANCE(t.ACCOUNT_ID) AS CURRENT_BALANCE
              FROM BANK_TRANSACTION t
             WHERE t.TRANSACTION_ID = :transactionId
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long transactionId;
        await using (var command = CreateCommand(connection, "SELECT BANK_TRANSACTION_SEQ.NEXTVAL FROM DUAL"))
        {
            command.Transaction = (OracleTransaction)transaction;
            transactionId = ToInt64(await command.ExecuteScalarAsync(cancellationToken))
                ?? throw new InvalidOperationException("BANK_TRANSACTION_SEQ did not return a value.");
        }

        await using (var command = CreateCommand(connection, insertSql))
        {
            command.Transaction = (OracleTransaction)transaction;
            command.Parameters.Add(Number("transactionId", transactionId));
            command.Parameters.Add(Number("accountId", accountId));
            command.Parameters.Add(Number("amount", amount));
            command.Parameters.Add(Text("referenceCode", referenceCode, 30));
            command.Parameters.Add(Text("directionCode", directionCode, 2));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTime transactionTs;
        decimal balance;
        await using (var command = CreateCommand(connection, readBackSql))
        {
            command.Transaction = (OracleTransaction)transaction;
            command.Parameters.Add(Number("transactionId", transactionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            transactionTs = reader.GetDateTime(0);
            balance = reader.GetDecimal(1);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TransactionCreated(transactionId, transactionTs, directionCode, amount, referenceCode, balance);
    }

    public async Task<IReadOnlyList<AccountRequestSummary>> ListAccountRequestsAsync(
        string requestStatus,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT REQUEST_ID, BRANCH_CODE, ACCOUNT_KIND, HONORIFIC, GIVEN_NAME, FAMILY_NAME,
                   DATE_OF_BIRTH, EMAIL_ADDRESS, REQUEST_STATUS, SUBMITTED_AT, DECIDED_AT
              FROM BANK_ACCOUNT_REQUEST
             WHERE REQUEST_STATUS = :requestStatus
             ORDER BY SUBMITTED_AT, REQUEST_ID
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add(Text("requestStatus", requestStatus, 12));

        var requests = new List<AccountRequestSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new AccountRequestSummary(
                (long)reader.GetDecimal(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDateTime(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDateTime(9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
        }

        return requests;
    }

    public async Task<(ApprovalOutcome Outcome, long? AccountId)> ApproveRequestAsync(
        long requestId,
        CancellationToken cancellationToken)
    {
        // The package raises user-defined exceptions without PRAGMA EXCEPTION_INIT, so they are
        // translated to outcome codes here rather than being caught as generic ORA-06510 errors.
        const string sql = """
            BEGIN
                :outcome := 'APPROVED';
                LEGACY_BANKING_API.APPROVE_REQUEST(:requestId, :accountId);
            EXCEPTION
                WHEN LEGACY_BANKING_API.REQUEST_NOT_FOUND THEN :outcome := 'NOT_FOUND';
                WHEN LEGACY_BANKING_API.ALREADY_DECIDED THEN :outcome := 'ALREADY_DECIDED';
            END;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Transaction = (OracleTransaction)transaction;

        var outcomeParameter = new OracleParameter("outcome", OracleDbType.Varchar2, 20)
        {
            Direction = ParameterDirection.Output
        };
        var accountIdParameter = new OracleParameter("accountId", OracleDbType.Decimal)
        {
            Direction = ParameterDirection.Output
        };

        command.Parameters.Add(outcomeParameter);
        command.Parameters.Add(Number("requestId", requestId));
        command.Parameters.Add(accountIdParameter);

        await command.ExecuteNonQueryAsync(cancellationToken);

        var outcome = ToText(outcomeParameter.Value) switch
        {
            "APPROVED" => ApprovalOutcome.Approved,
            "ALREADY_DECIDED" => ApprovalOutcome.AlreadyDecided,
            _ => ApprovalOutcome.RequestNotFound
        };

        if (outcome != ApprovalOutcome.Approved)
        {
            return (outcome, null);
        }

        var accountId = ToInt64(accountIdParameter.Value);
        await transaction.CommitAsync(cancellationToken);
        return (ApprovalOutcome.Approved, accountId);
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Oracle connection string is not configured.");
        }

        var connection = new OracleConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static OracleCommand CreateCommand(OracleConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        return command;
    }

    private static CustomerProfile ReadProfile(OracleDataReader reader) => new(
        (long)reader.GetDecimal(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3));

    private static OracleParameter Number(string name, decimal value) =>
        new(name, OracleDbType.Decimal) { Value = value };

    private static OracleParameter Text(string name, string? value, int size) =>
        new(name, OracleDbType.Varchar2, size) { Value = (object?)value ?? DBNull.Value };

    private static long? ToInt64(object? value) => value switch
    {
        null or DBNull => null,
        OracleDecimal oracleDecimal => oracleDecimal.IsNull ? null : (long)oracleDecimal.Value,
        long number => number,
        decimal number => (long)number,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    private static decimal? ToDecimal(object? value) => value switch
    {
        null or DBNull => null,
        OracleDecimal oracleDecimal => oracleDecimal.IsNull ? null : oracleDecimal.Value,
        decimal number => number,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
    };

    private static string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        OracleString oracleString => oracleString.IsNull ? null : oracleString.Value,
        string text => text,
        _ => value.ToString()
    };
}
